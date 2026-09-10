using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ConnectorWatch;

// Public identity diagnostic only. Private probes belong to the isolated maintainer command.
public static class NvapiProbe
{
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern IntPtr Query(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Enumerate([Out] IntPtr[] devices, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Name(IntPtr device, StringBuilder text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Pci(IntPtr device, out uint deviceId, out uint subsystemId, out uint revisionId, out uint extDeviceId);
    static T Function<T>(uint id) where T : Delegate
    {
        var p = Query(id); if (p == IntPtr.Zero) throw new Exception($"NVAPI entry point {id:X8} unavailable.");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }
    public static void Run()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The optional NVAPI diagnostic is Windows-only; ConnectorWatch telemetry uses NVML on Linux and Windows.");
        int initialized = Function<NoArgs>(0x0150e828)();
        if (initialized != 0) throw new Exception($"NVAPI initialization: {initialized}");
        try
        {
            var devices = new IntPtr[64];
            int result = Function<Enumerate>(0xe5ac921f)(devices, out uint count);
            if (result != 0 || count > 64) throw new Exception($"GPU enumeration failed: {result}");
            var output = new List<object>();
            for (int i = 0; i < count; i++)
            {
                var name = new StringBuilder(64); int nameStatus = Function<Name>(0xceee8e9f)(devices[i], name);
                int pciStatus = Function<Pci>(0x2ddfb66e)(devices[i], out var deviceId, out var subsystemId, out var revisionId, out var extDeviceId);
                output.Add(new { index = i, name = nameStatus == 0 ? name.ToString() : null,
                    pci_status = pciStatus, device_id = deviceId.ToString("X8"), subsystem_id = subsystemId.ToString("X8"),
                    connector_voltage_v = (double?)null,
                    connector_status = "PUBLIC_IDENTITY_ONLY; use isolated maintainer validation for private rail evidence" });
            }
            Console.WriteLine(JsonSerializer.Serialize(new { timestamp_utc = DateTimeOffset.UtcNow, devices = output }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Function<NoArgs>(0xd22bdd7e)(); }
    }
}
