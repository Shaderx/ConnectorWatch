using System.Security.Cryptography;

namespace ConnectorWatch;

public static class ShadowPredictionCommandTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAILED prediction command: " + name);
            checks++;
        }
        void Reject(string[] args, string name)
        {
            bool rejected = false;
            try { ShadowPredictionCommand.TryHandle(args, out _); }
            catch (Exception e) when (e is ArgumentException or IOException) { rejected = true; }
            Check(rejected, name);
        }
        Check(!ShadowPredictionCommand.TryHandle(["--unrelated"], out _), "unrelated command ignored");
        Reject(["--evaluate-prediction"], "missing directory rejected");
        string root = Path.Combine(Path.GetTempPath(), "ConnectorWatch-shadow-command-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(root, "input");
        Directory.CreateDirectory(data);
        var originalWriter = Console.Out;
        try
        {
            string source = Path.Combine(data, "telemetry-2020-01-01.csv");
            File.WriteAllText(source, "timestamp_utc,gpu_uuid,input_voltage_v\n");
            byte[] before = SHA256.HashData(File.ReadAllBytes(source));
            string report = Path.Combine(root, "reports", "first.json");
            Reject(["--evaluate-prediction", data, "--as-of", "invalid"], "invalid cutoff rejected");
            Reject(["--evaluate-prediction", data, "--as-of", "2999-01-01"], "future cutoff rejected");
            Reject(["--evaluate-prediction", data, "--output"], "missing output value rejected");
            Reject(["--evaluate-prediction", data, "--output", Path.Combine(data, "reference.json")], "source directory protected");
            Reject(["--evaluate-prediction", data, "--output", report, "--output", report], "duplicate option rejected");
            Reject(["--evaluate-prediction", data, "--output", Path.ChangeExtension(report, ".csv")], "JSON extension required");
            using var console = new StringWriter();
            Console.SetOut(console);
            bool handled = ShadowPredictionCommand.TryHandle(
                ["--evaluate-prediction", data, "--as-of", "2020-02-01", "--output", report], out int exit);
            Check(handled && exit == 0, "empty-data evaluation completes offline");
            Check(File.Exists(report) && File.Exists(Path.ChangeExtension(report, ".md")), "both report formats retained");
            using (var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(report)))
                Check(json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object, "valid machine-readable report");
            Reject(["--evaluate-prediction", data, "--output", report], "old report cannot be overwritten");
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))) &&
                Directory.GetFiles(data).Length == 1, "source bytes and directory remain untouched");

            // Cross the reader/evaluator/command seam: changing load must not
            // split each minute into an unrelated identity.
            string study = Path.Combine(root, "study");
            Directory.CreateDirectory(study);
            const string header = "timestamp_utc,gpu_uuid,input_voltage_v,connector_current_a,connector_power_w,board_power_w,gpu_temp_c,pcie_voltage_v,electrical_source,analysis_power_source,connector_power_provenance,status,sample_age_seconds\n";
            for (int day = 0; day < 9; day++)
            {
                var date = new DateTimeOffset(2020, 3, 1, 0, 0, 0, TimeSpan.Zero).AddDays(day);
                var csv = new System.Text.StringBuilder(header);
                for (int minute = 0; minute < 24; minute++)
                {
                    double current = 10 + minute % 12 * 2;
                    double temperature = 40 + minute % 3 * 3;
                    double voltage = 12.3 - current * .005 - temperature * .0005;
                    double power = voltage * current;
                    double boardPower = power + 5 + minute % 4;
                    for (int second = 0; second < 6; second++)
                        csv.Append(FormattableString.Invariant($"{date.AddMinutes(minute).AddSeconds(second):O},synthetic-gpu,{voltage:R},{current:R},{power:R},{boardPower:R},{temperature:R},12.3,synthetic-rails,CONNECTOR_POWER,DerivedFromVoltageAndCurrent,NO_SHIFT_DETECTED,0\n"));
                }
                File.WriteAllText(Path.Combine(study, $"telemetry-{date:yyyy-MM-dd}.csv"), csv.ToString());
            }
            string studyReport = Path.Combine(root, "reports", "study.json");
            Check(ShadowPredictionCommand.TryHandle(["--evaluate-prediction", study,
                "--as-of", "2020-04-01", "--output", studyReport], out exit) && exit == 0,
                "multi-day command completes without hardware");
            using (var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(studyReport)))
            {
                var evaluation = json.RootElement;
                Check(evaluation.GetProperty("State").GetString() == "SUFFICIENT",
                    "varying load across nine days supports a chronological comparison");
                Check(evaluation.GetProperty("Coverage").GetProperty("FairHeldoutRows").GetInt32() > 0,
                    "reader and evaluator share a matched heldout population");
            }
        }
        finally
        {
            Console.SetOut(originalWriter);
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine($"PASS: {checks} prediction command checks.");
    }
}
