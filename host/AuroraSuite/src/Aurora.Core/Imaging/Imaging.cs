using Aurora.Core.Protocol;

namespace Aurora.Core.Imaging;

public static class Palette332
{
    /// <summary>3-3-2 palette index from 8-bit channels.</summary>
    public static byte Index(byte r, byte g, byte b) =>
        (byte)(((r >> 5) << 5) | ((g >> 5) << 2) | (b >> 6));

    /// <summary>RGB565 value for a palette index (used to preview what the panel shows).</summary>
    public static ushort ToRgb565(byte index)
    {
        int r = ((index >> 5) & 7) * 255 / 7;
        int g = ((index >> 2) & 7) * 255 / 7;
        int b = (index & 3) * 255 / 3;
        return (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
    }
}

public static class RleCodec
{
    /// <summary>RLE over palette indices: [count][index] pairs, count 1..255.</summary>
    public static byte[] EncodeIndices(ReadOnlySpan<byte> indices)
    {
        var output = new List<byte>(indices.Length / 4 + 64);
        int i = 0;
        while (i < indices.Length)
        {
            byte v = indices[i];
            int run = 1;
            while (i + run < indices.Length && indices[i + run] == v && run < 255) run++;
            output.Add((byte)run);
            output.Add(v);
            i += run;
        }
        return output.ToArray();
    }

    /// <summary>RLE into a caller-owned buffer; returns the byte count. No allocations.</summary>
    public static int EncodeRgb565Into(ReadOnlySpan<byte> rgb565, byte[] dst)
    {
        int n = rgb565.Length / 2, o = 0, i = 0;
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

    /// <summary>RLE over RGB565 pixels (big-endian pairs): [count][lo][hi] triples.</summary>
    public static byte[] EncodeRgb565(ReadOnlySpan<byte> rgb565)
    {
        int n = rgb565.Length / 2;
        var output = new List<byte>(n / 4 + 64);
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
            output.Add((byte)run);
            output.Add((byte)(c & 0xFF));
            output.Add((byte)(c >> 8));
            i += run;
        }
        return output.ToArray();
    }
}

/// <summary>Builds the 32-byte background header + RLE payload the bootloader expects.</summary>
public static class BackgroundBuilder
{
    public const int Width = 320;
    public const int Height = 172;
    public const int HeaderSize = 32;
    public const uint Magic = 0x4732534D;
    public const ushort Format565 = 0x0055;

    /// <summary>Boot splash: 3-3-2 palette + RLE.
    /// Colour depth is never reduced; up to two smoothing passes are tried to fit maxPayload.</summary>
    public static byte[] Build332(ReadOnlySpan<byte> bgra, int stride, int maxPayload = int.MaxValue)
    {
        if (bgra.Length < stride * Height) throw new ArgumentException("image data too small");

        var work = bgra.ToArray();
        int best = -1;
        byte[] bestPayload = null;

        for (int pass = 0; pass < 3; pass++)
        {
            if (pass > 0) Blur(work, stride);

            var idx = new byte[Width * Height];
            for (int y = 0; y < Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < Width; x++)
                {
                    int p = row + x * 4;
                    idx[y * Width + x] = Palette332.Index(work[p + 2], work[p + 1], work[p]);
                }
            }

            var rle = RleCodec.EncodeIndices(idx);
            if (best < 0 || rle.Length < best)
            {
                best = rle.Length;
                bestPayload = Wrap332(rle);
            }
            if (rle.Length <= maxPayload) return Wrap332(rle);
        }

        if (bestPayload != null && bestPayload.Length <= maxPayload) return bestPayload;

        throw new InvalidOperationException(
            $"图片细节过多：最佳编码 {best + HeaderSize} 字节（{(best + HeaderSize + 255) / 256} 页），" +
            $"超过 8KB 槽位上限（{(maxPayload + HeaderSize + 255) / 256} 页）。请换一张更简洁的图片，或扩大开机图槽位。");
    }

    /// <summary>3x3 box blur on BGRA (alpha untouched): kills photo noise so RLE runs get longer.</summary>
    static void Blur(byte[] px, int stride)
    {
        var src = (byte[])px.Clone();
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int o = y * stride + x * 4;
                int r = 0, g = 0, b = 0, n = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y + dy;
                    if (yy < 0 || yy >= Height) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx;
                        if (xx < 0 || xx >= Width) continue;
                        int i = yy * stride + xx * 4;
                        b += src[i]; g += src[i + 1]; r += src[i + 2]; n++;
                    }
                }
                px[o] = (byte)(b / n);
                px[o + 1] = (byte)(g / n);
                px[o + 2] = (byte)(r / n);
            }
        }
    }

    static byte Quant(byte v, int bits)
    {
        int levels = (1 << bits) - 1;
        int q = (v * levels + 127) / 255;
        return (byte)(q * 255 / levels);
    }

    static byte[] Wrap332(byte[] rle)
    {
        int total = HeaderSize + rle.Length;
        int padded = (total + 255) / 256 * 256;
        var output = new byte[padded];
        Array.Fill(output, (byte)0xFF);

        var header = output.AsSpan(0, HeaderSize);
        BitConverter.GetBytes(Magic).CopyTo(header);
        BitConverter.GetBytes((ushort)Width).CopyTo(header[4..]);
        BitConverter.GetBytes((ushort)Height).CopyTo(header[6..]);
        BitConverter.GetBytes((ushort)0).CopyTo(header[8..]);
        BitConverter.GetBytes((ushort)0).CopyTo(header[10..]);
        BitConverter.GetBytes((uint)rle.Length).CopyTo(header[12..]);
        BitConverter.GetBytes(Crc32.Compute(rle)).CopyTo(header[16..]);
        rle.CopyTo(output, HeaderSize);
        return output;
    }

    /// <summary>Header + RLE. Format 0x0055 = RGB565 runs (65536 colours, no palette).</summary>
    public static byte[] Build(ReadOnlySpan<byte> bgra, int stride)
    {
        if (bgra.Length < stride * Height) throw new ArgumentException("image data too small");

        var rgb = new byte[Width * Height * 2];
        int o = 0;
        for (int y = 0; y < Height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < Width; x++)
            {
                int p = row + x * 4;
                byte b = bgra[p], g = bgra[p + 1], r = bgra[p + 2];
                ushort c = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
                rgb[o++] = (byte)(c >> 8);     /* big-endian: hi first, matches EncodeRgb565 */
                rgb[o++] = (byte)(c & 0xFF);
            }
        }

        var rle = RleCodec.EncodeRgb565(rgb);
        int total = HeaderSize + rle.Length;
        int padded = (total + 255) / 256 * 256;

        var output = new byte[padded];
        Array.Fill(output, (byte)0xFF);

        var header = output.AsSpan(0, HeaderSize);
        BitConverter.GetBytes(Magic).CopyTo(header);
        BitConverter.GetBytes((ushort)Width).CopyTo(header[4..]);
        BitConverter.GetBytes((ushort)Height).CopyTo(header[6..]);
        BitConverter.GetBytes(Format565).CopyTo(header[8..]);
        BitConverter.GetBytes((ushort)0).CopyTo(header[10..]);
        BitConverter.GetBytes((uint)rle.Length).CopyTo(header[12..]);
        BitConverter.GetBytes(Crc32.Compute(rle)).CopyTo(header[16..]);

        rle.CopyTo(output, HeaderSize);
        return output;
    }
}
