using static DotrAssetModder.Bin;
namespace DotrAssetModder;

/* One-call entry point for the GLB -> .mrg reimporter. The original .mrg is the template
   (RH2 texture packets, ANM entries, header preamble and aux regions are lifted from it);
   geometry, skeleton and motion are regenerated from the glb.
   Pipeline: GlbImporter.Load -> RebuildPipeline.Build -> Rh0Assembler.Assemble
   Textures come from the glb unless textures are passed; a slot with no image gets a flat
   white texture. Output is an RH0 block ready for the .MRG archive / ISO. */

public static class Rebuilder
{
    public sealed class ImportResult
    {
        public byte[] Block = Array.Empty<byte>();
        public List<string> Warnings = new();                     // worth showing the user
        public List<(int Slot, int From)> CopiedMotions = new();  // battle slots the glb lacked (MotWriter)
    }

    /* The import entry point both archives use: a loaded glb over a donor block, with a hard
       byte budget. MONSTER.MRG passes its fixed 1 MiB slot and trimToContent false (the padded
       block is the contract the ISO offset math assumes); MODEL.MRG passes the block's own slot
       and trimToContent true. confirmReduction answers whether the assembler may shrink
       textures or animation to fit. Each texture keeps its own power-of-two size rather than
       the donor slot's; a texture that fits its donor still takes the template path, so retail
       round trips are unchanged. If the block overflows, ReduceTextures asks through
       confirmReduction before shrinking anything. */
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
        foreach (var (slot, onto) in assembled.ShedTextureSlots)
        {
            string warning = $"texture slot {slot} did not fit in the 1 MiB block; its faces use slot {onto}'s texture";
            if (!model.TexSlotWarnings.Contains(warning)) model.TexSlotWarnings.Add(warning);
        }

        var result = new ImportResult();
        result.Block = assembled.Block;
        result.CopiedMotions = assembled.MotionClones;
        List<string> warnings = result.Warnings;
        foreach (string warning in model.TexSlotWarnings)
        {
            warnings.Add("texture slots: " + warning);
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
            string overflowLocation = "";
            if (assembled.OverflowLabel != null)
            {
                overflowLocation = " at " + assembled.OverflowLabel;
            }
            warnings.Add($"assembler ran {assembled.OverflowBytes:N0} B past the slot{overflowLocation}");
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

    // Node 0's radius that Import writes when no radius is given: the same build, stopped once the sphere is measured (no pages, no motions). donor may be empty.
    public static float MeasureRadius(ImportedModel model, byte[] donor)
    {
        return BuildOver(model, donor, 0f, true).BoundsRadius;
    }

    // RebuildPipeline.Build with the donor block's template values.
    private static RebuildPipeline.Result BuildOver(ImportedModel model, byte[] originalMrg, float boundsRadius, bool boundsOnly)
    {
        // adopt the template's animTransScale/root-scale so retail motion files decode 1:1
        float templateAnimTransScale = originalMrg.Length >= 0x68
            ? ReadF32(originalMrg, 0x64)
            : 0f;
        // template texture-slot count, for UV-atlas merging of excess material slots
        int templateTexCount = 0;
        if (originalMrg.Length >= 0x40)
        {
            int textureTable = (int)ReadU32(originalMrg, 0x38);
            if (textureTable > 0 && textureTable + 4 <= originalMrg.Length)
                templateTexCount = (int)ReadU32(originalMrg, textureTable);
        }
        /* The template root node's own scale (node tree +0x00). On all 683 monsters
           animTransScale == 1 / rootScale byte-exactly, but MODEL.MRG breaks it: STAGE7/00
           (model 162) stores rootScale 0.0534 with ats 187.2, and STAGE7/03 likewise, so
           1/ats is a tenth of the real scale. */
        float templateRootScale = 0f;
        if (originalMrg.Length >= 0x18)
        {
            int nodeTree = (int)ReadU32(originalMrg, 0x10);
            if (nodeTree > 0 && nodeTree + 0x40 <= originalMrg.Length)
            {
                float rootScale = ReadF32(originalMrg, nodeTree);
                if (rootScale > 1e-9f && !float.IsNaN(rootScale) && !float.IsInfinity(rootScale)) templateRootScale = rootScale;
            }
        }
        return RebuildPipeline.Build(model, templateAnimTransScale, templateTexCount, templateRootScale, boundsRadius, boundsOnly);
    }
}