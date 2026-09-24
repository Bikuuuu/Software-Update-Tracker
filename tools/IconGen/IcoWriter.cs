namespace IconGen;

internal static class IcoWriter
{
    public static void Write(string path, IReadOnlyList<(int Size, byte[] Png)> images)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)png.Length);
            writer.Write((uint)offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) writer.Write(png);
    }

    public static IReadOnlyList<(int Size, byte[] Png)> Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        reader.ReadUInt16();
        if (reader.ReadUInt16() != 1) throw new InvalidDataException("Not an icon file.");
        var count = reader.ReadUInt16();
        var entries = new List<(int Size, int Length, int Offset)>();
        for (var i = 0; i < count; i++)
        {
            int width = reader.ReadByte();
            reader.ReadBytes(7);
            var length = reader.ReadInt32();
            var offset = reader.ReadInt32();
            entries.Add((width == 0 ? 256 : width, length, offset));
        }
        return entries.Select(e =>
        {
            reader.BaseStream.Position = e.Offset;
            return (e.Size, reader.ReadBytes(e.Length));
        }).ToList();
    }
}
