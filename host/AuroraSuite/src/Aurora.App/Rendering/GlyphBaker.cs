using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Aurora.App.Rendering;

/// <summary>
/// Bakes one or more font faces into a single .afont package that the device renders with
/// face index:  DISP_TEXT face x y color text
///
/// Package layout (little endian, must match the device structs exactly):
///   32B  header : 'AFNP' ver faceCount pad facesOff dataOff rsv[16]
///   N*24 face   : size flags lineHeight baseline count(2) PAD(2) tableOff(4) dataOff(4) rsv[8]
///   glyphs*12   : code(2) w h advance bx by pad off(4)
///   alpha bytes : 8-bit coverage, row major
/// </summary>
public static class GlyphBaker
{
    /// <summary>Characters used by the dashboard template (labels, values, units, arrows).</summary>
    public const string DefaultCharset =
        " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`" +
        "abcdefghijklmnopqrstuvwxyz{|}~" +
        "·℃▲▼→←↑↓≥≤±°×÷●○■□✓✗µΩ";

    public sealed record FaceRequest(string Family, int Size, bool Bold, string? Charset = null);

    public sealed record Result(byte[] Package, IReadOnlyList<BitmapSource> Previews, int Faces, int Glyphs, long Bytes);

    /// <summary>Per-face metrics needed by the debug canvas.</summary>
    public sealed record FaceMetrics(int Size, int LineHeight, int Baseline);

    public sealed record BakedResult(Result Package, IReadOnlyList<FaceMetrics> Metrics);

    sealed class Glyph
    {
        public int Code, W, H, Advance, BearingX, BearingY;
        public byte[] Alpha = Array.Empty<byte>();
    }

    sealed class BakedFace
    {
        public int Size, LineHeight, Baseline, Flags;
        public List<Glyph> Glyphs = new();
    }

    public static BakedResult BakeWithMetrics(IReadOnlyList<FaceRequest> faces, string? charset = null)
    {
        var (baked, package, previews) = BakeCore(faces, charset);
        var metrics = baked.Select(f => new FaceMetrics(f.Size, f.LineHeight, f.Baseline)).ToList();
        return new BakedResult(
            new Result(package, previews, baked.Count, baked.Sum(f => f.Glyphs.Count), package.Length),
            metrics);
    }

    public static Result Bake(IReadOnlyList<FaceRequest> faces, string? charset = null)
    {
        var (baked, package, previews) = BakeCore(faces, charset);
        return new Result(package, previews, baked.Count, baked.Sum(f => f.Glyphs.Count), package.Length);
    }

    static (List<BakedFace> baked, byte[] package, List<BitmapSource> previews) BakeCore(
        IReadOnlyList<FaceRequest> faces, string? charset)
    {
        var baked = new List<BakedFace>();

        foreach (var req in faces)
        {
            string faceChars = !string.IsNullOrEmpty(req.Charset) ? req.Charset!
                             : (!string.IsNullOrEmpty(charset) ? charset! : DefaultCharset);

            var typeface = new Typeface(new FontFamily(req.Family),
                FontStyles.Normal, req.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);

            var face = new BakedFace
            {
                Size = req.Size,
                LineHeight = (int)Math.Ceiling(req.Size * 1.35),
                Baseline = (int)Math.Ceiling(req.Size * 1.05),
                Flags = req.Bold ? 1 : 0
            };

            foreach (char ch in faceChars.Distinct())
            {
                var g = RenderGlyph(typeface, ch, req.Size, face.LineHeight);
                if (g != null) face.Glyphs.Add(g);
            }
            face.Glyphs.Sort((a, b) => a.Code.CompareTo(b.Code));
            baked.Add(face);
        }

        var package = WritePackage(baked);
        var previews = baked.Select(RenderPreview).ToList();
        return (baked, package, previews);
    }

