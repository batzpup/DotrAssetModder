using System.Buffers.Binary;
using System.IO;
using System.Linq;

namespace DotrAssetModder;
// Mostly AI

// === MODEL.MRG-style pack indexing & splicing ========================================
// Unlike monster MRGs (fixed 1 MiB slots, byIndex = index * 0x100000, in-place overwrite
// only), MODEL.MRG packs variable-sized RH0 blocks back-to-back: each block's true size
// (header[0x0C] + its full mot-chain, exactly what Rh0Assembler.ComputeBlockEnd walks) is
// followed by a gap of UNKNOWN, NOT-FORMULA-DERIVED size before the next block's magic -
// verified against the real MODEL.MRG, this gap is often exactly zero but sometimes up to
// ~0x7d0 bytes, and is NOT a fixed alignment (most blocks' real starts are not 0x800-
// aligned). Whatever tool originally packed this file decided those gaps for reasons not
// reversed here. Scan therefore does not predict the gap - it searches forward from each
// block's computed end for the next block's actual magic, which reproduces the real
// layout exactly (verified: all 297 blocks, zero exceptions).
//
// Practical consequence: replacing one model with a different-sized rebuild shifts the
// start of every later block. There is no in-place overwrite option here the way there is
// for monster MRGs - every replace is a full re-serialize of the pack. ReplaceAt pads the
// NEW block up to a 0x800 boundary (a safe, simple convention for freshly written data,
// not an attempt to replicate the original tool's undetermined gap-sizing) and shifts
// everything after it to follow.
// =======================================================================================

public static class ModelPackIndex
{
    public sealed class Entry
    {
        public int Index;          // position in pack-scan order (0-based) - this IS the
                                    // "model index" used everywhere else (matches the
                                    // model_037/model_039/... extractor naming convention)
        public long Offset;        // absolute byte offset of this block's RH0 magic
        public long Size;          // true block size (ComputeBlockEnd - Offset) - the
                                    // same number Rh0Assembler would report for a fresh
                                    // rebuild of just this model, with NO trailing padding
        public long Stride;        // Offset of (this block's start) to (next block's start)
                                    // in the ORIGINAL pack - i.e. Size plus whatever gap
                                    // happened to precede the next block. Only meaningful
                                    // for re-deriving the original layout; ReplaceAt does
                                    // NOT reuse this for the replacement (ties newBlock's
                                    // padding to 0x800 instead, see ReplaceAt's remarks)
        public uint Flags;         // header+0x04 (mot-chain-shape bits, model-type bits)
        public int NodeCount;      // nodes reachable from the root (child/sibling links)
        public int TexCount;       // texTable's own count field (header+0x38 -> +0x00)
        public bool HasEmbeddedMot;// flags bit 0..7 nonzero (this block carries its own MOT)
    }

    private static uint U32(ReadOnlySpan<byte> d, long o) => BinaryPrimitives.ReadUInt32LittleEndian(d.Slice((int)o));

