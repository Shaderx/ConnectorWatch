using System.IO.Pipes;
using System.Text;

namespace ConnectorWatch;

public static class ControlServerTests
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ConnectorWatch-control-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var helloWire = ControlProtocol.Serialize(new ControlRequest("hello", "gui-a"));
            Check(helloWire.Contains("\"command\":\"hello\"") && helloWire.Contains("\"client_id\":\"gui-a\"") &&
                  !helloWire.Contains("expected_instance_id"), "control request wire shape");
            Check(ControlProtocol.TryParseRequest(helloWire + "\r", out var parsed) && parsed!.Command == "hello" && parsed.ClientId == "gui-a",
                "control request parser");
            using var stop = new CancellationTokenSource();
            using var server = new ControlServer(folder, stop, instanceId: "test-instance", processId: 1234);
            server.Start();
            server.Ready.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();

            // A connected client that never sends a newline must not block the
            // next heartbeat from acquiring the single pipe instance.
            using (var stalled = new NamedPipeClientStream(".", server.PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                stalled.ConnectAsync(2000).GetAwaiter().GetResult();
                Task.Delay(TimeSpan.FromSeconds(2.2)).GetAwaiter().GetResult();
            }

            var hello = SendAsync(server, new ControlRequest("hello", "gui-a")).GetAwaiter().GetResult();
            Check(hello.Protocol == ControlProtocol.Version && hello.Pid == 1234 && hello.Ok,
                "control hello identity");
            Check(hello.DataDirectory == ControlEndpoint.NormalizeDataDirectory(folder) &&
                  hello.InstanceId == "test-instance", "control hello path and instance");

            var lease = SendAsync(server, new ControlRequest("lease", "gui-a", hello.InstanceId)).GetAwaiter().GetResult();
            Check(lease.Ok && server.AlertsSuppressed, "control lease suppresses alerts");
            var competingLease = SendAsync(server, new ControlRequest("lease", "gui-b", hello.InstanceId)).GetAwaiter().GetResult();
            Check(!competingLease.Ok && server.AlertsSuppressed, "competing lease rejected");
            var wrongIdentity = SendAsync(server, new ControlRequest("release", "gui-b", "old-instance")).GetAwaiter().GetResult();
            Check(!wrongIdentity.Ok && server.AlertsSuppressed, "stale instance rejected");
            var wrongRelease = SendAsync(server, new ControlRequest("release", "gui-b", hello.InstanceId)).GetAwaiter().GetResult();
            Check(!wrongRelease.Ok && server.AlertsSuppressed, "foreign release rejected");
            var release = SendAsync(server, new ControlRequest("release", "gui-a", hello.InstanceId)).GetAwaiter().GetResult();
            Check(release.Ok && !server.AlertsSuppressed, "control release restores alerts");

            server.ReferenceCommand = request => new ControlCommandResult(
                true, request.Operator ?? request.ClientId, "REFERENCE_ACCEPTED");
            int refreshes = 0;
            server.ApprovalRefresh = () => { refreshes++; return new(true, "Checking approvals"); };
            Check(!SendAsync(server, new ControlRequest("refresh-driver-approvals", "gui-a", "old-instance"))
                .GetAwaiter().GetResult().Ok && refreshes == 0, "stale instance cannot refresh approvals");
            Check(SendAsync(server, new ControlRequest("refresh-driver-approvals", "gui-a", hello.InstanceId))
                .GetAwaiter().GetResult().Ok && refreshes == 1, "approval refresh is identity-bound");
            var accept = SendAsync(server, new ControlRequest("accept-reference", "gui-a",
                hello.InstanceId, "operator", "reviewed")).GetAwaiter().GetResult();
            Check(accept.Ok && accept.ReferenceState == "REFERENCE_ACCEPTED" &&
                accept.Detail == "operator", "identity-bound reference operator command");
            var staleAccept = SendAsync(server, new ControlRequest("accept-reference", "gui-a",
                "old-instance")).GetAwaiter().GetResult();
            Check(!staleAccept.Ok && staleAccept.ReferenceState == null,
                "stale instance cannot mutate reference lifecycle");

            var invalid = SendAsync(server, new ControlRequest("unknown", "gui-a")).GetAwaiter().GetResult();
            Check(!invalid.Ok && invalid.Protocol == ControlProtocol.Version, "unknown command rejected");

            for (int i = 0; i < 600; i++) server.Publish("{\"sample\":" + i + "}", i + "\n");
            var live = SendAsync(server, new ControlRequest("live", "gui-a", hello.InstanceId)).GetAwaiter().GetResult();
            Check(live.Ok && live.Live?.Rows.Length == 512 && live.Live.Rows[0] == "88\n" && live.Live.Status == "{\"sample\":599}", "live pipe returns bounded latest RAM history and status");
            var staleLive = SendAsync(server, new ControlRequest("live", "gui-a", "old-instance")).GetAwaiter().GetResult();
            Check(!staleLive.Ok && staleLive.Live == null, "stale instance cannot receive replacement live data");
            var stopResponse = SendAsync(server, new ControlRequest("stop", "gui-a", hello.InstanceId)).GetAwaiter().GetResult();
            Check(stopResponse.Ok && stop.IsCancellationRequested, "control stop cancels daemon");
            server.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            Check(ControlEndpoint.Name(folder) == server.PipeName, "normalized endpoint name is stable");
            Console.WriteLine("PASS: control pipe, identity, lease and stop checks.");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    static async Task<ControlResponse> SendAsync(ControlServer server, ControlRequest request)
    {
        await using var client = new NamedPipeClientStream(".", server.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        using var reader = new StreamReader(client, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true)
        {
            NewLine = "\n",
            AutoFlush = true,
        };
        await writer.WriteLineAsync(ControlProtocol.Serialize(request));
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        if (line is null) throw new Exception("Control server closed before its response.");
        return System.Text.Json.JsonSerializer.Deserialize<ControlResponse>(line, ControlProtocol.Json)
            ?? throw new Exception("Control response was empty.");
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
    }
}
