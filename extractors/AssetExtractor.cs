using System.Buffers.Binary;
namespace DotrAssetModder;

public enum AssetKind
{
    Unknown,
    Rh0Model,
    Rh2Textures,
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
        return kind switch
        {
            AssetKind.Rh0Model    => ModelExtractor.Extract(data, outDir, stem, names, progress),
            AssetKind.Rh2Textures => TextureExtractor.Extract(data, outDir, stem, names, progress),
            _ => new ExtractResult(AssetKind.Unknown, 0, 0, 0),
        };
    }

    public ExtractResult Extract(string mrgPath, string outDir,
                                 IList<string>? names = null)
    {
        if (!File.Exists(mrgPath))
            throw new FileNotFoundException("mrg not found", mrgPath);
        return Extract(
            File.ReadAllBytes(mrgPath),
            outDir,
            Path.GetFileNameWithoutExtension(mrgPath),
            names);
    }

    public static AssetKind DetectKind(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return AssetKind.Unknown;
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic == 0x00304852) return AssetKind.Rh0Model; // RH0\0
        if (data.IndexOf(Rh2Magic) >= 0) return AssetKind.Rh2Textures;
        return AssetKind.Unknown;
    }

    internal static ReadOnlySpan<byte> Rh2Magic =>
        new byte[] { 0x52, 0x48, 0x32, 0x00 };

    internal static List<uint> FindAllRh2Offsets(ReadOnlySpan<byte> data)
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

internal static class Bin
{
    public static uint   U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(o, 4));
    public static int    I32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.Slice(o, 4));
    public static ushort U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(o, 2));
    public static short  I16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt16LittleEndian(d.Slice(o, 2));
    public static float  U32f(ReadOnlySpan<byte> d, int o) => BitConverter.Int32BitsToSingle(I32(d, o));
}