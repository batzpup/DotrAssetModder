using ImGuiNET;
using Raylib_cs;
using rlImGui_cs;
using System.Linq;
using System.Numerics;
using DotrAssetModder;
using NativeFileDialogSharp;

public class ModelViewerWindow
{
    AnimatedGltfModel model;
    bool modelLoaded = false;
    string loadError = null;
    string loadedPath = string.Empty;


    string[] modelFiles = Array.Empty<string>();
    int currentModelIndex = 0;


    const float FPS = 30f;
    int currentAnim = 0;
    float animTime = 0f;
    bool animPlaying = false;
    bool loopAnim = true;
    float animSpeed = 1.0f;
    float animSpeedStep = 0.25f;
    bool needsAnimUpdate = false;
    bool staticPose = false;
    bool needsStaticRefresh = false;

    RenderTexture2D sceneTexture;

    int viewportWidth = 1280;
    int viewportHeight = 720;

    OrbitCamera orbit = new OrbitCamera();
    Vector2 savedMousePos;
    bool freeLooking = false;
    int flySpeed = 50;


    bool resetPressed;
    bool showgrid = true;

    // View: Feet on grid / Duel board / Map leader, as the game places a model (ViewScene). The
    // board and map come from the loaded ISO; a file has no card, so the map height's card is
    // picked, and no radius, so it is measured (the farthest point from the origin).
    ViewScene viewScene = new ViewScene();
    ModelMrg viewSceneMrg;
    string viewSceneIso = string.Empty;
    int mapCard;
    float viewLowest;                                 // the idle animation's extent (MeasureStanding)
    float viewHeight;
    float viewLeft;
    float viewRight;
    float viewBack;
    float viewFront;
    float viewRadius = 1f;

    public ModelViewerWindow()
    {
        sceneTexture = Raylib.LoadRenderTexture(viewportWidth, viewportHeight);
    }

    void ButtonLoadModel()
    {
        DialogResult dialogResult = Dialog.FileOpen(FileDialogFilter.Of("glb,gltf,fbx,dae"));
        if (!dialogResult.IsOk)
        {
            return;
        }

        int idx = Array.IndexOf(modelFiles, dialogResult.Path);
        currentModelIndex = idx;
        LoadModel(dialogResult.Path);
    }

    void ButtonLoadFolder()
    {
        DialogResult dialogResult = Dialog.FolderPicker(Program.config.lastModelFolderPath);
        if (!dialogResult.IsOk)
        {
            return;
        }
        LoadFolder(dialogResult.Path);
    }

    public void LoadFolder(string folderPath)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(folderPath, "*.*")
                .Where(f => f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".dae", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception e)
        {
            loadError = e.Message;
            return;
        }

        modelFiles = files;
        Program.config.lastModelFolderPath = folderPath;

        if (modelFiles.Length == 0)
        {
            currentModelIndex = -1;
            loadError = "No .glb/.gltf files found in folder.";
            return;
        }

