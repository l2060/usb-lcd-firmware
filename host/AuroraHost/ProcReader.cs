using System.Runtime.InteropServices;

namespace AuroraHost;

/// <summary>
/// Reads Linux /proc and /sys directly (no shell, no python) and parses with spans.
/// All buffers are reused; the steady-state loop allocates only the small display strings.
/// </summary>
public sealed class ProcReader
{
    readonly byte[] _buf = new byte[128 * 1024];
    readonly byte[] _small = new byte[64 * 1024];
    readonly Metrics _m = new();

    long _prevTotal, _prevIdle, _prevIo, _prevRx, _prevTx, _prevRd, _prevWr, _prevTicks;
    double _tempSmooth, _freqSmooth;

    public Metrics Current => _m;
    public int IntervalMs { get; set; } = 1000;

    int Read(string path, byte[] dst)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.None);
            int total = 0;
            while (total < dst.Length)
            {
                int n = fs.Read(dst, total, dst.Length - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    public Metrics Next()
    {
        Thread.Sleep(IntervalMs);

        long total = 0, idle = 0, io = 0;
        long memTotal = 0, memAvail = 0;
        long rx = 0, tx = 0, rd = 0, wr = 0;
        double l1 = 0, l5 = 0, l15 = 0, up = 0, pkg = 0, acpi = 0, freqK = 0;
        int nicSpeed = 0;

        // ---------- /proc/stat ----------
        int n = Read("/proc/stat", _buf);
        if (n > 0)
        {
            var line = _buf.AsSpan(0, n);
            int nl = line.IndexOf((byte)'\n');
            if (nl > 0) line = line[..nl];
            if (line.StartsWith("cpu "u8))
            {
                Span<long> v = stackalloc long[8];
                var f = line[4..];
                int k = 0;
                while (k < 8 && f.Length > 0)
                {
                    int sp = f.IndexOf((byte)' ');
                    var tok = sp < 0 ? f : f[..sp];
                    f = sp < 0 ? ReadOnlySpan<byte>.Empty : f[(sp + 1)..];
                    if (tok.Length == 0) continue;
                    v[k++] = Num(tok);
                }
                for (int i = 0; i < k; i++) total += v[i];
                idle = v[3] + v[4];
                io = v[4];
            }
        }

        // ---------- /proc/meminfo ----------
        n = Read("/proc/meminfo", _buf);
        if (n > 0)
        {
            var s = _buf.AsSpan(0, n);
            while (s.Length > 0)
            {
                int nl = s.IndexOf((byte)'\n');
                var line = nl < 0 ? s : s[..nl];
                s = nl < 0 ? ReadOnlySpan<byte>.Empty : s[(nl + 1)..];
                if (line.StartsWith("MemTotal:"u8)) memTotal = Num(line[9..]);
                else if (line.StartsWith("MemAvailable:"u8)) memAvail = Num(line[13..]);
            }
        }

        // ---------- loadavg / uptime ----------
        n = Read("/proc/loadavg", _small);
        if (n > 0)
        {
            var s = _buf.AsSpan(0, 0);
            var l = new ReadOnlySpan<byte>(_small, 0, n);
            Span<double> lv = stackalloc double[3];
            int k = 0;
            var f = l;
            while (k < 3 && f.Length > 0)
            {
                int sp = f.IndexOf((byte)' ');
                var tok = sp < 0 ? f : f[..sp];
                f = sp < 0 ? ReadOnlySpan<byte>.Empty : f[(sp + 1)..];
                if (tok.Length == 0) continue;
                lv[k++] = Dbl(tok);
            }
            if (k == 3) { l1 = lv[0]; l5 = lv[1]; l15 = lv[2]; }
        }

        n = Read("/proc/uptime", _small);
        if (n > 0) up = Dbl(new ReadOnlySpan<byte>(_small, 0, n));

        // ---------- diskstats ----------
        n = Read("/proc/diskstats", _buf);
        if (n > 0)
        {
            var s = _buf.AsSpan(0, n);
            while (s.Length > 0)
            {
                int nl = s.IndexOf((byte)'\n');
                var line = nl < 0 ? s : s[..nl];
                s = nl < 0 ? ReadOnlySpan<byte>.Empty : s[(nl + 1)..];
                if (line.Length == 0) continue;
                int sp = line.IndexOf((byte)' ');
                if (sp <= 0) continue;
                // skip partitions/loop/ram by name
                Span<long> f = stackalloc long[14];
                int k = 0;
                var r = line;
                bool bad = false;
                while (k < 14 && r.Length > 0)
                {
                    int q = r.IndexOf((byte)' ');
                    var tok = q < 0 ? r : r[..q];
                    r = q < 0 ? ReadOnlySpan<byte>.Empty : r[(q + 1)..];
                    if (tok.Length == 0) continue;
                    if (!IsDigits(tok)) { bad = true; break; }
                    f[k++] = Num(tok);
                }
                if (bad || k < 14) continue;
                rd += f[5];
                wr += f[9];
            }
        }

        // ---------- /proc/net/dev ----------
        n = Read("/proc/net/dev", _buf);
        if (n > 0)
        {
            var s = _buf.AsSpan(0, n);
            while (s.Length > 0)
            {
                int nl = s.IndexOf((byte)'\n');
                var line = nl < 0 ? s : s[..nl];
                s = nl < 0 ? ReadOnlySpan<byte>.Empty : s[(nl + 1)..];
                int colon = line.IndexOf((byte)':');
                if (colon <= 0) continue;
                var name = line[..colon].Trim();
                if (name.SequenceEqual("lo"u8)) continue;
                Span<long> f = stackalloc long[16];
                int k = 0;
                var r = line[(colon + 1)..];
                while (k < 16 && r.Length > 0)
                {
                    int q = r.IndexOf((byte)' ');
                    var tok = q < 0 ? r : r[..q];
                    r = q < 0 ? ReadOnlySpan<byte>.Empty : r[(q + 1)..];
                    if (tok.Length == 0) continue;
                    f[k++] = Num(tok);
                }
                if (k >= 16) { rx += f[0]; tx += f[8]; }
            }
        }

        // ---------- thermal ----------
        try
        {
            foreach (var zdir in Directory.EnumerateDirectories("/sys/class/thermal", "thermal_zone*"))
            {
                string typ = ReadText(Path.Combine(zdir, "type")).Trim();
                double t = Dbl(ReadText(Path.Combine(zdir, "temp"))) / 1000.0;
                if (typ == "x86_pkg_temp") pkg = t;
                else if (typ == "acpitz") acpi = t;
            }
        }
        catch { }

        // ---------- frequency ----------
        string fk = ReadText("/sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq").Trim();
        if (fk.Length > 0) freqK = Dbl(System.Text.Encoding.ASCII.GetBytes(fk));

        // ---------- nic speed ----------
        string ns = ReadText($"/sys/class/net/{_m.NetIf}/speed").Trim();
        if (ns.Length > 0) nicSpeed = (int)Dbl(System.Text.Encoding.ASCII.GetBytes(ns));

        // ---------- deltas ----------
        long now = Environment.TickCount64;
        double dt = _prevTicks == 0 ? 1.0 : (now - _prevTicks) / 1000.0;
        if (dt < 0.2) dt = 0.2;
        _prevTicks = now;

        if (_prevTotal != 0)
        {
            long dtick = total - _prevTotal;
            if (dtick > 0)
            {
                double cpu = 1.0 - (double)(idle - _prevIdle) / dtick;
                _m.Cpu = cpu < 0 ? 0 : cpu > 1 ? 1 : cpu;
                _m.Iowait = 100.0 * (io - _prevIo) / dtick;
            }
            _m.Rx = Human((rx - _prevRx) / dt);
            _m.Tx = Human((tx - _prevTx) / dt);
            _m.Rd = Human((rd - _prevRd) * 512.0 / dt);
            _m.Wr = Human((wr - _prevWr) * 512.0 / dt);
        }
        _prevTotal = total; _prevIdle = idle; _prevIo = io;
        _prevRx = rx; _prevTx = tx; _prevRd = rd; _prevWr = wr;

        if (memTotal > 0)
        {
            _m.MemTotal = memTotal / 1048576.0;
            _m.MemUsed = _m.MemTotal - memAvail / 1048576.0;
            _m.MemPct = _m.MemUsed / _m.MemTotal;
        }

        _m.L1 = l1.ToString("0.00");
        _m.L5 = l5.ToString("0.00");
        _m.L15 = l15.ToString("0.00");
        _m.UptimeDays = (int)(up / 86400);

        double t2 = pkg > 0 ? pkg : acpi;
        _tempSmooth = _tempSmooth == 0 ? t2 : _tempSmooth * 0.75 + t2 * 0.25;
        _m.Temp = ((int)Math.Round(_tempSmooth)).ToString();

        if (freqK > 0)
        {
            double f = freqK / 1_000_000.0;
            _freqSmooth = _freqSmooth == 0 ? f : _freqSmooth * 0.7 + f * 0.3;
            _m.Freq = _freqSmooth.ToString("0.0");
        }

        _m.NicSpeed = nicSpeed > 0 ? $"{_m.NetIf}:{nicSpeed}" : _m.NetIf;
        return _m;
    }

    string ReadText(string path)
    {
        try
        {
            int n = Read(path, _small);
            return n <= 0 ? "" : System.Text.Encoding.ASCII.GetString(_small, 0, n);
        }
        catch { return ""; }
    }

    public void Init(string host, string ip, int cores, string nic, string disk)
    {
        _m.Host = host;
        _m.Ip = ip;
        _m.Cores = cores;
        _m.NetIf = nic;
        _m.Disk = disk;
    }

    static bool IsDigits(ReadOnlySpan<byte> s)
    {
        foreach (byte b in s)
            if (b < (byte)'0' || b > (byte)'9') return false;
        return s.Length > 0;
    }

    static long Num(ReadOnlySpan<byte> s)
    {
        long v = 0;
        foreach (byte b in s)
        {
            if (b < (byte)'0' || b > (byte)'9') break;
            v = v * 10 + (b - (byte)'0');
        }
        return v;
    }

    static double Dbl(ReadOnlySpan<byte> s)
    {
        double v = 0, frac = 0.1;
        bool dot = false;
        foreach (byte b in s)
        {
            if (b == (byte)'.') { dot = true; continue; }
            if (b < (byte)'0' || b > (byte)'9')
            {
                if (dot) break;
                continue;
            }
            if (dot) { v += (b - (byte)'0') * frac; frac *= 0.1; }
            else v = v * 10 + (b - (byte)'0');
        }
        return v;
    }

    static string Human(double bps)
    {
        if (bps >= 1048576) return (bps / 1048576).ToString("0.0") + "M";
        if (bps >= 1024) return (bps / 1024).ToString("0.0") + "K";
        return ((long)bps).ToString() + "B";
    }
}
