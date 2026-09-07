using System.Runtime.InteropServices;

namespace ConnectorWatch;

// Desktop notification belongs to the platform shell; analysis remains portable.
// A modal Windows dialog runs on a separate background thread, never the sampler.
public static class DesktopAlerts
{
    private static int showing;

    public static bool ShouldNotify(string status) =>
        status is "SUDDEN_DROOP" or "BASELINE_SHIFT" or "VOLTAGE_UNAVAILABLE";

    public static void Notify(string status, double? volts, string detail)
    {
        if (!ShouldNotify(status) || !OperatingSystem.IsWindows() || !Environment.UserInteractive)
            return;
        if (Interlocked.CompareExchange(ref showing, 1, 0) != 0) return;
        new Thread(() =>
        {
            try
            {
                string message = status == "VOLTAGE_UNAVAILABLE"
                    ? "GPU input-voltage monitoring is unavailable. Check ConnectorWatch status and logs."
                    : "ConnectorWatch detected a GPU input-voltage drop. Save your work and reduce GPU load while you investigate.";
                if (volts.HasValue) message += $"\n\n16-pin input: {volts.Value:F3} V";
                message += $"\nStatus: {status}";
                if (!string.IsNullOrWhiteSpace(detail)) message += "\n" + detail;
                message += "\n\nThis is a voltage-trend warning, not a diagnosis of connector damage.";
                _ = MessageBox(IntPtr.Zero, message, "ConnectorWatch warning", 0x00040000 | 0x00010000 | 0x00000030);
            }
            catch (Exception ex) { Console.Error.WriteLine("Desktop warning could not be displayed: " + ex.Message); }
            finally { Volatile.Write(ref showing, 0); }
        }) { IsBackground = true, Name = "ConnectorWatch-desktop-warning" }.Start();
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
