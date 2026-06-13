using System.Buffers.Binary;
// GsSwizzle (PSMT8/PSMT4 tables) - same assembly, reused so the
// encoder can never diverge from the decoder's swizzle.

namespace DotrAssetModder;

// === Step 6.4 — RH2 texture encoder ==================================================
// Re-encodes an edited RGBA image back into an RH2 block by TEMPLATE + PATCH: the
// original RH2 (same texture slot) is kept verbatim - header, the nSecs x 0x30 GS
// register packet, every GIF tag and the inter-page 0x60 gaps - and only two payload
// regions are overwritten: the CLUT (palette) bytes and the swizzled image page data.
// This matches how the game itself treats the block (SzText_Load_Pipe copies the packet
// and only patches per-section dest GS addresses at +0x24), so a same-dimension re-encode
// stays valid without reconstructing any DMA/GIF plumbing.
//
// PSMT8 path validated by a pixel-perfect round-trip on all 10 textures across
// timeater.mrg + blue-eyes_white_dragon.mrg (256x256/256x128/128x256/128x64). PSMT4 is
// the analytic inverse of Rh2Decoder.DecodePsmt4 but UNTESTED (no PSMT4 sample shipped).
//
// Output keeps the template's dimensions / PSM / CLUT format. A differently-sized source
// image is resampled (nearest) to the template's decoded dimensions.
// =====================================================================================

public static class Rh2Encoder
{
    private const int Pad = 0x60;
    private static readonly byte[] EndSig = { 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// Encode <paramref name="rgba"/> (srcW x srcH, 4 bytes/px) into a copy of <paramref name="templateRh2"/>.
    public static byte[] Encode(ReadOnlySpan<byte> templateRh2, byte[] rgba, int srcW, int srcH)
    {
        uint totalSize = U32(templateRh2, 8);
        int psm = U16(templateRh2, 0x50); // 0x13 PSMT8, 0x14 PSMT4
        int clutPsm = U16(templateRh2, 0x52); // 0x00 RGBA32, 0x02/0x0A RGBA5551
        int w = U16(templateRh2, 0x54);
        int h = U16(templateRh2, 0x56);
        bool is4 = psm == 0x14;

        // decoded image dimensions (PSMT4 decodes to half size)
        int decW = is4 ? Math.Max(w / 2, 1) : w;
        int decH = is4 ? Math.Max(h / 2, 1) : h;

        // locate palette + image inside the template (same scan the decoder uses)
        int end = (int)Math.Min(totalSize, (uint)templateRh2.Length);
        int p1 = IndexOf(templateRh2, EndSig, 0, end);
        if (p1 < 0) throw new InvalidOperationException("RH2 template: end-of-transfer marker not found");
        int paletteOff = p1 + EndSig.Length;
        int p2 = IndexOf(templateRh2, EndSig, paletteOff, end);
        if (p2 < 0) throw new InvalidOperationException("RH2 template: second marker not found");
        int paletteSize = (p2 + EndSig.Length) - paletteOff - Pad;
        int startOfImage = paletteOff + paletteSize + Pad;

        int maxColors = is4 ? 16 : 256;
        bool clut32 = clutPsm == 0x00;
        int paletteLen = clut32 ? Math.Min(maxColors, paletteSize / 4) : Math.Min(maxColors, paletteSize / 2);

        // resample source -> decoded dims, then quantise
        byte[] img = Resample(rgba, srcW, srcH, decW, decH);
        var (palette, indices) = Quantize(img, decW * decH, paletteLen);

        var outBuf = new byte[totalSize];
        templateRh2.Slice(0, (int)totalSize).CopyTo(outBuf);

        WritePalette(outBuf, paletteOff, paletteLen, clut32, palette);

        if (is4) WriteImagePsmt4(outBuf, startOfImage, w, h, decW, decH, indices);
        else WriteImagePsmt8(outBuf, startOfImage, w, h, paletteLen, indices);

        return outBuf;
    }

    // ---- palette ---------------------------------------------------------------------
    private static void WritePalette(byte[] o, int off, int len, bool clut32, (byte R, byte G, byte B, byte A)[] pal)
    {
        for (int i = 0; i < len; i++)
        {
            var c = i < pal.Length ? pal[i] : default;
            if (clut32)
            {
                int p = off + i * 4;
                o[p] = c.R;
                o[p + 1] = c.G;
                o[p + 2] = c.B;
                o[p + 3] = (byte)Math.Clamp((c.A + 1) / 2, 0, 128); // A8 -> 0..128 (decoder does A8=min(255,raw*2))
            }
            else
            {
                int r = (c.R * 31 + 127) / 255, g = (c.G * 31 + 127) / 255, b = (c.B * 31 + 127) / 255;
                ushort px = (ushort)(r | (g << 5) | (b << 10) | (c.A >= 128 ? 0x8000 : 0));
                BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(off + i * 2), px);
            }
        }
    }

