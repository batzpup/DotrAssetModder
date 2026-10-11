using System.Buffers.Binary;
using System.Collections.Concurrent;
using static DotrAssetModder.Bin;
namespace DotrAssetModder;

//Based on GMS' Picture extractor
static class TextureExtractor
{
    public static ExtractResult Extract(byte[] data, string outDir, string stem, IList<string>? names, ExtractionProgress? progress = null)
    {
        var offsets = AssetExtractor.FindAllRh2Offsets(data);
        bool haveWordlist = names is { Count: > 0 };
        int failed = 0;

        var alphaMode = string.Equals(Path.GetFileNameWithoutExtension(stem), "TEXANM", StringComparison.OrdinalIgnoreCase)
            ? Rh2Decoder.AlphaTreat.ChromaKeyGreen
            : Rh2Decoder.AlphaTreat.ForceOpaque;

        progress?.SetTotal(offsets.Count);
        int maxDop = Math.Max(1, Environment.ProcessorCount - 1);
        var popts = new ParallelOptions { MaxDegreeOfParallelism = maxDop };

        Parallel.For(0, offsets.Count, popts, () => 0, (i, _, localFails) =>
        {
            if (progress?.IsCancellationRequested == true)
            {
                return localFails;
            }
            uint off = offsets[i];
            string fname;
            if (haveWordlist && i < names!.Count && !string.IsNullOrEmpty(names[i]))
            {
                fname = $"{names[i]}_{i}.png";
            }
            else if (haveWordlist)
            {
                fname = $"{i}.png";
            }
            else
            {
                fname = $"{stem}_{i}.png";
            }
            bool ok = TryDecodeAndWrite(data, (int)off, outDir, fname, alphaMode);
            progress?.Increment();
            return ok ? localFails : localFails + 1;
        }, lf => Interlocked.Add(ref failed, lf));

        return new ExtractResult(
            Kind: AssetKind.Rh2Textures,
            Written: offsets.Count - failed,
            Failed: failed,
            Total: offsets.Count);
    }

