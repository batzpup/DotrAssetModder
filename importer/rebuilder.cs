namespace DotrAssetModder;

// 
// === Step 6 — Rebuilder facade =======================================================
// One-call entry point for the GLB -> .mrg reimporter. Uses the original .mrg as the
// template (RH2 texture packets, ANM entries, header preamble + aux regions are lifted
// from it) and injects geometry / skeleton / motion regenerated from the edited glb.
//
// Pipeline:  GlbImporter.Load -> RebuildPipeline.Build -> Rh0Assembler.Assemble
//
// Textures come from the glb unless TextureInputs are passed; a texture slot with no image gets a
// flat white texture. Output is an RH0 block ready to drop back into the .MRG archive / ISO.
// =====================================================================================

public static class Rebuilder
{
    public sealed class ImportResult
    {
        public byte[] Block = Array.Empty<byte>();
        public List<string> Warnings = new();                     // worth showing the user
        public List<(int Slot, int From)> CopiedMotions = new();  // battle slots the glb lacked (MotWriter)
    }

    // The one import entry point both archives use: a loaded glb over a donor block, with a hard
    // byte budget. MONSTER.MRG passes its fixed 1 MiB slot and trimToContent false (the padded
    // block is the contract the ISO offset math assumes); MODEL.MRG passes the block's own slot
    // and trimToContent true. confirmReduction answers whether the assembler may shrink
    // textures or animation to fit.
    // Each texture keeps its own power-of-two size instead of being squeezed into the donor
    // slot's (a 256x256 face became 128x64 over a small donor: m853). A texture that already
    // fits its donor still takes the template path, so retail round trips are unchanged; if the
    // block overflows, ReduceTextures asks through confirmReduction before shrinking anything.
    public static ImportResult Import(ImportedModel model, byte[] donor, int budget, bool trimToContent,
        Rh0Assembler.ConfirmReduction confirmReduction,
        IReadOnlyList<Rh0Assembler.TextureInput>? textures = null, float boundsRadius = 0f)
    {
        if (textures == null)
        {
            textures = GlbImporter.DecodeGlbTextures(model);
        }
        RebuildPipeline.Result rebuilt = BuildOver(model, donor, boundsRadius, false);
        Rh0Assembler.Result assembled = Rh0Assembler.Assemble(donor, rebuilt, textures, budget, trimToContent,
            confirmReduction, !model.IsDotrExtractedGlb);
        // a slot past the template's count that the 1 MiB block could not grow to hold
        foreach (var (slot, onto) in assembled.ShedTexSlots)
        {
            string w = $"texture slot {slot} did not fit in the 1 MiB block; its faces use slot {onto}'s texture";
            if (!model.TexSlotWarnings.Contains(w)) model.TexSlotWarnings.Add(w);
        }

        var result = new ImportResult();
        result.Block = assembled.Block;
        result.CopiedMotions = assembled.MotionClones;
        List<string> warnings = result.Warnings;
        foreach (string w in model.TexSlotWarnings)
        {
            warnings.Add("texture slots: " + w);
        }
        if (rebuilt.ClampedValues > 0)
        {
            warnings.Add($"{rebuilt.ClampedValues} vertex values were out of range and clamped; " +
                         "parts may look stretched or flattened in game");
        }
        if (rebuilt.DroppedAnimations.Count > 0)
        {
            warnings.Add($"{rebuilt.DroppedAnimations.Count} animation(s) left out, no free motion slot (idle 0, attack 2, death 4, win 5, damage 6, extras 1 and 3): " +
                         string.Join(", ", rebuilt.DroppedAnimations));
        }
        if (assembled.OverflowBytes > 0)
        {
            string at = "";
            if (assembled.OverflowLabel != null)
            {
                at = " at " + assembled.OverflowLabel;
            }
            warnings.Add($"assembler ran {assembled.OverflowBytes:N0} B past the slot{at}");
        }
        if (assembled.HalvedForPages > 0)
        {
            warnings.Add($"textures halved {assembled.HalvedForPages} time(s) to fit the game's texture memory " +
                         $"({assembled.TexturePages} of {Rh0Assembler.MaxModelTexturePages} pages)");
        }
        if (!model.IsDotrExtractedGlb && assembled.TexturePages > Rh0Assembler.MaxModelTexturePages)
        {
            warnings.Add($"textures take {assembled.TexturePages} GS pages, over the {Rh0Assembler.MaxModelTexturePages} " +
                         "a retail monster uses; the screen may flash when this model is drawn");
        }
        return result;
    }

    // Node 0's radius an Import of `model` over `donor` writes when no Radius is given: the same
    // build, stopped once the bounding sphere is measured (no pages, no motions), so the windows
    // show the number the game gets. donor may be empty (a model not over a block yet).
    public static float MeasureRadius(ImportedModel model, byte[] donor)
    {
        return BuildOver(model, donor, 0f, true).BoundsRadius;
    }

    // RebuildPipeline.Build with the donor block's template values.
    private static RebuildPipeline.Result BuildOver(ImportedModel model, byte[] originalMrg, float boundsRadius, bool boundsOnly)
    {
        // adopt the template's animTransScale/root-scale so retail motion files decode 1:1
        float templateATS = originalMrg.Length >= 0x68
            ? System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(originalMrg.AsSpan(0x64))
            : 0f;
        // template texture-slot count, for UV-atlas merging of excess material slots
        int templateTexCount = 0;
        if (originalMrg.Length >= 0x40)
        {
            int tt = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(originalMrg.AsSpan(0x38));
            if (tt > 0 && tt + 4 <= originalMrg.Length)
                templateTexCount = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(originalMrg.AsSpan(tt));
        }
        // The template root node's own scale (node tree +0x00). On all 683 monsters
        // animTransScale == 1 / rootScale byte-exactly, but MODEL.MRG breaks the contract:
        // STAGE7/00 (model 162) stores rootScale 0.0534 with ats 187.2 and STAGE7/03 the same
        // way, so 1/ats is a tenth of the real scale and those two rebuilt 10x too small.
        float templateRootScale = 0f;
        if (originalMrg.Length >= 0x18)
        {
            int nt = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(originalMrg.AsSpan(0x10));
            if (nt > 0 && nt + 0x40 <= originalMrg.Length)
            {
                float rs = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(originalMrg.AsSpan(nt));
                if (rs > 1e-9f && !float.IsNaN(rs) && !float.IsInfinity(rs)) templateRootScale = rs;
            }
        }
        return RebuildPipeline.Build(model, templateATS, templateTexCount, templateRootScale, boundsRadius, boundsOnly);
    }
}