    // ---- PSMT8 image (inverse of DecodePsmt8) ----------------------------------------
    private static void WriteImagePsmt8(byte[] o, int startOfImage, int w, int h, int paletteLen, int[] idx)
    {
        // CLUT pt-remap is an involution: stored byte = q(paletteIndex)
        int Q(int p)
        {
            int pt = (p % 32) / 8;
            int q = p + (pt == 1 ? 8 : pt == 2 ? -8 : 0);
            return ((q % paletteLen) + paletteLen) % paletteLen;
        }

        if (w < 128 || h < 64)
        {
            int n = w * h;
            for (int i = 0; i < n; i++) o[startOfImage + i] = (byte)Q(idx[i]);
            return;
        }

        int bw = Math.Min(w, 128), bh = Math.Min(h, 64), partial = bw * bh;
        int cols = Math.Max(w / bw, 1), rows = Math.Max(h / bh, 1);
        int[] pos = GsSwizzle.GetPsmt8Swizzle(bw, bh, w, h); // entries = (y*w+x)*4 or -1
        for (int sec = 0; sec < cols * rows; sec++)
        {
            int secOff = startOfImage + (partial + Pad) * sec;
            int xs = bw * (sec % cols), ys = bh * (sec / cols);
            for (int i = 0; i < partial; i++)
            {
                int baseDst = pos[i];
                if (baseDst < 0) continue;
                int pix = baseDst / 4 + ys * w + xs;
                if ((uint)pix < (uint)(w * h)) o[secOff + i] = (byte)Q(idx[pix]);
            }
        }
    }

    // ---- PSMT4 image (inverse of DecodePsmt4) — UNTESTED, no PSMT4 sample available ---
    private static void WriteImagePsmt4(byte[] o, int startOfImage, int w, int h, int decW, int decH, int[] idx)
    {
        int[] bitTbl = GsSwizzle.GetPsmt4BitIndices(decW, decH); // 4 bit-positions per pixel
        int npix = decW * decH;
        int maxByte = startOfImage;
        for (int i = 0; i < npix; i++)
        for (int k = 0; k < 4; k++)
            maxByte = Math.Max(maxByte, startOfImage + (bitTbl[i * 4 + k] >> 3));
        for (int b = startOfImage; b <= maxByte && b < o.Length; b++) o[b] = 0; // clear page region, then set bits
        for (int i = 0; i < npix; i++)
        {
            int nib = idx[i] & 0xF;
            for (int k = 0; k < 4; k++)
            {
                if (((nib >> k) & 1) == 0) continue;
                int pos = bitTbl[i * 4 + k];
                o[startOfImage + (pos >> 3)] |= (byte)(1 << (pos & 7));
            }
        }
    }

    // ---- quantiser: exact for <=maxColors unique colours, else median-cut -------------
    private static ((byte R, byte G, byte B, byte A)[] pal, int[] idx) Quantize(byte[] rgba, int n, int maxColors)
    {
        if (maxColors < 1) maxColors = 1;
        var uniq = new Dictionary<int, int>();
        var idx = new int[n];
        bool overflow = false;
        for (int i = 0; i < n; i++)
        {
            int key = rgba[i * 4] | (rgba[i * 4 + 1] << 8) | (rgba[i * 4 + 2] << 16) | (rgba[i * 4 + 3] << 24);
            if (!uniq.TryGetValue(key, out int ix))
            {
                if (uniq.Count < maxColors)
                {
                    ix = uniq.Count;
                    uniq[key] = ix;
                }
                else
                {
                    overflow = true;
                    break;
                }
            }
            idx[i] = ix;
        }
        if (!overflow)
        {
            var pal = new (byte, byte, byte, byte)[Math.Max(uniq.Count, 1)];
            foreach (var kv in uniq)
                pal[kv.Value] = ((byte)(kv.Key & 0xFF), (byte)((kv.Key >> 8) & 0xFF), (byte)((kv.Key >> 16) & 0xFF), (byte)((kv.Key >> 24) & 0xFF));
            return (pal, idx);
        }
        return MedianCut(rgba, n, maxColors);
    }

