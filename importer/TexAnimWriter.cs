namespace DotrAssetModder;

// === Step 6.5 — ANM (texture animation) writer =======================================
// The "post-section" at TexBlock.postOff is a packed array of 24-byte entries read until
// one has flags.bit0 (last). The entries are pure data with NO internal pointers
// (grid/mode/src/dst/timing), so they are position-independent and can be re-embedded
// verbatim anywhere; the assembler (6.6) places the bytes and writes TexBlock.postOff.
//
// Because the extractor BAKES texture animation into flat per-frame images (destructive -
// grid/mode/sprite-sheet walk are lost), ANM cannot be reconstructed from the glb. The
// faithful path is therefore TEMPLATE: lift the original entry array from the source .mrg
// per texture (like the RH2 template). A texture with no source ANM simply gets postOff=0
// (static texture).
//
// Entry (24 bytes): flags@0 (bit0=last), slotMask@1, grid@2, state@3 (runtime),
// aux0@4, aux1@6, blockW@8, blockH@0xA, srcX@0xC, srcY@0xE, dstX@0x10, dstY@0x12,
// counter@0x14 (runtime). Verified against timeater.mrg tex2 (modes 4 + 6, 2 entries).
// =====================================================================================

public static class TexAnmWriter
{
    private const int EntrySize = 0x18;
    private const int FlagsLastBit = 0x01;

    /// Read the entry array starting at <paramref name="postOff"/> in <paramref name="srcRh0"/>
    /// (up to and including the entry whose flags.bit0 is set) and return a clean copy with the
    /// runtime fields (state, counter) zeroed. Returns an empty array if there is no animation.
    public static byte[] ExtractEntries(ReadOnlySpan<byte> srcRh0, int postOff, int maxEntries = 256)
    {
        if (postOff <= 0 || postOff + EntrySize > srcRh0.Length) return Array.Empty<byte>();
        int count = 0, o = postOff;
        while (count < maxEntries)
        {
            if (o + EntrySize > srcRh0.Length) return Array.Empty<byte>(); // malformed / unterminated
            byte flags = srcRh0[o];
            count++;
            if ((flags & FlagsLastBit) != 0) break;
            o += EntrySize;
        }
        return Clean(srcRh0.Slice(postOff, count * EntrySize));
    }

    /// Copy an entry array, zeroing the per-entry runtime fields (state @+0x03, counter @+0x14)
    /// so the stored data matches the game's on-disk convention.
    public static byte[] Clean(ReadOnlySpan<byte> entries)
    {
        int n = entries.Length / EntrySize;
        var outBuf = new byte[n * EntrySize];
        entries.Slice(0, outBuf.Length).CopyTo(outBuf);
        for (int i = 0; i < n; i++)
        {
            int b = i * EntrySize;
            outBuf[b + 0x03] = 0;                       // state (runtime frame index / phase)
            outBuf[b + 0x14] = 0; outBuf[b + 0x15] = 0; // counter (u32 runtime tick)
            outBuf[b + 0x16] = 0; outBuf[b + 0x17] = 0;
        }
        return outBuf;
    }

    public static int EntryCount(ReadOnlySpan<byte> entries) => entries.Length / EntrySize;
}