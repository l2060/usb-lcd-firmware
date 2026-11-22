using SkiaSharp;

namespace AuroraHost;

public sealed class Metrics
{
    public string Host = "linux";
    public string Ip = "";
    public int Cores = 8;
    public double Cpu;
    public double MemPct;
    public double MemUsed, MemTotal;
    public string Rx = "0B", Tx = "0B", Rd = "0B", Wr = "0B";
    public string L1 = "0.00", L5 = "0.00", L15 = "0.00";
    public int UptimeDays;
    public string NetIf = "eno1";
    public string Disk = "nvme0";
    public string MemSpeed = "2666";
    public string NicSpeed = "";
    public double Iowait;
    public string Temp = "0";
    public string Freq = "0";

    public SKColor CpuColor =>
        Cpu >= 0.85 ? new SKColor(0xF8, 0x71, 0x71)
        : Cpu >= 0.60 ? new SKColor(0xF5, 0xBE, 0x46)
        : new SKColor(0x4A, 0xDE, 0x8C);
}

/// <summary>Draws the 320x172 dashboard with SkiaSharp and returns RGB565 (big-endian).</summary>
public sealed class SkiaDashboard
{
    public const int W = 320, H = 172;
    const int M = 10, CARD_H = 62;

    static readonly SKColor Bg = new(0x09, 0x0C, 0x11);
    static readonly SKColor CardBg = new(0x12, 0x17, 0x1F);
    static readonly SKColor Edge = new(0x26, 0x30, 0x3E);
    static readonly SKColor Fg = new(0xEE, 0xF3, 0xF9);
    static readonly SKColor Dim = new(0x8C, 0x99, 0xAB);
    static readonly SKColor Mid = new(0xB6, 0xC2, 0xD0);
    static readonly SKColor Green = new(0x4A, 0xDE, 0x8C);
    static readonly SKColor Blue = new(0x45, 0xD2, 0xFF);
    static readonly SKColor Amber = new(0xF7, 0xC4, 0x4C);