    private static bool TryDecodeAndWrite(byte[] data, int off, string outDir, string fileName, Rh2Decoder.AlphaTreat alpha)
    {
        try
        {
            var tex = Rh2Decoder.Decode(data, off, alpha);
            if (tex.IsEmpty) return false;
            var png = PngEncoder.Encode(tex.Width, tex.Height, tex.Rgba);
            File.WriteAllBytes(Path.Combine(outDir, fileName), png);
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
        public required int[] Sw4 { get; init; }
        public required int Bw4 { get; init; }
        public required int[] Inv32 { get; init; }
    }

    static Psmt4Tables? _psmt4Cache;
    static readonly object _psmt4Lock = new();

    internal static Psmt4Tables GetPsmt4Tables()
    {
        if (_psmt4Cache != null) return _psmt4Cache;
        lock (_psmt4Lock)
        {
            if (_psmt4Cache != null) return _psmt4Cache;
            var (sw4, bw4, _) = Build(4, 8, 32, 4, 4, PAGE_PSMCT16, COL_PSMT4);
            var (sw32, bw32, _) = Build(8, 4, 8, 2, 32, PAGE_PSMCT32, COL_PSMCT32);
            var inv32 = new int[PAGE_BITS];
            for (int f = 0; f < PAGE_BITS; f++)
            {
                int idx = (f / bw32) * bw32 + (f % bw32);
                inv32[sw32[idx]] = f;
            }
            _psmt4Cache = new Psmt4Tables { Sw4 = sw4, Bw4 = bw4, Inv32 = inv32 };
            return _psmt4Cache;
        }
    }

    static (int[] sw, int bitsW, int pxTall) Build(
        int blocksX, int blocksY, int pxColX, int pxColY, int bpp,
        int[] pageSw, int[] colSw)
    {
        int blockBitW = pxColX * bpp;
        int bitsW = blocksX * blockBitW;
        int pxTall = blocksY * pxColY * 4;
        var sw = new int[bitsW * pxTall];
        for (int block = 0; block < 32; block++)
        {
            int bbase = pageSw[block] * BLOCK_BITS;
            int bX = (block % blocksX) * blockBitW;
            int bY = (block / blocksX) * (pxColY * 4);
            for (int i = 0; i < BLOCK_BITS; i++)
            {
                int px = i / bpp;
                int off = colSw[px % colSw.Length];
                int blockOff = (i / COL_BITS) * COL_BITS;
                int bitOff = off * bpp + blockOff + (i % bpp);
                int x = bX + (i % blockBitW);
                int y = bY + (i / blockBitW);
                sw[y * bitsW + x] = bbase + bitOff;
            }
        }
        return (sw, bitsW, pxTall);
    }

    static readonly ConcurrentDictionary<(int w, int h), int[]> _psmt4BitCache = new();

    internal static int[] GetPsmt4BitIndices(int wActual, int hActual)
        => _psmt4BitCache.GetOrAdd((wActual, hActual), static key =>
        {
            var t = GetPsmt4Tables();
            int npix = key.w * key.h;
            var outArr = new int[npix * 4];
            for (int y = 0; y < key.h; y++)
            {
                int rowOff = y * t.Bw4;
                int ry = y * key.w;
                for (int x = 0; x < key.w; x++)
                {
                    int pos = t.Sw4[rowOff + x * 4];
                    int baseIdx = (ry + x) * 4;
                    for (int k = 0; k < 4; k++)
                        outArr[baseIdx + k] = t.Inv32[pos + k];
                }
            }
            return outArr;
        });

    static readonly ConcurrentDictionary<(int bw, int bh, int w, int h), int[]> _psmt8Cache = new();

    internal static int[] GetPsmt8Swizzle(int blockWidth, int blockHeight, int w, int h)
        => _psmt8Cache.GetOrAdd((blockWidth, blockHeight, w, h), static key =>
        {
            int subBlocksPerRow = Math.Max(key.bw / 16, 1);
            int partial = key.bw * key.bh;
            var outArr = new int[partial];
            for (int i = 0; i < partial; i++)
            {
                int block = i / 32;
                int blockHoriz = 16 * (block % subBlocksPerRow);
                int blockRow = block / subBlocksPerRow;
                int blockVert = (2 * blockRow) - (blockRow % 2);
                int subBlock = i % 4;
                int blockOffset = (block / 16) % 2;
                int subBlockInd = (4 * (blockOffset + subBlock) + (i / 4)) % 8;
                int x = blockHoriz + 8 * (subBlock / 2) + subBlockInd;
                int y = blockVert + 2 * (subBlock % 2);
                outArr[i] = (x >= 0 && x < key.w && y >= 0 && y < key.h)
                    ? (y * key.w + x) * 4
                    : -1;
            }
            return outArr;
        });
}

public static class Rh2Decoder
{
    public readonly record struct DecodedTexture(int Width, int Height, byte[] Rgba, bool HasAlpha)
    {
        public bool IsEmpty => Width == 0 || Height == 0 || Rgba.Length == 0;
    }

    const int Pad = 0x60;

    static ReadOnlySpan<byte> EndSig => new byte[]
        { 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    public enum AlphaTreat
    {
        Keep,
        ForceOpaque,
        ChromaKeyGreen
    }

    public static DecodedTexture Decode(ReadOnlySpan<byte> data, int off, AlphaTreat alpha = AlphaTreat.Keep)
    {
        var tex = DecodeCore(data, off);
        if (tex.IsEmpty) return tex;
        var rgba = tex.Rgba;
        if (alpha == AlphaTreat.ForceOpaque)
        {
            for (int i = 3; i < rgba.Length; i += 4)
                rgba[i] = 255;
            return tex with { HasAlpha = false };
        }
        if (alpha == AlphaTreat.ChromaKeyGreen)
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
            return tex with { HasAlpha = any };
        }
        return tex;
    }