    // Walks child (+0x1C) / sibling (+0x2C) links from the root. The size alone can't tell
    // retail 64-byte nodes from the old importer's 48-byte nodes plus a 16-byte record.
    private static int CountNodes(byte[] pack, long treeAbs, int treeSize)
    {
        int count = 0;
        var stack = new Stack<long>();
        var seen = new HashSet<long>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            long off = stack.Pop();
            if (off < 0 || off + 0x30 > treeSize || treeAbs + off + 0x30 > pack.Length || !seen.Add(off)) continue;
            count++;
            long child = U32(pack, treeAbs + off + 0x1C) & 0x0FFFFFFF;
            long sib = U32(pack, treeAbs + off + 0x2C) & 0x0FFFFFFF;
            if (sib != 0) stack.Push(sib);
            if (child != 0) stack.Push(child);
        }
        return count;
    }

    // The gap between one block's computed end and the next block's real start is NOT a
    // fixed alignment rule - verified against the real MODEL.MRG, it varies block to block
    // (often zero, sometimes up to ~0x7d0 bytes) for reasons not fully reversed (plausibly
    // per-group padding decided by whatever tool packed this file, not a per-block rule).
    // Rather than guess a formula, Scan searches forward for the next block's actual magic
    // within a generous window - this is verified to reproduce the exact real layout with
    // zero exceptions across all 297 blocks in MODEL.MRG.
    private const int MaxInterBlockGap = 0x1000;

    /// Scans an entire MODEL.MRG-style pack and returns one Entry per model, in on-disk
    /// order. Index 0..Count-1 matches the extractor's pack-scan numbering (the same
    /// numbering baked into names like "model_037").
    public static List<Entry> Scan(byte[] pack)
    {
        var entries = new List<Entry>();
        long pos = 0;
        int idx = 0;
        while (pos + 4 <= pack.Length)
        {
            if (U32(pack, pos) != Rh0Assembler.Rh0Magic)
            {
                // Not a valid next block - in a well-formed pack this only happens at the
                // very end (trailing zero padding after the last block).
                break;
            }

            long end = Rh0Assembler.ComputeBlockEnd(pack, pos, out _);
            if (end < 0 || end == long.MaxValue)
                throw new Exception($"pack scan failed at offset 0x{pos:X}: could not determine block size " +
                                     "(open-ended trailing region (header bit 21) is not supported for pack " +
                                     "scanning - only single standalone blocks)");

            uint flags = U32(pack, pos + 0x04);
            int nodeTreeOff = (int)U32(pack, pos + 0x10);
            int nodeTreeSize = (int)U32(pack, pos + 0x14);
            int texTableOff = (int)U32(pack, pos + 0x38);

            int nodeCount = CountNodes(pack, pos + nodeTreeOff, nodeTreeSize);
            int texCount = 0;
            long texTableAbs = pos + texTableOff;
            if (texTableAbs >= pos && texTableAbs + 4 <= end)
                texCount = (int)U32(pack, texTableAbs);

            // find the next block's real start, if any
            long nextPos = pack.Length;
            bool isLast = true;
            long windowEnd = Math.Min(end + MaxInterBlockGap, pack.Length);
            for (long s = end; s + 4 <= windowEnd; s++)
            {
                if (U32(pack, s) == Rh0Assembler.Rh0Magic) { nextPos = s; isLast = false; break; }
            }
            if (!isLast && nextPos < end)
                throw new Exception($"pack scan inconsistency at offset 0x{pos:X}: found next magic before this " +
                                     "block's computed end - ComputeBlockEnd likely mis-sized this block");

            entries.Add(new Entry {
                Index = idx,
                Offset = pos,
                Size = end - pos,
                Stride = (isLast ? pack.Length : nextPos) - pos,
                Flags = flags,
                NodeCount = nodeCount,
                TexCount = texCount,
                HasEmbeddedMot = (flags & 0xFFu) != 0,
            });

            if (isLast) break;
            pos = nextPos;
            idx++;
        }
        return entries;
    }

    /// Replaces the model at <paramref name="modelIndex"/> (as numbered by Scan) with
    /// <paramref name="newBlock"/> and returns a brand-new pack byte[] with everything
    /// after the replaced block shifted to match. newBlock does not need to be
    /// pre-padded - it is padded up to a 0x800 boundary here (see file header remarks on
    /// why 0x800 is used for new output despite not being the original gap rule), so a
    /// freshly Rh0Assembler.Assemble'd block (whatever exact size it comes out to) can be
    /// passed straight in.
    ///
    /// Unlike monster MRGs, this can NEVER be an in-place overwrite: even a same-size
    /// replacement isn't guaranteed (Rh0Assembler's own internal layout/padding choices
    /// can shift its output's exact length by a few bytes run to run), and any size delta
    /// at all cascades through every subsequent block's offset. Callers should re-scan
    /// (Scan) the returned pack if they need fresh offsets afterward.
    public static byte[] ReplaceAt(byte[] pack, List<Entry> manifest, int modelIndex, byte[] newBlock)
    {
        if (modelIndex < 0 || modelIndex >= manifest.Count)
            throw new ArgumentOutOfRangeException(nameof(modelIndex),
                $"model index {modelIndex} out of range (pack has {manifest.Count} models)");
        if (newBlock.Length < 0x10 || BinaryPrimitives.ReadUInt32LittleEndian(newBlock) != Rh0Assembler.Rh0Magic)
            throw new Exception("replacement block does not start with a valid RH0 header");

        var target = manifest[modelIndex];

        // Prefer the original stride (the real gap to the next block, however large or
        // small it already was) whenever the new content fits there at 16-byte alignment -
        // this keeps the pack's total length unchanged for the common equal-or-smaller
        // replacement case, rather than always re-padding to a fresh 0x800 boundary
        // regardless of how tight the original gap already was. Only fall back to 0x800-
        // rounding (a safe, simple convention - see file header remarks) when the new
        // content doesn't fit within the original stride even at minimum alignment.
        long minAligned = (newBlock.Length + 0xF) & ~0xFL;
        long newPaddedSize = minAligned <= target.Stride ? target.Stride : (newBlock.Length + 0x7FF) & ~0x7FFL;

        // Tail begins right after the replaced block's ORIGINAL real end-of-region
        // (Offset + Stride, i.e. exactly where the next block's magic was actually found
        // by Scan - NOT a predicted/formula padding). Every byte from there to the end of
        // the pack is unrelated to the replaced model and is copied through unchanged,
        // just shifted to start at the new offset.
        long tailStart = target.Offset + target.Stride;
        long tailLength = pack.Length - tailStart;

        long newLength = target.Offset + newPaddedSize + tailLength;
        var result = new byte[newLength];

        Array.Copy(pack, 0, result, 0, target.Offset);                          // unchanged head
        Array.Copy(newBlock, 0, result, target.Offset, newBlock.Length);        // new block
        // zero-pad the gap between newBlock's real end and newPaddedSize (Array default is
        // already 0, but stated explicitly since it's load-bearing, not incidental)
        for (long z = target.Offset + newBlock.Length; z < target.Offset + newPaddedSize; z++)
            result[z] = 0;
        Array.Copy(pack, tailStart, result, target.Offset + newPaddedSize, tailLength); // shifted tail

        return result;
    }

    /// Outcome of a successful ImportModel call - everything the UI needs to report status,
    /// with no byte-level details left for the caller to compute itself.
    public sealed class ImportResult
    {
        public byte[] NewPack;        // full pack, padded back up to originalPackLength
        public int NewModelLength;
        public long OldModelLength;
        public bool PackShrank;
    }

    /// Full orchestration for replacing one model in a MODEL.MRG-style pack: extracts the
    /// target model's own exact bytes as the rebuild template, runs Rebuilder.Rebuild with
    /// trim-to-content semantics, writes diagnostic dump files UNCONDITIONALLY (before the
    /// size check below, so a rejected import still leaves behind something to diagnose),
    /// splices the result in via ReplaceAt, and enforces the one hard constraint that
    /// actually matters: the WHOLE PACK's new total length must not exceed the original
    /// (FileLoader.WriteBytes can only safely overwrite within the file's existing ISO9660
    /// extent - see ModelImporterWindow's import flow for why). An individual model is free
    /// to grow or shrink relative to the slot it replaces; only the pack-wide total is
    /// capped. Throws with a clear message if that cap would be exceeded; otherwise returns
    /// an ImportResult ready to write back via FileLoader.WriteBytes at the file's base ISO
    /// offset (the caller still owns that write, and the ISO/FileLoader specifics, since
    /// this class has no knowledge of FileLoader or the ISO at all).
    /// `enforcePackSizeCap`: true for the normal ISO-overwrite path, where the pack's total
    /// length cannot exceed the original (FileLoader.WriteBytes can only safely overwrite
    /// within the file's existing ISO9660 extent). Pass false when the caller is writing the
    /// result to a brand-new standalone file instead (no fixed extent to respect) - the pack
    /// is then free to grow, and the returned NewPack is the model's real new length with no
    /// zero-padding tail (padding to the old length only makes sense for a same-length
    /// in-place overwrite).
    public static ImportResult ImportModel(byte[] pack, List<Entry> manifest, int modelIndex,
        ImportedModel model, IReadOnlyList<Rh0Assembler.TextureInput> texInputs,
        Rebuilder.Options baseOptions, Rebuilder.FitInfo fitInfo, out RebuildPipeline.Result pipelineResult,
        bool enforcePackSizeCap = true)
    {
        if (modelIndex < 0 || modelIndex >= manifest.Count)
            throw new ArgumentOutOfRangeException(nameof(modelIndex),
                $"model index {modelIndex} out of range (pack has {manifest.Count} models)");

        var target = manifest[modelIndex];

        // Template = exactly this model's own bytes (Size, not padded) - every other
        // header-preamble/aux-region/texture-table assumption in Rh0Assembler.Assemble
        // already works off the template's own real layout, never a fixed 1 MiB shape.
        var template = new byte[target.Size];
        Array.Copy(pack, target.Offset, template, 0, target.Size);

        // Generous ceiling for the texture-table growth/shrink budgeting logic to work
        // with - NOT a hard per-model size limit (there isn't one; only the whole-pack
        // check below is enforced). 8x the original model's size comfortably covers adding
        // several full-resolution native textures without being so large it defeats the
        // budgeting logic's purpose entirely. A private copy is used (rather than mutating
        // baseOptions in place) so callers that might reuse an Options instance across
        // calls aren't surprised by it picking up pack-specific overrides.
        var opt = new Rebuilder.Options {
            WarnMinRatio = baseOptions.WarnMinRatio,
            WarnMaxRatio = baseOptions.WarnMaxRatio,
            RebuildTexturesNative = baseOptions.RebuildTexturesNative,
            MaxTextureDim = baseOptions.MaxTextureDim,
            AdoptTemplateScaleForCustomModels = baseOptions.AdoptTemplateScaleForCustomModels,
            UseNeutralTextureForMissingSlots = baseOptions.UseNeutralTextureForMissingSlots,
            PreserveTemplateTextureAnimation = baseOptions.PreserveTemplateTextureAnimation,
            DebugFrame = baseOptions.DebugFrame,
            MaxOutputSize = (int)Math.Min(int.MaxValue / 2L, Math.Max(target.Size * 8, 0x100000)),
            TrimToContent = true,
        };

        var resultBox = new RebuildPipeline.Result[1];
        byte[] newModelBlock = Rebuilder.Rebuild(model, template, texInputs, opt, fitInfo, resultBox);
        pipelineResult = resultBox[0];

        // Dump diagnostics UNCONDITIONALLY, before the rejection check below - a failed
        // import (e.g. "pack would grow") is exactly when these files matter most.
        DumpRoundTripDiagnostics(modelIndex, template, newModelBlock, pipelineResult);

        byte[] newPack = ReplaceAt(pack, manifest, modelIndex, newModelBlock);

        if (enforcePackSizeCap && newPack.LongLength > pack.LongLength)
            throw new Exception(
                $"resulting pack is {newPack.LongLength:N0} bytes, {newPack.LongLength - pack.LongLength:N0} " +
                $"bytes larger than the original {pack.LongLength:N0}. This model alone is " +
                $"{(newModelBlock.LongLength > target.Size ? $"{newModelBlock.LongLength - target.Size:N0} bytes larger than the slot it replaces" : "not larger than the slot it replaces, so the growth came from rounding/padding elsewhere in the pack")}. " +
                "Shrink this model (smaller textures/geometry), or free up space by re-importing a " +
                "different model in this pack at a smaller size first, then try this one again. " +
                "See last_import_dump.mrg / last_import_template.mrg / last_import_diagnostics.txt " +
                "for a byte-level comparison.");

        // Pad the (equal-or-shorter) new pack back up to the original file's exact length:
        // ONLY meaningful when the caller's eventual write is a same-length ISO overwrite.
        // A standalone-file dump (enforcePackSizeCap=false) skips this and returns the
        // pack's real new length verbatim, growth and all.
        byte[] toWrite = newPack;
        if (enforcePackSizeCap && newPack.LongLength < pack.LongLength)
        {
            toWrite = new byte[pack.Length];
            Array.Copy(newPack, toWrite, newPack.Length);
            // remainder is already zero-filled by `new byte[]`
        }

        return new ImportResult {
            NewPack = toWrite,
            NewModelLength = newModelBlock.Length,
            OldModelLength = target.Size,
            PackShrank = newPack.LongLength < pack.LongLength,
        };
    }

    /// Writes three files for offline diagnosis of a MODEL.MRG round-trip size mismatch:
    ///   last_import_dump.mrg      - exactly the new, assembled model block
    ///   last_import_template.mrg  - exactly the OLD model's bytes it would replace
    ///   last_import_diagnostics.txt - a region-by-region byte-size comparison between the
    ///                                 two (header fields read directly, not guessed), plus
    ///                                 the pipeline's own node/page/strip/texture counts.
    /// Called unconditionally right after assembly succeeds, before any size-rejection
    /// check, so a REJECTED import still leaves behind everything needed to see which
    /// region actually grew rather than nothing at all.
    private static void DumpRoundTripDiagnostics(int modelIndex, byte[] template,
        byte[] newModelBlock, RebuildPipeline.Result result)
    {
        try
        {
            File.WriteAllBytes("last_import_dump.mrg", newModelBlock);
            File.WriteAllBytes("last_import_template.mrg", template);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"MODEL.MRG round-trip diagnostics - pack index {modelIndex}");
            sb.AppendLine($"generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine($"old (template) length: {template.Length:N0} bytes");
            sb.AppendLine($"new (rebuilt)  length: {newModelBlock.Length:N0} bytes");
            sb.AppendLine($"delta:                 {newModelBlock.Length - template.Length:+#,##0;-#,##0;0} bytes");
            sb.AppendLine();

            void Region(string label, byte[] oldBuf, byte[] newBuf, int offOff, int sizeOff)
            {
                long oldOff = SafeU32(oldBuf, offOff), oldSize = SafeU32(oldBuf, sizeOff);
                long newOff = SafeU32(newBuf, offOff), newSize = SafeU32(newBuf, sizeOff);
                sb.AppendLine($"{label,-16} old: off=0x{oldOff:X6} size={oldSize,7:N0}   " +
                              $"new: off=0x{newOff:X6} size={newSize,7:N0}   " +
                              $"delta={(newSize - oldSize):+#,##0;-#,##0;0}");
            }

            sb.AppendLine("-- header regions (offsets/sizes read straight from each block's own header) --");
            Region("node tree", template, newModelBlock, 0x10, 0x14);
            Region("Section1", template, newModelBlock, 0x18, 0x1C);
            Region("Section2", template, newModelBlock, 0x20, 0x24);
            Region("Section1Alt", template, newModelBlock, 0x28, 0x2C);
            Region("Section2Alt", template, newModelBlock, 0x30, 0x34);
            Region("texTable", template, newModelBlock, 0x38, 0x3C);
            Region("aux0", template, newModelBlock, 0x40, 0x44);
            Region("aux1", template, newModelBlock, 0x48, 0x4C);
            Region("relocWalk", template, newModelBlock, 0x50, 0x54);
            Region("aux2", template, newModelBlock, 0x58, 0x5C);
            sb.AppendLine();

            sb.AppendLine("-- per-texture sizes (derived from gaps between consecutive table entries) --");
            DumpTexTable("old", template, sb);
            DumpTexTable("new", newModelBlock, sb);
            sb.AppendLine();

            sb.AppendLine("-- pipeline result (pre-assembly, from RebuildPipeline.Build) --");
            sb.AppendLine(result?.Summary() ?? "(no result captured)");
            if (result != null)
            {
                sb.AppendLine($"pages: {result.Pages.Count}, total strips: {result.Pages.Sum(p => p.Strips.Count)}, " +
                              $"total verts: {result.Pages.Sum(p => p.Strips.Sum(s => s.Verts.Count))}");
                foreach (var p in result.Pages)
                    sb.AppendLine($"  page texIdx={p.TexIdx} strips={p.Strips.Count} " +
                                  $"verts={p.Strips.Sum(s => s.Verts.Count)} boneBinds={p.BoneBinds.Count}");
            }

            File.WriteAllText("last_import_diagnostics.txt", sb.ToString());
        }
        catch
        {
            // diagnostics are best-effort: never let a failure here mask the real
            // success/failure status of the import itself
        }
    }

    private static long SafeU32(byte[] buf, int off)
    {
        if (buf == null || off < 0 || off + 4 > buf.Length) return -1;
        return BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off, 4));
    }

    private static void DumpTexTable(string label, byte[] block, System.Text.StringBuilder sb)
    {
        long ttOff = SafeU32(block, 0x38);
        if (ttOff < 0 || ttOff + 4 > block.Length) { sb.AppendLine($"{label}: no texture table"); return; }
        long count = SafeU32(block, (int)ttOff);
        if (count < 0 || count > 64)
        {
            sb.AppendLine($"{label}: texture count {count} looks invalid, skipping per-texture breakdown");
            return;
        }
        sb.AppendLine($"{label}: {count} textures");
        var offs = new List<long>();
        for (int i = 0; i < count; i++)
        {
            long e = ttOff + 0x10 + i * 16;
            offs.Add(SafeU32(block, (int)e));
        }
        offs.Add(block.Length); // sentinel so the last texture's size is the gap to EOF
        for (int i = 0; i < count; i++)
        {
            long size = offs[i] >= 0 && offs[i + 1] >= offs[i] ? offs[i + 1] - offs[i] : -1;
            sb.AppendLine($"  {label} tex[{i}] off=0x{offs[i]:X} size={size:N0}");
        }
    }
}