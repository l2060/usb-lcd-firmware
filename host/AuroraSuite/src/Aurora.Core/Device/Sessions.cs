using Aurora.Core.Protocol;

namespace Aurora.Core.Device;

public sealed record FlashMeta(uint Magic, uint Size, uint Crc, uint Flag, uint AppWord0)
{
    public bool IsValid => Magic == 0x4232534D && Size > 0;
    public bool AppLooksValid => (AppWord0 & 0x7F) == 0x6F;   // JAL opcode
}

public sealed record BackgroundHeader(uint Magic, int Width, int Height, int Length, uint Crc)
{
    public bool IsValid => Magic == 0x4732534D && Width == 320 && Height == 172 && Length > 0;
}

/// <summary>Pixel payload format selected for a streaming session.</summary>
public enum StreamPixelFormat : byte
{
    Rgb565Rle = 0,
    Index8Raw = 1,
    Index8Rle = 2,
}

/// <summary>Bootloader-side operations: firmware flashing, background upload, diagnostics.</summary>
public sealed class BootloaderSession
{
    readonly DeviceSession _s;
    public BootloaderSession(DeviceSession session) => _s = session;

    public static readonly int PageSize = 256;
    public const int BackgroundBase = 0x3200;   /* boot image slot in the new layout */
    public const int HeaderSize = 32;

    public FlashMeta? Meta()
    {
        var (st, d) = _s.Cmd(Commands.Meta, ReadOnlySpan<byte>.Empty, 2500, 0);
        if (st != StatusCodes.Ok || d.Length < 20) return null;
        uint flag = BitConverter.ToUInt32(d, 12);
        uint app0 = d.Length >= 24 ? BitConverter.ToUInt32(d, 20) : 0;
        return new FlashMeta(BitConverter.ToUInt32(d, 0), BitConverter.ToUInt32(d, 4),
                             BitConverter.ToUInt32(d, 8), flag, app0);
    }

    /// <summary>One META command yields both the app metadata and the background header.</summary>
    public (FlashMeta? meta, BackgroundHeader? bg) Diagnostics()
    {
        var (st, d) = _s.Cmd(Commands.Meta, ReadOnlySpan<byte>.Empty, 2500, 0);
        if (st != StatusCodes.Ok || d.Length < 24) return (null, null);

        var meta = new FlashMeta(
            BitConverter.ToUInt32(d, 0),
            BitConverter.ToUInt32(d, 4),
            BitConverter.ToUInt32(d, 8),
            BitConverter.ToUInt32(d, 12),
            BitConverter.ToUInt32(d, 16));

        BackgroundHeader? bg = null;
        if (d.Length >= 40)
        {
            uint magic = BitConverter.ToUInt32(d, 20);
            uint wh = BitConverter.ToUInt32(d, 24);
            int w = (int)(wh & 0xFFFF);
            int h = (int)(wh >> 16);
            int len = (int)BitConverter.ToUInt32(d, 32);
            uint crc = BitConverter.ToUInt32(d, 36);
            bg = new BackgroundHeader(magic, w, h, len, crc);
        }

        return (meta, bg);
    }

    public BackgroundHeader? Background()
    {
        var (st, d) = _s.Cmd(Commands.Meta, ReadOnlySpan<byte>.Empty, 2500, 0);
        if (st != StatusCodes.Ok || d.Length < 40) return null;
        uint magic = BitConverter.ToUInt32(d, 20);
        uint wh = BitConverter.ToUInt32(d, 24);
        int len = (int)BitConverter.ToUInt32(d, 32);
        uint crc = BitConverter.ToUInt32(d, 36);
        return new BackgroundHeader(magic, (int)(wh & 0xFFFF), (int)(wh >> 16), len, crc);
    }

