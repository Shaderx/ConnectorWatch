using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ConnectorWatch.Gui;

// Best effort: reporting an error must never cause a second GUI failure.
public sealed class GuiLog
{
    public static GuiLog Current { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ConnectorWatch", "logs", "gui.jsonl"));
    readonly object gate = new();
    readonly Dictionary<string, long> lastErrors = new();
    readonly string path;
    readonly long maxBytes;
    public GuiLog(string path, long maxBytes = 2 * 1024 * 1024)
    { this.path = path; this.maxBytes = maxBytes; }

    public void Write(string eventName, object? details = null, Exception? exception = null, bool throttle = false)
    {
        try
        {
            lock (gate)
            {
                long now = Stopwatch.GetTimestamp();
                if (throttle && lastErrors.TryGetValue(eventName, out long previous) &&
                    Stopwatch.GetElapsedTime(previous, now).TotalSeconds < 60) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length >= maxBytes)
                    File.Move(path, path + ".1", true);
                string line = JsonSerializer.Serialize(new {
                    timestamp_utc = DateTimeOffset.UtcNow, event_name = eventName,
                    pid = Environment.ProcessId, version = typeof(GuiLog).Assembly.GetName().Version?.ToString(),
                    details, exception = exception?.ToString()
                });
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                if (throttle) lastErrors[eventName] = now;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { Debug.WriteLine("GUI diagnostic log unavailable: " + ex.Message); }
    }
}
