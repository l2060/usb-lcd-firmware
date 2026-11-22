using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Aurora.App.Rendering;

/// <summary>Renders debug frames (text, clock, patterns) to 320x172 BGRA and RGB565.</summary>
public static class FrameRenderer
{
    public const int Width = 320, Height = 172;

    static readonly Typeface Yahei = new(new FontFamily("Microsoft YaHei UI"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    static readonly Typeface Mono = new(new FontFamily("Consolas"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public sealed record Rendered(byte[] Bgra, byte[] Rgb565, BitmapSource Preview);

    /* ---- persistent surfaces: nothing is re-allocated per frame ---- */
    static readonly RenderTargetBitmap Rtb = CreateRtb();
    static readonly byte[] BgraBuf = new byte[Width * 4 * Height];
    static readonly byte[] Rgb565Buf = new byte[Width * 2 * Height];
    static readonly WriteableBitmap PreviewBitmap = CreatePreview();

    static RenderTargetBitmap CreateRtb()
    {
        /* must stay unfrozen: it is reused for every frame (frozen bitmaps reject Render) */
        return new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
    }

    static WriteableBitmap CreatePreview()
    {
        /* must stay unfrozen: it is updated in place every frame */
        return new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32, null);
    }

    public static long LastAllocBytes { get; private set; }

    /// <summary>Live surface for the streaming preview (updated in place, never frozen).</summary>
    public static WriteableBitmap PreviewSurface => PreviewBitmap;

    public static Rendered Text(string text, Color color)
    {
        return Build(dc =>
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, Width, Height));
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB)), 2);
            dc.DrawRectangle(null, pen, new Rect(1, 1, Width - 2, Height - 2));

            var ft = new FormattedText(text ?? "", CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, Yahei, 34, new SolidColorBrush(color), 96);
            dc.DrawText(ft, new Point((Width - ft.Width) / 2, (Height - ft.Height) / 2));
        });
    }

    public static Rendered Clock()
    {
        return Build(dc =>
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, Width, Height));
            var now = DateTime.Now;
            var t = new FormattedText(now.ToString("HH:mm:ss"), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Mono, 58, new SolidColorBrush(Color.FromRgb(0x00, 0xE0, 0xFF)), 96);
            dc.DrawText(t, new Point((Width - t.Width) / 2, Height / 2 - t.Height / 2 - 8));

            var d = new FormattedText(now.ToString("yyyy-MM-dd  dddd"), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, Yahei, 14, new SolidColorBrush(Color.FromRgb(0x7D, 0x8B, 0x9C)), 96);
            dc.DrawText(d, new Point((Width - d.Width) / 2, Height - 34));
        });
    }

    /// <summary>Streaming demo frame: all dynamic content sits inside one compact band.</summary>
    public static Rendered Stream(int tick)
    {
        return Build(dc =>
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x18)), null,
                             new Rect(0, 0, Width, Height));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB)), null,
                             new Rect(0, 0, Width, 3));

            var title = new FormattedText("STREAM PUSH", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Mono, 13, new SolidColorBrush(Color.FromRgb(0x7D, 0x8B, 0x9C)), 96);
            dc.DrawText(title, new Point(10, 10));

            /* dynamic band: y 60..134 */
            var big = new FormattedText(tick.ToString("D5"), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Mono, 46, new SolidColorBrush(Color.FromRgb(0x3C, 0xC8, 0xFF)), 96);
            dc.DrawText(big, new Point(10, 64));

            var t = new FormattedText(DateTime.Now.ToString("HH:mm:ss"), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Mono, 30, new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF5)), 96);
            dc.DrawText(t, new Point(Width - t.Width - 10, 72));

            int bw = (tick % 40) * 7;
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x8C)), null,
                             new Rect(10, 122, Math.Max(2, bw), 10));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x22, 0x2C, 0x38)), null,
                             new Rect(10 + Math.Max(2, bw), 122, 300 - Math.Max(2, bw), 10));
        });
    }

    public static Rendered Solid(Color color)
    {
        return Build(dc => dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, Width, Height)));
    }

    public static Rendered Half()
    {
        return Build(dc =>
        {
            dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, Width, Height / 2.0));
            dc.DrawRectangle(Brushes.Blue, null, new Rect(0, Height / 2.0, Width, Height / 2.0));
        });
    }

    public static Rendered Bars()
    {
        Color[] colors =
        {
            Colors.Red, Colors.Lime, Colors.Blue, Colors.Yellow,
            Colors.Magenta, Colors.Cyan, Colors.White, Colors.Black
        };
        return Build(dc =>
        {
            for (int i = 0; i < 8; i++)
            {
                int y = i * Height / 8;
                int h = (i + 1) * Height / 8 - y;
                dc.DrawRectangle(new SolidColorBrush(colors[i]), null, new Rect(0, y, Width, h));
            }
        });
    }

    public static Rendered Rows()
    {
        return Build(dc =>
        {
            const int n = 17;
            for (int i = 0; i < n; i++)
            {
                byte v = (byte)(i * 255 / (n - 1));
                var c = Color.FromRgb(v, 0, (byte)(255 - v));
                int y = i * Height / n;
                int h = (i + 1) * Height / n - y;
                dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(0, y, Width, h));
            }
            var ft = new FormattedText("ROW 0 TOP", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Mono, 16, Brushes.White, 96);
            dc.DrawText(ft, new Point(4, 4));
        });
    }

    static Rendered Build(Action<DrawingContext> draw)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);

        Rtb.Clear();
        Rtb.Render(visual);

        int stride = Width * 4;
        Rtb.CopyPixels(BgraBuf, stride, 0);
        ToRgb565Into(BgraBuf, stride, Rgb565Buf);

        var preview = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Pbgra32, null, BgraBuf, stride);
        preview.Freeze();

        LastAllocBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        return new Rendered(BgraBuf, Rgb565Buf, preview);
    }

    public static void ToRgb565Into(byte[] bgra, int stride, byte[] output)
    {
        int o = 0;
        for (int y = 0; y < Height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < Width; x++)
            {
                int p = row + x * 4;
                byte b = bgra[p], g = bgra[p + 1], r = bgra[p + 2];
                ushort c = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
                output[o++] = (byte)(c >> 8);
                output[o++] = (byte)(c & 0xFF);
            }
        }
    }

    public static byte[] ToRgb565(byte[] bgra, int stride)
    {
        var output = new byte[Width * Height * 2];
        int o = 0;
        for (int y = 0; y < Height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < Width; x++)
            {
                int p = row + x * 4;
                byte b = bgra[p], g = bgra[p + 1], r = bgra[p + 2];
                ushort c = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
                output[o++] = (byte)(c >> 8);
                output[o++] = (byte)(c & 0xFF);
            }
        }
        return output;
    }
}
