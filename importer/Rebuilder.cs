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
    /// Tunables for the GLB -> .mrg reimport. Defaults reproduce the previous behaviour.
    public sealed class Options
    {
        /// Manual size multiplier relative to the donor's world size. 1 = leave the
        /// auto-fit in charge; any other value forces a rescale to that fraction of the
        /// donor (0.5 = half donor size, 2 = double).
        public float WorldFitScale = 1f;
        /// Master switch for automatic rescaling of far-off-size models.
        public bool AutoFit = true;
        /// Auto-fit triggers only when the model's size is below MinRatio or above MaxRatio
        /// times the donor's world size. In-band sizes are left untouched.
        public float FitMinRatio = 0.125f;
        public float FitMaxRatio = 2.0f;
    }

    /// Outcome of the world auto-fit, for surfacing in the UI.
    public sealed class FitInfo
    {
        public float Factor = 1f;             // 1 == no rescale
        public double OriginalWorld;          // model's world extent before fit
        public double DonorWorld;             // donor template's reference extent
        public string? Message;               // human-readable explanation, or null
        public bool Rescaled => MathF.Abs(Factor - 1f) > 1e-4f;
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
        var rebuilt = RebuildPipeline.Build(model, templateATS, templateTexCount,
            opt.WorldFitScale, opt.AutoFit, opt.FitMinRatio, opt.FitMaxRatio);

        if (fit != null)
        {
            fit.Factor = rebuilt.FitFactor;
            fit.OriginalWorld = rebuilt.FitOriginalWorld;
            fit.DonorWorld = rebuilt.FitDonorWorld;
            fit.Message = rebuilt.FitReason;
        }
        return Rh0Assembler.Assemble(originalMrg, rebuilt, textures);
    }
}