    static DecodedTexture DecodeCore(ReadOnlySpan<byte> data, int off)
    {
        if (off + 0x60 > data.Length)
            throw new ArgumentException($"RH2 header out of range at 0x{off:x}");

        uint totalSize = U32(data, off + 8);
        ushort psm = U16(data, off + 0x50); // image GS pixel format: 0x13=PSMT8, 0x14=PSMT4
        ushort clutPsm = U16(data, off + 0x52); // CLUT GS pixel format (see clut32 below)
        ushort w = U16(data, off + 0x54);
        ushort h = U16(data, off + 0x56);
        ushort nSecs = U16(data, off + 0x58);
        bool is4Bit = (psm == 0x14);

        // Palette entry width is dictated by clutPsm, NOT by the byte gap between
        // the two end-of-transfer markers. Per SzText_GetClutSize in the PS2 ELF,
        // clutPsm 0x00 (PSMCT32) -> 32-bit RGBA entries; 0x02/0x0A (PSMCT16/16S)
        // -> 16-bit RGBA5551 entries. Picking by paletteSize misdecodes a PSMT4
        // image carrying a 32-bit CLUT (16 entries x 4B = 64B, which the old
        // size test wrongly routed to the 5551 path).
        bool clut32 = (clutPsm == 0x00);

        int endOfImage = (int)Math.Min((long)off + totalSize, data.Length);
        int p1 = IndexOf(data, EndSig, off, endOfImage);
        if (p1 < 0) return new DecodedTexture(w, h, new byte[w * h * 4], false);
        int paletteOff = p1 + EndSig.Length;
        int p2 = IndexOf(data, EndSig, paletteOff, endOfImage);
        if (p2 < 0) return new DecodedTexture(w, h, new byte[w * h * 4], false);
        int paletteSize = (p2 + EndSig.Length) - paletteOff - Pad;
        int startOfImage = paletteOff + paletteSize + Pad;

        Span<byte> palette = stackalloc byte[256 * 4];
        bool hasAlpha = false;
        int paletteLen;
        if (clut32)
        {
            // 32-bit RGBA entries. PS2 stores alpha 0..128, so A8 = min(255, raw*2).
            paletteLen = Math.Min(256, paletteSize / 4);
            for (int i = 0; i < paletteLen; i++)
            {
                int p = paletteOff + i * 4;
                byte b0 = data[p], b1 = data[p + 1], b2 = data[p + 2], b3 = data[p + 3];
                byte a = (byte)Math.Min(255, b3 * 2);
                if (a < 255) hasAlpha = true;
                int o = i * 4;
                palette[o] = b0;
                palette[o + 1] = b1;
                palette[o + 2] = b2;
                palette[o + 3] = a;
            }
        }
        else
        {
            // 16-bit RGBA5551 entries.
            paletteLen = Math.Min(256, paletteSize / 2);
            for (int i = 0; i < paletteLen; i++)
            {
                ushort px = U16(data, paletteOff + i * 2);
                // 5 -> 8 bits by bit replication (v << 3 | v >> 2), the rounded expansion
                // DotR-PC's tools/textures.py uses. Truncating (v * 255 / 31) was one low
                // for 12 of the 32 levels, so no decoded pixel matched the reference tools.
                static byte X5(int v) => (byte)(v << 3 | v >> 2);
                byte r = X5(px & 0x1F);
                byte g = X5((px >> 5) & 0x1F);
                byte b = X5((px >> 10) & 0x1F);
                byte a = (byte)(((px & 0x8000) != 0) ? 255 : 0);
                if (a == 0) hasAlpha = true;
                int o = i * 4;
                palette[o] = r;
                palette[o + 1] = g;
                palette[o + 2] = b;
                palette[o + 3] = a;
            }
        }

        return is4Bit
            ? DecodePsmt4(data, startOfImage, w, h, palette, paletteLen, hasAlpha)
            : DecodePsmt8(data, startOfImage, w, h, nSecs, palette, paletteLen, hasAlpha);
    }

    private static DecodedTexture DecodePsmt4(
        ReadOnlySpan<byte> data, int startOfImage, int w, int h,
        ReadOnlySpan<byte> palette, int paletteLen, bool hasAlpha)
    {
        int wActual = Math.Max(w / 2, 1);
        int hActual = Math.Max(h / 2, 1);
        var bitTbl = GsSwizzle.GetPsmt4BitIndices(wActual, hActual);

        const int PageBytes = 8192;
        Span<byte> img = stackalloc byte[PageBytes];
        int copyLen = Math.Min(PageBytes, data.Length - startOfImage);
        if (copyLen > 0)
            data.Slice(startOfImage, copyLen).CopyTo(img);

        int npix = wActual * hActual;
        var rgba = new byte[npix * 4];
        for (int i = 0; i < npix; i++)
        {
            int b0 = bitTbl[i * 4];
            int b1 = bitTbl[i * 4 + 1];
            int b2 = bitTbl[i * 4 + 2];
            int b3 = bitTbl[i * 4 + 3];
            int nib =
                ((img[b0 >> 3] >> (b0 & 7)) & 1)
                | (((img[b1 >> 3] >> (b1 & 7)) & 1) << 1)
                | (((img[b2 >> 3] >> (b2 & 7)) & 1) << 2)
                | (((img[b3 >> 3] >> (b3 & 7)) & 1) << 3);
            if (nib >= paletteLen) continue;
            int po = nib * 4;
            int o = i * 4;
            rgba[o] = palette[po];
            rgba[o + 1] = palette[po + 1];
            rgba[o + 2] = palette[po + 2];
            rgba[o + 3] = palette[po + 3];
        }
        return new DecodedTexture(wActual, hActual, rgba, hasAlpha);
    }

