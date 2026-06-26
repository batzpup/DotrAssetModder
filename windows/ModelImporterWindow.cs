using System;
using System.IO;
using ImGuiNET;
using NativeFileDialogSharp;

namespace DotrAssetModder;

public class ModelImporterWindow
{
    const int ModelCount = 683;
    const int BlockSize = 0x100000;
    const int MaxTextureDim = 512;

    int modelIndex;
    string modelPath;
    string templatePath;
    string status;
    bool hasMonster = false;
    string monsterPath;
    bool ok;

    public void Render()
    {
        var fl = FileLoader.Instance;

        if (!fl.IsIsoLoaded || fl.iso_data_files == null)
        {
            ImGui.TextDisabled("Load an ISO first.");
            return;
        }
        if (!hasMonster)
        {
            
            monsterPath = fl.iso_data_files[5];
        }

        if (ImGui.Button("Load GLB"))
        {
            var result = Dialog.FileOpen("glb,gltf",Path.GetDirectoryName(Program.config.lastModelImportPath));
            if (result.IsOk)
            {
                modelPath = result.Path;
                Program.config.lastModelImportPath = modelPath;
            }
        }

        ImGui.SameLine();
        ImGui.Text(modelPath == null ? "No model loaded" : Path.GetFileName(modelPath));

        ImGui.SliderInt("Model index", ref modelIndex, 0, ModelCount - 1);

        if (ImGui.Button("Load clean template"))
        {
            var result = Dialog.FileOpen("mrg,rh0,bin");
            if (result.IsOk) templatePath = result.Path;
        }

        ImGui.SameLine();
        ImGui.Text(templatePath == null ? "No template" : Path.GetFileName(templatePath));

        ImGui.BeginDisabled(modelPath == null);
        if (ImGui.Button("Import model"))
        {
            ImGui.OpenPopup("Confirm Import");
        }

        ImGui.EndDisabled();

        bool open = true;
        if (ImGui.BeginPopupModal("Confirm Import", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("This overwrites the ISO in place. Continue?");

            if (ImGui.Button("Yes"))
            {
                ImGui.CloseCurrentPopup();
                Import(fl, monsterPath);
            }

            ImGui.SameLine();

            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }

        if (status != null)
        {
            ImGui.TextColored(
                ok
                    ? new System.Numerics.Vector4(0.4f, 1f, 0.4f, 1f)
                    : new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f),
                status
            );
        }
    }


    byte[] LoadTemplate(byte[] mrg, long blockOff)
    {
        byte[] block;

        if (!string.IsNullOrWhiteSpace(templatePath) && File.Exists(templatePath))
        {
            block = Rh0Assembler.ExtractTemplateBlock(
                File.ReadAllBytes(templatePath),
                modelIndex,
                blockOff
            );
        }
        else
        {
            block = new byte[BlockSize];
            Array.Copy(mrg, blockOff, block, 0, BlockSize);
        }

        Rh0Assembler.ValidateRh0Block(block, "template");
        return block;
    }

    void Import(FileLoader fl, string dataPath)
    {
        try
        {
            byte[] mrg = fl.GetFileBytesFromDisk(dataPath);
            long blockOff = (long)modelIndex * BlockSize;

            if (blockOff + BlockSize > mrg.Length)
                throw new Exception("Model slot is outside MONSTER.MRG.");

            var model = GlbImporter.Load(modelPath);
            var textures = GlbImporter.DecodeGlbTextures(model);

            var options = new Rebuilder.Options {
                MaxTextureDim = MaxTextureDim,
                DebugFrame = true,
                AdoptTemplateScaleForCustomModels = false,
                UseNeutralTextureForMissingSlots = true,
                PreserveTemplateTextureAnimation = false,
            };

            byte[] rebuiltModel = Rebuilder.Rebuild(
                model,
                LoadTemplate(mrg, blockOff),
                textures,
                options,
                new Rebuilder.FitInfo()
            );

            long isoOff = fl.GetStartOfModel(dataPath, modelIndex);
            if (isoOff < 0)
                throw new Exception("Could not find model slot in ISO.");

            //File.WriteAllBytes("last_import_dump.mrg", rebuiltModel);
            fl.WriteBytes(isoOff, rebuiltModel);

            ok = true;
            status = $"Imported index {modelIndex}. ISO offset 0x{isoOff:X}.";
        }
        catch (Exception e)
        {
            ok = false;
            status = "Import failed: " + e.Message;
        }
    }
}