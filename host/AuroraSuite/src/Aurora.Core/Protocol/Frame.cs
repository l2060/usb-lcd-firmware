namespace Aurora.Core.Protocol;

/// <summary>A single MS2-Link v1 frame: A5 5A | ver | flags | seq | cmd | len | payload | crc16.</summary>
public sealed class Frame
{
    public const byte Magic0 = 0xA5;
    public const byte Magic1 = 0x5A;
    public const int HeaderLength = 9;

    public byte Flags { get; init; }
    public byte Sequence { get; init; }
    public ushort Command { get; init; }
    public byte[] Payload { get; init; } = Array.Empty<byte>();

    public bool IsResponse => (Flags & 1) != 0;

    public byte Status => Payload.Length > 0 ? Payload[0] : (byte)0;
    public ReadOnlySpan<byte> Data => Payload.Length > 1 ? Payload.AsSpan(1) : ReadOnlySpan<byte>.Empty;

    public byte[] ToBytes()
    {
        var buf = new byte[HeaderLength + Payload.Length + 2];
        buf[0] = Magic0;
        buf[1] = Magic1;
        buf[2] = 1;
        buf[3] = Flags;
        buf[4] = Sequence;
        buf[5] = (byte)(Command & 0xFF);
        buf[6] = (byte)(Command >> 8);
        buf[7] = (byte)(Payload.Length & 0xFF);
        buf[8] = (byte)(Payload.Length >> 8);
        Payload.CopyTo(buf, HeaderLength);

        ushort crc = Crc16.Compute(buf.AsSpan(2, 7 + Payload.Length));
        buf[9 + Payload.Length] = (byte)(crc & 0xFF);
        buf[10 + Payload.Length] = (byte)(crc >> 8);
        return buf;
    }

    /// <summary>Incremental parser that tolerates garbage and split packets.</summary>
    public sealed class Parser
    {
        readonly byte[] _buf = new byte[64 * 1024];
        int _len;

        public void Feed(ReadOnlySpan<byte> chunk)
        {
            if (_len + chunk.Length > _buf.Length) _len = 0;
            chunk.CopyTo(_buf.AsSpan(_len));
            _len += chunk.Length;
        }

        public bool TryRead(out Frame frame)
        {
            frame = null!;
            while (true)
            {
                int start = -1;
                for (int i = 0; i + 1 < _len; i++)
                {
                    if (_buf[i] == Magic0 && _buf[i + 1] == Magic1) { start = i; break; }
                }
                if (start < 0) { _len = 0; return false; }
                if (start > 0)
                {
                    Buffer.BlockCopy(_buf, start, _buf, 0, _len - start);
                    _len -= start;
                }
                if (_len < HeaderLength + 2) return false;

                int len = _buf[7] | (_buf[8] << 8);
                if (len > 8192 || _len < HeaderLength + len + 2) return false;

                ushort crc = (ushort)(_buf[9 + len] | (_buf[10 + len] << 8));
                if (Crc16.Compute(_buf.AsSpan(2, 7 + len)) != crc)
                {
                    Buffer.BlockCopy(_buf, 2, _buf, 0, _len - 2);
                    _len -= 2;
                    continue;
                }

                frame = new Frame
                {
                    Flags = _buf[3],
                    Sequence = _buf[4],
                    Command = (ushort)(_buf[5] | (_buf[6] << 8)),
                    Payload = _buf.AsSpan(HeaderLength, len).ToArray(),
                };
                int consumed = HeaderLength + len + 2;
                Buffer.BlockCopy(_buf, consumed, _buf, 0, _len - consumed);
                _len -= consumed;
                return true;
            }
        }
    }
}
