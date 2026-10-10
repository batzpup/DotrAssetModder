using System.Numerics;

namespace DotrAssetModder;

// 
// === Step 6 — Rebuilder facade =======================================================
// One-call entry point for the GLB -> .mrg reimporter. Uses the original .mrg as the
// template (RH2 texture packets, ANM entries, header preamble + aux regions are lifted
// from it) and injects geometry / skeleton / motion regenerated from the edited glb.
//
// Pipeline:  GlbImporter.Load -> RebuildPipeline.Build -> Rh0Assembler.Assemble
//
// Texture edits are optional: pass a TextureInput per edited texture index (with its RGBA
// + dimensions); any texture left out keeps the original RH2 verbatim. Output is a 1 MiB
// RH0 block ready to drop back into the .MRG archive / ISO.
// =====================================================================================

public static class Rebuilder
{
    /// Tunables for the GLB -> .mrg reimport.
    public sealed class Options
    {
        /// Size-warning band, as a fraction of the donor's world size. If the model is
        /// below WarnMinRatio or above WarnMaxRatio of the donor's size, the rebuild
        /// surfaces a warning (FitInfo.Message) so the user can rescale it themselves.
        /// No automatic rescaling is ever applied — the model is imported as authored.
        public float WarnMinRatio = 0.125f;

        public float WarnMaxRatio = 2.0f;

        /// Rebuild imported textures at their native (power-of-two) size via
        /// Rh2Encoder.BuildNative instead of squeezing them into the donor slot's
        /// dimensions. A source that already fits its donor keeps the cheaper template-patch
        /// path regardless. For the fixed 1 MiB monster.mrg block, native slots that would
        /// overflow are downgraded back to the donor-fit path (largest first) until the block
        /// fits. Default off (preserves the previous squeeze-to-donor behaviour).
        public bool RebuildTexturesNative = false;

        /// Max width/height for a native rebuild target (power of two). 512 is the
        /// retail-plausible default; 1024 is opt-in and untested on hardware.
        public int MaxTextureDim = 512;

        /// Custom GLBs generate their own rootScale/animTransScale by default. Enable this
        /// only if intentionally testing retail-slot/template scale compatibility.
        public bool AdoptTemplateScaleForCustomModels = false;

        /// When true, missing GLB texture slots are filled with a generated neutral texture
        /// instead of copying whatever RH2 happened to be in the template slot. This prevents
        /// one imported model's textures from contaminating the next import.
        public bool UseNeutralTextureForMissingSlots = true;

        /// When false, template texture animation post entries are not copied into custom
        /// imports. Enable only when intentionally preserving original slot texture animation.
        public bool PreserveTemplateTextureAnimation = false;

        /// Print frame diagnostics to the console during rebuild.
        public bool DebugFrame = false;

        /// Upper bound (bytes) the assembled RH0 block must fit within - Rh0Assembler's
        /// texture-table growth/shrink budgeting and the final Place() bounds check both
        /// use this. Defaults to the monster-MRG fixed slot size. For MODEL.MRG-style
        /// variable packs, pass the replaced model's own size budget instead (see
        /// TrimToContent below - this should still be set generously so the texture
        /// budgeting logic has room to work, NOT set to the exact old model's size, since
        /// the actual returned array is trimmed to content regardless of this ceiling).
        public int MaxOutputSize = 0x100000;

        /// When true, the returned byte[] is trimmed to the model's actual content length
        /// (header + node tree + geometry + textures + motion, 16-byte aligned) instead of
        /// being padded out to MaxOutputSize. Monster MRG imports must leave this false
        /// (the fixed 1 MiB slot is the contract every other system - ISO offset math,
        /// the importer's own template loader - assumes). MODEL.MRG-style variable-pack
        /// replacement should set this true, since there is no fixed slot to pad to.
        public bool TrimToContent = false;

        /// Called at most once if the assembled block would overflow MaxOutputSize, for
        /// whichever of {texture, animation} is the larger byte share - lets a caller (e.g.
        /// the import UI) ask the user before a fidelity-reduction fallback is applied.
        /// Null (default) keeps every fallback fully automatic, matching non-interactive use.
        public Rh0Assembler.ConfirmReduction? ConfirmReduction = null;

        /// Node 0's bounding radius to write instead of the one measured from the geometry
        /// (0 = measured). The board shrinks a monster by clamp(40 / radius, 0.3, 1).
        public float BoundsRadius = 0f;
    }

    /// Size-check outcome, for surfacing a warning in the UI. No rescaling is performed.
    public sealed class FitInfo
    {
        public double OriginalWorld; // model's world extent
        public double DonorWorld; // donor template's reference extent
        public string? Message; // size warning, or null when in-band
        public bool HasWarning => Message != null;
    }

