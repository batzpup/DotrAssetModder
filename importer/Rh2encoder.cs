using System.Buffers.Binary;
// GsSwizzle (PSMT8/PSMT4 tables) - same assembly, reused so the
// encoder can never diverge from the decoder's swizzle.

namespace DotrAssetModder;

// === Step 6.4 — RH2 texture encoder ==================================================
// Two code paths:
//
//   Encode(...)      TEMPLATE + PATCH. Keeps a donor RH2 (same slot) verbatim - header,
//                    the nSecs x 0x30 GS register packet, every GIF tag and the inter-page
//                    0x60 gaps - and overwrites only the CLUT and the swizzled image data.
//                    Validated pixel-perfect on all 10 retail textures. Used whenever the
//                    source already fits the donor's dimensions (the cheap, safe path).
//
//   BuildNative(...) FROM-SCRATCH RH2 at an arbitrary power-of-two size (Phase 1 of the
//                    slot-rebuild plan). Emits a complete PSMT8 / PSMCT16 (5551) block:
//                    0x60 header, nSecs x 0x30 register packet, CLUT setup + 256-entry
//                    palette, then per section a 0x60 setup block + 0x2000 swizzled page.
//                    The register/setup blocks are retail bytes captured VERBATIM with only
//                    the layout-dependent fields substituted (ref pointers, dimensions,
//                    section count, totalSize, TEX0 TW/TH/TBW) - substitution, not invention.
//
// How the on-disk container was verified (against blue-eyes_white_dragon.mrg):
//   * The parametric layout below regenerates ALL 11 retail RH2 blocks BYTE-FOR-BYTE
//     (256x128 -> nSecs 5, 128x64 -> nSecs 2).
//   * TEX0 TW/TH/TBW substitution reproduces both retail TEX0 values exactly and extends
//     to any POT size (TW=log2 w, TH=log2 h, TBW=w/64, TBP0=0 patched at runtime).
//   * Section count at +0x58 = 1 (CLUT) + cols*rows, matching SzText_Load_Pipe in the ELF,
//     which reads the count straight from +0x58 and advances one 8 KB GS page per section.
//   * A native build at a retail texture's native dimensions decodes back with ZERO RGB
//     and ZERO alpha-bit error (Phase 1 round-trip gate, items a + b).
//
// On-disk RH2 layout (PSMT8 + PSMCT16), all offsets from the RH2 base:
//   +0x00  0x60 header. Three (offset,size) DMA-region pairs drive the engine's copy:
//          (+0x10,+0x14)=register packet, (+0x18,+0x1c)=CLUT region, (+0x20,+0x24)=image
//          region; +0x28=totalSize; +0x30 TEX0; +0x50/52 PSM/clutPSM; +0x54/56 w/h; +0x58 nSecs.
//   +0x60  register packet: block0 = CLUT, blocks 1..N = image sections; each 0x30 bytes,
//          template-constant except +0x04 (DMA ref ADDR -> its setup block) and the +0x27
//          flag (0x02 CLUT / 0x00 image). +0x24 of each block is the GS dest, patched at
//          runtime (0 on disk).
//   then   CLUT setup block (0x60) + palette (256 x PSMCT16 = 512 B)
//   then   per image section: setup block (0x60) + 0x2000 swizzled PSMT8 page data.
//
// PSMT4 native generation and PSMCT32-CLUT native generation are intentionally NOT emitted
// here: no retail sample exercises their setup blocks, so they fall outside the verified
// "capture + substitute" guarantee. BuildNative targets the retail-matching PSMT8/5551 form.
// =====================================================================================

