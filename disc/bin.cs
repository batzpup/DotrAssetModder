using System.Buffers.Binary;
namespace DotrAssetModder;

// Little-endian reads and writes: the game's files, the ISO and the ELF are all little-endian.
static class Bin
{
    public static ushort ReadU16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
    public static short ReadI16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(offset, 2));
    public static uint ReadU32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
    public static int ReadI32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4));
    public static ulong ReadU64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));
    public static float ReadF32(ReadOnlySpan<byte> bytes, int offset) => BitConverter.Int32BitsToSingle(ReadI32(bytes, offset));

    public static void WriteU16(Span<byte> bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(offset, 2), value);
    public static void WriteU32(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(offset, 4), value);
    public static void WriteI32(Span<byte> bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(offset, 4), value);
    public static void WriteU64(Span<byte> bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(offset, 8), value);
    public static void WriteF32(Span<byte> bytes, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(offset, 4), value);

    // Appended to the end of a stream.
    public static void AppendU16(Stream stream, ushort value)
    {
        Span<byte> valueBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(valueBytes, value);
        stream.Write(valueBytes);
    }

    public static void AppendI16(Stream stream, short value)
    {
        Span<byte> valueBytes = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(valueBytes, value);
        stream.Write(valueBytes);
    }

    public static void AppendU32(Stream stream, uint value)
    {
        Span<byte> valueBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(valueBytes, value);
        stream.Write(valueBytes);
    }

    public static void AppendI32(Stream stream, int value)
    {
        Span<byte> valueBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(valueBytes, value);
        stream.Write(valueBytes);
    }

    public static void AppendF32(Stream stream, float value)
    {
        Span<byte> valueBytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(valueBytes, value);
        stream.Write(valueBytes);
    }
}
