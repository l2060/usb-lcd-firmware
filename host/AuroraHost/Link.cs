using System.IO.Ports;

namespace AuroraHost;

/// <summary>MS2-Link v1 framing over a serial port (Linux /dev/ttyACM*).</summary>
public sealed class Link : IDisposable
{
    const byte M0 = 0xA5, M1 = 0x5A;
    readonly SerialPort _sp;
    readonly byte[] _rx = new byte[16384];
    readonly byte[] _tmp = new byte[4096];
    int _rxLen;
    byte _seq;

    public Link(SerialPort sp) => _sp = sp;
    public void Dispose() => _sp.Dispose();

    static ushort Crc16(ReadOnlySpan<byte> d)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in d)
        {
            crc ^= (ushort)(b << 8);
            for (int k = 0; k < 8; k++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    byte[] Build(byte seq, ushort cmd, ReadOnlySpan<byte> payload)
    {
        var f = new byte[9 + payload.Length + 2];
        f[0] = M0; f[1] = M1; f[2] = 1; f[3] = 0; f[4] = seq;
        f[5] = (byte)(cmd & 0xFF); f[6] = (byte)(cmd >> 8);
        f[7] = (byte)(payload.Length & 0xFF); f[8] = (byte)(payload.Length >> 8);
        payload.CopyTo(f.AsSpan(9));
        ushort crc = Crc16(f.AsSpan(2, 7 + payload.Length));
        f[9 + payload.Length] = (byte)(crc & 0xFF);
        f[10 + payload.Length] = (byte)(crc >> 8);
        return f;
    }

    void PumpOnce()
    {
        int n;
        try { n = _sp.Read(_tmp, 0, _tmp.Length); } catch (TimeoutException) { return; }
        if (n <= 0) return;
        if (_rxLen + n > _rx.Length) _rxLen = 0;
        Array.Copy(_tmp, 0, _rx, _rxLen, n);
        _rxLen += n;
    }

    bool TryTake(byte seq, out byte status, out byte[] data)
    {
        status = 0; data = Array.Empty<byte>();
        while (true)
        {
            int start = -1;
            for (int i = 0; i + 1 < _rxLen; i++)
                if (_rx[i] == M0 && _rx[i + 1] == M1) { start = i; break; }
            if (start < 0) { _rxLen = 0; return false; }
            if (start > 0)
            {
                Array.Copy(_rx, start, _rx, 0, _rxLen - start);
                _rxLen -= start;
            }
            if (_rxLen < 11) return false;

            int len = _rx[7] | (_rx[8] << 8);
            if (len > 4096 || _rxLen < 9 + len + 2) return false;

            ushort crc = (ushort)(_rx[9 + len] | (_rx[10 + len] << 8));
            if (Crc16(_rx.AsSpan(2, 7 + len)) != crc)
            {
                Array.Copy(_rx, 2, _rx, 0, _rxLen - 2);
                _rxLen -= 2;
                continue;
            }

            byte flags = _rx[3], s = _rx[4];
            var payload = _rx.AsSpan(9, len).ToArray();
            int consumed = 9 + len + 2;
            Array.Copy(_rx, consumed, _rx, 0, _rxLen - consumed);
            _rxLen -= consumed;

            if ((flags & 1) != 0 && s == seq && len >= 1)
            {
                status = payload[0];
                data = payload[1..];
                return true;
            }
        }
    }

    public (byte status, byte[] data) Cmd(ushort cmd, ReadOnlySpan<byte> payload, int timeoutMs = 1500, int retries = 2)
    {
        byte seq = _seq++;
        var frame = Build(seq, cmd, payload);
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            _sp.Write(frame, 0, frame.Length);
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (TryTake(seq, out byte st, out byte[] d)) return (st, d);
                if (_sp.BytesToRead > 0) PumpOnce();
                else Thread.Sleep(1);          // avoid busy polling
            }
        }
        throw new TimeoutException($"cmd 0x{cmd:X4} 超时");
    }

    public (byte status, byte[] data) Info() => Cmd(0x0001, ReadOnlySpan<byte>.Empty, 3000);
}