    public static byte[] Rebuild(string glbPath, byte[] originalMrg,
        IReadOnlyList<Rh0Assembler.TextureInput>? textures = null,
        Options? options = null, FitInfo? fit = null)
        => Assemble(GlbImporter.Load(glbPath), originalMrg, textures, options, fit);

    public static byte[] RebuildFromBytes(byte[] glb, byte[] originalMrg,
        IReadOnlyList<Rh0Assembler.TextureInput>? textures = null,
        Options? options = null, FitInfo? fit = null)
        => Assemble(GlbImporter.LoadFromBytes(glb), originalMrg, textures, options, fit);

    /// Overload for callers that pre-load the glb (e.g. to decode model.TextureImages into
    /// TextureInputs first) so the file isn't parsed twice. Pass a FitInfo to receive the
    /// auto-fit outcome (factor + message) for display. Pass a RebuildPipeline.Result-typed
    /// out box (resultOut) to also capture the pre-assembly pipeline result (node/page/strip
    /// counts etc) for diagnostics, without re-running the pipeline a second time.
    public static byte[] Rebuild(ImportedModel model, byte[] originalMrg,
        IReadOnlyList<Rh0Assembler.TextureInput>? textures = null,
        Options? options = null, FitInfo? fit = null, RebuildPipeline.Result[]? resultOut = null)
        => Assemble(model, originalMrg, textures, options, fit, resultOut);

    // The one import entry point both archives use: a loaded glb over a donor block, with a hard
    // byte budget. MONSTER.MRG passes its fixed 1 MiB slot and trimToContent false (the padded
    // block is the contract the ISO offset math assumes); MODEL.MRG passes the block's own slot
    // and trimToContent true. confirmReduction answers whether the assembler may shrink
    // textures or animation to fit. Warnings worth showing the user are added to `warnings`.
    public static byte[] Import(ImportedModel model, byte[] donor, int budget, bool trimToContent,
        Rh0Assembler.ConfirmReduction confirmReduction, List<string> warnings, int maxTextureDim = 512,
        IReadOnlyList<Rh0Assembler.TextureInput>? textures = null, float boundsRadius = 0f)
    {
        var opt = ImportOptions();
        opt.BoundsRadius = boundsRadius;
        opt.MaxOutputSize = budget;
        opt.TrimToContent = trimToContent;
        opt.MaxTextureDim = maxTextureDim;
        // Keep each texture at its own power-of-two size instead of squeezing it into the donor
        // slot's (a 256x256 face became 128x64 over a small donor: m853). A texture that already
        // fits its donor still takes the template path, so retail round trips are unchanged; if the
        // block overflows, ReduceTextures asks through confirmReduction before shrinking anything.
        opt.RebuildTexturesNative = true;
        opt.UseNeutralTextureForMissingSlots = true;
        opt.PreserveTemplateTextureAnimation = false;
        opt.ConfirmReduction = confirmReduction;
        if (textures == null)
        {
            textures = GlbImporter.DecodeGlbTextures(model);
        }
        byte[] bytes = Assemble(model, donor, textures, opt, null);

        foreach (string w in model.TexSlotWarnings)
        {
            warnings.Add("texture slots: " + w);
        }
        if (RebuildPipeline.LastClampedValues > 0)
        {
            warnings.Add($"{RebuildPipeline.LastClampedValues} vertex values were out of range and clamped; " +
                         "parts may look stretched or flattened in game");
        }
        if (RebuildPipeline.LastDroppedAnimations is { Count: > 0 } dropped)
        {
            warnings.Add($"{dropped.Count} animation(s) left out, no free motion slot (idle 0, attack 2, death 4, win 5, damage 6, extras 1 and 3): " +
                         string.Join(", ", dropped));
        }
        if (Rh0Assembler.LastOverflowBytes > 0)
        {
            string at = "";
            if (Rh0Assembler.LastOverflowLabel != null)
            {
                at = " at " + Rh0Assembler.LastOverflowLabel;
            }
            warnings.Add($"assembler ran {Rh0Assembler.LastOverflowBytes:N0} B past the slot{at}");
        }
        if (Rh0Assembler.LastHalvedForPages > 0)
        {
            warnings.Add($"textures halved {Rh0Assembler.LastHalvedForPages} time(s) to fit the game's texture memory " +
                         $"({Rh0Assembler.LastTexturePages} of {Rh0Assembler.MaxModelTexturePages} pages)");
        }
        if (!model.IsDotrExtractedGlb && Rh0Assembler.LastTexturePages > Rh0Assembler.MaxModelTexturePages)
        {
            warnings.Add($"textures take {Rh0Assembler.LastTexturePages} GS pages, over the {Rh0Assembler.MaxModelTexturePages} " +
                         "a retail monster uses; the screen may flash when this model is drawn");
        }
        return bytes;
    }

