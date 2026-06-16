namespace DotrAssetModder;

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

        /// External Blender/Sketchfab GLBs do not carry DotR's slot-specific pivot/camera
        /// frame. When this is true, non-MrgRoot GLBs are transformed into the original
        /// donor MRG's frame: camera_lookat/bbox-centre maps to donor bounds centre, and
        /// GNode(1) is written at the donor pivot. Extracted DotR GLBs are never fitted;
        /// they round-trip their original tree.
        public bool FitExternalModelsToTemplateFrame = true;

        /// When fitting an external model to the donor frame, scale it uniformly to match
        /// either the donor pivot->camera distance (when camera_lookat + worldPosCenter
        /// markers are present) or the donor bounds radius. Disable this to only translate
        /// into the donor frame.
        public bool ScaleExternalModelsToTemplateFrame = true;

        /// Fallback camera target height for external Blender GLBs when no camera_lookat
        /// empty exists. 0.55 means aim slightly above the vertical midpoint, using
        /// worldPosCenter/Blender origin for X/Z so asymmetric geometry does not pull the
        /// camera/pivot behind the model.
        public float ExternalCameraLookAtHeightRatio = 0.55f;
    }

    /// Size-check outcome, for surfacing a warning in the UI. No rescaling is performed.
    public sealed class FitInfo
    {
        public double OriginalWorld;          // model's world extent
        public double DonorWorld;             // donor template's reference extent
        public string? Message;               // size warning, or null when in-band
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
    /// auto-fit outcome (factor + message) for display.
    public static byte[] Rebuild(ImportedModel model, byte[] originalMrg,
        IReadOnlyList<Rh0Assembler.TextureInput>? textures = null,
        Options? options = null, FitInfo? fit = null)
        => Assemble(model, originalMrg, textures, options, fit);

    private static byte[] Assemble(ImportedModel model, byte[] originalMrg,
        IReadOnlyList<Rh0Assembler.TextureInput>? textures, Options? options, FitInfo? fit)
    {
        var opt = options ?? new Options();

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
        var templateFrame = ReadTemplateFrame(originalMrg);
        var rebuilt = RebuildPipeline.Build(model, templateATS, templateTexCount,
            opt.WarnMinRatio, opt.WarnMaxRatio, templateFrame,
            opt.FitExternalModelsToTemplateFrame, opt.ScaleExternalModelsToTemplateFrame, opt.ExternalCameraLookAtHeightRatio);

        if (fit != null)
        {
            fit.OriginalWorld = rebuilt.FitOriginalWorld;
            fit.DonorWorld = rebuilt.FitDonorWorld;
            fit.Message = rebuilt.FitReason;
        }
        return Rh0Assembler.Assemble(originalMrg, rebuilt, textures, 0x100000,
            opt.RebuildTexturesNative, opt.MaxTextureDim);
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

        int gnode1 = I32(nodeTree + 0x1C); // root's first child, offsets are node-tree-relative
        if (gnode1 <= 0 || nodeTree + gnode1 + 0x2C > originalMrg.Length) return null;
        var pivotRaw = new System.Numerics.Vector3(
            F32(nodeTree + gnode1 + 0x20),
            F32(nodeTree + gnode1 + 0x24),
            F32(nodeTree + gnode1 + 0x28));
        var pivot = pivotRaw / ats;

        if (!IsFinite(bounds) || !IsFinite(pivot) || !(radius > 1e-5f) ||
            float.IsNaN(radius) || float.IsInfinity(radius)) return null;

        return new RebuildPipeline.TemplateFrame {
            PivotWorld = pivot,
            BoundsCenterWorld = bounds,
            BoundsRadius = radius,
        };

        static bool IsFinite(System.Numerics.Vector3 v) =>
            !float.IsNaN(v.X) && !float.IsInfinity(v.X) &&
            !float.IsNaN(v.Y) && !float.IsInfinity(v.Y) &&
            !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);
    }

}