using System.Text;

namespace Aurora.Core.Firmware;

/// <summary>Parsed Intel HEX image: contiguous bytes plus their start address.</summary>
public sealed class FirmwareImage
{
    public uint BaseAddress { get; }
    public byte[] Data { get; }
    public int Length => Data.Length;

    FirmwareImage(uint baseAddress, byte[] data)
    {
        BaseAddress = baseAddress;
        Data = data;
    }

    public int PaddedLength => (Data.Length + 255) / 256 * 256;

    public byte[] PadTo256()
    {
        var buf = new byte[PaddedLength];
        Array.Fill(buf, (byte)0xFF);
        Array.Copy(Data, buf, Data.Length);
        return buf;
    }

    public static FirmwareImage FromHexFile(string path) => FromHexText(File.ReadAllText(path));

    public static FirmwareImage FromHexText(string text)
    {
        var map = new SortedDictionary<uint, byte>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 11 || line[0] != ':') continue;

            var bytes = new byte[(line.Length - 1) / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(line.Substring(1 + i * 2, 2), 16);

            int len = bytes[0];
            uint addr = (uint)((bytes[1] << 8) | bytes[2]);
            int type = bytes[3];
            if (type == 0 && bytes.Length >= 5 + len)
            {
                for (int i = 0; i < len; i++) map[addr + (uint)i] = bytes[4 + i];
            }
        }

        if (map.Count == 0) throw new InvalidDataException("HEX 文件没有数据");

        uint min = map.Keys.First();
        uint max = map.Keys.Last();
        var data = new byte[max - min + 1];
        Array.Fill(data, (byte)0xFF);
        foreach (var kv in map) data[kv.Key - min] = kv.Value;
        return new FirmwareImage(min, data);
    }
}
