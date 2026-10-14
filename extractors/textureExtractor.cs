using System.Collections.Concurrent;
using static DotrAssetModder.Bin;
namespace DotrAssetModder;

//Based on GMS' Picture extractor
static class TextureExtractor
{
    public static ExtractResult Extract(byte[] assetBytes, string outputDirectory, string fileNameStem, IList<string>? assetNames, ExtractionProgress? progress = null)
    {
        var offsets = AssetExtractor.FindAllRh2Offsets(assetBytes);
        bool haveWordlist = assetNames is { Count: > 0 };
        int failed = 0;

        var alphaMode = string.Equals(Path.GetFileNameWithoutExtension(fileNameStem), "TEXANM", StringComparison.OrdinalIgnoreCase)
            ? Rh2Decoder.AlphaTreatment.ChromaKeyGreen
            : Rh2Decoder.AlphaTreatment.ForceOpaque;

        progress?.SetTotal(offsets.Count);
        int maxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1);
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism };

        Parallel.For(0, offsets.Count, parallelOptions, () => 0, (i, _, localFails) =>
        {
            if (progress?.IsCancellationRequested == true)
            {
                return localFails;
            }
            uint textureOffset = offsets[i];
            string textureFileName;
            if (haveWordlist && i < assetNames!.Count && !string.IsNullOrEmpty(assetNames[i]))
            {
                textureFileName = $"{assetNames[i]}_{i}.png";
            }
            else if (haveWordlist)
            {
                textureFileName = $"{i}.png";
            }
            else
            {
                textureFileName = $"{fileNameStem}_{i}.png";
            }
            bool wasWritten = TryDecodeAndWrite(assetBytes, (int)textureOffset, outputDirectory, textureFileName, alphaMode);
            progress?.Increment();
            return wasWritten ? localFails : localFails + 1;
        }, localFailureCount => Interlocked.Add(ref failed, localFailureCount));

        return new ExtractResult(
            Kind: AssetKind.Rh2Textures,
            Written: offsets.Count - failed,
            Failed: failed,
            Total: offsets.Count);
    }

    private static bool TryDecodeAndWrite(byte[] assetBytes, int textureOffset, string outputDirectory, string fileName, Rh2Decoder.AlphaTreatment alphaTreatment)
    {
        try
        {
            var decodedTexture = Rh2Decoder.Decode(assetBytes, textureOffset, alphaTreatment);
            if (decodedTexture.IsEmpty) return false;
            var pngBytes = PngEncoder.Encode(decodedTexture.Width, decodedTexture.Height, decodedTexture.Rgba);
            File.WriteAllBytes(Path.Combine(outputDirectory, fileName), pngBytes);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

static class GsSwizzle
{
    static readonly int[] PAGE_PSMCT16 = {
        0, 2, 8, 10, 1, 3, 9, 11, 4, 6, 12, 14, 5, 7, 13, 15,
        16, 18, 24, 26, 17, 19, 25, 27, 20, 22, 28, 30, 21, 23, 29, 31,
    };

    static readonly int[] COL_PSMT4 = {
        0, 8, 32, 40, 64, 72, 96, 104, 2, 10, 34, 42, 66, 74, 98, 106, 4, 12, 36, 44, 68, 76, 100, 108, 6, 14, 38, 46, 70, 78, 102, 110,
        16, 24, 48, 56, 80, 88, 112, 120, 18, 26, 50, 58, 82, 90, 114, 122, 20, 28, 52, 60, 84, 92, 116, 124, 22, 30, 54, 62, 86, 94, 118, 126,
        65, 73, 97, 105, 1, 9, 33, 41, 67, 75, 99, 107, 3, 11, 35, 43, 69, 77, 101, 109, 5, 13, 37, 45, 71, 79, 103, 111, 7, 15, 39, 47,
        81, 89, 113, 121, 17, 25, 49, 57, 83, 91, 115, 123, 19, 27, 51, 59, 85, 93, 117, 125, 21, 29, 53, 61, 87, 95, 119, 127, 23, 31, 55, 63,
        64, 72, 96, 104, 0, 8, 32, 40, 66, 74, 98, 106, 2, 10, 34, 42, 68, 76, 100, 108, 4, 12, 36, 44, 70, 78, 102, 110, 6, 14, 38, 46,
        80, 88, 112, 120, 16, 24, 48, 56, 82, 90, 114, 122, 18, 26, 50, 58, 84, 92, 116, 124, 20, 28, 52, 60, 86, 94, 118, 126, 22, 30, 54, 62,
        1, 9, 33, 41, 65, 73, 97, 105, 3, 11, 35, 43, 67, 75, 99, 107, 5, 13, 37, 45, 69, 77, 101, 109, 7, 15, 39, 47, 71, 79, 103, 111,
        17, 25, 49, 57, 81, 89, 113, 121, 19, 27, 51, 59, 83, 91, 115, 123, 21, 29, 53, 61, 85, 93, 117, 125, 23, 31, 55, 63, 87, 95, 119, 127,
    };

    static readonly int[] PAGE_PSMCT32 = {
        0, 1, 4, 5, 16, 17, 20, 21, 2, 3, 6, 7, 18, 19, 22, 23,
        8, 9, 12, 13, 24, 25, 28, 29, 10, 11, 14, 15, 26, 27, 30, 31,
    };

    static readonly int[] COL_PSMCT32 =
        { 0, 1, 4, 5, 8, 9, 12, 13, 2, 3, 6, 7, 10, 11, 14, 15 };

    const int PAGE_BITS = 65536;
    const int BLOCK_BITS = 2048;
    const int COL_BITS = 512;

    internal sealed class Psmt4Tables
    {
        public required int[] Swizzle4 { get; init; }
        public required int BitsWide4 { get; init; }
        public required int[] Inverse32 { get; init; }
    }

    static Psmt4Tables? _psmt4Cache;
    static readonly object _psmt4Lock = new();

    internal static Psmt4Tables GetPsmt4Tables()
    {
        if (_psmt4Cache != null) return _psmt4Cache;
        lock (_psmt4Lock)
        {
            if (_psmt4Cache != null) return _psmt4Cache;
            var (swizzle4, bitsWide4, _) = Build(4, 8, 32, 4, 4, PAGE_PSMCT16, COL_PSMT4);
            var (swizzle32, bitsWide32, _) = Build(8, 4, 8, 2, 32, PAGE_PSMCT32, COL_PSMCT32);
            var inverse32 = new int[PAGE_BITS];
            for (int bitIndex = 0; bitIndex < PAGE_BITS; bitIndex++)
            {
                int swizzleIndex = (bitIndex / bitsWide32) * bitsWide32 + (bitIndex % bitsWide32);
                inverse32[swizzle32[swizzleIndex]] = bitIndex;
            }
            _psmt4Cache = new Psmt4Tables { Swizzle4 = swizzle4, BitsWide4 = bitsWide4, Inverse32 = inverse32 };
            return _psmt4Cache;
        }
    }

    static (int[] SwizzleTable, int BitsWide, int PixelsTall) Build(
        int blocksX, int blocksY, int pixelsPerColumnX, int pixelsPerColumnY, int bitsPerPixel,
        int[] pageSwizzle, int[] columnSwizzle)
    {
        int blockBitW = pixelsPerColumnX * bitsPerPixel;
        int bitsWide = blocksX * blockBitW;
        int pixelsTall = blocksY * pixelsPerColumnY * 4;
        var swizzleTable = new int[bitsWide * pixelsTall];
        for (int block = 0; block < 32; block++)
        {
            int blockBaseBits = pageSwizzle[block] * BLOCK_BITS;
            int blockOriginX = (block % blocksX) * blockBitW;
            int blockOriginY = (block / blocksX) * (pixelsPerColumnY * 4);
            for (int i = 0; i < BLOCK_BITS; i++)
            {
                int pixelIndex = i / bitsPerPixel;
                int columnOffset = columnSwizzle[pixelIndex % columnSwizzle.Length];
                int blockOffsetBits = (i / COL_BITS) * COL_BITS;
                int bitOffset = columnOffset * bitsPerPixel + blockOffsetBits + (i % bitsPerPixel);
                int x = blockOriginX + (i % blockBitW);
                int y = blockOriginY + (i / blockBitW);
                swizzleTable[y * bitsWide + x] = blockBaseBits + bitOffset;
            }
        }
        return (swizzleTable, bitsWide, pixelsTall);
    }

    static readonly ConcurrentDictionary<(int Width, int Height), int[]> _psmt4BitCache = new();

    internal static int[] GetPsmt4BitIndices(int actualWidth, int actualHeight)
        => _psmt4BitCache.GetOrAdd((actualWidth, actualHeight), static sizeKey =>
        {
            var psmt4Tables = GetPsmt4Tables();
            int pixelCount = sizeKey.Width * sizeKey.Height;
            var bitIndices = new int[pixelCount * 4];
            for (int y = 0; y < sizeKey.Height; y++)
            {
                int rowOffset = y * psmt4Tables.BitsWide4;
                int rowStart = y * sizeKey.Width;
                for (int x = 0; x < sizeKey.Width; x++)
                {
                    int tablePosition = psmt4Tables.Swizzle4[rowOffset + x * 4];
                    int baseIndex = (rowStart + x) * 4;
                    for (int k = 0; k < 4; k++)
                        bitIndices[baseIndex + k] = psmt4Tables.Inverse32[tablePosition + k];
                }
            }
            return bitIndices;
        });

    static readonly ConcurrentDictionary<(int BlockWidth, int BlockHeight, int Width, int Height), int[]> _psmt8Cache = new();

    internal static int[] GetPsmt8Swizzle(int blockWidth, int blockHeight, int width, int height)
        => _psmt8Cache.GetOrAdd((blockWidth, blockHeight, width, height), static sizeKey =>
        {
            int subBlocksPerRow = Math.Max(sizeKey.BlockWidth / 16, 1);
            int blockPixelCount = sizeKey.BlockWidth * sizeKey.BlockHeight;
            var swizzleTable = new int[blockPixelCount];
            for (int i = 0; i < blockPixelCount; i++)
            {
                int block = i / 32;
                int blockHoriz = 16 * (block % subBlocksPerRow);
                int blockRow = block / subBlocksPerRow;
                int blockVert = (2 * blockRow) - (blockRow % 2);
                int subBlock = i % 4;
                int blockOffset = (block / 16) % 2;
                int subBlockIndex = (4 * (blockOffset + subBlock) + (i / 4)) % 8;
                int x = blockHoriz + 8 * (subBlock / 2) + subBlockIndex;
                int y = blockVert + 2 * (subBlock % 2);
                swizzleTable[i] = (x >= 0 && x < sizeKey.Width && y >= 0 && y < sizeKey.Height)
                    ? (y * sizeKey.Width + x) * 4
                    : -1;
            }
            return swizzleTable;
        });
}

public static class Rh2Decoder
{
    public readonly record struct DecodedTexture(int Width, int Height, byte[] Rgba, bool HasAlpha)
    {
        public bool IsEmpty => Width == 0 || Height == 0 || Rgba.Length == 0;
    }

    const int SetupBlockSize = 0x60;

    static ReadOnlySpan<byte> EndSig => new byte[]
        { 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    public enum AlphaTreatment
    {
        Keep,
        ForceOpaque,
        ChromaKeyGreen
    }

    public static DecodedTexture Decode(ReadOnlySpan<byte> textureBytes, int textureOffset, AlphaTreatment alphaTreatment = AlphaTreatment.Keep)
    {
        var decodedTexture = DecodeCore(textureBytes, textureOffset);
        if (decodedTexture.IsEmpty) return decodedTexture;
        var rgba = decodedTexture.Rgba;
        if (alphaTreatment == AlphaTreatment.ForceOpaque)
        {
            for (int i = 3; i < rgba.Length; i += 4)
                rgba[i] = 255;
            return decodedTexture with { HasAlpha = false };
        }
        if (alphaTreatment == AlphaTreatment.ChromaKeyGreen)
        {
            bool any = false;
            for (int i = 0; i < rgba.Length; i += 4)
            {
                if (rgba[i] == 0 && rgba[i + 1] == 255 && rgba[i + 2] == 0)
                {
                    rgba[i + 3] = 0;
                    any = true;
                }
                else
                {
                    rgba[i + 3] = 255;
                }
            }
            return decodedTexture with { HasAlpha = any };
        }
        return decodedTexture;
    }

    static DecodedTexture DecodeCore(ReadOnlySpan<byte> textureBytes, int textureOffset)
    {
        if (textureOffset + 0x60 > textureBytes.Length)
            throw new ArgumentException($"RH2 header out of range at 0x{textureOffset:x}");

        uint totalSize = ReadU32(textureBytes, textureOffset + 8);
        ushort psm = ReadU16(textureBytes, textureOffset + 0x50); // image GS pixel format: 0x13=PSMT8, 0x14=PSMT4
        ushort clutPsm = ReadU16(textureBytes, textureOffset + 0x52); // CLUT GS pixel format (see clut32 below)
        ushort width = ReadU16(textureBytes, textureOffset + 0x54);
        ushort height = ReadU16(textureBytes, textureOffset + 0x56);
        ushort sectionCount = ReadU16(textureBytes, textureOffset + 0x58);
        bool is4Bit = (psm == 0x14);

        /* Palette entry width comes from clutPsm, not from the byte gap between the two
           end-of-transfer markers. Per SzText_GetClutSize in the PS2 ELF, clutPsm 0x00 (PSMCT32)
           -> 32-bit RGBA entries; 0x02/0x0A (PSMCT16/16S) -> 16-bit RGBA5551 entries. Choosing by
           paletteSize misdecodes a PSMT4 image with a 32-bit CLUT (16 entries x 4B = 64B looks
           like 5551). */
        bool clut32 = (clutPsm == 0x00);

        int endOfImage = (int)Math.Min((long)textureOffset + totalSize, textureBytes.Length);
        int firstEndMarker = IndexOf(textureBytes, EndSig, textureOffset, endOfImage);
        if (firstEndMarker < 0) return new DecodedTexture(width, height, new byte[width * height * 4], false);
        int paletteOff = firstEndMarker + EndSig.Length;
        int secondEndMarker = IndexOf(textureBytes, EndSig, paletteOff, endOfImage);
        if (secondEndMarker < 0) return new DecodedTexture(width, height, new byte[width * height * 4], false);
        int paletteSize = (secondEndMarker + EndSig.Length) - paletteOff - SetupBlockSize;
        int startOfImage = paletteOff + paletteSize + SetupBlockSize;

        Span<byte> palette = stackalloc byte[256 * 4];
        bool hasAlpha = false;
        int paletteLength;
        if (clut32)
        {
            // 32-bit RGBA entries. PS2 stores alpha 0..128, so A8 = min(255, raw*2).
            paletteLength = Math.Min(256, paletteSize / 4);
            for (int i = 0; i < paletteLength; i++)
            {
                int entryOffset = paletteOff + i * 4;
                byte redByte = textureBytes[entryOffset], greenByte = textureBytes[entryOffset + 1], blueByte = textureBytes[entryOffset + 2], rawAlphaByte = textureBytes[entryOffset + 3];
                byte alphaByte = (byte)Math.Min(255, rawAlphaByte * 2);
                if (alphaByte < 255) hasAlpha = true;
                int outputOffset = i * 4;
                palette[outputOffset] = redByte;
                palette[outputOffset + 1] = greenByte;
                palette[outputOffset + 2] = blueByte;
                palette[outputOffset + 3] = alphaByte;
            }
        }
        else
        {
            // 16-bit RGBA5551 entries.
            paletteLength = Math.Min(256, paletteSize / 2);
            for (int i = 0; i < paletteLength; i++)
            {
                ushort packedColour = ReadU16(textureBytes, paletteOff + i * 2);
                /* 5 -> 8 bits by bit replication (fiveBitValue << 3 | fiveBitValue >> 2), as
                   DotR-PC's tools/textures.py does. Truncating (fiveBitValue * 255 / 31) is one low
                   for 12 of the 32 levels. */
                static byte ExpandFiveBitsToEightBits(int fiveBitValue) => (byte)(fiveBitValue << 3 | fiveBitValue >> 2);
                byte red = ExpandFiveBitsToEightBits(packedColour & 0x1F);
                byte green = ExpandFiveBitsToEightBits((packedColour >> 5) & 0x1F);
                byte blue = ExpandFiveBitsToEightBits((packedColour >> 10) & 0x1F);
                byte alphaByte = (byte)(((packedColour & 0x8000) != 0) ? 255 : 0);
                if (alphaByte == 0) hasAlpha = true;
                int outputOffset = i * 4;
                palette[outputOffset] = red;
                palette[outputOffset + 1] = green;
                palette[outputOffset + 2] = blue;
                palette[outputOffset + 3] = alphaByte;
            }
        }

        return is4Bit
            ? DecodePsmt4(textureBytes, startOfImage, width, height, palette, paletteLength, hasAlpha)
            : DecodePsmt8(textureBytes, startOfImage, width, height, sectionCount, palette, paletteLength, hasAlpha);
    }

    private static DecodedTexture DecodePsmt4(
        ReadOnlySpan<byte> textureBytes, int startOfImage, int width, int height,
        ReadOnlySpan<byte> palette, int paletteLength, bool hasAlpha)
    {
        int actualWidth = Math.Max(width / 2, 1);
        int actualHeight = Math.Max(height / 2, 1);
        var bitIndexTable = GsSwizzle.GetPsmt4BitIndices(actualWidth, actualHeight);

        const int PageBytes = 8192;
        Span<byte> pageBytes = stackalloc byte[PageBytes];
        int copyLength = Math.Min(PageBytes, textureBytes.Length - startOfImage);
        if (copyLength > 0)
            textureBytes.Slice(startOfImage, copyLength).CopyTo(pageBytes);

        int pixelCount = actualWidth * actualHeight;
        var rgba = new byte[pixelCount * 4];
        for (int i = 0; i < pixelCount; i++)
        {
            int bitIndex0 = bitIndexTable[i * 4];
            int bitIndex1 = bitIndexTable[i * 4 + 1];
            int bitIndex2 = bitIndexTable[i * 4 + 2];
            int bitIndex3 = bitIndexTable[i * 4 + 3];
            int nibble =
                ((pageBytes[bitIndex0 >> 3] >> (bitIndex0 & 7)) & 1)
                | (((pageBytes[bitIndex1 >> 3] >> (bitIndex1 & 7)) & 1) << 1)
                | (((pageBytes[bitIndex2 >> 3] >> (bitIndex2 & 7)) & 1) << 2)
                | (((pageBytes[bitIndex3 >> 3] >> (bitIndex3 & 7)) & 1) << 3);
            if (nibble >= paletteLength) continue;
            int paletteByteOffset = nibble * 4;
            int rgbaOffset = i * 4;
            rgba[rgbaOffset] = palette[paletteByteOffset];
            rgba[rgbaOffset + 1] = palette[paletteByteOffset + 1];
            rgba[rgbaOffset + 2] = palette[paletteByteOffset + 2];
            rgba[rgbaOffset + 3] = palette[paletteByteOffset + 3];
        }
        return new DecodedTexture(actualWidth, actualHeight, rgba, hasAlpha);
    }

    static DecodedTexture DecodePsmt8(
        ReadOnlySpan<byte> textureBytes, int startOfImage, int width, int height, int sectionCount,
        ReadOnlySpan<byte> palette, int paletteLength, bool hasAlpha)
    {
        Span<byte> lookupTable = stackalloc byte[256 * 4];
        if (paletteLength > 0)
        {
            for (int paletteIndex = 0; paletteIndex < 256; paletteIndex++)
            {
                int paletteGroup = (paletteIndex % 32) / 8;
                int shuffledIndex = paletteIndex;
                if (paletteGroup == 1) shuffledIndex += 8;
                else if (paletteGroup == 2) shuffledIndex -= 8;

                shuffledIndex = ((shuffledIndex % paletteLength) + paletteLength) % paletteLength;
                int sourceOffset = shuffledIndex * 4;
                int destinationOffset = paletteIndex * 4;
                lookupTable[destinationOffset] = palette[sourceOffset];
                lookupTable[destinationOffset + 1] = palette[sourceOffset + 1];
                lookupTable[destinationOffset + 2] = palette[sourceOffset + 2];
                lookupTable[destinationOffset + 3] = palette[sourceOffset + 3];
            }
        }

        int blockWidth = Math.Min(width, 128);
        int blockHeight = Math.Min(height, 64);
        int sectionPixelCount = blockWidth * blockHeight;
        int colsPerRow = Math.Max(width / blockWidth, 1);
        int rowsOfSections = Math.Max(height / blockHeight, 1);
        /* Only a full 128x64 GS page carries the PSMT8 block swizzle; a narrower (or shorter)
           texture stores each section as a plain linear blockWidth x blockHeight block
           (retail's 64x64, 64x128 and 64x256 textures). Sections are always
           (sectionPixelCount + SetupBlockSize) apart, each after its own 0x60 setup block, so a
           64x128 texture is two stacked blocks, not w*h linear bytes (that would run through the
           second section's setup block). */
        var positionTable = blockWidth == 128 && blockHeight == 64
            ? GsSwizzle.GetPsmt8Swizzle(blockWidth, blockHeight, width, height)
            : null;
        int rgbaLength = width * height * 4;
        var rgbaBytes = new byte[rgbaLength];
        int sectionCap = Math.Min(sectionCount, colsPerRow * rowsOfSections);

        for (int sectionIndex = 0; sectionIndex < sectionCap; sectionIndex++)
        {
            int sectionOffset = startOfImage + (sectionPixelCount + SetupBlockSize) * sectionIndex;
            if (sectionOffset + sectionPixelCount > textureBytes.Length) break;
            int xShift = blockWidth * (sectionIndex % colsPerRow);
            int yShift = blockHeight * (sectionIndex / colsPerRow);
            int sectionDestinationShift = (yShift * width + xShift) * 4;
            for (int i = 0; i < sectionPixelCount; i++)
            {
                int pixelOffset;
                if (positionTable == null) pixelOffset = ((i / blockWidth) * width + i % blockWidth) * 4 + sectionDestinationShift;
                else
                {
                    int baseDestination = positionTable[i];
                    if (baseDestination < 0) continue;
                    pixelOffset = baseDestination + sectionDestinationShift;
                }
                if ((uint)pixelOffset > (uint)(rgbaLength - 4)) continue;
                int lookupOffset = textureBytes[sectionOffset + i] * 4;
                rgbaBytes[pixelOffset] = lookupTable[lookupOffset];
                rgbaBytes[pixelOffset + 1] = lookupTable[lookupOffset + 1];
                rgbaBytes[pixelOffset + 2] = lookupTable[lookupOffset + 2];
                rgbaBytes[pixelOffset + 3] = lookupTable[lookupOffset + 3];
            }
        }
        return new DecodedTexture(width, height, rgbaBytes, hasAlpha);
    }

    static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle,
        int start, int end)
    {
        if (start >= haystack.Length || start >= end) return -1;
        int limit = Math.Min(end, haystack.Length);
        var sliced = haystack.Slice(start, limit - start);
        int found = sliced.IndexOf(needle);
        return found < 0 ? -1 : start + found;
    }
}

// RGBA (straight alpha) -> PNG, by SkiaSharp.
static class PngEncoder
{
    public static byte[] Encode(int width, int height, byte[] rgba)
    {
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("rgba buffer too small for given dimensions");
        }
        using var bitmap = TextureSlotManager.RgbaToBitmap(width, height, rgba);
        using var encodedData = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return encodedData.ToArray();
    }
}