    public (bool ok, string message) SelfTest()
    {
        try
        {
            var (st, d) = _s.Cmd(Commands.SelfTest, ReadOnlySpan<byte>.Empty, 4000, 0);
            if (st != StatusCodes.Ok) return (false, StatusCodes.Describe(st));
            if (d.Length >= 12)
            {
                bool a = d[2] == 0x5A && d[3] == 0x5B;
                bool b = d[8] == 0x5A && d[9] == 0x5B;
                return (a && b, $"0xF400 {(a ? "OK" : "FAIL")} / 0xF500 {(b ? "OK" : "FAIL")}");
            }
            return (true, "完成");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public void Erase(Action<int>? progress = null)
    {
        var (st, _) = _s.Cmd(Commands.Erase, ReadOnlySpan<byte>.Empty, 30000, 0);
        if (st != StatusCodes.Ok) throw new InvalidOperationException($"擦除失败: {StatusCodes.Describe(st)}");
        progress?.Invoke(0);
    }

    /// <summary>Writes a firmware image (already padded to 256 bytes) at the given base address.</summary>
    public void WriteFirmware(byte[] image, uint baseAddress, Action<int, int>? progress = null,
                              CancellationToken token = default)
    {
        int pages = image.Length / PageSize;
        for (int p = 0; p < pages; p++)
        {
            token.ThrowIfCancellationRequested();

            var payload = new byte[4 + PageSize];
            uint addr = baseAddress + (uint)(p * PageSize);
            payload[0] = (byte)(addr & 0xFF);
            payload[1] = (byte)(addr >> 8);
            payload[2] = (byte)(addr >> 16);
            payload[3] = (byte)(addr >> 24);
            Array.Copy(image, p * PageSize, payload, 4, PageSize);

            var (st, _) = _s.Cmd(Commands.Write, payload, 2500, 1);
            if (st != StatusCodes.Ok)
                throw new InvalidOperationException($"写入第 {p} 页失败: {StatusCodes.Describe(st)}");
            progress?.Invoke(p + 1, pages);
        }
    }

    public void Finish(int size, uint crc)
    {
        var payload = new byte[8];
        BitConverter.GetBytes((uint)size).CopyTo(payload, 0);
        BitConverter.GetBytes(crc).CopyTo(payload, 4);
        var (st, _) = _s.Cmd(Commands.Finish, payload, 4000, 0);
        if (st != StatusCodes.Ok) throw new InvalidOperationException($"校验失败: {StatusCodes.Describe(st)}");
    }

    /// <summary>Sends RUN without waiting for an ACK: the device reboots immediately.</summary>
    public void Run()
    {
        _s.SendRestart(Commands.Run);
    }

    /// <summary>
    /// Requests the Bootloader-native restart.  The BL ACKs this frame before
    /// resetting, so an unsupported/old BL is reported without dropping COM.
    /// </summary>
    public void Restart()
    {
        var (status, _) = _s.Cmd(Commands.Restart, ReadOnlySpan<byte>.Empty, 300, 0);
        if (status != StatusCodes.Ok)
            throw new InvalidOperationException($"Bootloader RESTART failed: {StatusCodes.Describe(status)}");
    }

    /// <summary>Uploads a background: header(32) + RLE, 256-byte pages.</summary>
    public void UploadBackground(byte[] encoded, Action<int, int>? progress = null,
                                 CancellationToken token = default)
    {
        int pages = encoded.Length / PageSize;
        for (int p = 0; p < pages; p++)
        {
            token.ThrowIfCancellationRequested();
            var payload = new byte[4 + PageSize];
            int addr = BackgroundBase + p * PageSize;
            payload[0] = (byte)(addr & 0xFF);
            payload[1] = (byte)(addr >> 8);
            payload[2] = (byte)(addr >> 16);
            payload[3] = (byte)(addr >> 24);
            Array.Copy(encoded, p * PageSize, payload, 4, PageSize);

            var (st, _) = _s.Cmd(Commands.BgWrite, payload, 2500, 1);
            if (st != StatusCodes.Ok) throw new InvalidOperationException($"背景写入第 {p} 页失败: {StatusCodes.Describe(st)}");
            progress?.Invoke(p + 1, pages);
        }

        var fin = new byte[8];
        BitConverter.GetBytes((uint)encoded.Length).CopyTo(fin, 0);
        BitConverter.GetBytes(Crc32.Compute(encoded)).CopyTo(fin, 4);
        var (s2, _) = _s.Cmd(Commands.BgFinish, fin, 4000, 0);
        if (s2 != StatusCodes.Ok) throw new InvalidOperationException($"背景校验失败: {StatusCodes.Describe(s2)}");
    }
}

/// <summary>Application-side display operations.</summary>
public sealed class AppSession
{
    readonly DeviceSession _s;
    public AppSession(DeviceSession session) => _s = session;

    public void Fill(ushort color565)
    {
        var p = new byte[2];
        p[0] = (byte)(color565 & 0xFF);
        p[1] = (byte)(color565 >> 8);
        Check(_s.Cmd(Commands.DispFill, p, 1500, 1), "整屏填充");
    }

    public void Rect(int x, int y, int w, int h, ushort color565)
    {
        var p = new byte[10];
        BitConverter.GetBytes((ushort)x).CopyTo(p, 0);
        BitConverter.GetBytes((ushort)y).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)w).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)h).CopyTo(p, 6);
        BitConverter.GetBytes(color565).CopyTo(p, 8);
        Check(_s.Cmd(Commands.DispRect, p, 1500, 1), "矩形");
    }

    public void SendRle(byte[] rle, int chunkRuns = 682)
    {
        // A complete, sufficiently compressible frame fits in one stream packet.
        // That packet is sent without per-chunk request/response gaps, which avoids
        // exposing partial updates while the host waits for each ACK.
        if (SendFullFrameRleFast(rle)) return;

        int step = chunkRuns * 3;
        for (int off = 0; off < rle.Length; off += step)
        {
            int n = Math.Min(step, rle.Length - off);
            Check(_s.Cmd(Commands.DispRle, rle.AsSpan(off, n), 2000, 1), "RLE 数据");
        }
        var (st, d) = _s.Cmd(Commands.DispRle, ReadOnlySpan<byte>.Empty, 2000, 0);
        Check((st, d), "RLE 收尾");
        if (d.Length >= 4 && BitConverter.ToUInt32(d, 0) != 320u * 172u)
            throw new InvalidOperationException("设备像素数不符");
    }

    public bool SendFullFrameRleFast(byte[] rle)
    {
        const int StreamPayloadLimit = 2040;
        const int FramePixels = 320 * 172;

        if (rle.Length + 8 > StreamPayloadLimit) return false;

        BeginStream();
        try
        {
            var rects = new (int x, int y, int w, int h, byte[] rle)[]
            {
                (0, 0, 320, 172, rle),
            };
            uint crc = SendRects(rects);
            var (_, pixels) = CommitStream(crc);
            if (pixels != FramePixels) throw new InvalidOperationException("设备像素数不符");
            return true;
        }
        finally
        {
            EndStream();
        }
    }

    /// <summary>Writes a prepared background image into flash while running the app (0x0110/0x0111).</summary>
    public void WriteBackground(byte[] encoded, Action<int, int>? progress = null)
    {
        const int Base = 0x020000;   /* app background: 128KB quota */
        int pages = encoded.Length / 256;
        for (int p = 0; p < pages; p++)
        {
            var payload = new byte[4 + 256];
            int addr = Base + p * 256;
            payload[0] = (byte)(addr & 0xFF);
            payload[1] = (byte)(addr >> 8);
            payload[2] = (byte)(addr >> 16);
            payload[3] = (byte)(addr >> 24);
            Array.Copy(encoded, p * 256, payload, 4, 256);

            Check(_s.Cmd(0x0110, payload, 3000, 1), "图片写入");
            progress?.Invoke(p + 1, pages);
        }

        var fin = new byte[8];
        BitConverter.GetBytes((uint)encoded.Length).CopyTo(fin, 0);
        BitConverter.GetBytes(Crc32.Compute(encoded)).CopyTo(fin, 4);
        Check(_s.Cmd(0x0111, fin, 5000, 0), "图片校验");
    }

    public byte[] ReadImageHeader()
    {
        var (st, d) = _s.Cmd(0x0112, ReadOnlySpan<byte>.Empty, 3000, 0);
        if (st != StatusCodes.Ok) throw new InvalidOperationException(StatusCodes.Describe(st));
        return d;
    }

    public void RedrawBackground()
    {
        var (st, _) = _s.Cmd(0x0115, ReadOnlySpan<byte>.Empty, 8000, 0);
        if (st != StatusCodes.Ok) throw new InvalidOperationException(StatusCodes.Describe(st));
    }

    /// <summary>Draws a rectangle from RGB565 RLE runs without per-packet ACKs. Returns the pixel count the device drew.</summary>
    public uint Blit(int x, int y, int w, int h, byte[] rle, int chunkBytes = 2040)
    {
        BeginStream();
        try
        {
            var rects = new (int x, int y, int w, int h, byte[] rle)[]
            {
                (x, y, w, h, rle),
            };
            uint crc = SendRects(rects);
            var (_, pixels) = CommitStream(crc);
            return (uint)pixels;
        }
        finally
        {
            EndStream();
        }
    }

    const int StreamPacketBytes = 2040;
    const int StreamInterPacketDelayMs = 2;
    readonly byte[] _streamBuf = new byte[StreamPacketBytes];

    /// <summary>Sends a packed frame (rect blocks back to back) without ACKs; returns its CRC32.</summary>
    public uint SendPackedFrame(byte[] frame, int length)
    {
        for (int off = 0; off < length; off += _streamBuf.Length)
        {
            int n = Math.Min(_streamBuf.Length, length - off);
            _s.SendNoAck(0x0121, frame.AsSpan(off, n));
            /* The device expands RLE directly to the LCD while receiving.  Do
               not overrun its USB receive ring when a video frame spans many
               packets; CRC can only validate, not recover dropped packets. */
            if (off + n < length) System.Threading.Thread.Sleep(StreamInterPacketDelayMs);
        }
        return Crc32.Compute(frame.AsSpan(0, length));
    }

    /// <summary>Streams a frame with an ACK for every packet. Use for large or loss-intolerant frames.</summary>
    public uint SendPackedFrameReliable(byte[] frame, int length)
    {
        for (int off = 0; off < length; off += _streamBuf.Length)
        {
            int n = Math.Min(_streamBuf.Length, length - off);
            Check(_s.Cmd(0x0124, frame.AsSpan(off, n), 4000, 1), "可靠流数据");
        }
        return Crc32.Compute(frame.AsSpan(0, length));
    }

    public const int FontBase = 0x040000;
    public const int FontCapacity = 768 * 1024;   /* 0x040000 .. 0x0FFFFF */

    /// <summary>Uploads a baked .afont atlas into the dedicated font region (0x040000).</summary>
    public (int glyphs, int lineHeight) UploadFont(byte[] font, Action<int, int>? progress = null)
    {
        if (font.Length > FontCapacity)
            throw new InvalidOperationException(
                $"字库 {font.Length:N0} 字节超过区域容量 {FontCapacity:N0} 字节（768KB），请减少字面或字符数");
        int pages = (font.Length + 255) / 256;
        var padded = new byte[pages * 256];
        Array.Fill(padded, (byte)0xFF);
        Array.Copy(font, padded, font.Length);

        for (int p = 0; p < pages; p++)
        {
            var payload = new byte[4 + 256];
            int addr = FontBase + p * 256;
            payload[0] = (byte)(addr & 0xFF); payload[1] = (byte)(addr >> 8);
            payload[2] = (byte)(addr >> 16); payload[3] = (byte)(addr >> 24);
            Array.Copy(padded, p * 256, payload, 4, 256);

            Check(_s.Cmd(0x0130, payload, 3000, 1), "字库写入");
            progress?.Invoke(p + 1, pages);
        }

        var fin = new byte[8];
        BitConverter.GetBytes((uint)font.Length).CopyTo(fin, 0);
        BitConverter.GetBytes(Crc32.Compute(padded)).CopyTo(fin, 4);
        var (st, d) = _s.Cmd(0x0131, fin, 4000, 0);
        Check((st, d), "字库校验");
        int glyphs = d.Length >= 2 ? d[0] | (d[1] << 8) : 0;
        int lh = d.Length >= 3 ? d[2] : 0;
        return (glyphs, lh);
    }

    /// <summary>Draws text with the uploaded font (0x0132).</summary>
    /// <summary>Draws text, optionally clearing a cell first (w/h > 0). 0 = auto-size.</summary>
    public void Text(int face, int x, int y, int w, int h, ushort color565, string text,
                     ushort background565 = 0x0841)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var payload = new byte[13 + bytes.Length];
        payload[0] = (byte)face;
        BitConverter.GetBytes((ushort)x).CopyTo(payload, 1);
        BitConverter.GetBytes((ushort)y).CopyTo(payload, 3);
        BitConverter.GetBytes((ushort)w).CopyTo(payload, 5);
        BitConverter.GetBytes((ushort)h).CopyTo(payload, 7);
        BitConverter.GetBytes(color565).CopyTo(payload, 9);
        BitConverter.GetBytes(background565).CopyTo(payload, 11);
        bytes.CopyTo(payload, 13);
        Check(_s.Cmd(0x0132, payload, 3000, 0), "文本绘制");
    }

    /// <summary>Auto-sized convenience overload.</summary>
    public void Text(int face, int x, int y, ushort color565, string text, ushort background565 = 0x0841)
        => Text(face, x, y, 0, 0, color565, text, background565);

    /// <summary>Rectangle with optional rounded corners; fill and border use separate colours.</summary>
    public void DrawRect(int x, int y, int w, int h, ushort fill, ushort border,
                         bool doFill = true, bool doBorder = true, int radius = 0, int thick = 1)
    {
        byte flags = (byte)((doFill ? 1 : 0) | (doBorder ? 2 : 0));
        var p = new byte[15];
        p[0] = flags;
        BitConverter.GetBytes((ushort)x).CopyTo(p, 1);
        BitConverter.GetBytes((ushort)y).CopyTo(p, 3);
        BitConverter.GetBytes((ushort)w).CopyTo(p, 5);
        BitConverter.GetBytes((ushort)h).CopyTo(p, 7);
        p[9] = (byte)Math.Clamp(radius, 0, 255);
        p[10] = (byte)Math.Clamp(thick, 1, 255);
        BitConverter.GetBytes(fill).CopyTo(p, 11);
        BitConverter.GetBytes(border).CopyTo(p, 13);
        Check(_s.Cmd(0x0150, p, 4000, 0), "矩形");
    }

    /// <summary>Ellipse (w == h for a circle) with separate fill and border colours.</summary>
    public void DrawEllipse(int x, int y, int w, int h, ushort fill, ushort border,
                            bool doFill = true, bool doBorder = true, int thick = 1)
    {
        byte flags = (byte)((doFill ? 1 : 0) | (doBorder ? 2 : 0));
        var p = new byte[14];
        p[0] = flags;
        BitConverter.GetBytes((ushort)x).CopyTo(p, 1);
        BitConverter.GetBytes((ushort)y).CopyTo(p, 3);
        BitConverter.GetBytes((ushort)w).CopyTo(p, 5);
        BitConverter.GetBytes((ushort)h).CopyTo(p, 7);
        p[9] = (byte)Math.Clamp(thick, 1, 255);
        BitConverter.GetBytes(fill).CopyTo(p, 10);
        BitConverter.GetBytes(border).CopyTo(p, 12);
        Check(_s.Cmd(0x0151, p, 4000, 0), "椭圆");
    }

    public void DrawLine(int x0, int y0, int x1, int y1, ushort color, int thick = 1)
    {
        var p = new byte[11];
        BitConverter.GetBytes((ushort)x0).CopyTo(p, 0);
        BitConverter.GetBytes((ushort)y0).CopyTo(p, 2);
        BitConverter.GetBytes((ushort)x1).CopyTo(p, 4);
        BitConverter.GetBytes((ushort)y1).CopyTo(p, 6);
        BitConverter.GetBytes(color).CopyTo(p, 8);
        p[10] = (byte)Math.Clamp(thick, 1, 255);
        Check(_s.Cmd(0x0152, p, 4000, 0), "直线");
    }

    /// <summary>Stores the display rotation (0..3). Takes effect after a reboot.</summary>
    public void SetDisplayRotation(byte rot) => Check(_s.Cmd(0x0140, new[] { rot }, 3000, 0), "设置屏幕方向");

    /// <summary>Reads the stored rotation and the logical size it implies.</summary>
    public (byte rot, int w, int h) GetDisplayRotation()
    {
        var (st, d) = _s.Cmd(0x0141, ReadOnlySpan<byte>.Empty, 3000, 0);
        if (st != StatusCodes.Ok || d.Length < 5) return (0, 320, 172);
        return (d[0], d[1] | (d[2] << 8), d[3] | (d[4] << 8));
    }

    /// <summary>Starts a streaming session. Empty payload retains legacy RGB565-RLE compatibility.</summary>
    public void BeginStream(StreamPixelFormat format = StreamPixelFormat.Rgb565Rle)
    {
        ReadOnlySpan<byte> payload = format == StreamPixelFormat.Rgb565Rle ? ReadOnlySpan<byte>.Empty : new byte[] { (byte)format };
        Check(_s.Cmd(0x0120, payload, 2500, 0), "流式开始");
    }

    /// <summary>Serialises rect blocks and streams them without waiting for ACKs. Returns the payload CRC.</summary>
    public uint SendRects(IReadOnlyList<(int x, int y, int w, int h, byte[] rle)> rects)
    {
        var buffer = new List<byte>(4096);
        foreach (var r in rects)
        {
            buffer.Add((byte)(r.x & 0xFF)); buffer.Add((byte)(r.x >> 8));
            buffer.Add((byte)(r.y & 0xFF)); buffer.Add((byte)(r.y >> 8));
            buffer.Add((byte)(r.w & 0xFF)); buffer.Add((byte)(r.w >> 8));
            buffer.Add((byte)(r.h & 0xFF)); buffer.Add((byte)(r.h >> 8));
            buffer.AddRange(r.rle);
        }

        var data = buffer.ToArray();
        for (int off = 0; off < data.Length; off += StreamPacketBytes)
        {
            int n = Math.Min(StreamPacketBytes, data.Length - off);
            _s.SendNoAck(0x0121, data.AsSpan(off, n));
            if (off + n < data.Length) System.Threading.Thread.Sleep(StreamInterPacketDelayMs);
        }
        return Crc32.Compute(data);
    }

    /// <summary>Commits the frame; returns (frames drawn, pixels). Throws when the CRC does not match.</summary>
    public (int frames, int px) CommitStream(uint crc)
    {
        var p = new byte[4];
        BitConverter.GetBytes(crc).CopyTo(p, 0);
        var (st, d) = _s.Cmd(0x0122, p, 4000, 1);
        if (st != StatusCodes.Ok) throw new InvalidOperationException("流式帧校验失败: " + StatusCodes.Describe(st));
        int frames = d.Length >= 2 ? d[0] | (d[1] << 8) : 0;
        int px = d.Length >= 4 ? d[2] | (d[3] << 8) : 0;
        return (frames, px);
    }

    public void EndStream() => _s.Cmd(0x0123, ReadOnlySpan<byte>.Empty, 2500, 0);

    /// <summary>Requests a reboot into the bootloader without waiting for an ACK.</summary>
    public void RebootToBootloader()
    {
        _s.SendRestart(Commands.ToBootloader);
    }

    static void Check((byte status, byte[] data) r, string what)
    {
        if (r.status != StatusCodes.Ok)
            throw new InvalidOperationException($"{what}失败: {StatusCodes.Describe(r.status)}");
    }
}