public static class Rh2Encoder
{
    private const int Pad = 0x60;       // inter-section / pre-palette gap, also the setup-block size
    private const int Section = 0x2000; // one 128x64 PSMT8 page = one 8 KB GS page
    private const int RegBlock = 0x30;  // one GS register block in the upload packet
    private const int HdrSize = 0x60;
    private const int ClutEntries = 256;
    private static readonly byte[] EndSig = { 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    // ---- verbatim retail templates (captured from blue-eyes_white_dragon.mrg) ---------
    // Layout-dependent fields are zeroed and substituted at build time (see BuildNative).

    // 0x60 header with computed fields (totalSize, the 4 region pairs, TEX0, w/h, nSecs)
    // zeroed. The remaining constant bytes (TEX1=6, +0x40=0x60, +0x48=0x14, PSM=0x13,
    // clutPSM=0x02) are carried verbatim.
    private static readonly byte[] HeaderSkeleton =
    {
        0x52, 0x48, 0x32, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x60, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x13, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    // CLUT register block (0x30): +0x04 ref pointer zeroed, +0x27 flag = 0x02
    private static readonly byte[] ClutRegBlock =
    {
        0x02, 0x00, 0x00, 0x50, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x11, 0x02, 0x00, 0x00, 0x51,
        0x01, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x0e, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x50, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    // Image register block (0x30): +0x04 ref pointer zeroed, +0x27 flag = 0x00
    private static readonly byte[] ImgRegBlock =
    {
        0x02, 0x00, 0x00, 0x50, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x11, 0x02, 0x00, 0x00, 0x51,
        0x01, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x0e, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x50, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    // CLUT upload setup block (0x60): GIF A+D, TRXREG 16x16 (256-entry CLUT). Constant.
    private static readonly byte[] ClutSetupBlock =
    {
        0x25, 0x00, 0x00, 0x60, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x25, 0x00, 0x00, 0x51,
        0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x0e, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x51, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x10, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x52, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x53, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x20, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    // Image-section upload setup block (0x60): GIF A+D, one 128x64 page. Constant for every
    // section; the GS destination is supplied by the register block's +0x24, patched at runtime.
    private static readonly byte[] SectionSetupBlock =
    {
        0x05, 0x02, 0x00, 0x60, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x02, 0x00, 0x51,
        0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x0e, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x51, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x40, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x52, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x53, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x82, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    // Retail PSMT8 TEX0 used as the substitution base (256x128). TBP0/CBP are 0 on disk
    // (the engine assigns and patches them per upload); we only substitute TW/TH/TBW/PSM.
    private const ulong Tex0Template = 0x20100005e1310000UL;

    // =================================================================================
    // Phase 1 — true RH2 slot rebuilder
    // =================================================================================

    /// Build a complete native PSMT8 / PSMCT16 RH2 from <paramref name="rgba"/>
    /// (<paramref name="srcW"/> x <paramref name="srcH"/>, 4 bytes/px) at a power-of-two
    /// target. <paramref name="targetW"/> must be a POT multiple of 128 (>=128);
    /// <paramref name="targetH"/> a POT multiple of 64 (>=64). Use <see cref="ChooseTarget"/>
    /// to derive a valid target from a source size.
    public static byte[] BuildNative(byte[] rgba, int srcW, int srcH, int targetW, int targetH)
    {
        ValidateTarget(targetW, targetH);

        int cols = targetW / 128;
        int rows = targetH / 64;
        int nImg = cols * rows;
        int nSecs = 1 + nImg;                       // 1 CLUT + cols*rows image sections

        int palBytes = ClutEntries * 2;             // PSMCT16: 256 entries x 2 bytes
        int regSize = nSecs * RegBlock;
        int clutSetupOff = HdrSize + regSize;
        int clutRegionSize = Pad + palBytes;        // setup block + palette
        int sec0SetupOff = clutSetupOff + clutRegionSize;
        int imageRegionSize = nImg * (Pad + Section);
        int total = sec0SetupOff + imageRegionSize;

        var o = new byte[total];
        HeaderSkeleton.CopyTo(o, 0);

        // header: substitute the layout-dependent fields (3 region pairs + total + TEX0 + dims)
        W32(o, 0x08, (uint)total);
        W32(o, 0x10, HdrSize);
        W32(o, 0x14, (uint)regSize);
        W32(o, 0x18, (uint)clutSetupOff);
        W32(o, 0x1c, (uint)clutRegionSize);
        W32(o, 0x20, (uint)sec0SetupOff);
        W32(o, 0x24, (uint)(total - sec0SetupOff));
        W32(o, 0x28, (uint)total);
        W64(o, 0x30, Tex0For(targetW, targetH));
        W16(o, 0x54, (ushort)targetW);
        W16(o, 0x56, (ushort)targetH);
        W16(o, 0x58, (ushort)nSecs);

        // register packet: CLUT block then one image block per section, each pointing at its setup
        int p = HdrSize;
        WriteRegBlock(o, ref p, ClutRegBlock, clutSetupOff);
        for (int s = 0; s < nImg; s++)
            WriteRegBlock(o, ref p, ImgRegBlock, sec0SetupOff + s * (Pad + Section));

        // CLUT region: setup block + 256-entry palette
        ClutSetupBlock.CopyTo(o, clutSetupOff);
        int paletteOff = clutSetupOff + Pad;

        byte[] img = Resample(rgba, srcW, srcH, targetW, targetH);
        var (palette, indices) = Quantize(img, targetW * targetH, ClutEntries);
        WritePalette(o, paletteOff, ClutEntries, clut32: false, palette);

        // image region: per-section setup block + swizzled page data
        for (int s = 0; s < nImg; s++)
            SectionSetupBlock.CopyTo(o, sec0SetupOff + s * (Pad + Section));

        int startOfImage = sec0SetupOff + Pad;      // first section's data (after its setup block)
        WriteImagePsmt8(o, startOfImage, targetW, targetH, ClutEntries, indices);

        return o;
    }

    /// Pick a POT target for a source image: next-POT of each axis, clamped to the section
    /// grid minimum (128 x 64) and to <paramref name="maxDim"/> (default 512; 1024 opt-in).
    /// <paramref name="maxDim"/> should itself be a power of two.
    public static (int W, int H) ChooseTarget(int srcW, int srcH, int maxDim = 512)
    {
        int w = Math.Min(Math.Max(NextPot(srcW), 128), maxDim);
        int h = Math.Min(Math.Max(NextPot(srcH), 64), maxDim);
        return (w, h);
    }

    public static int NextPot(int v)
    {
        if (v < 1) return 1;
        int p = 1;
        while (p < v) p <<= 1;
        return p;
    }

    /// GS TEX0 for a POT target: substitute TW=log2(w), TH=log2(h), TBW=w/64, PSM=PSMT8 into
    /// the retail template; TBP0 stays 0 (the engine patches it, and CBP, at upload time).
    private static ulong Tex0For(int w, int h)
    {
        const ulong mask = (0x3fUL << 14)   // TBW
                         | (0x3fUL << 20)   // PSM
                         | (0xfUL << 26)    // TW
                         | (0xfUL << 30)    // TH
                         | 0x3fffUL;        // TBP0
        ulong t = Tex0Template & ~mask;
        t |= ((ulong)(w / 64) << 14)
           | (0x13UL << 20)                 // PSMT8
           | ((ulong)Log2(w) << 26)
           | ((ulong)Log2(h) << 30);
        return t;
    }

    private static void ValidateTarget(int w, int h)
    {
        if (w < 128 || h < 64 || !IsPow2(w) || !IsPow2(h))
            throw new ArgumentException(
                $"BuildNative target must be power-of-two with w>=128, h>=64 (got {w}x{h}). " +
                "Use ChooseTarget to derive a valid size.");
    }

    private static bool IsPow2(int v) => v > 0 && (v & (v - 1)) == 0;

    private static int Log2(int v)
    {
        int r = 0;
        while ((1 << r) < v) r++;
        return r;
    }

    // Copy a 0x30 register-block template and patch its +0x04 DMA ref pointer to a setup block.
    private static void WriteRegBlock(byte[] o, ref int p, byte[] template, int refOff)
    {
        template.CopyTo(o, p);
        W32(o, p + 4, (uint)refOff);
        p += template.Length;
    }

    // =================================================================================
    // Template + patch encoder (unchanged validated path)
    // =================================================================================

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

        // resample source -> decoded dims
        byte[] img = Resample(rgba, srcW, srcH, decW, decH);

        // An unedited image decodes to exactly the donor's pixels. The quantiser would
        // renumber the palette and reassign every index without changing a pixel, so an
        // untouched round trip would differ from retail byte for byte; keep the donor's
        // CLUT order and indices by returning its bytes verbatim instead.
        if (DecodesTo(templateRh2, img, decW, decH))
            return templateRh2.Slice(0, (int)totalSize).ToArray();

        var (palette, indices) = Quantize(img, decW * decH, paletteLen);

        var outBuf = new byte[totalSize];
        templateRh2.Slice(0, (int)totalSize).CopyTo(outBuf);

        WritePalette(outBuf, paletteOff, paletteLen, clut32, palette);

        if (is4) WriteImagePsmt4(outBuf, startOfImage, w, h, decW, decH, indices);
        else WriteImagePsmt8(outBuf, startOfImage, w, h, paletteLen, indices);

        return outBuf;
    }

    /// True when <paramref name="img"/> (w x h RGBA) is pixel-for-pixel what the donor RH2
    /// decodes to, i.e. the image came out of this template and was never edited.
    private static bool DecodesTo(ReadOnlySpan<byte> templateRh2, byte[] img, int w, int h)
    {
        try
        {
            var dec = Rh2Decoder.Decode(templateRh2, 0);
            return dec.Width == w && dec.Height == h && dec.Rgba.AsSpan().SequenceEqual(img);
        }
        catch
        {
            return false; // a template the decoder cannot read goes down the quantiser path
        }
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

        int bw = Math.Min(w, 128), bh = Math.Min(h, 64), partial = bw * bh;
        int cols = Math.Max(w / bw, 1), rows = Math.Max(h / bh, 1);
        // Only a full 128x64 GS page carries the PSMT8 block swizzle. A narrower (or
        // shorter) texture stores each section as a plain linear bw x bh block - retail's
        // 64x64, 64x128 and 64x256 textures are all like that. Either way the sections are
        // (partial + Pad) apart, because each one follows its own 0x60 setup block: writing
        // w*h bytes straight from startOfImage (what this did for w < 128) overwrote the
        // second and later setup blocks.
        int[] pos = w >= 128 && h >= 64
            ? GsSwizzle.GetPsmt8Swizzle(bw, bh, w, h) // entries = (y*w+x)*4 or -1
            : null!;
        for (int sec = 0; sec < cols * rows; sec++)
        {
            int secOff = startOfImage + (partial + Pad) * sec;
            int xs = bw * (sec % cols), ys = bh * (sec / cols);
            for (int i = 0; i < partial; i++)
            {
                int pix;
                if (pos == null) pix = (ys + i / bw) * w + xs + i % bw;
                else
                {
                    int baseDst = pos[i];
                    if (baseDst < 0) continue;
                    pix = baseDst / 4 + ys * w + xs;
                }
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

    // Bilinear resample (replaces the previous nearest sampler). Smooth on both up- and
    // down-scales. Identity when dimensions already match, so same-size re-encodes (the
    // validated round-trip path) are untouched. All four channels are filtered; a hard
    // 1-bit-alpha cutout may soften by one pixel at edges when actually rescaled, which is
    // inherent to interpolation and accepted per the slot-rebuild plan.
    private static byte[] Resample(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (sw == dw && sh == dh) return src;
        var dst = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        {
            double fy = dh <= 1 ? 0 : (y + 0.5) * sh / (double)dh - 0.5;
            int y0 = (int)Math.Floor(fy);
            double wy = fy - y0;
            int y0c = Math.Clamp(y0, 0, sh - 1);
            int y1c = Math.Clamp(y0 + 1, 0, sh - 1);
            for (int x = 0; x < dw; x++)
            {
                double fx = dw <= 1 ? 0 : (x + 0.5) * sw / (double)dw - 0.5;
                int x0 = (int)Math.Floor(fx);
                double wx = fx - x0;
                int x0c = Math.Clamp(x0, 0, sw - 1);
                int x1c = Math.Clamp(x0 + 1, 0, sw - 1);

                int i00 = (y0c * sw + x0c) * 4;
                int i01 = (y0c * sw + x1c) * 4;
                int i10 = (y1c * sw + x0c) * 4;
                int i11 = (y1c * sw + x1c) * 4;
                int d = (y * dw + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    double top = src[i00 + c] * (1 - wx) + src[i01 + c] * wx;
                    double bot = src[i10 + c] * (1 - wx) + src[i11 + c] * wx;
                    dst[d + c] = (byte)Math.Clamp((int)Math.Round(top * (1 - wy) + bot * wy), 0, 255);
                }
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

    private static void W16(byte[] d, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(o), v);
    private static void W32(byte[] d, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), v);
    private static void W64(byte[] d, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(o), v);
}