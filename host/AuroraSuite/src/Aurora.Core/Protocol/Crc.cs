namespace Aurora.Core.Protocol;

public static class Crc16
{
    /// <summary>CRC-16/CCITT-FALSE over the given span.</summary>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int k = 0; k < 8; k++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }
}

public static class Crc32
{
    public static uint Compute(ReadOnlySpan<byte> data)
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

public static class Commands
{
    // shared
    public const ushort Info = 0x0001;
    public const ushort Ping = 0x0002;
    public const ushort Reset = 0x0003;
    public const ushort Restart = 0x0004;

    // application
    public const ushort DispBegin = 0x0100;
    public const ushort DispData = 0x0101;
    public const ushort DispEnd = 0x0102;
    public const ushort DispFill = 0x0103;
    public const ushort DispRect = 0x0104;
    public const ushort DispRle = 0x0105;

    // bootloader
    public const ushort Erase = 0x0010;
    public const ushort Write = 0x0011;
    public const ushort Finish = 0x0012;
    public const ushort Run = 0x0013;
    public const ushort BgWrite = 0x0020;
    public const ushort BgFinish = 0x0021;
    public const ushort SelfTest = 0x0022;
    public const ushort Echo = 0x0023;
    public const ushort Meta = 0x0024;
    public const ushort ToBootloader = 0x00FF;
}

public static class StatusCodes
{
    public const byte Ok = 0;
    public const byte Crc = 1;
    public const byte UnknownCommand = 2;
    public const byte Length = 3;
    public const byte Busy = 4;
    public const byte Argument = 5;
    public const byte Flash = 6;

    public static string Describe(byte s) => s switch
    {
        Ok => "成功",
        Crc => "CRC 校验失败",
        UnknownCommand => "设备不支持该命令",
        Length => "长度/数据不符",
        Busy => "设备忙",
        Argument => "参数非法",
        Flash => "Flash 操作失败",
        _ => $"未知状态 {s}",
    };
}