    static Glyph? RenderGlyph(Typeface typeface, char ch, int size, int lineHeight)
    {
        var ft = new FormattedText(ch.ToString(), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, size, Brushes.White, 1.0);

        int pad = 2;
        int w = (int)Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + pad * 2;
        int h = lineHeight + pad * 2;

        int textTop = pad + (lineHeight - (int)ft.Height) / 2;
        int baselineRow = textTop + (int)Math.Round(ft.Baseline);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
            dc.DrawText(ft, new Point(pad, textTop));
        }

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);

        int stride = w * 4;
        var px = new byte[stride * h];
        bmp.CopyPixels(px, stride, 0);

        var mask = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                mask[y * w + x] = px[y * stride + x * 4];

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (mask[y * w + x] > 2)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }

        var g = new Glyph { Code = ch, Advance = Math.Max(1, (int)Math.Round(ft.WidthIncludingTrailingWhitespace)) };
        if (maxX < 0) return g;

        g.W = maxX - minX + 1;
        g.H = maxY - minY + 1;
        g.BearingX = minX - 2;
        g.BearingY = baselineRow - minY;
        g.Alpha = new byte[g.W * g.H];
        for (int y = 0; y < g.H; y++)
            Array.Copy(mask, (minY + y) * w + minX, g.Alpha, y * g.W, g.W);
        return g;
    }

    static byte[] WritePackage(List<BakedFace> faces)
    {
        const int headerSize = 32, faceSize = 24, glyphSize = 12;

        int facesOff = headerSize;
        int tableBase = facesOff + faces.Count * faceSize;

        var tableOffsets = new int[faces.Count];
        int cursor = tableBase;
        for (int i = 0; i < faces.Count; i++)
        {
            tableOffsets[i] = cursor;
            cursor += faces[i].Glyphs.Count * glyphSize;
        }

        var dataOffsets = new int[faces.Count];
        for (int i = 0; i < faces.Count; i++)
        {
            dataOffsets[i] = cursor;
            cursor += faces[i].Glyphs.Sum(g => g.Alpha.Length);
        }

        using var ms = new MemoryStream(cursor);
        using var bw = new BinaryWriter(ms);

        bw.Write(new[] { (byte)'A', (byte)'F', (byte)'N', (byte)'P' });
        bw.Write((byte)1);
        bw.Write((byte)faces.Count);
        bw.Write((ushort)0);
        bw.Write((uint)facesOff);
        bw.Write((uint)dataOffsets[0]);
        for (int i = 0; i < 16; i++) bw.Write((byte)0);

        for (int i = 0; i < faces.Count; i++)
        {
            var f = faces[i];
            bw.Write((byte)f.Size);
            bw.Write((byte)f.Flags);
            bw.Write((byte)f.LineHeight);
            bw.Write((byte)f.Baseline);
            bw.Write((ushort)f.Glyphs.Count);
            bw.Write((ushort)0);                            /* C struct padding */
            bw.Write((uint)tableOffsets[i]);
            bw.Write((uint)dataOffsets[i]);
            for (int k = 0; k < 8; k++) bw.Write((byte)0);
        }

        foreach (var f in faces)
        {
            int off = 0;
            foreach (var g in f.Glyphs)
            {
                bw.Write((ushort)g.Code);
                bw.Write((byte)g.W);
                bw.Write((byte)g.H);
                bw.Write((byte)Math.Min(255, g.Advance));
                bw.Write((sbyte)Math.Clamp(g.BearingX, -128, 127));
                bw.Write((sbyte)Math.Clamp(g.BearingY, -128, 127));
                bw.Write((byte)0);
                bw.Write((uint)off);
                off += g.Alpha.Length;
            }
        }

        foreach (var f in faces)
            foreach (var g in f.Glyphs)
                bw.Write(g.Alpha);

        bw.Flush();
        return ms.ToArray();
    }

    static BitmapSource RenderPreview(BakedFace face)
    {
        const int W = 320, H = 172;
        int stride = W * 4;
        var px = new byte[stride * H];
        var lookup = face.Glyphs.ToDictionary(g => g.Code);

        Fill(px, stride, 0x10, 0x14, 0x18);
        Rect(px, stride, 0, 0, W, 3, 0x1F, 0x6F, 0xEB);

        int y = 12;
        Draw(px, stride, lookup, "FONT FACE 0", 10, y, 0x7D, 0x8B, 0x9C, face); y += face.LineHeight + 2;
        Draw(px, stride, lookup, "CPU 42%  MEM 71%", 10, y, 0x3C, 0xC8, 0xFF, face); y += face.LineHeight + 2;
        Draw(px, stride, lookup, "192.168.1.30", 10, y, 0xE8, 0xEE, 0xF5, face); y += face.LineHeight + 2;
        Draw(px, stride, lookup, "abcdefg 0123456789", 10, y, 0x4A, 0xDE, 0x8C, face); y += face.LineHeight + 2;
        Draw(px, stride, lookup, "!@#$%^&*()[]{}<>", 10, y, 0xF7, 0xC4, 0x4C, face); y += face.LineHeight + 2;
        Draw(px, stride, lookup, "℃ ± ° × ÷ ≥ ≤ → ↓ ● ■ ✓", 10, y, 0xF8, 0x71, 0x71, face);

        var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Pbgra32, null, px, stride);
        bmp.Freeze();
        return bmp;
    }

    static void Draw(byte[] px, int stride, Dictionary<int, Glyph> font, string text,
                     int x, int y, byte r, byte g, byte b, BakedFace face)
    {
        int pen = x;
        foreach (char ch in text)
        {
            if (!font.TryGetValue(ch, out var glyph)) { pen += 4; continue; }
            int gx = pen + glyph.BearingX;
            int gy = y + face.Baseline - glyph.BearingY;

            for (int row = 0; row < glyph.H; row++)
            {
                int py2 = gy + row;
                if (py2 < 0 || py2 >= 172) continue;
                for (int col = 0; col < glyph.W; col++)
                {
                    int pxx = gx + col;
                    if (pxx < 0 || pxx >= 320) continue;
                    int a = glyph.Alpha[row * glyph.W + col];
                    if (a == 0) continue;
                    int o = py2 * stride + pxx * 4;
                    px[o] = (byte)((b * a + px[o] * (255 - a)) / 255);
                    px[o + 1] = (byte)((g * a + px[o + 1] * (255 - a)) / 255);
                    px[o + 2] = (byte)((r * a + px[o + 2] * (255 - a)) / 255);
                    px[o + 3] = 255;
                }
            }
            pen += glyph.Advance;
        }
    }

    static void Fill(byte[] px, int stride, byte r, byte g, byte b)
    {
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
    }

    static void Rect(byte[] px, int stride, int x, int y, int w, int h, byte r, byte g, byte b)
    {
        for (int row = y; row < y + h; row++)
            for (int col = x; col < x + w; col++)
            {
                int o = row * stride + col * 4;
                px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = 255;
            }
    }
}
