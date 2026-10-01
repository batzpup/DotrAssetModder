using System;
using System.IO;
using System.Numerics;
using ImGuiNET;
using NativeFileDialogSharp;
using Raylib_cs;
using rlImGui_cs;

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
    string monsterPath;
    bool ok;

    AnimatedGltfModel previewModel;
    bool previewLoaded = false;
    RenderTexture2D previewTex;
    bool previewTexReady = false;
    int previewW = 1;
    int previewH = 1;

    Camera3D camera = new Camera3D {
        Position = new Vector3(0, 2, 6),
        Target = Vector3.Zero,
        Up = Vector3.UnitY,
        FovY = 60,
        Projection = CameraProjection.Perspective
    };

    float orbitYaw = 0f;
    float orbitPitch = 0.3f;
    float orbitDistance = 6f;
    Vector3 orbitTarget = Vector3.Zero;
    float lookSens = 0.0035f;

    int currentAnim = 0;
    float animTime = 0f;
    bool animPlaying = false;


    public void Render()
    {
        var fl = FileLoader.Instance;

        if (!fl.IsIsoLoaded || fl.iso_data_files_all == null)
        {
            ImGui.TextDisabled("Load an ISO first.");
            return;
        }
        monsterPath = fl.iso_data_files_all.FirstOrDefault(f => IsoPaths.NameEquals(f, "MONSTER.MRG"));
        if (monsterPath == null)
        {
            ImGui.TextDisabled("MONSTER.MRG not found in ISO.");
            return;
        }
        //Orange
        ImGui.TextColored( new Vector4(1,0.6f,0,1), "Only imports Monster models currently");
        if (ImGui.Button("Load GLB"))
        {
            var result = Dialog.FileOpen(FileDialogFilter.Of("glb,gltf"),Path.GetDirectoryName(Program.config.lastModelImportPath));
            if (result.IsOk)
            {
                SetupModel(result.Path);
            }
        }

        ImGui.SameLine();
        ImGui.Text(modelPath == null ? "No model loaded" : Path.GetFileName(modelPath));

        ImGui.SliderInt("Model index", ref modelIndex, 0, ModelCount - 1);

        if (ImGui.Button("Load clean template"))
        {
            var result = Dialog.FileOpen(FileDialogFilter.Of("mrg,rh0,bin"));
            if (result.IsOk)
            {
                templatePath = result.Path;
            }
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
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (openOverflowPopup)
        {
            ImGui.OpenPopup("Model Too Large");
            openOverflowPopup = false;
        }

        bool overflowOpen = true;
        if (ImGui.BeginPopupModal("Model Too Large", ref overflowOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            double pct = pendingBudget > 0 ? 100.0 * pendingCategoryBytes / pendingBudget : 0;
            bool isAnim = pendingCategory == Rh0Assembler.OverflowCategory.Animation;
            string what = isAnim ? "the animations" : "the texture(s)";
            string action = isAnim ? "store their rotations less precisely (up to 0.2 degrees)" : "downscale them";
            ImGui.TextWrapped(
                $"This model is too big for the 1 MiB slot ({pendingTotalBytes:N0} / {pendingBudget:N0} bytes). " +
                $"{what} {(isAnim ? "are" : "is")} taking up {pct:0.#}% of that space " +
                $"({pendingCategoryBytes:N0} bytes).");
            ImGui.Spacing();
            ImGui.Text($"Would you like to {action} to try to fit?");
            ImGui.Spacing();

            if (ImGui.Button(isAnim ? "Reduce animation fidelity" : "Downscale texture(s)"))
            {
                ImGui.CloseCurrentPopup();
                pendingOverflowConfirm = false;
                Import(pendingFl, pendingDataPath, true);
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel import"))
            {
                ImGui.CloseCurrentPopup();
                pendingOverflowConfirm = false;
                Import(pendingFl, pendingDataPath, false);
            }
            ImGui.EndPopup();
        }

        if (status != null)
        {
            ImGui.TextColored(
                ok ? new Vector4(0.4f, 1f, 0.4f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f),
                status);
        }

        if (previewLoaded)
        {

            DrawPreview();
        }
    }

    public void SetupModel(string path)
    {

        modelPath = path;
        Program.config.lastModelImportPath = modelPath;
        LoadPreview(modelPath);
    }

    void LoadPreview(string path)
    {
        try
        {
            previewModel?.Dispose();
            previewModel = AnimatedGltfModel.Load(path);
            previewLoaded = true;
            currentAnim = 0;
            animTime = 0f;
            animPlaying = previewModel.AnimationCount > 0;

            float radius = previewModel.Radius;
            orbitDistance = MathF.Max(radius * 2.5f, 0.5f);
            orbitTarget = Vector3.Zero;
            orbitYaw = 0f;
            orbitPitch = 0.3f;

            previewModel.SetAnimationFrame(animPlaying ? 0 : -1, 0f);
        }
        catch (Exception e)
        {
            previewLoaded = false;
            status = "Preview failed: " + e.Message;
            ok = false;
        }
    }

    void DrawPreview()
    {
        if (previewModel.AnimationCount > 0)
        {
            if (ImGui.Button(animPlaying ? "Pause" : "Play"))
            {
                animPlaying = !animPlaying;
            }


        }

        ImGui.BeginChild("preview_vp");
        Vector2 avail = ImGui.GetContentRegionAvail();
        int width = Math.Max((int)avail.X, 1);
        int height = Math.Max((int)avail.Y, 1);

        if (!previewTexReady || width != previewW || height != previewH)
        {
            if (previewTexReady)
            {
                Raylib.UnloadRenderTexture(previewTex);
            }
            previewTex = Raylib.LoadRenderTexture(width, height);
            previewW = width;
            previewH = height;
            previewTexReady = true;
        }

        if (ImGui.IsWindowHovered())
        {
            UpdateOrbit();
        }

        AdvanceAnimation();
        RenderScene();

        rlImGui.ImageRenderTexture(previewTex);
        ImGui.EndChild();
    }

    void UpdateOrbit()
    {
        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0f)
        {
            orbitDistance = Math.Clamp(orbitDistance - wheel * orbitDistance * 0.1f, 0.1f, 5000f);
        }

        if (Raylib.IsMouseButtonDown(MouseButton.Left))
        {
            Vector2 delta = Raylib.GetMouseDelta();
            orbitYaw -= delta.X * lookSens;
            orbitPitch += delta.Y * lookSens;
            orbitPitch = Math.Clamp(orbitPitch, -1.5f, 1.5f);
        }

        if (Raylib.IsMouseButtonDown(MouseButton.Middle))
        {
            Vector3 forward = OrbitForward();
            Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));
            Vector2 delta = Raylib.GetMouseDelta();
            float panSpd = orbitDistance * 0.0015f;
            orbitTarget += (-right * delta.X + up * delta.Y) * panSpd;
        }

        Vector3 pos = orbitTarget - OrbitForward() * orbitDistance;
        camera.Position = pos;
        camera.Target = orbitTarget;
    }

    Vector3 OrbitForward()
    {
        float cp = MathF.Cos(orbitPitch);
        Vector3 off = new Vector3(cp * MathF.Sin(orbitYaw), MathF.Sin(orbitPitch), cp * MathF.Cos(orbitYaw));
        return -off;
    }

    void AdvanceAnimation()
    {
        if (!previewLoaded || previewModel.AnimationCount == 0)
        {
            return;
        }

        previewModel.AdvanceTextureAnim(Raylib.GetFrameTime());

        if (animPlaying)
        {
            float dur = previewModel.Duration(currentAnim);
            if (dur > 0f)
            {
                animTime += Raylib.GetFrameTime();
                animTime %= dur;
            }
        }

        previewModel.SetAnimationFrame(currentAnim, animTime);
    }

    void RenderScene()
    {
        float radius = previewModel.Radius;
        float near = MathF.Max(radius * 0.05f, 0.01f);
        float far = MathF.Max(radius * 20f, 100f);

        Raylib.BeginTextureMode(previewTex);
        Raylib.ClearBackground(Color.DarkGray);
        Raylib.BeginMode3D(camera);
        Rlgl.SetClipPlanes(near, far);
        Raylib.DrawGrid(100, 1f);
        previewModel.Draw();
        Raylib.EndMode3D();
        Raylib.EndTextureMode();
    }

    byte[] LoadTemplate(byte[] mrg, long blockOff)
    {
        byte[] block;
        if (!string.IsNullOrWhiteSpace(templatePath) && File.Exists(templatePath))
        {
            block = Rh0Assembler.ExtractTemplateBlock(File.ReadAllBytes(templatePath), modelIndex, blockOff);
        }
        else
        {
            block = new byte[BlockSize];
            Array.Copy(mrg, blockOff, block, 0, BlockSize);
        }
        Rh0Assembler.ValidateRh0Block(block, "template");
        return block;
    }

    // ---- overflow-confirm popup state ----
    // Set when a first Import() attempt hits a fixable-but-unconfirmed overflow; Render()
    // opens the popup on the next frame, and the user's choice re-invokes Import() with
    // reductionDecision fixed to true/false so it never re-prompts for the same attempt.
    bool openOverflowPopup = false;
    bool pendingOverflowConfirm = false;
    Rh0Assembler.OverflowCategory pendingCategory;
    int pendingCategoryBytes, pendingTotalBytes, pendingBudget;
    FileLoader pendingFl;
    string pendingDataPath;

    void Import(FileLoader fl, string dataPath, bool? reductionDecision = null)
    {
        bool overflowAsked = false;
        Rh0Assembler.OverflowCategory askedCategory = default;
        int askedBytes = 0, askedTotal = 0, askedBudget = 0;

        try
        {
            byte[] mrg = fl.GetFileBytesFromDisk(dataPath);
            long blockOff = (long)modelIndex * BlockSize;

            if (blockOff + BlockSize > mrg.Length)
            {
                throw new Exception("Model slot is outside MONSTER.MRG.");
            }

            var model = GlbImporter.Load(modelPath);
            var textures = GlbImporter.DecodeGlbTextures(model);

            var options = new Rebuilder.Options {
                MaxTextureDim = MaxTextureDim,
                AdoptTemplateScaleForCustomModels = false,
                UseNeutralTextureForMissingSlots = true,
                PreserveTemplateTextureAnimation = false,
                ConfirmReduction = (category, categoryBytes, totalBytes, budget) =>
                {
                    if (reductionDecision.HasValue) return reductionDecision.Value;
                    overflowAsked = true;
                    askedCategory = category;
                    askedBytes = categoryBytes;
                    askedTotal = totalBytes;
                    askedBudget = budget;
                    return false; // don't apply yet - just probing whether reduction is needed
                },
            };

            byte[] rebuiltModel = Rebuilder.Rebuild(model, LoadTemplate(mrg, blockOff), textures, options, new Rebuilder.FitInfo());

            long isoOff = fl.GetStartOfModel(dataPath, modelIndex);
            if (isoOff < 0)
            {
                throw new Exception("Could not find model slot in ISO.");
            }

            fl.WriteBytes(isoOff, rebuiltModel);
            ok = true;
            status = $"Imported index {modelIndex}. ISO offset 0x{isoOff:X}.";
            if (RebuildPipeline.LastClampedValues > 0)
                status += $" Warning: {RebuildPipeline.LastClampedValues} vertex values were out of range and clamped; " +
                          "parts may look stretched or flattened in game.";
            // texture-slot problems found while reading the glb (GlbImporter) and while
            // fitting it to the template's RH2 count (RebuildPipeline)
            if (model.TexSlotWarnings.Count > 0)
                status += " Texture slots: " + string.Join("; ", model.TexSlotWarnings) + ".";
            // battle slots the glb lacked, filled with a copy of another motion (MotWriter)
            if (MotWriter.LastClones is { Count: > 0 } clones)
                status += " Missing motions copied: " +
                          string.Join(", ", clones.Select(c => $"slot {c.Slot} from slot {c.From}")) +
                          " (4 death, 5 win, 6 damage; a copied idle never shows a fall).";
        }
        catch (Exception e)
        {
            if (!reductionDecision.HasValue && overflowAsked)
            {
                // Not a real failure yet - stash and let the user decide via the popup.
                pendingOverflowConfirm = true;
                openOverflowPopup = true;
                pendingCategory = askedCategory;
                pendingCategoryBytes = askedBytes;
                pendingTotalBytes = askedTotal;
                pendingBudget = askedBudget;
                pendingFl = fl;
                pendingDataPath = dataPath;
                return;
            }

            ok = false;
            status = "Import failed: " + e.Message;
        }
    }
}