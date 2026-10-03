using System.Buffers.Binary;
namespace DotrAssetModder;

public enum AssetKind
{
    Unknown,
    Rh0Model,
    Rh2Textures,
    Ps2Icon,
}

public sealed record FailureInfo(int Slot, string Name, string Reason);

public sealed record ExtractResult(
    AssetKind Kind,
    int Written,
    int Failed,
    int Total)
{
    public IReadOnlyList<FailureInfo> Failures { get; init; } = System.Array.Empty<FailureInfo>();
}

public class AssetExtractor
{
    public ExtractResult Extract(byte[] data, string outDir, string stem,
        IList<string>? names = null,
        ExtractionProgress? progress = null)
    {
        Directory.CreateDirectory(outDir);
        var kind = DetectKind(data);
        return kind switch {
            AssetKind.Rh0Model => ModelExtractor.Extract(data, outDir, stem, names, progress),
            AssetKind.Rh2Textures => TextureExtractor.Extract(data, outDir, stem, names, progress),
            AssetKind.Ps2Icon => IconExtractor.Extract(data, outDir, stem),
            _ => new ExtractResult(AssetKind.Unknown, 0, 0, 0),
        };
    }

   

    public static AssetKind DetectKind(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return AssetKind.Unknown;
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic == 0x00304852) return AssetKind.Rh0Model; // RH0\0
        if (data.IndexOf(Rh2Magic) >= 0) return AssetKind.Rh2Textures;
        // PS2 save icon: contains ICN magic 0x010000 as a uint32
        ReadOnlySpan<byte> icnMagic = stackalloc byte[] { 0x00, 0x00, 0x01, 0x00 };
        if (data.IndexOf(icnMagic) >= 0) return AssetKind.Ps2Icon;
        return AssetKind.Unknown;
    }

    internal static ReadOnlySpan<byte> Rh2Magic =>
        new byte[] { 0x52, 0x48, 0x32, 0x00 };

    public static List<uint> FindAllRh2Offsets(ReadOnlySpan<byte> data)
    {
        var result = new List<uint>();
        int pos = 0;
        while (true)
        {
            int found = data.Slice(pos).IndexOf(Rh2Magic);
            if (found < 0) break;
            result.Add((uint)(pos + found));
            pos += found + 4;
        }
        return result;
    }
}

static class Bin
{
    public static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(o, 4));
    public static int I32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.Slice(o, 4));
    public static ushort U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(o, 2));
    public static short I16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt16LittleEndian(d.Slice(o, 2));
    public static float U32f(ReadOnlySpan<byte> d, int o) => BitConverter.Int32BitsToSingle(I32(d, o));
}