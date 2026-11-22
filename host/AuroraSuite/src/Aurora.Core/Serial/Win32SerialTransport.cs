using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Aurora.Core.Serial;

public interface ISerialTransport : IDisposable
{
    string PortName { get; }
    bool IsOpen { get; }
    void Open();
    void Close();
    void Write(ReadOnlySpan<byte> data);
    void Flush();
    int Read(byte[] buffer, int offset, int count);
    int BytesToRead { get; }
}

/// <summary>Win32 serial transport (no NuGet dependency).</summary>
public sealed class Win32SerialTransport : ISerialTransport
{
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct Dcb
    {
        public uint DCBlength, BaudRate, Flags;
        public ushort wReserved, XonLim, XoffLim;
        public byte ByteSize, Parity, StopBits;
        public short XonChar, XoffChar, ErrorChar, EofChar, EvtChar;
        public ushort wReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CommTimeouts
    {
        public uint ReadIntervalTimeout, ReadTotalTimeoutMultiplier, ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier, WriteTotalTimeoutConstant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ComStat
    {
        public uint Flags;
        public uint cbInQue, cbOutQue;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec,
                                            uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool EscapeCommFunction(IntPtr hFile, uint dwFunc);

    const uint SETDTR = 5;
    const uint CLRDTR = 6;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetupComm(SafeFileHandle h, int inQ, int outQ);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetCommState(SafeFileHandle h, ref Dcb dcb);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetCommState(SafeFileHandle h, ref Dcb dcb);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetCommTimeouts(SafeFileHandle h, ref CommTimeouts t);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool PurgeComm(SafeFileHandle h, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ClearCommError(SafeFileHandle h, out uint errors, ref ComStat stat);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FlushFileBuffers(SafeFileHandle h);

    SafeFileHandle? _handle;
    FileStream? _stream;

    public string PortName { get; }
    public int BaudRate { get; }
    public bool IsOpen => _stream != null;

    public Win32SerialTransport(string portName, int baudRate = 115200)
    {
        PortName = portName;
        BaudRate = baudRate;
    }

    public int BytesToRead
    {
        get
        {
            if (_handle == null) return 0;
            var st = new ComStat();
            return ClearCommError(_handle, out _, ref st) ? (int)st.cbInQue : 0;
        }
    }

    public void Open()
    {
        string path = PortName.StartsWith(@"\\.\") ? PortName : @"\\.\" + PortName;
        _handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (_handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            _handle.Dispose();
            _handle = null;
            throw new IOException($"打开 {PortName} 失败 (Win32 {err})");
        }

        SetupComm(_handle, 65536, 65536);

        var dcb = new Dcb { DCBlength = (uint)Marshal.SizeOf<Dcb>() };
        if (!GetCommState(_handle, ref dcb)) throw new IOException("GetCommState 失败");
        dcb.BaudRate = (uint)BaudRate;
        dcb.ByteSize = 8;
        dcb.Parity = 0;
        dcb.StopBits = 0;
        dcb.Flags = 1;
        if (!SetCommState(_handle, ref dcb)) throw new IOException("SetCommState 失败");

        var to = new CommTimeouts
        {
            ReadIntervalTimeout = 0,
            ReadTotalTimeoutMultiplier = 0,
            /* CmdCore already owns the command deadline and retries.  A long
               blocking read adds that delay to every packet ACK (27+ per
               indexed full frame), so keep CDC response polling responsive. */
            ReadTotalTimeoutConstant = 2,
            WriteTotalTimeoutMultiplier = 0,
            WriteTotalTimeoutConstant = 2000,
        };
        SetCommTimeouts(_handle, ref to);
        PurgeComm(_handle, 0x000F);

        _stream = new FileStream(_handle, FileAccess.ReadWrite, 16384, false);
    }

    public void Close()
    {
        try { _stream?.Dispose(); } catch { }
        _stream = null;
        try { _handle?.Dispose(); } catch { }
        _handle = null;
    }

    public void Write(ReadOnlySpan<byte> data) => _stream?.Write(data);

    /// <summary>
    /// Waits until the serial driver has transmitted its pending bytes.  This is
    /// needed before closing a port immediately after a reboot command; closing
    /// a CDC handle can otherwise discard that last short frame.
    /// </summary>
    public void Flush()
    {
        _stream?.Flush();
        if (_handle is { IsInvalid: false }) FlushFileBuffers(_handle);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        try { return _stream?.Read(buffer, offset, count) ?? 0; }
        catch (IOException) { return 0; }
    }

    public void Dispose() => Close();
}