    // The options Import builds the model with (RebuildPipeline.Build reads these).
    private static Options ImportOptions()
    {
        var opt = new Options();
        opt.DebugFrame = false;
        opt.AdoptTemplateScaleForCustomModels = false;
        return opt;
    }

    // Node 0's radius an Import of `model` over `donor` writes when no Radius is given: the same
    // build, stopped once the bounding sphere is measured (no pages, no motions), so the windows
    // show the number the game gets. donor may be empty (a model not over a block yet).
    public static float MeasureRadius(ImportedModel model, byte[] donor)
    {
        return BuildOver(model, donor, ImportOptions(), true).BoundsRadius;
    }

    private static byte[] Assemble(ImportedModel model, byte[] originalMrg,
        IReadOnlyList<Rh0Assembler.TextureInput>? textures, Options? options, FitInfo? fit,
        RebuildPipeline.Result[]? resultOut = null)
    {
        var opt = options ?? new Options();
        var rebuilt = BuildOver(model, originalMrg, opt, false);

        if (resultOut != null && resultOut.Length > 0) resultOut[0] = rebuilt;

        if (fit != null)
        {
            fit.OriginalWorld = rebuilt.FitOriginalWorld;
            fit.DonorWorld = rebuilt.FitDonorWorld;
            fit.Message = rebuilt.FitReason;
        }
        var outBytes = Rh0Assembler.Assemble(originalMrg, rebuilt, textures, opt.MaxOutputSize,
            opt.RebuildTexturesNative, opt.MaxTextureDim,
            opt.UseNeutralTextureForMissingSlots, opt.PreserveTemplateTextureAnimation,
            opt.TrimToContent, opt.ConfirmReduction, !model.IsDotrExtractedGlb);
        // a slot past the template's count that the 1 MiB block could not grow to hold
        foreach (var (slot, onto) in Rh0Assembler.LastShedTexSlots ?? new())
        {
            string w = $"texture slot {slot} did not fit in the 1 MiB block; its faces use slot {onto}'s texture";
            if (!model.TexSlotWarnings.Contains(w)) model.TexSlotWarnings.Add(w);
        }
        return outBytes;
    }

    // RebuildPipeline.Build with the donor block's template values.
    private static RebuildPipeline.Result BuildOver(ImportedModel model, byte[] originalMrg, Options opt, bool boundsOnly)
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
        // Read it here, NOT in ReadTemplateFrame: both models are single-node, so the frame's
        // GNode(1) pivot check rejects them and the frame comes back null (which is why the
        // earlier attempt inside ReadTemplateFrame changed nothing).
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
        var templateFrame = ReadTemplateFrame(originalMrg);
        return RebuildPipeline.Build(model, templateATS, templateTexCount,
            opt.WarnMinRatio, opt.WarnMaxRatio, templateFrame,
            opt.DebugFrame, opt.AdoptTemplateScaleForCustomModels, templateRootScale, opt.BoundsRadius, boundsOnly);
    }

    private static RebuildPipeline.TemplateFrame? ReadTemplateFrame(byte[] originalMrg)
    {
        if (originalMrg.Length < 0x68) return null;
        uint U32(int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(originalMrg.AsSpan(o));
        int I32(int o) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(originalMrg.AsSpan(o));
        float F32(int o) => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(originalMrg.AsSpan(o));

        if (U32(0) != Rh0Assembler.Rh0Magic) return null;
        int nodeTree = (int)U32(0x10);
        float ats = F32(0x64);
        if (nodeTree <= 0 || nodeTree + 0x40 > originalMrg.Length || ats <= 0f ||
            float.IsNaN(ats) || float.IsInfinity(ats)) return null;

        var bounds = new System.Numerics.Vector3(
            F32(nodeTree + 0x30), F32(nodeTree + 0x34), F32(nodeTree + 0x38));
        float radius = F32(nodeTree + 0x3C);

        int gnode1 = I32(nodeTree + 0x1C); 
        if (gnode1 <= 0 || nodeTree + gnode1 + 0x2C > originalMrg.Length) return null;
        var pivotRaw = new System.Numerics.Vector3(
            F32(nodeTree + gnode1 + 0x20),
            F32(nodeTree + gnode1 + 0x24),
            F32(nodeTree + gnode1 + 0x28));
        var pivot = pivotRaw / ats;

        var frame = new RebuildPipeline.TemplateFrame {
            PivotWorld = pivot,
            BoundsCenterWorld = bounds,
            BoundsRadius = radius,
            NodeTreeOffset = nodeTree,
            AnimTransScale = ats,
            RootScale = 1f / ats,
            PivotRaw = pivotRaw,
        };
        return frame.IsValid ? frame : null;
    }
}