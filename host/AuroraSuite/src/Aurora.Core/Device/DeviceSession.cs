using Aurora.Core.Protocol;

namespace Aurora.Core.Device;

public enum DeviceMode
{
    Unknown,
    Application,
    Bootloader,
}

public sealed record DeviceInfo(
    DeviceMode Mode,
    byte VersionMajor,
    byte VersionMinor,
    int Width,
    int Height,
    int MaxPayload,
    byte Caps,
    uint AppStart,
    uint AppEnd,
    int PageSize,
    byte[] Raw)
{
    public string VersionText => Mode == DeviceMode.Bootloader
        ? $"Bootloader v{VersionMajor}"
        : $"App v{VersionMajor}.{VersionMinor}";

    public bool SupportsIndex8Stream => Mode == DeviceMode.Application && (Caps & 0x02) != 0;

    public override string ToString() => Mode switch
    {
        DeviceMode.Bootloader =>
            $"Bootloader v{VersionMajor}  App 0x{AppStart:X4}..0x{AppEnd:X4}  page={PageSize}",
        DeviceMode.Application =>
            $"App v{VersionMajor}.{VersionMinor}  {Width}x{Height}  max={MaxPayload}  caps=0x{Caps:X2}",
        _ => "未知设备",
    };
}

/// <summary>Owns one open serial link and the MS2-Link command/response cycle.</summary>
public sealed class DeviceSession : IDisposable
{
    readonly Serial.ISerialTransport _port;
    readonly Frame.Parser _parser = new();
    readonly byte[] _readBuf = new byte[8192];
    byte _seq = (byte)Random.Shared.Next(1, 256);

    public string PortName => _port.PortName;
    public DeviceInfo? Info { get; private set; }
    public DeviceMode Mode => Info?.Mode ?? DeviceMode.Unknown;

    DeviceSession(Serial.ISerialTransport port) => _port = port;

    public static DeviceSession Connect(string portName, int baud = 115200)
    {
        var port = new Serial.Win32SerialTransport(portName, baud);
        port.Open();
        var s = new DeviceSession(port);
        return s;
    }

    public DeviceInfo? Info_Refresh() => Info = RequestInfo();

    /// <summary>Stores a DeviceInfo obtained elsewhere (e.g. from a probe).</summary>
    public void SetInfo(DeviceInfo info) => Info = info;

    /// <summary>Fast one-shot INFO used while scanning ports (single attempt, short timeout).</summary>
    public DeviceInfo? Probe(int timeoutMs = 800, int attempts = 3)
    {
        Thread.Sleep(60);                       // let the CDC line settle after open
        for (int i = 0; i < attempts; i++)
        {
            try
            {
            var found = TryInfo(timeoutMs);
                if (found != null) return found;
            }
            catch { }
            Thread.Sleep(120);
        }
        return null;
    }

    DeviceInfo? TryInfo(int timeoutMs)
    {
        try
        {
            var (status, data) = Cmd(Commands.Info, ReadOnlySpan<byte>.Empty, timeoutMs, 0);
            if (status != StatusCodes.Ok || data.Length < 9) return null;

            if (data[0] == 0xB0 && data.Length >= 12)
                return new DeviceInfo(DeviceMode.Bootloader, data[1], 0, 0, 0, 0, 0,
                    BitConverter.ToUInt32(data, 2), BitConverter.ToUInt32(data, 6),
                    data[10] | (data[11] << 8), data);

            return new DeviceInfo(DeviceMode.Application, data[0], data[1],
                data[2] | (data[3] << 8), data[4] | (data[5] << 8),
                data[6] | (data[7] << 8), data[8], 0, 0, 256, data);
        }
        catch
        {
            return null;
        }
    }

    DeviceInfo? RequestInfo()
    {
        try
        {
            var (status, data) = Cmd(Commands.Info, ReadOnlySpan<byte>.Empty, 3000, 1);
            if (status != StatusCodes.Ok || data.Length < 9) return null;

            if (data[0] == 0xB0 && data.Length >= 12)
            {
                return new DeviceInfo(DeviceMode.Bootloader, data[1], 0, 0, 0, 0, 0,
                    BitConverter.ToUInt32(data, 2), BitConverter.ToUInt32(data, 6),
                    data[10] | (data[11] << 8), data);
            }

            return new DeviceInfo(DeviceMode.Application, data[0], data[1],
                data[2] | (data[3] << 8), data[4] | (data[5] << 8),
                data[6] | (data[7] << 8), data[8], 0, 0, 256, data);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes a frame without waiting for a response (streaming commands).</summary>
    public void SendNoAck(ushort command, ReadOnlySpan<byte> payload)
    {
        lock (_cmdLock)
        {
        byte seq = _seq++;
        var frame = new Frame { Sequence = seq, Command = command, Payload = payload.ToArray() };
        _port.Write(frame.ToBytes());
        }
    }

    /// <summary>
    /// Sends a command which will immediately restart the device.  It deliberately
    /// does not wait for a protocol ACK, but does drain the local COM transmit queue
    /// before its owner closes the handle for reconnection.
    /// </summary>
    public void SendRestart(ushort command)
    {
        lock (_cmdLock)
        {
            byte seq = _seq++;
            var frame = new Frame { Sequence = seq, Command = command, Payload = Array.Empty<byte>() };
            _port.Write(frame.ToBytes());
            _port.Flush();
        }

        // USB CDC drivers may batch a tiny frame for a few milliseconds even after
        // FlushFileBuffers returns.  This is still far below the former 400 ms ACK wait.
        Thread.Sleep(60);
    }

    readonly object _cmdLock = new();

    public (byte status, byte[] data) Cmd(ushort command, ReadOnlySpan<byte> payload,
                                          int timeoutMs = 1500, int retries = 2)
    {
        lock (_cmdLock)
        {
            return CmdCore(command, payload, timeoutMs, retries);
        }
    }

    (byte status, byte[] data) CmdCore(ushort command, ReadOnlySpan<byte> payload,
                                       int timeoutMs, int retries)
    {
        byte seq = _seq++;
        var frame = new Frame { Sequence = seq, Command = command, Payload = payload.ToArray() };
        var bytes = frame.ToBytes();

        for (int attempt = 0; attempt <= retries; attempt++)
        {
            _port.Write(bytes);
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                while (_parser.TryRead(out var f))
                {
                    if (f.IsResponse && f.Sequence == seq)
                        return (f.Status, f.Data.ToArray());
                }
                int n = _port.Read(_readBuf, 0, _readBuf.Length);
                if (n > 0) _parser.Feed(_readBuf.AsSpan(0, n));
                else Thread.Sleep(1);
            }
        }
        throw new TimeoutException($"命令 0x{command:X4} 超时");
    }

    public void Dispose()
    {
        try { _port.Dispose(); } catch { }
    }
}
