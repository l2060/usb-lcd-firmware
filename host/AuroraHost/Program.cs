using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AuroraHost;

public static class Program
{
    static readonly string Nic = "eno1";
    static readonly string Disk = "nvme0";

    public static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "upload-bg")
            return BgUploader.Run(args[1], args[2]);

        string portName = ResolvePort(args.Length > 0 ? args[0] : "");

        string host;
        try { host = File.ReadAllText("/etc/hostname").Trim(); }
        catch { host = Environment.MachineName; }
        if (string.IsNullOrEmpty(host)) host = Environment.MachineName;

        string ip = LocalIp();
        int cores = Environment.ProcessorCount;

        var sp = new SerialPort(portName, 115200)
        {
            ReadTimeout = 20,
            WriteTimeout = 2000,
            DtrEnable = true,
            RtsEnable = true,
        };
        for (int i = 0; i < 60; i++)
        {
            try
            {
                if (!sp.IsOpen)
                {
                    sp.PortName = ResolvePort(portName);
                    sp.Open();
                }
                break;
            }
            catch (Exception ex)
            {
                if (i == 0) Console.WriteLine($"[!] 打开串口失败: {ex.Message}  等待设备…");
                Thread.Sleep(1000);
            }
        }
        if (!sp.IsOpen) { Console.WriteLine("设备未出现，退出"); return 1; }
        portName = sp.PortName;
        using var link = new Link(sp);

        var reader = new ProcReader();
        reader.Init(host, ip, cores, Nic, Disk);
        var dash = new SkiaDashboard();

        // make sure the device runs the application, not the bootloader
        for (int i = 0; i < 3; i++)
        {
            try
            {
                var (st, data) = link.Info();
                if (data.Length >= 1 && data[0] == 0xB0)
                {
                    Console.WriteLine("在 Bootloader，发送 RUN…");
                    try { link.Cmd(0x0013, ReadOnlySpan<byte>.Empty, 800, 0); } catch { }
                    Thread.Sleep(4000);
                    sp.Close(); sp.Open();
                    continue;
                }
                Console.WriteLine($"设备 App v{data[0]}.{data[1]}  {data[2] | (data[3] << 8)}x{data[4] | (data[5] << 8)}");
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] INFO: {ex.Message}");
                Thread.Sleep(1500);
            }
        }

        Console.WriteLine($"监控 {host} ({ip})  {cores} cores  ->  {portName}");

        var img = new byte[SkiaDashboard.W * SkiaDashboard.H * 2];
        var rle = new byte[80 * 1024];
        var sw = new Stopwatch();
        for (; ; )
        {
            var t0 = Stopwatch.GetTimestamp();
            Metrics m = reader.Next();
            var t1 = Stopwatch.GetTimestamp();
            dash.RenderInto(m, img);
            var t2 = Stopwatch.GetTimestamp();
            try
            {
                int rleLen = SendFrame(link, img, rle);
                var t3 = Stopwatch.GetTimestamp();
                double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
                Console.WriteLine($"{DateTime.Now:HH:mm:ss}  rle={rleLen}B  read={Ms(t0,t1):F0} render={Ms(t1,t2):F0} send={Ms(t2,t3):F0}ms  cpu={m.Cpu:P0} mem={m.MemPct:P0} {m.Temp}C {m.Freq}GHz");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] {ex.Message} (重开串口)");
                try { sp.Close(); Thread.Sleep(1500); sp.PortName = ResolvePort(portName); sp.Open(); } catch { }
            }
        }
    }

    static string ResolvePort(string wanted)
    {
        if (!string.IsNullOrEmpty(wanted) && File.Exists(wanted)) return wanted;
        try
        {
            var byId = Directory.GetFiles("/dev/serial/by-id", "*wch*");
            if (byId.Length > 0) return byId[0];
        }
        catch { }
        var acm = Directory.GetFiles("/dev", "ttyACM*").OrderBy(p => p).ToArray();
        if (acm.Length > 0) return acm[0];
        return string.IsNullOrEmpty(wanted) ? "/dev/ttyACM0" : wanted;
    }

    static int SendFrame(Link link, byte[] img, byte[] rleBuf)
    {
        int rleLen = EncodeRle(img, rleBuf);
        const int RUNS = 682;
        for (int off = 0; off < rleLen; off += RUNS * 3)
        {
            int n = Math.Min(RUNS * 3, rleLen - off);
            var (st, _) = link.Cmd(0x0105, rleBuf.AsSpan(off, n), 2000);
            if (st != 0) throw new IOException($"RLE off={off} status={st}");
        }
        var (s2, data) = link.Cmd(0x0105, ReadOnlySpan<byte>.Empty, 2000);
        if (s2 != 0) throw new IOException($"RLE 收尾 status={s2}");
        if (data.Length >= 4)
        {
            uint px = BitConverter.ToUInt32(data, 0);
            if (px != (uint)(SkiaDashboard.W * SkiaDashboard.H))
                throw new IOException($"像素数不符 {px}");
        }
        return rleLen;
    }

    static int EncodeRle(byte[] rgb565, byte[] dst)
    {
        int n = rgb565.Length / 2;
        int o = 0;
        int i = 0;
        while (i < n)
        {
            ushort c = (ushort)((rgb565[i * 2] << 8) | rgb565[i * 2 + 1]);
            int run = 1;
            while (i + run < n && run < 255)
            {
                ushort c2 = (ushort)((rgb565[(i + run) * 2] << 8) | rgb565[(i + run) * 2 + 1]);
                if (c2 != c) break;
                run++;
            }
            dst[o++] = (byte)run;
            dst[o++] = (byte)(c & 0xFF);
            dst[o++] = (byte)(c >> 8);
            i += run;
        }
        return o;
    }

    static string LocalIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ua.Address))
                        return ua.Address.ToString();
                }
            }
        }
        catch { }
        return "0.0.0.0";
    }
}
