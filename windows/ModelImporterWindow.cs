using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using NativeFileDialogSharp;
using Raylib_cs;
namespace DotrAssetModder;

public class ModelImporterWindow
{
    int currentDataFileIndex;
    int modelIndex;
    int maxModelIndex;
    int scannedFileIndex = -1;
    string modelPath;
    string status;
    bool statusOk;

   
    const float WarnMinRatio = 0.25f;  // below this * donor size -> warn "too small"
    const float WarnMaxRatio = 1.75f;    // above this * donor size -> warn "too large"
    bool rebuildTexNative = false;  // rebuild imported textures at native POT size (vs squeeze-to-donor)
    int maxTexDimIndex = 1;         // 0 = 256, 1 = 512, 2 = 1024 (opt-in)
    static readonly int[] MaxTexDims = { 256, 512, 1024 };
    static readonly string[] MaxTexDimLabels = { "256", "512 (recommended)", "1024 (opt-in for non monster, untested)" };
    string fitMessage;              

    public void Render()
    {
        var fl = FileLoader.Instance;
        if (!fl.IsIsoLoaded || fl.iso_data_files == null || fl.iso_data_files.Length == 0)
        {
            ImGui.TextDisabled("Load an ISO in the Extractor tab first.");
            return;
        }

        ImGui.Text("Data file");
        if (ImGui.Combo("##data", ref currentDataFileIndex, fl.iso_data_files, fl.iso_data_files.Length))
            scannedFileIndex = -1; // selection changed -> rescan block count

        if (scannedFileIndex != currentDataFileIndex)
        {
            try { maxModelIndex = Math.Max(0, fl.CountRH0(fl.iso_data_files[currentDataFileIndex]) - 1); }
            catch { maxModelIndex = 0; }
            modelIndex = 0;
            scannedFileIndex = currentDataFileIndex;
        }

        if (ImGui.Button("Model to load"))
        {
            DialogResult dr = Dialog.FileOpen("glb,gltf");
            if (dr.IsOk) modelPath = dr.Path;
        }
        ImGui.SameLine();
        ImGui.Text(modelPath is null ? "No glb loaded" : $"Loaded: {modelPath}");

        ImGui.SliderInt("Model index", ref modelIndex, 0, maxModelIndex);
        modelIndex = Math.Clamp(modelIndex, 0, Math.Max(0, maxModelIndex));

        ImGui.Separator();
        // No auto-scaling: models import at their authored size. A size warning (set after
        // each import) is shown so the user can rescale in their editor if it's off.
        if (fitMessage != null)
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.7f, 0.3f, 1f), "\u26A0 " + fitMessage);
        ImGui.Separator();

        ImGui.Checkbox("Rebuild textures to native size", ref rebuildTexNative);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Rebuild each imported texture as a fresh power-of-two RH2 slot \n" +
                "instead of squeezing it into the donor slot's dimensions. Sources that already \n" +
                "fit their donor keep the cheaper path. Native slots that would overflow the \n" +
                "1 MiB model block are downgraded back to the donor size (largest first).");
        if (rebuildTexNative)
        {
            ImGui.Indent();
            ImGui.SetNextItemWidth(220);
            ImGui.Combo("Max texture size", ref maxTexDimIndex, MaxTexDimLabels, MaxTexDimLabels.Length);
            ImGui.Unindent();
        }
        ImGui.Separator();

        ImGui.BeginDisabled(modelPath is null);
        if (ImGui.Button("Import model")) ImGui.OpenPopup("ConfirmImport");
        ImGui.EndDisabled();

        bool open = true;
        if (ImGui.BeginPopupModal("ConfirmImport", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("This overwrites the ISO in place. Make sure you have a backup.\nContinue?");
            if (ImGui.Button("Yes, import")) { ImGui.CloseCurrentPopup(); DoImport(); }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (status != null)
            ImGui.TextColored(statusOk ? new System.Numerics.Vector4(0.4f, 1f, 0.4f, 1f)
                                       : new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f), status);
    }

    /// Decode the glb's embedded base-colour images (per "mat_texN" texture index) into raw
    /// RGBA TextureInputs for the RH2 re-encoder. Uses the same SaveToFile -> Raylib.LoadImage
    /// path as the model viewer (version-safe across SharpGLTF releases). Best-effort: a
    /// texture that fails to decode is skipped and keeps the template's original RH2.
    static List<Rh0Assembler.TextureInput> DecodeGlbTextures(ImportedModel model)
    {
        var inputs = new List<Rh0Assembler.TextureInput>();
        foreach (var (texIdx, img) in model.TextureImages)
        {
            string tmp = null;
            try
            {
                string ext = string.IsNullOrEmpty(img.FileExtension) ? "png" : img.FileExtension;
                tmp = Path.Combine(Path.GetTempPath(), "dotr_imp_" + Guid.NewGuid().ToString("N") + "." + ext);
                img.SaveToFile(tmp);

                Image cpu = Raylib.LoadImage(tmp);
                if (cpu.Width <= 0 || cpu.Height <= 0) continue;
                Raylib.ImageFormat(ref cpu, PixelFormat.UncompressedR8G8B8A8);

                byte[] rgba = new byte[cpu.Width * cpu.Height * 4];
                unsafe { System.Runtime.InteropServices.Marshal.Copy((IntPtr)cpu.Data, rgba, 0, rgba.Length); }
                inputs.Add(new Rh0Assembler.TextureInput
                {
                    TexIdx = texIdx,
                    Rgba = rgba,
                    Width = cpu.Width,
                    Height = cpu.Height,
                });
                Raylib.UnloadImage(cpu);
            }
            catch { /* skip this texture; the template RH2 stays */ }
            finally
            {
                try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
        return inputs;
    }

    void DoImport()
    {
        try
        {
            var fl = FileLoader.Instance;
            string dataPath = fl.iso_data_files[currentDataFileIndex];

            // read the .MRG once: used both as the assembler template and to locate the file
            byte[] mrg = fl.GetFileBytesFromDisk(dataPath);
            long blockOff = (long)modelIndex * 0x100000;
            if (blockOff + 0x100000 > mrg.Length)
                throw new Exception("model block extends past end of file");

            byte[] template = new byte[0x100000];
            Array.Copy(mrg, blockOff, template, 0, 0x100000);

            // load the glb once: geometry/skeleton/animation + embedded textures
            var model = GlbImporter.Load(modelPath);
            var texInputs = DecodeGlbTextures(model);

            var opt = new Rebuilder.Options
            {
                WarnMinRatio = WarnMinRatio,
                WarnMaxRatio = WarnMaxRatio,
                RebuildTexturesNative = rebuildTexNative,
                MaxTextureDim = MaxTexDims[Math.Clamp(maxTexDimIndex, 0, MaxTexDims.Length - 1)],
            };
            var fitInfo = new Rebuilder.FitInfo();
            byte[] newBlock = Rebuilder.Rebuild(model, template, texInputs, opt, fitInfo);
            fitMessage = fitInfo.HasWarning ? fitInfo.Message : null;

            long isoOffset = fl.GetStartOfModel(dataPath, modelIndex);
            if (isoOffset < 0) throw new Exception("could not locate this file inside the ISO");

            // debug: keep a copy of exactly what was written, for offline analysis
            File.WriteAllBytes("last_import_dump.mrg", newBlock);

            fl.WriteBytes(isoOffset, newBlock);
            status = $"Imported into {dataPath} (index {modelIndex}, ISO offset 0x{isoOffset:X}).";
            statusOk = true;
        }
        catch (Exception e)
        {
            status = "Import failed: " + e.Message;
            statusOk = false;
        }
    }
}