    readonly SKBitmap _bmp = new(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
    readonly SKCanvas _cv;
    readonly SKTypeface _sans = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Normal)!;
    readonly SKTypeface _sansBold = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold)!;
    readonly SKTypeface _mono = SKTypeface.FromFamilyName("DejaVu Sans Mono", SKFontStyle.Bold)!;

    readonly SKFont _fHost, _fTitle, _fBig, _fUnit, _fTiny, _fMono, _fFoot, _fClock;
    readonly SKPaint _p = new() { IsAntialias = false };

    public SkiaDashboard()
    {
        _cv = new SKCanvas(_bmp);
        _fHost = new SKFont(_sansBold, 13);
        _fTitle = new SKFont(_sans, 9.5f);
        _fBig = new SKFont(_sansBold, 30);
        _fUnit = new SKFont(_sans, 9);
        _fTiny = new SKFont(_sans, 7);
        _fMono = new SKFont(_mono, 16);
        _fFoot = new SKFont(_sans, 8);
        _fClock = new SKFont(_mono, 13);
    }

    void Txt(string s, SKFont f, SKColor c, float x, float baseline, bool right = false)
    {
        _p.Color = c;
        float w = f.MeasureText(s, _p);
        _cv.DrawText(s, right ? x - w : x, baseline, f, _p);
    }

    void TxtC(string s, SKFont f, SKColor c, float cx, float baseline)
    {
        _p.Color = c;
        float w = f.MeasureText(s, _p);
        _cv.DrawText(s, cx - w / 2, baseline, f, _p);
    }

    void Round(SKRect r, float rad, SKColor fill, SKColor edge)
    {
        _p.Color = fill;
        _p.Style = SKPaintStyle.Fill;
        _cv.DrawRoundRect(r, rad, rad, _p);
        _p.Color = edge;
        _p.Style = SKPaintStyle.Stroke;
        _p.StrokeWidth = 1;
        _cv.DrawRoundRect(r, rad, rad, _p);
        _p.Style = SKPaintStyle.Fill;
    }

    void Tri(float x, float y, float size, bool up, SKColor c)
    {
        using var path = new SKPath();
        if (up) { path.MoveTo(x, y + size); path.LineTo(x + size, y + size); path.LineTo(x + size / 2, y); }
        else { path.MoveTo(x, y); path.LineTo(x + size, y); path.LineTo(x + size / 2, y + size); }
        path.Close();
        _p.Color = c;
        _cv.DrawPath(path, _p);
    }

    void NetIcon(float x, float y, SKColor c)
    {
        _p.Color = c; _p.Style = SKPaintStyle.Stroke; _p.StrokeWidth = 1;
        _cv.DrawCircle(x + 7, y + 7, 7, _p);
        _cv.DrawLine(x, y + 7, x + 14, y + 7, _p);
        _cv.DrawLine(x + 4, y + 2, x + 4, y + 12, _p);
        _cv.DrawLine(x + 10, y + 2, x + 10, y + 12, _p);
        _p.Style = SKPaintStyle.Fill;
    }

    void DiskIcon(float x, float y, SKColor c)
    {
        _p.Color = c;
        _cv.DrawRect(x, y, 14, 3, _p);
        _cv.DrawRect(x, y + 5, 14, 3, _p);
        _cv.DrawRect(x, y + 10, 14, 3, _p);
    }

    public void RenderInto(Metrics m, byte[] dst)
    {
        RenderCore(m);
        ToRgb565Into(dst);
    }

    void RenderCore(Metrics m)
    {
        _cv.Clear(Bg);

        // header
        Txt(m.Host, _fHost, Fg, M, 3 + 12);
        Txt(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), _fClock, Blue, W - M, 2 + 12, true);

        int cw = (W - 2 * M - 8) / 2;         // 146
        int x1 = M, x2 = M + cw + 8;
        int y1 = 24, y2 = y1 + CARD_H + 4;

        // CPU
        string temp = (m.Temp != "0" && m.Temp != "") ? m.Temp + "℃" : "";
        string freq = (m.Freq != "0" && m.Freq != "") ? m.Freq + "GHz" : "";
        BigCard(new SKRect(x1, y1, x1 + cw, y1 + CARD_H),
                "CPU · " + m.Cores + " Core", temp, $"{m.Cpu:P0}", m.CpuColor, freq);

        // MEM
        string memTitle = string.IsNullOrEmpty(m.MemSpeed) ? "MEM" : "MEM " + m.MemSpeed;
        BigCard(new SKRect(x2, y1, x2 + cw, y1 + CARD_H),
                memTitle, $"{m.MemUsed:0.0}/{m.MemTotal:0.0}G", $"{m.MemPct:P0}", Blue,
                $"free {m.MemTotal - m.MemUsed:0.0}G");

        // NET
        var net = new SKRect(x1, y2, x1 + cw, y2 + CARD_H);
        Round(net, 6, CardBg, Edge);
        string nic = m.NetIf;
        if (!string.IsNullOrEmpty(m.NicSpeed) && m.NicSpeed.Contains(':'))
            nic = m.NicSpeed.Split(':')[0];
        NetIcon(net.Left + 28, net.Top + 17, Dim);
        TxtC(nic, _fTiny, Dim, net.Left + 36, net.Top + 33 + 7);
        Tri(net.Left + 66, net.Top + 14, 8, true, Green);
        Txt(m.Rx, _fMono, Green, net.Left + 75, net.Top + 9 + 13);
        Tri(net.Left + 66, net.Top + 41, 8, false, Blue);
        Txt(m.Tx, _fMono, Blue, net.Left + 75, net.Top + 34 + 13);

        // IO
        var io = new SKRect(x2, y2, x2 + cw, y2 + CARD_H);
        Round(io, 6, CardBg, Edge);
        DiskIcon(io.Left + 28, io.Top + 17, Amber);
        TxtC($"{m.Iowait:0.0}%", _fTiny, Dim, io.Left + 35, io.Top + 33 + 7);
        Txt("R " + m.Rd, _fMono, Amber, io.Left + 65, io.Top + 9 + 13);
        Txt("W " + m.Wr, _fMono, Amber, io.Left + 65, io.Top + 34 + 13);

        // footer
        Txt(m.Ip, _fClock, Green, M, H - 18 + 12);
        Txt("Power By Hanks", _fFoot, Dim, W - M, H - 18 + 11, true);

            }

    public byte[] Render(Metrics m)
    {
        RenderCore(m);
        var buf = new byte[W * H * 2];
        ToRgb565Into(buf);
        return buf;
    }

    void BigCard(SKRect r, string title, string rightTop, string big, SKColor bigColor, string rightBottom)
    {
        Round(r, 6, CardBg, Edge);
        Txt(title, _fTitle, Dim, r.Left + 9, r.Top + 5 + 8);
        Txt(rightTop, _fUnit, Mid, r.Right - 9, r.Top + 5 + 8, true);
        Txt(big, _fBig, bigColor, r.Left + 8, r.Top + 20 + 24);
        Txt(rightBottom, _fUnit, Mid, r.Right - 9, r.Bottom - 16 + 8, true);
    }

    unsafe void ToRgb565Into(byte[] dst)
    {
        byte* p = (byte*)_bmp.GetPixels().ToPointer();
        int o = 0, n = W * H;
        for (int i = 0; i < n; i++)
        {
            byte b = p[0], g = p[1], r = p[2];
            p += 4;
            ushort c = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
            dst[o++] = (byte)(c >> 8);
            dst[o++] = (byte)(c & 0xFF);
        }
    }
}