        currentModelIndex = 0;
        LoadModel(modelFiles[0]);
    }

    // The shown model's file name; empty when it was loaded by itself, not from a folder.
    string CurrentName()
    {
        if (currentModelIndex >= 0 && currentModelIndex < modelFiles.Length)
        {
            return modelFiles[currentModelIndex];
        }
        return loadedPath;
    }

    void GoToModelIndex(int index)
    {
        if (modelFiles.Length == 0) return;
        index = Math.Clamp(index, 0, modelFiles.Length - 1);
        LoadModel(modelFiles[index]);
    }

    public void LoadModel(string path)
    {
        loadedPath = path;
        AnimatedGltfModel newModel;
        ImportedModel imported;
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".fbx" || ext == ".dae")
            {
                ConvertOptions options = new ConvertOptions();
                options.AnimsFrom = ModelConverter.FindMotionFiles(path);
                ConvertResult converted = ModelConverter.ConvertFile(path, options, message => { });
                newModel = AnimatedGltfModel.Load(converted.Glb);
                imported = GlbImporter.LoadFromBytes(converted.Glb);
            }
            else
            {
                newModel = AnimatedGltfModel.Load(path);
                imported = GlbImporter.Load(path);
            }
        }
        catch (Exception e)
        {
            loadError = e.Message;
            return;
        }

        model?.Dispose();
        model = newModel;
        modelLoaded = true;
        loadError = null;

        currentAnim = 0;
        animTime = 0f;
        animPlaying = !staticPose && model.AnimationCount > 0;

        model.MeasureStanding(ModelImporterWindow.IdleAnimIndex(model), out viewLowest, out viewHeight,
            out viewLeft, out viewRight, out viewBack, out viewFront);
        // the radius an import of this model writes (the importer's own measure), so the board
        // and map show it at the size the game will; 0 (real size) if the importer cannot take it
        try
        {
            viewRadius = Rebuilder.MeasureRadius(imported, Array.Empty<byte>());
        }
        catch (Exception)
        {
            viewRadius = 0f;
        }
        FrameStanding();


        int initAnim = (!staticPose && model.AnimationCount > 0) ? currentAnim : -1;
        model.SetAnimationFrame(initAnim, 0f);
        needsAnimUpdate = false;
        needsStaticRefresh = false;
        resetPressed = true;
    }

    // Fits the camera to the model where View puts it.
    void FrameStanding()
    {
        if (!modelLoaded)
        {
            orbit.Yaw = 0f;
            orbit.Pitch = 0.3f;
            orbit.Target = Vector3.Zero;
            return;
        }
        float s = viewScene.ModelScale(viewRadius);
        Vector3 shift = viewScene.StandShift(model, s);
        float bottom = viewScene.StandY(viewLowest, mapCard) + shift.Y + s * viewLowest;
        float top = bottom + s * viewHeight;
        float left = shift.X + s * viewLeft;
        float right = shift.X + s * viewRight;
        bottom = MathF.Min(bottom, 0f);                // keep the grid in view
        orbit.Frame(left, right, bottom, top, 0.3f);
    }

    // Opens the loaded ISO's MODEL.MRG for the board and map the first time View needs them,
    // and again when another ISO is loaded.
    void UpdateViewSceneSource()
    {
        if (viewScene.Placement == ViewScene.PlaceFeet)
        {
            return;
        }
        FileLoader fl = FileLoader.Instance;
        string iso = string.Empty;
        if (fl.IsIsoLoaded && !string.IsNullOrEmpty(fl.CurrentIsoPath))
        {
            iso = fl.CurrentIsoPath;
        }
        if (iso == viewSceneIso)
        {
            return;
        }
        viewSceneIso = iso;
        viewScene.SetSource(null);
        if (viewSceneMrg != null)
        {
            viewSceneMrg.Dispose();
            viewSceneMrg = null;
        }
        if (iso.Length == 0)
        {
            return;
        }
        try
        {
            viewSceneMrg = ModelMrg.Open(iso);
            viewScene.SetSource(viewSceneMrg);
        }
        catch (Exception e)
        {
            loadError = "Could not read the ISO's MODEL.MRG: " + e.Message;
        }
    }

    void UpdateCamera()
    {
        bool rightPressed = Raylib.IsMouseButtonPressed(MouseButton.Right);

        if (rightPressed)
        {
            savedMousePos = Raylib.GetMousePosition();
            glRD.Io.ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;
            Raylib.HideCursor();
            Raylib.SetMousePosition(Raylib.GetScreenWidth() / 2, Raylib.GetScreenHeight() / 2);
            freeLooking = true;
        }

        if (Raylib.IsMouseButtonReleased(MouseButton.Right))
        {
            Raylib.ShowCursor();
            glRD.Io.ConfigFlags &= ~ImGuiConfigFlags.NoMouseCursorChange;
            Raylib.SetMousePosition((int)savedMousePos.X, (int)savedMousePos.Y);
            freeLooking = false;
        }

        Vector3 cameraPos = orbit.Position;
        orbit.Zoom(Raylib.GetMouseWheelMove());
        if (Raylib.IsMouseButtonDown(MouseButton.Left))
        {
            orbit.Turn(Raylib.GetMouseDelta());
        }

        // free-look: turn in place, the camera stays where it is and the target swings round it
        if (Raylib.IsMouseButtonDown(MouseButton.Right) && !rightPressed)
        {
            orbit.Turn(Raylib.GetMouseDelta());
            orbit.Target = cameraPos + orbit.Forward * orbit.Distance;
            Raylib.SetMousePosition(Raylib.GetScreenWidth() / 2, Raylib.GetScreenHeight() / 2);
        }

        if (Raylib.IsMouseButtonDown(MouseButton.Middle))
        {
            orbit.Pan(Raylib.GetMouseDelta());
        }
        Vector3 forward = orbit.Forward;
        Vector3 right = orbit.Right;

        float dt = Raylib.GetFrameTime();
        float moveSpeed = flySpeed * dt;
        if (Raylib.IsKeyDown(KeyboardKey.LeftShift)) moveSpeed *= 3f;

        Vector3 move = Vector3.Zero;
        if (Raylib.IsKeyDown(KeyboardKey.W)) move += forward;
        if (Raylib.IsKeyDown(KeyboardKey.S)) move -= forward;
        if (Raylib.IsKeyDown(KeyboardKey.D)) move += right;
        if (Raylib.IsKeyDown(KeyboardKey.A)) move -= right;
        if (Raylib.IsKeyDown(KeyboardKey.Space)) move += Vector3.UnitY;
        if (Raylib.IsKeyDown(KeyboardKey.LeftControl)) move -= Vector3.UnitY;

        if (move != Vector3.Zero)
            orbit.Target += Vector3.Normalize(move) * moveSpeed;

        orbit.Apply();
    }

    void AdvanceAnimation()
    {
        if (!modelLoaded) return;


        if (staticPose)
        {
            if (needsStaticRefresh)
            {
                model.SetAnimationFrame(-1, 0f);
                needsStaticRefresh = false;
            }
            return;
        }

        if (animPlaying || model.AnimationCount == 0)
        {
            model.AdvanceTextureAnim(Raylib.GetFrameTime() * animSpeed);
        }


        if (model.AnimationCount > 0 && animPlaying)
        {
            float dur = model.Duration(currentAnim);
            if (dur > 0f)
            {
                animTime += Raylib.GetFrameTime() * animSpeed;
                if (loopAnim)
                {
                    animTime %= dur;
                    if (animTime < 0f) animTime += dur;
                }
                else if (animTime >= dur)
                {
                    animTime = dur;
                    animPlaying = false;
                }
            }
            needsAnimUpdate = true;
        }

        if (needsAnimUpdate)
        {
            model.SetAnimationFrame(model.AnimationCount > 0 ? currentAnim : -1, animTime);
            needsAnimUpdate = false;
        }
    }

    public void Render()
    {
        if (ImGui.Button("Load Model"))
        {
            ButtonLoadModel();
        }
        ImGui.SameLine();
        if (ImGui.Button("Load Folder"))
        {
            ButtonLoadFolder();
        }

        if (!modelLoaded)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("No model loaded");
        }

        if (loadError != null)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "Load failed: " + loadError);
        }



        if (modelFiles.Length > 0)
        {
            ImGui.Separator();
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt($"{currentModelIndex}/{modelFiles.Length - 1} {Path.GetFileName(modelFiles[Math.Max(currentModelIndex, 0)])}", ref currentModelIndex))
            {
                if (currentModelIndex > modelFiles.Length - 1)
                {
                    currentModelIndex = 0;
                }
                if (currentModelIndex < 0)
                {
                    currentModelIndex = modelFiles.Length - 1;
                }
                GoToModelIndex(currentModelIndex);

            }


        }

        if (ImGui.Button("Reset View"))
        {
            FrameStanding();
            if (modelLoaded)
            {
                resetPressed = true;
            }
        }
        ImGui.SameLine();
        ImGui.Checkbox("Show Grid", ref showgrid);
        ImGui.SameLine();
        ImGui.SliderInt("Fly Speed", ref flySpeed, 40, 500);
        ImGui.Text("Hover for controls");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("WASD to move, space and left Ctrl to go up and down\nleft click drag to orbit, right click drag to free-look, middle click drag to pan, mouse wheel to zoom");
        }

        FileLoader isoLoader = FileLoader.Instance;
        bool isoLoaded = isoLoader.IsIsoLoaded && !string.IsNullOrEmpty(isoLoader.CurrentIsoPath);
        if (modelLoaded && !isoLoaded && viewScene.Placement != ViewScene.PlaceFeet)
        {
            // the board and map come from the ISO: without one only the grid is offered
            viewScene.Placement = ViewScene.PlaceFeet;
            FrameStanding();
            resetPressed = true;
        }
        if (modelLoaded && isoLoaded)
        {
            UpdateViewSceneSource();
            if (viewScene.DrawControls(true, ref mapCard, true,
                "\nThe radius is measured from the model: the farthest point from its origin."))
            {
                FrameStanding();
                resetPressed = true;
            }
            viewScene.Update();
            if (viewScene.Error != null)
            {
                loadError = viewScene.Error;
                viewScene.Error = null;
            }
        }

        bool hasSkel = modelLoaded && model.AnimationCount > 0;
        if (hasSkel)
        {
            ImGui.Separator();

            if (ImGui.Checkbox("Static pose (no animation)", ref staticPose))
            {
                if (staticPose) needsStaticRefresh = true;
                else needsAnimUpdate = true;
            }
            if (hasSkel)
            {
                ImGui.SameLine();
                if (ImGui.Button(animPlaying ? "Pause" : "Play"))
                {
                    animPlaying = !animPlaying;
                }

                ImGui.SameLine();
                if (ImGui.Button("Stop"))
                {
                    animPlaying = false;
                    animTime = 0f;
                    needsAnimUpdate = true;
                }
                ImGui.SameLine();
                ImGui.Checkbox("Loop", ref loopAnim);
            }

            ImGui.BeginDisabled(staticPose);

            if (hasSkel)
            {
                if (ImGui.Combo("Animation", ref currentAnim, model.AnimationNames, model.AnimationNames.Length))
                {
                    animTime = 0f;
                    animPlaying = true;
                    needsAnimUpdate = true;
                }

                float dur = model.Duration(currentAnim);
                int lastFrame = (int)MathF.Round(dur * FPS);
                if (lastFrame < 1) lastFrame = 1;
                int currentFrame = (int)MathF.Round(animTime * FPS);
                if (currentFrame > lastFrame) currentFrame = lastFrame;
                if (ImGui.SliderInt("##Frame", ref currentFrame, 0, lastFrame))
                {
                    animTime = currentFrame / FPS;
                    needsAnimUpdate = true;
                }
                ImGui.SameLine();
                ImGui.Text($"Frames: {lastFrame + 1}  ({FPS * animSpeed} fps)");

            }


            if (ImGui.SliderFloat("Anim Speed", ref animSpeed, 0.25f, 4.0f, "%.2f", ImGuiSliderFlags.AlwaysClamp))
            {
                animSpeed = MathF.Round(animSpeed / animSpeedStep) * animSpeedStep;
            }

            ImGui.EndDisabled();
        }
        else if (modelLoaded)
        {
            ImGui.Separator();
            ImGui.TextDisabled("Model has no animations");
        }

        ImGui.BeginChild("viewport");

        Vector2 avail = ImGui.GetContentRegionAvail();
        int width = Math.Max((int)avail.X, 1);
        int height = Math.Max((int)avail.Y, 1);

        if (width != viewportWidth || height != viewportHeight)
        {
            Raylib.UnloadRenderTexture(sceneTexture);
            sceneTexture = Raylib.LoadRenderTexture(width, height);
            viewportWidth = width;
            viewportHeight = height;
        }

        if (ImGui.IsWindowHovered() || resetPressed)
        {
            if (resetPressed)
            {
                resetPressed = false;
            }
            UpdateCamera();
        }


        AdvanceAnimation();

        Raylib.BeginTextureMode(sceneTexture);
        Raylib.ClearBackground(Color.DarkGray);

        viewScene.SetClipPlanes(orbit.Camera, modelLoaded ? viewScene.ModelScale(viewRadius) * viewRadius : 0f);
        Raylib.BeginMode3D(orbit.Camera);
        bool scenery = modelLoaded && viewScene.SceneryShown;
        if (showgrid && !scenery)
        {
            if (modelLoaded)
            {
                float s = viewScene.ModelScale(viewRadius);
                ViewScene.DrawFloorGrid(s * viewLeft, s * viewRight, s * MathF.Max(viewRight - viewLeft, viewHeight));
            }
            else
            {
                Raylib.DrawGrid(200, 10f);
            }
        }

        if (modelLoaded)
        {
            if (scenery)
            {
                viewScene.DrawScenery();
            }
            viewScene.DrawStanding(model, 0f, viewLowest, mapCard, viewRadius);
#if DEBUG
            if (viewScene.DebugLines && viewScene.Placement == ViewScene.PlaceBoard)
            {
                Rlgl.DrawRenderBatchActive();
                Rlgl.DisableDepthTest();
                viewScene.DrawDebugViewLines(model, 0f, viewLowest, mapCard, viewRadius,
                    new Vector3(viewLeft, viewLowest, viewBack),
                    new Vector3(viewRight, viewLowest + viewHeight, viewFront));
                Rlgl.DrawRenderBatchActive();
                Rlgl.EnableDepthTest();
            }
#endif
        }

        Raylib.EndMode3D();
        Raylib.EndTextureMode();

        rlImGui.ImageRenderTexture(sceneTexture);
        ImGui.EndChild();
    }
}