using static DotrAssetModder.Bin;
// GsSwizzle (PSMT8/PSMT4 tables) is shared with the decoder so the encoder cannot diverge from it.

namespace DotrAssetModder;

/* RH2 texture encoder. Two code paths:

   Encode(...)      TEMPLATE + PATCH. Keeps a donor RH2 (same slot) verbatim - header, the
                    nSecs x 0x30 GS register packet, every GIF tag and the inter-page 0x60
                    gaps - and overwrites only the CLUT and the swizzled image data.
                    Validated pixel-perfect on all 10 retail textures. Used whenever the
                    source already fits the donor's dimensions.

   BuildNative(...) FROM-SCRATCH RH2 at an arbitrary power-of-two size. Emits a complete
                    PSMT8 / PSMCT16 (5551) block: 0x60 header, nSecs x 0x30 register packet,
                    CLUT setup + 256-entry palette, then per section a 0x60 setup block +
                    0x2000 swizzled page. The register/setup blocks are retail bytes captured
                    verbatim with only the layout-dependent fields substituted (ref pointers,
                    dimensions, section count, totalSize, TEX0 TW/TH/TBW).

   Verified against blue-eyes_white_dragon.mrg:
   * The parametric layout regenerates all 11 retail RH2 blocks byte for byte
     (256x128 -> nSecs 5, 128x64 -> nSecs 2).
   * TEX0 TW/TH/TBW substitution reproduces both retail TEX0 values and extends to any POT
     size (TW=log2 w, TH=log2 h, TBW=w/64, TBP0=0 patched at runtime).
   * Section count at +0x58 = 1 (CLUT) + cols*rows, matching SzText_Load_Pipe in the ELF,
     which reads the count straight from +0x58 and advances one 8 KB GS page per section.
   * A native build at a retail texture's native dimensions decodes back with zero RGB and
     zero alpha-bit error.

   On-disk RH2 layout (PSMT8 + PSMCT16), all offsets from the RH2 base:
   +0x00  0x60 header. Three (offset,size) DMA-region pairs drive the engine's copy:
          (+0x10,+0x14)=register packet, (+0x18,+0x1c)=CLUT region, (+0x20,+0x24)=image
          region; +0x08 and +0x28=totalSize; +0x30 TEX0; +0x50/52 PSM/clutPSM; +0x54/56 w/h;
          +0x58 nSecs.
   +0x60  register packet: block0 = CLUT, blocks 1..N = image sections; each 0x30 bytes,
          template-constant except +0x04 (DMA ref ADDR -> its setup block) and the +0x27
          flag (0x02 CLUT / 0x00 image). +0x24 of each block is the GS dest, patched at
          runtime (0 on disk).
   then   CLUT setup block (0x60) + palette (256 x PSMCT16 = 512 B)
   then   per image section: setup block (0x60) + 0x2000 swizzled PSMT8 page data.

   PSMT4 and PSMCT32-CLUT native generation are not emitted: no retail sample exercises
   their setup blocks, so they fall outside the "capture + substitute" guarantee. */

