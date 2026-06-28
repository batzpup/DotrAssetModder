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
    bool hasMonster = false;
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
            var result = Dialog.FileOpen("glb,gltf", Path.GetDirectoryName(Program.config.lastModelImportPath));
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
            var result = Dialog.FileOpen("mrg,rh0,bin");
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

    void Import(FileLoader fl, string dataPath)
    {
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
                DebugFrame = true,
                AdoptTemplateScaleForCustomModels = false,
                UseNeutralTextureForMissingSlots = true,
                PreserveTemplateTextureAnimation = false,
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
        }
        catch (Exception e)
        {
            ok = false;
            status = "Import failed: " + e.Message;
        }
    }
}