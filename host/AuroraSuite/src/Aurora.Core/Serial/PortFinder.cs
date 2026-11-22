using Microsoft.Win32;

namespace Aurora.Core.Serial;

/// <summary>Finds the WCH CDC ports (VID 1A86 / PID FE0C or FE0C-like) on Windows.</summary>
public static class PortFinder
{
    public const string WchVid = "VID_1A86";

    public sealed record PortInfo(string Name, string Description, bool IsAurora);

    public static IReadOnlyList<PortInfo> List()
    {
        var list = new List<PortInfo>();
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb != null)
            {
                foreach (var vid in usb.GetSubKeyNames())
                {
                    if (!vid.Contains(WchVid, StringComparison.OrdinalIgnoreCase)) continue;
                    using var dev = usb.OpenSubKey(vid);
                    if (dev == null) continue;
                    foreach (var inst in dev.GetSubKeyNames())
                    {
                        using var k = dev.OpenSubKey(inst);
                        if (k == null) continue;
                        string? port = k.GetValue("PortName") as string;
                        if (string.IsNullOrEmpty(port)) continue;
                        string desc = (k.GetValue("FriendlyName") as string) ?? vid;
                        list.Add(new PortInfo(port, desc, true));
                    }
                }
            }
        }
        catch { }

        // fallback: every serial port
        try
        {
            using var sc = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (sc != null)
            {
                foreach (var name in sc.GetValueNames())
                {
                    string? port = sc.GetValue(name) as string;
                    if (string.IsNullOrEmpty(port)) continue;
                    if (list.Any(p => p.Name.Equals(port, StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add(new PortInfo(port, name, false));
                }
            }
        }
        catch { }

        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Probe candidates with an INFO command to find a live Aurora device.</summary>
    public static Device.DeviceSession? Probe(IEnumerable<string> candidates, Action<string>? log = null)
    {
        foreach (var port in candidates)
        {
            Device.DeviceSession? session = null;
            try
            {
                session = Device.DeviceSession.Connect(port);
                var info = session.Info_Refresh();
                if (info != null)
                {
                    log?.Invoke($"检测到设备: {port}  {info}");
                    return session;
                }
                session.Dispose();
            }
            catch
            {
                session?.Dispose();
            }
        }
        return null;
    }
}