    static DecodedTexture DecodePsmt8(
        ReadOnlySpan<byte> data, int startOfImage, int w, int h, int nSecs,
        ReadOnlySpan<byte> palette, int paletteLen, bool hasAlpha)
    {
        Span<byte> lut = stackalloc byte[256 * 4];
        if (paletteLen > 0)
        {
            for (int p = 0; p < 256; p++)
            {
                int pt = (p % 32) / 8;
                int q = p;
                if (pt == 1) q += 8;
                else if (pt == 2) q -= 8;

                q = ((q % paletteLen) + paletteLen) % paletteLen;
                int from = q * 4;
                int to = p * 4;
                lut[to] = palette[from];
                lut[to + 1] = palette[from + 1];
                lut[to + 2] = palette[from + 2];
                lut[to + 3] = palette[from + 3];
            }
        }

        int blockWidth = Math.Min(w, 128);
        int blockHeight = Math.Min(h, 64);
        int partial = blockWidth * blockHeight;
        int colsPerRow = Math.Max(w / blockWidth, 1);
        int rowsOfSecs = Math.Max(h / blockHeight, 1);
        // Only a full 128x64 GS page carries the PSMT8 block swizzle; a narrower (or shorter)
        // texture stores each section as a plain linear blockWidth x blockHeight block
        // (retail's 64x64, 64x128 and 64x256 textures). Sections are always (partial + Pad)
        // apart, each after its own 0x60 setup block, so a 64x128 texture is two stacked
        // blocks - not w*h linear bytes, which is what this read before and which ran
        // straight through the second section's setup block.
        var posTbl = blockWidth == 128 && blockHeight == 64
            ? GsSwizzle.GetPsmt8Swizzle(blockWidth, blockHeight, w, h)
            : null;
        int rgbaLen = w * h * 4;
        var outArr = new byte[rgbaLen];
        int sectionCap = Math.Min(nSecs, colsPerRow * rowsOfSecs);

        for (int sec = 0; sec < sectionCap; sec++)
        {
            int secOff = startOfImage + (partial + Pad) * sec;
            if (secOff + partial > data.Length) break;
            int xShift = blockWidth * (sec % colsPerRow);
            int yShift = blockHeight * (sec / colsPerRow);
            int secDstShift = (yShift * w + xShift) * 4;
            for (int i = 0; i < partial; i++)
            {
                int pOff;
                if (posTbl == null) pOff = ((i / blockWidth) * w + i % blockWidth) * 4 + secDstShift;
                else
                {
                    int baseDst = posTbl[i];
                    if (baseDst < 0) continue;
                    pOff = baseDst + secDstShift;
                }
                if ((uint)pOff > (uint)(rgbaLen - 4)) continue;
                int place = data[secOff + i] * 4;
                outArr[pOff] = lut[place];
                outArr[pOff + 1] = lut[place + 1];
                outArr[pOff + 2] = lut[place + 2];
                outArr[pOff + 3] = lut[place + 3];
            }
        }
        return new DecodedTexture(w, h, outArr, hasAlpha);
    }

    static int IndexOf(ReadOnlySpan<byte> hay, ReadOnlySpan<byte> needle,
        int start, int end)
    {
        if (start >= hay.Length || start >= end) return -1;
        int limit = Math.Min(end, hay.Length);
        var sliced = hay.Slice(start, limit - start);
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
        var info = new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
        using var bmp = new SkiaSharp.SKBitmap(info);
        IntPtr dst = bmp.GetPixels();
        for (int y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(rgba, y * width * 4, dst + y * bmp.RowBytes, width * 4);
        }
        using var data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