    private static ((byte R, byte G, byte B, byte A)[] pal, int[] idx) MedianCut(byte[] rgba, int n, int maxColors)
    {
        var buckets = new List<List<int>> { new List<int>(n) };
        for (int i = 0; i < n; i++) buckets[0].Add(i);

        while (buckets.Count < maxColors)
        {
            int bi = -1, bestRange = -1, bestCh = 0;
            for (int b = 0; b < buckets.Count; b++)
            {
                if (buckets[b].Count < 2) continue;
                for (int ch = 0; ch < 4; ch++)
                {
                    int mn = 255, mx = 0;
                    foreach (int pix in buckets[b])
                    {
                        int v = rgba[pix * 4 + ch];
                        if (v < mn) mn = v;
                        if (v > mx) mx = v;
                    }
                    int range = mx - mn;
                    if (range > bestRange)
                    {
                        bestRange = range;
                        bi = b;
                        bestCh = ch;
                    }
                }
            }
            if (bi < 0) break;
            var src = buckets[bi];
            src.Sort((x, y) => rgba[x * 4 + bestCh].CompareTo(rgba[y * 4 + bestCh]));
            int mid = src.Count / 2;
            buckets[bi] = src.GetRange(0, mid);
            buckets.Add(src.GetRange(mid, src.Count - mid));
        }

        var pal = new (byte R, byte G, byte B, byte A)[buckets.Count];
        for (int b = 0; b < buckets.Count; b++)
        {
            long r = 0, g = 0, bl = 0, a = 0;
            int c = Math.Max(buckets[b].Count, 1);
            foreach (int pix in buckets[b])
            {
                r += rgba[pix * 4];
                g += rgba[pix * 4 + 1];
                bl += rgba[pix * 4 + 2];
                a += rgba[pix * 4 + 3];
            }
            pal[b] = ((byte)(r / c), (byte)(g / c), (byte)(bl / c), (byte)(a / c));
        }

        var idx = new int[n];
        for (int i = 0; i < n; i++)
        {
            int br = rgba[i * 4], bg = rgba[i * 4 + 1], bb = rgba[i * 4 + 2], ba = rgba[i * 4 + 3];
            int best = 0;
            long bestD = long.MaxValue;
            for (int p = 0; p < pal.Length; p++)
            {
                long dr = br - pal[p].R, dg = bg - pal[p].G, db = bb - pal[p].B, da = ba - pal[p].A;
                long d = dr * dr + dg * dg + db * db + da * da;
                if (d < bestD)
                {
                    bestD = d;
                    best = p;
                }
            }
            idx[i] = best;
        }
        return (pal, idx);
    }

    // ---- helpers ----------------------------------------------------------------------
    private static byte[] Resample(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (sw == dw && sh == dh) return src;
        var dst = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        {
            int sy = sh <= 1 ? 0 : Math.Min(sh - 1, y * sh / dh);
            for (int x = 0; x < dw; x++)
            {
                int sx = sw <= 1 ? 0 : Math.Min(sw - 1, x * sw / dw);
                int s = (sy * sw + sx) * 4, d = (y * dw + x) * 4;
                dst[d] = src[s];
                dst[d + 1] = src[s + 1];
                dst[d + 2] = src[s + 2];
                dst[d + 3] = src[s + 3];
            }
        }
        return dst;
    }

    private static int IndexOf(ReadOnlySpan<byte> hay, ReadOnlySpan<byte> needle, int start, int end)
    {
        int limit = Math.Min(end, hay.Length);
        if (start >= limit) return -1;
        int f = hay.Slice(start, limit - start).IndexOf(needle);
        return f < 0 ? -1 : start + f;
    }

    private static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(o));
    private static int U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(o));
}