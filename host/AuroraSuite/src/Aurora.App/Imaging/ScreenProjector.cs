using System.Runtime.InteropServices;

namespace Aurora.App.Imaging;

/// <summary>
/// Grabs the desktop with GDI and downsamples it to the panel resolution,
/// returning a big-endian RGB565 buffer compatible with the streaming path.
/// </summary>
public sealed class ScreenProjector
{
    public const int W = 320;
    public const int H = 172;

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public int[] bmiColors;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")]
    static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFO bi, uint usage);

    const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    const uint SRCCOPY = 0x00CC0020;

    byte[] _bgra = Array.Empty<byte>();
    int _sw, _sh;

    /// <summary>Captures the primary screen and returns 320x172 RGB565 (hi, lo) pairs.</summary>
    public byte[] Capture(byte[] target)
    {
        int sw = GetSystemMetrics(SM_CXSCREEN);
        int sh = GetSystemMetrics(SM_CYSCREEN);
        if (sw <= 0 || sh <= 0) return target;

        if (sw != _sw || sh != _sh || _bgra.Length != sw * sh * 4)
        {
            _sw = sw; _sh = sh;
            _bgra = new byte[sw * sh * 4];
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return target;

        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr bmp = CreateCompatibleBitmap(screenDc, sw, sh);
        IntPtr old = SelectObject(memDc, bmp);
        try
        {
            if (!BitBlt(memDc, 0, 0, sw, sh, screenDc, 0, 0, SRCCOPY)) return target;

            var bi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = 40,
                    biWidth = sw,
                    biHeight = -sh,          // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                },
                bmiColors = new int[3],
            };

            if (GetDIBits(memDc, bmp, 0, (uint)sh, _bgra, ref bi, 0) == 0) return target;
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(bmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        Downscale(target);
        return target;
    }

    /// <summary>Area-averaged downscale with aspect-preserving letterbox.</summary>
    void Downscale(byte[] target)
    {
        double sx = (double)_sw / W, sy = (double)_sh / H;
        double scale = Math.Max(sx, sy);              // fit, keep aspect
        double scaledW = _sw / scale, scaledH = _sh / scale;
        double offX = (W - scaledW) / 2, offY = (H - scaledH) / 2;

        for (int y = 0; y < H; y++)
        {
            int rowOut = y * W * 2;
            for (int x = 0; x < W; x++)
            {
                double fx = (x - offX) * scale;
                double fy = (y - offY) * scale;

                int idx = rowOut + x * 2;
                if (fx < 0 || fy < 0 || fx >= _sw || fy >= _sh)
                {
                    target[idx] = 0; target[idx + 1] = 0;      // letterbox: black
                    continue;
                }

                int x0 = (int)fx, y0 = (int)fy;
                int x1 = Math.Min(x0 + (int)scale + 1, _sw);
                int y1 = Math.Min(y0 + (int)scale + 1, _sh);
                if (x1 <= x0) x1 = x0 + 1;
                if (y1 <= y0) y1 = y0 + 1;

                int r = 0, g = 0, b = 0, n = 0;
                for (int yy = y0; yy < y1; yy++)
                {
                    int baseIdx = (yy * _sw + x0) * 4;
                    for (int xx = x0; xx < x1; xx++, baseIdx += 4)
                    {
                        b += _bgra[baseIdx];
                        g += _bgra[baseIdx + 1];
                        r += _bgra[baseIdx + 2];
                        n++;
                    }
                }

                if (n == 0) n = 1;
                ushort c = (ushort)((((r / n) >> 3) << 11) | (((g / n) >> 2) << 5) | ((b / n) >> 3));
                target[idx] = (byte)(c >> 8);
                target[idx + 1] = (byte)(c & 0xFF);
            }
        }
    }
}
