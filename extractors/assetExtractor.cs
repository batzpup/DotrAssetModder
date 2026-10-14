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
    public ExtractResult Extract(byte[] assetBytes, string outputDirectory, string fileNameStem,
        IList<string>? assetNames = null,
        ExtractionProgress? progress = null)
    {
        Directory.CreateDirectory(outputDirectory);
        var kind = DetectKind(assetBytes);
        return kind switch {
            AssetKind.Rh0Model => ModelExtractor.Extract(assetBytes, outputDirectory, fileNameStem, assetNames, progress),
            AssetKind.Rh2Textures => TextureExtractor.Extract(assetBytes, outputDirectory, fileNameStem, assetNames, progress),
            AssetKind.Ps2Icon => IconExtractor.Extract(assetBytes, outputDirectory, fileNameStem),
            _ => new ExtractResult(AssetKind.Unknown, 0, 0, 0),
        };
    }

   

    public static AssetKind DetectKind(ReadOnlySpan<byte> assetBytes)
    {
        if (assetBytes.Length < 4) return AssetKind.Unknown;
        uint magic = Bin.ReadU32(assetBytes, 0);
        if (magic == Rh0Assembler.Rh0Magic) return AssetKind.Rh0Model;
        if (assetBytes.IndexOf(Rh2Magic) >= 0) return AssetKind.Rh2Textures;
        // PS2 save icon: contains ICN magic 0x010000 as a uint32
        ReadOnlySpan<byte> icnMagic = stackalloc byte[] { 0x00, 0x00, 0x01, 0x00 };
        if (assetBytes.IndexOf(icnMagic) >= 0) return AssetKind.Ps2Icon;
        return AssetKind.Unknown;
    }

    internal static ReadOnlySpan<byte> Rh2Magic =>
        new byte[] { 0x52, 0x48, 0x32, 0x00 };

    public static List<uint> FindAllRh2Offsets(ReadOnlySpan<byte> assetBytes)
    {
        var offsets = new List<uint>();
        int searchStart = 0;
        while (true)
        {
            int foundIndex = assetBytes.Slice(searchStart).IndexOf(Rh2Magic);
            if (foundIndex < 0) break;
            offsets.Add((uint)(searchStart + foundIndex));
            searchStart += foundIndex + 4;
        }
        return offsets;
    }
}