public static class Rh2Encoder
{
    private const int GapSize = 0x60;       // inter-section / pre-palette gap, also the setup-block size
    private const int SectionPageSize = 0x2000; // one 128x64 PSMT8 page = one 8 KB GS page
    private const int RegisterBlockSize = 0x30;  // one GS register block in the upload packet
    private const int HeaderSize = 0x60;
    private const int ClutEntries = 256;
    private static readonly byte[] EndSignature = { 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

    // Verbatim retail templates (blue-eyes_white_dragon.mrg); layout-dependent fields are zeroed
    // and substituted by BuildNative.

    // 0x60 header with computed fields (totalSize, the 3 region pairs, TEX0, w/h, nSecs) zeroed.
    // Constant bytes carried verbatim: TEX1=6, +0x40=0x60, +0x48=0x14, PSM=0x13, clutPSM=0x02.
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
    private static readonly byte[] ClutRegisterBlock =
    {
        0x02, 0x00, 0x00, 0x50, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x11, 0x02, 0x00, 0x00, 0x51,
        0x01, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x0e, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x50, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    // Image register block (0x30): +0x04 ref pointer zeroed, +0x27 flag = 0x00
    private static readonly byte[] ImageRegisterBlock =
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
    // section; the GS destination comes from the register block's +0x24 (patched at runtime).
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
    // (the engine patches them per upload); only TW/TH/TBW/PSM are substituted.
    private const ulong Tex0Template = 0x20100005e1310000UL;

    /// Build a complete native PSMT8 / PSMCT16 RH2 from <paramref name="rgba"/>
    /// (<paramref name="sourceWidth"/> x <paramref name="sourceHeight"/>, 4 bytes/px) at a power-of-two
    /// target. <paramref name="targetWidth"/> must be a POT multiple of 128 (>=128);
    /// <paramref name="targetHeight"/> a POT multiple of 64 (>=64). Use <see cref="ChooseTarget"/>
    /// to derive a valid target from a source size.
    public static byte[] BuildNative(byte[] rgba, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        ValidateTarget(targetWidth, targetHeight);

        int cols = targetWidth / 128;
        int rows = targetHeight / 64;
        int imageSectionCount = cols * rows;
        int sectionCount = 1 + imageSectionCount;

        int paletteByteCount = ClutEntries * 2;             // PSMCT16: 256 entries x 2 bytes
        int registerPacketSize = sectionCount * RegisterBlockSize;
        int clutSetupOffset = HeaderSize + registerPacketSize;
        int clutRegionSize = GapSize + paletteByteCount;        // setup block + palette
        int firstSectionSetupOffset = clutSetupOffset + clutRegionSize;
        int imageRegionSize = imageSectionCount * (GapSize + SectionPageSize);
        int totalSize = firstSectionSetupOffset + imageRegionSize;

        var encodedBytes = new byte[totalSize];
        HeaderSkeleton.CopyTo(encodedBytes, 0);

        WriteU32(encodedBytes, 0x08, (uint)totalSize);
        WriteU32(encodedBytes, 0x10, HeaderSize);
        WriteU32(encodedBytes, 0x14, (uint)registerPacketSize);
        WriteU32(encodedBytes, 0x18, (uint)clutSetupOffset);
        WriteU32(encodedBytes, 0x1c, (uint)clutRegionSize);
        WriteU32(encodedBytes, 0x20, (uint)firstSectionSetupOffset);
        WriteU32(encodedBytes, 0x24, (uint)(totalSize - firstSectionSetupOffset));
        WriteU32(encodedBytes, 0x28, (uint)totalSize);
        WriteU64(encodedBytes, 0x30, Tex0For(targetWidth, targetHeight));
        WriteU16(encodedBytes, 0x54, (ushort)targetWidth);
        WriteU16(encodedBytes, 0x56, (ushort)targetHeight);
        WriteU16(encodedBytes, 0x58, (ushort)sectionCount);

        // register packet: CLUT block, then one image block per section, each pointing at its setup block
        int packetOffset = HeaderSize;
        WriteRegBlock(encodedBytes, ref packetOffset, ClutRegisterBlock, clutSetupOffset);
        for (int sectionIndex = 0; sectionIndex < imageSectionCount; sectionIndex++)
            WriteRegBlock(encodedBytes, ref packetOffset, ImageRegisterBlock, firstSectionSetupOffset + sectionIndex * (GapSize + SectionPageSize));

        ClutSetupBlock.CopyTo(encodedBytes, clutSetupOffset);
        int paletteOffset = clutSetupOffset + GapSize;

        byte[] resampledRgba = Resample(rgba, sourceWidth, sourceHeight, targetWidth, targetHeight);
        var (palette, indices) = Quantize(resampledRgba, targetWidth * targetHeight, ClutEntries);
        WritePalette(encodedBytes, paletteOffset, ClutEntries, clut32: false, palette);

        for (int sectionIndex = 0; sectionIndex < imageSectionCount; sectionIndex++)
            SectionSetupBlock.CopyTo(encodedBytes, firstSectionSetupOffset + sectionIndex * (GapSize + SectionPageSize));

        int startOfImage = firstSectionSetupOffset + GapSize;
        WriteImagePsmt8(encodedBytes, startOfImage, targetWidth, targetHeight, ClutEntries, indices);

        return encodedBytes;
    }

    /// Pick a POT target for a source image: next-POT of each axis, clamped to the section
    /// grid minimum (128 x 64) and to <paramref name="maximumDimension"/> (default 512; 1024 opt-in).
    /// <paramref name="maximumDimension"/> should itself be a power of two.
    public static (int Width, int Height) ChooseTarget(int sourceWidth, int sourceHeight, int maximumDimension = 512)
    {
        int width = Math.Min(Math.Max(NextPot(sourceWidth), 128), maximumDimension);
        int height = Math.Min(Math.Max(NextPot(sourceHeight), 64), maximumDimension);
        return (width, height);
    }

    public static int NextPot(int value)
    {
        if (value < 1) return 1;
        int power = 1;
        while (power < value) power <<= 1;
        return power;
    }

    /// GS TEX0 for a POT target: substitute TW=log2(w), TH=log2(h), TBW=w/64, PSM=PSMT8 into
    /// the retail template; TBP0 stays 0 (the engine patches it, and CBP, at upload time).
    private static ulong Tex0For(int width, int height)
    {
        const ulong mask = (0x3fUL << 14)   // TBW
                         | (0x3fUL << 20)   // PSM
                         | (0xfUL << 26)    // TW
                         | (0xfUL << 30)    // TH
                         | 0x3fffUL;        // TBP0
        ulong tex0 = Tex0Template & ~mask;
        tex0 |= ((ulong)(width / 64) << 14)
           | (0x13UL << 20)                 // PSMT8
           | ((ulong)Log2(width) << 26)
           | ((ulong)Log2(height) << 30);
        return tex0;
    }

    private static void ValidateTarget(int targetWidth, int targetHeight)
    {
        if (targetWidth < 128 || targetHeight < 64 || !IsPow2(targetWidth) || !IsPow2(targetHeight))
            throw new ArgumentException(
                $"BuildNative target must be power-of-two with w>=128, h>=64 (got {targetWidth}x{targetHeight}). " +
                "Use ChooseTarget to derive a valid size.");
    }

    private static bool IsPow2(int value) => value > 0 && (value & (value - 1)) == 0;

    private static int Log2(int value)
    {
        int exponent = 0;
        while ((1 << exponent) < value) exponent++;
        return exponent;
    }

    // Copy a 0x30 register-block template and patch its +0x04 DMA ref pointer to a setup block.
    private static void WriteRegBlock(byte[] destinationBytes, ref int writeOffset, byte[] template, int referenceOffset)
    {
        template.CopyTo(destinationBytes, writeOffset);
        WriteU32(destinationBytes, writeOffset + 4, (uint)referenceOffset);
        writeOffset += template.Length;
    }

    /// Encode <paramref name="rgba"/> (srcW x srcH, 4 bytes/px) into a copy of <paramref name="templateRh2"/>.
    /// <paramref name="alphaTreatment"/> must be the treatment the image was decoded with, so an
    /// unedited image is recognised as unedited (see DecodesTo).
    public static byte[] Encode(ReadOnlySpan<byte> templateRh2, byte[] rgba, int sourceWidth, int sourceHeight,
        Rh2Decoder.AlphaTreatment alphaTreatment = Rh2Decoder.AlphaTreatment.Keep)
    {
        uint totalSize = ReadU32(templateRh2, 8);
        int psm = ReadU16(templateRh2, 0x50); // 0x13 PSMT8, 0x14 PSMT4
        int clutPsm = ReadU16(templateRh2, 0x52); // 0x00 RGBA32, 0x02/0x0A RGBA5551
        int width = ReadU16(templateRh2, 0x54);
        int height = ReadU16(templateRh2, 0x56);
        bool isPsmt4 = psm == 0x14;

        // PSMT4 decodes to half size
        int decodedWidth = isPsmt4 ? Math.Max(width / 2, 1) : width;
        int decodedHeight = isPsmt4 ? Math.Max(height / 2, 1) : height;

        // palette + image location: same end-marker scan as the decoder
        int searchEnd = (int)Math.Min(totalSize, (uint)templateRh2.Length);
        int firstMarkerOffset = IndexOf(templateRh2, EndSignature, 0, searchEnd);
        if (firstMarkerOffset < 0) throw new InvalidOperationException("RH2 template: end-of-transfer marker not found");
        int paletteOffset = firstMarkerOffset + EndSignature.Length;
        int secondMarkerOffset = IndexOf(templateRh2, EndSignature, paletteOffset, searchEnd);
        if (secondMarkerOffset < 0) throw new InvalidOperationException("RH2 template: second marker not found");
        int paletteSize = (secondMarkerOffset + EndSignature.Length) - paletteOffset - GapSize;
        int startOfImage = paletteOffset + paletteSize + GapSize;

        int maxColors = isPsmt4 ? 16 : 256;
        bool clut32 = clutPsm == 0x00;
        int paletteLength = clut32 ? Math.Min(maxColors, paletteSize / 4) : Math.Min(maxColors, paletteSize / 2);

        byte[] resampledRgba = Resample(rgba, sourceWidth, sourceHeight, decodedWidth, decodedHeight);

        /* An unedited image decodes to exactly the donor's pixels. The quantiser would renumber
           the palette and reassign every index without changing a pixel, so an untouched round
           trip would differ from retail byte for byte; return the donor's bytes verbatim to
           keep its CLUT order and indices. */
        if (DecodesTo(templateRh2, resampledRgba, decodedWidth, decodedHeight, alphaTreatment))
            return templateRh2.Slice(0, (int)totalSize).ToArray();

        var (palette, indices) = Quantize(resampledRgba, decodedWidth * decodedHeight, paletteLength);

        var encodedBytes = new byte[totalSize];
        templateRh2.Slice(0, (int)totalSize).CopyTo(encodedBytes);

        WritePalette(encodedBytes, paletteOffset, paletteLength, clut32, palette);

        if (isPsmt4) WriteImagePsmt4(encodedBytes, startOfImage, decodedWidth, decodedHeight, indices);
        else WriteImagePsmt8(encodedBytes, startOfImage, width, height, paletteLength, indices);

        return encodedBytes;
    }

    /// True when <paramref name="rgba"/> (w x h RGBA) is pixel-for-pixel what the donor RH2
    /// decodes to, i.e. the image came out of this template and was never edited.
    /// Must use the alpha treatment the caller decoded with (the texture editor forces alpha
    /// opaque on the MRGs the game draws opaque), or an untouched image looks edited and gets
    /// re-quantised, renumbering the CLUT and every index.
    private static bool DecodesTo(ReadOnlySpan<byte> templateRh2, byte[] rgba, int width, int height,
        Rh2Decoder.AlphaTreatment alphaTreatment)
    {
        try
        {
            var decoded = Rh2Decoder.Decode(templateRh2, 0, alphaTreatment);
            return decoded.Width == width && decoded.Height == height && decoded.Rgba.AsSpan().SequenceEqual(rgba);
        }
        catch
        {
            return false; // a template the decoder cannot read goes down the quantiser path
        }
    }

    // ---- palette ---------------------------------------------------------------------
    private static void WritePalette(byte[] destinationBytes, int paletteOffset, int entryCount, bool clut32, (byte R, byte G, byte B, byte A)[] palette)
    {
        for (int i = 0; i < entryCount; i++)
        {
            var color = i < palette.Length ? palette[i] : default;
            if (clut32)
            {
                int entryOffset = paletteOffset + i * 4;
                destinationBytes[entryOffset] = color.R;
                destinationBytes[entryOffset + 1] = color.G;
                destinationBytes[entryOffset + 2] = color.B;
                destinationBytes[entryOffset + 3] = (byte)Math.Clamp((color.A + 1) / 2, 0, 128); // A8 -> 0..128 (decoder does A8=min(255,raw*2))
            }
            else
            {
                int red = (color.R * 31 + 127) / 255, green = (color.G * 31 + 127) / 255, blue = (color.B * 31 + 127) / 255;
                ushort packedPixel = (ushort)(red | (green << 5) | (blue << 10) | (color.A >= 128 ? 0x8000 : 0));
                WriteU16(destinationBytes, paletteOffset + i * 2, packedPixel);
            }
        }
    }

    // Inverse of DecodePsmt8.
    private static void WriteImagePsmt8(byte[] destinationBytes, int startOfImage, int width, int height, int paletteLength, int[] paletteIndices)
    {
        // CLUT index remap (swap 8-index groups 1 and 2 of each 32) is an involution: stored byte = q(paletteIndex)
        int RemapClutIndex(int paletteIndex)
        {
            int remapGroup = (paletteIndex % 32) / 8;
            int remappedIndex = paletteIndex + (remapGroup == 1 ? 8 : remapGroup == 2 ? -8 : 0);
            return ((remappedIndex % paletteLength) + paletteLength) % paletteLength;
        }

        int blockWidth = Math.Min(width, 128), blockHeight = Math.Min(height, 64), pagePixelCount = blockWidth * blockHeight;
        int cols = Math.Max(width / blockWidth, 1), rows = Math.Max(height / blockHeight, 1);
        /* Only a full 128x64 GS page carries the PSMT8 block swizzle. A narrower (or shorter)
           texture stores each section as a plain linear bw x bh block - retail's 64x64, 64x128
           and 64x256 textures are all like that. Either way the sections are
           (pagePixelCount + GapSize) apart, because each follows its own 0x60 setup block;
           writing w*h bytes straight from startOfImage would overwrite the later setup blocks. */
        int[] swizzleOffsets = width >= 128 && height >= 64
            ? GsSwizzle.GetPsmt8Swizzle(blockWidth, blockHeight, width, height) // entries = (y*w+x)*4 or -1
            : null!;
        for (int sectionIndex = 0; sectionIndex < cols * rows; sectionIndex++)
        {
            int sectionOffset = startOfImage + (pagePixelCount + GapSize) * sectionIndex;
            int sectionX = blockWidth * (sectionIndex % cols), sectionY = blockHeight * (sectionIndex / cols);
            for (int i = 0; i < pagePixelCount; i++)
            {
                int pixelIndex;
                if (swizzleOffsets == null) pixelIndex = (sectionY + i / blockWidth) * width + sectionX + i % blockWidth;
                else
                {
                    int swizzledDestination = swizzleOffsets[i];
                    if (swizzledDestination < 0) continue;
                    pixelIndex = swizzledDestination / 4 + sectionY * width + sectionX;
                }
                if ((uint)pixelIndex < (uint)(width * height)) destinationBytes[sectionOffset + i] = (byte)RemapClutIndex(paletteIndices[pixelIndex]);
            }
        }
    }

    // Inverse of DecodePsmt4. Untested: no PSMT4 sample available.
    private static void WriteImagePsmt4(byte[] destinationBytes, int startOfImage, int decodedWidth, int decodedHeight, int[] paletteIndices)
    {
        int[] bitPositionTable = GsSwizzle.GetPsmt4BitIndices(decodedWidth, decodedHeight); // 4 bit-positions per pixel
        int pixelCount = decodedWidth * decodedHeight;
        int maxByte = startOfImage;
        for (int i = 0; i < pixelCount; i++)
        for (int k = 0; k < 4; k++)
            maxByte = Math.Max(maxByte, startOfImage + (bitPositionTable[i * 4 + k] >> 3));
        for (int byteIndex = startOfImage; byteIndex <= maxByte && byteIndex < destinationBytes.Length; byteIndex++) destinationBytes[byteIndex] = 0; // clear page region, then set bits
        for (int i = 0; i < pixelCount; i++)
        {
            int nibble = paletteIndices[i] & 0xF;
            for (int k = 0; k < 4; k++)
            {
                if (((nibble >> k) & 1) == 0) continue;
                int bitPosition = bitPositionTable[i * 4 + k];
                destinationBytes[startOfImage + (bitPosition >> 3)] |= (byte)(1 << (bitPosition & 7));
            }
        }
    }

    // Quantiser: exact for <=maxColors unique colours, else median-cut.
    private static ((byte R, byte G, byte B, byte A)[] Palette, int[] Indices) Quantize(byte[] rgba, int pixelCount, int maxColors)
    {
        if (maxColors < 1) maxColors = 1;
        var indexByColor = new Dictionary<int, int>();
        var paletteIndices = new int[pixelCount];
        bool overflow = false;
        for (int i = 0; i < pixelCount; i++)
        {
            int colorKey = rgba[i * 4] | (rgba[i * 4 + 1] << 8) | (rgba[i * 4 + 2] << 16) | (rgba[i * 4 + 3] << 24);
            if (!indexByColor.TryGetValue(colorKey, out int paletteIndex))
            {
                if (indexByColor.Count < maxColors)
                {
                    paletteIndex = indexByColor.Count;
                    indexByColor[colorKey] = paletteIndex;
                }
                else
                {
                    overflow = true;
                    break;
                }
            }
            paletteIndices[i] = paletteIndex;
        }
        if (!overflow)
        {
            var palette = new (byte, byte, byte, byte)[Math.Max(indexByColor.Count, 1)];
            foreach (var entry in indexByColor)
                palette[entry.Value] = ((byte)(entry.Key & 0xFF), (byte)((entry.Key >> 8) & 0xFF), (byte)((entry.Key >> 16) & 0xFF), (byte)((entry.Key >> 24) & 0xFF));
            return (palette, paletteIndices);
        }
        return MedianCut(rgba, pixelCount, maxColors);
    }

    private static ((byte R, byte G, byte B, byte A)[] Palette, int[] Indices) MedianCut(byte[] rgba, int pixelCount, int maxColors)
    {
        var buckets = new List<List<int>> { new List<int>(pixelCount) };
        for (int i = 0; i < pixelCount; i++) buckets[0].Add(i);

        while (buckets.Count < maxColors)
        {
            int bestBucketIndex = -1, bestRange = -1, bestChannel = 0;
            for (int bucketIndex = 0; bucketIndex < buckets.Count; bucketIndex++)
            {
                if (buckets[bucketIndex].Count < 2) continue;
                for (int channel = 0; channel < 4; channel++)
                {
                    int minValue = 255, maxValue = 0;
                    foreach (int pixelIndex in buckets[bucketIndex])
                    {
                        int channelValue = rgba[pixelIndex * 4 + channel];
                        if (channelValue < minValue) minValue = channelValue;
                        if (channelValue > maxValue) maxValue = channelValue;
                    }
                    int range = maxValue - minValue;
                    if (range > bestRange)
                    {
                        bestRange = range;
                        bestBucketIndex = bucketIndex;
                        bestChannel = channel;
                    }
                }
            }
            if (bestBucketIndex < 0) break;
            var splitBucket = buckets[bestBucketIndex];
            splitBucket.Sort((leftPixel, rightPixel) => rgba[leftPixel * 4 + bestChannel].CompareTo(rgba[rightPixel * 4 + bestChannel]));
            int midpoint = splitBucket.Count / 2;
            buckets[bestBucketIndex] = splitBucket.GetRange(0, midpoint);
            buckets.Add(splitBucket.GetRange(midpoint, splitBucket.Count - midpoint));
        }

        var palette = new (byte R, byte G, byte B, byte A)[buckets.Count];
        for (int bucketIndex = 0; bucketIndex < buckets.Count; bucketIndex++)
        {
            long redSum = 0, greenSum = 0, blueSum = 0, alphaSum = 0;
            int bucketSize = Math.Max(buckets[bucketIndex].Count, 1);
            foreach (int pixelIndex in buckets[bucketIndex])
            {
                redSum += rgba[pixelIndex * 4];
                greenSum += rgba[pixelIndex * 4 + 1];
                blueSum += rgba[pixelIndex * 4 + 2];
                alphaSum += rgba[pixelIndex * 4 + 3];
            }
            palette[bucketIndex] = ((byte)(redSum / bucketSize), (byte)(greenSum / bucketSize), (byte)(blueSum / bucketSize), (byte)(alphaSum / bucketSize));
        }

        var paletteIndices = new int[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            int pixelRed = rgba[i * 4], pixelGreen = rgba[i * 4 + 1], pixelBlue = rgba[i * 4 + 2], pixelAlpha = rgba[i * 4 + 3];
            int bestPaletteIndex = 0;
            long bestDistance = long.MaxValue;
            for (int paletteIndex = 0; paletteIndex < palette.Length; paletteIndex++)
            {
                long redDelta = pixelRed - palette[paletteIndex].R, greenDelta = pixelGreen - palette[paletteIndex].G, blueDelta = pixelBlue - palette[paletteIndex].B, alphaDelta = pixelAlpha - palette[paletteIndex].A;
                long distance = redDelta * redDelta + greenDelta * greenDelta + blueDelta * blueDelta + alphaDelta * alphaDelta;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestPaletteIndex = paletteIndex;
                }
            }
            paletteIndices[i] = bestPaletteIndex;
        }
        return (palette, paletteIndices);
    }

    /* Bilinear resample. Identity when dimensions already match, so same-size re-encodes
       are untouched. All four channels are filtered; a hard 1-bit-alpha cutout may soften
       by one pixel at edges when actually rescaled (inherent to interpolation, accepted). */
    private static byte[] Resample(byte[] sourceRgba, int sourceWidth, int sourceHeight, int destinationWidth, int destinationHeight)
    {
        if (sourceWidth == destinationWidth && sourceHeight == destinationHeight) return sourceRgba;
        var destinationRgba = new byte[destinationWidth * destinationHeight * 4];
        for (int y = 0; y < destinationHeight; y++)
        {
            double sourceY = destinationHeight <= 1 ? 0 : (y + 0.5) * sourceHeight / (double)destinationHeight - 0.5;
            int floorY = (int)Math.Floor(sourceY);
            double fractionY = sourceY - floorY;
            int clampedY0 = Math.Clamp(floorY, 0, sourceHeight - 1);
            int clampedY1 = Math.Clamp(floorY + 1, 0, sourceHeight - 1);
            for (int x = 0; x < destinationWidth; x++)
            {
                double sourceX = destinationWidth <= 1 ? 0 : (x + 0.5) * sourceWidth / (double)destinationWidth - 0.5;
                int floorX = (int)Math.Floor(sourceX);
                double fractionX = sourceX - floorX;
                int clampedX0 = Math.Clamp(floorX, 0, sourceWidth - 1);
                int clampedX1 = Math.Clamp(floorX + 1, 0, sourceWidth - 1);

                int topLeftIndex = (clampedY0 * sourceWidth + clampedX0) * 4;
                int topRightIndex = (clampedY0 * sourceWidth + clampedX1) * 4;
                int bottomLeftIndex = (clampedY1 * sourceWidth + clampedX0) * 4;
                int bottomRightIndex = (clampedY1 * sourceWidth + clampedX1) * 4;
                int destinationIndex = (y * destinationWidth + x) * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    double topBlend = sourceRgba[topLeftIndex + channel] * (1 - fractionX) + sourceRgba[topRightIndex + channel] * fractionX;
                    double bottomBlend = sourceRgba[bottomLeftIndex + channel] * (1 - fractionX) + sourceRgba[bottomRightIndex + channel] * fractionX;
                    destinationRgba[destinationIndex + channel] = (byte)Math.Clamp((int)Math.Round(topBlend * (1 - fractionY) + bottomBlend * fractionY), 0, 255);
                }
            }
        }
        return destinationRgba;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, int start, int end)
    {
        int limit = Math.Min(end, haystack.Length);
        if (start >= limit) return -1;
        int foundIndex = haystack.Slice(start, limit - start).IndexOf(needle);
        return foundIndex < 0 ? -1 : start + foundIndex;
    }
}