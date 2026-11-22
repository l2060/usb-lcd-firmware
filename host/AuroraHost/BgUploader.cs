using System.IO.Ports;
using SkiaSharp;

namespace AuroraHost;

/// <summary>Uploads a 320x172 boot background (332 palette + RLE) into the device flash.</summary>
public static class BgUploader
{
    const uint MAGIC = 0x4732534D;      // 'MS2G'
    const int BG_BASE = 0x9000;
    const int HDR = 32;

    public static int Run(string portName, string imagePath)
    {
        using var bmp0 = SKBitmap.Decode(imagePath);
        if (bmp0 == null) { Console.WriteLine($"无法读取图片 {imagePath}"); return 1; }

        using var bmp = new SKBitmap(320, 172, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var cv = new SKCanvas(bmp))
        {
            cv.Clear(SKColors.Black);
            float s = Math.Max(320f / bmp0.Width, 172f / bmp0.Height);
            float dw = bmp0.Width * s, dh = bmp0.Height * s;
            var dst = new SKRect((320 - dw) / 2, (172 - dh) / 2, (320 + dw) / 2, (172 + dh) / 2);
            cv.DrawBitmap(bmp0, dst);
        }

        // 3-3-2 palette indices
        var idx = new byte[320 * 172];
        using (var px = bmp.PeekPixels())
        {
            unsafe
            {
                byte* p = (byte*)px.GetPixels();
                for (int i = 0; i < idx.Length; i++)
                {
                    byte b = p[0], g = p[1], r = p[2];
                    p += 4;
                    idx[i] = (byte)(((r >> 5) << 5) | ((g >> 5) << 2) | (b >> 6));
                }
            }
        }

        // RLE: [count][index] pairs, max 255
        var rle = new List<byte>(8192);
        int k = 0;
        while (k < idx.Length)
        {
            byte v = idx[k];
            int run = 1;
            while (k + run < idx.Length && idx[k + run] == v && run < 255) run++;
            rle.Add((byte)run);
            rle.Add(v);
            k += run;
        }

        uint crc = Crc32(rle);
        int total = HDR + rle.Count;
        int padded = (total + 255) / 256 * 256;
        var buf = new byte[padded];
        Array.Fill(buf, (byte)0xFF);

        var hdr = new byte[HDR];
        BitConverter.GetBytes(MAGIC).CopyTo(hdr, 0);
        BitConverter.GetBytes((ushort)320).CopyTo(hdr, 4);
        BitConverter.GetBytes((ushort)172).CopyTo(hdr, 6);
        BitConverter.GetBytes((ushort)0).CopyTo(hdr, 8);
        BitConverter.GetBytes((ushort)0).CopyTo(hdr, 10);
        BitConverter.GetBytes((uint)rle.Count).CopyTo(hdr, 12);
        BitConverter.GetBytes(crc).CopyTo(hdr, 16);
        hdr.CopyTo(buf, 0);
        rle.CopyTo(buf, HDR);

        using var sp = new SerialPort(portName, 115200) { ReadTimeout = 20, WriteTimeout = 2000 };
        sp.Open();
        using var link = new Link(sp);

        // if the app is running, ask it to jump back into the bootloader
        try
        {
            var (_, data) = link.Info();
            if (data.Length >= 1 && data[0] != 0xB0)
            {
                Console.WriteLine("设备在 App，切换到 Bootloader…");
                try { link.Cmd(0x00FF, ReadOnlySpan<byte>.Empty, 800, 0); } catch { }
                sp.Close();
                Thread.Sleep(2500);
                sp.Open();
            }
            var (_, d2) = link.Info();
            Console.WriteLine(d2.Length >= 1 && d2[0] == 0xB0 ? "已在 Bootloader" : "警告：不在 Bootloader");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] 探测设备失败: {ex.Message}");
        }

        int pages = padded / 256;
        for (int p = 0; p < pages; p++)
        {
            int addr = BG_BASE + p * 256;
            var pl = new byte[260];
            pl[0] = (byte)(addr & 0xFF); pl[1] = (byte)(addr >> 8);
            pl[2] = (byte)(addr >> 16); pl[3] = (byte)(addr >> 24);
            Array.Copy(buf, p * 256, pl, 4, 256);
            var (st, _) = link.Cmd(0x0020, pl, 2000, 1);
            if (st != 0) { Console.WriteLine($"写入失败 page={p} status={st}"); return 2; }
            if (p % 8 == 0) Console.Write($"\r写入 {p}/{pages}   ");
        }
        Console.WriteLine();

        var fin = new byte[8];
        BitConverter.GetBytes((uint)padded).CopyTo(fin, 0);
        BitConverter.GetBytes(Crc32(buf)).CopyTo(fin, 4);
        var (s2, _) = link.Cmd(0x0021, fin, 3000, 0);
        Console.WriteLine(s2 == 0 ? "背景写入成功，重启后生效" : $"背景校验失败 status={s2}");
        return s2 == 0 ? 0 : 3;
    }

    static uint Crc32(IReadOnlyList<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < data.Count; i++)
        {
            crc ^= data[i];
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }
}
