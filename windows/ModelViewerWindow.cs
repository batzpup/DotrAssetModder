using ImGuiNET;
using Raylib_cs;
using rlImGui_cs;
using System.Numerics;
using DotrAssetModder;
using NativeFileDialogSharp;

public class ModelViewerWindow
{
    Camera3D camera;

    AnimatedGltfModel model;
    bool modelLoaded = false;
    string loadError = null;


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

    float orbitYaw = 0f;
    float orbitPitch = 0.3f;
    float orbitDistance = 6f;
    Vector3 orbitTarget = Vector3.Zero;

    Vector2 savedMousePos;
    bool freeLooking = false;
    int flySpeed = 50;
    float lookSensitivity = 0.0035f;


    float clipNear = 0.01f;
    float clipFar = 1000f;
    bool resetPressed;
    bool showgrid = true;

    public ModelViewerWindow()
    {
        camera = new Camera3D {
            Position = new Vector3(0, 2, 6),
            Target = Vector3.Zero,
            Up = Vector3.UnitY,
            FovY = 60,
            Projection = CameraProjection.Perspective
        };

        sceneTexture = Raylib.LoadRenderTexture(viewportWidth, viewportHeight);
    }

    void LoadModel()
    {
        DialogResult dialogResult = Dialog.FileOpen("glb,gltf");
        if (!dialogResult.IsOk)
            return;

        AnimatedGltfModel newModel;
        try
        {
            newModel = AnimatedGltfModel.Load(dialogResult.Path);
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

        orbitTarget = Vector3.Zero;
        orbitYaw = 0f;
        orbitPitch = 0.3f;

        float radius = model.Radius;
        orbitDistance = MathF.Max(radius * 2.5f, 0.5f);
        clipNear = MathF.Max(radius * 0.05f, 0.01f);
        clipFar = MathF.Max(radius * 20f, 100f);


        int initAnim = (!staticPose && model.AnimationCount > 0) ? currentAnim : -1;
        model.SetAnimationFrame(initAnim, 0f);
        needsAnimUpdate = false;
        needsStaticRefresh = false;
    }

    Vector3 OrbitForward()
    {
        float cp = MathF.Cos(orbitPitch);
        Vector3 offset = new Vector3(cp * MathF.Sin(orbitYaw), MathF.Sin(orbitPitch), cp * MathF.Cos(orbitYaw));
        return -offset;
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

        Vector3 cameraPos = orbitTarget - OrbitForward() * orbitDistance;
        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0)
            orbitDistance = Math.Clamp(orbitDistance - wheel * (orbitDistance * 0.1f), 0.1f, 5000f);

        if (Raylib.IsMouseButtonDown(MouseButton.Left))
        {
            Vector2 delta = Raylib.GetMouseDelta();
            orbitYaw -= delta.X * lookSensitivity;
            orbitPitch += delta.Y * lookSensitivity;
            orbitPitch = Math.Clamp(orbitPitch, -1.5f, 1.5f);
        }

        if (Raylib.IsMouseButtonDown(MouseButton.Right) && !rightPressed)
        {
            Vector2 delta = Raylib.GetMouseDelta();
            orbitYaw -= delta.X * lookSensitivity;
            orbitPitch += delta.Y * lookSensitivity;
            orbitPitch = Math.Clamp(orbitPitch, -1.5f, 1.5f);
            orbitTarget = cameraPos + OrbitForward() * orbitDistance;
            Raylib.SetMousePosition(Raylib.GetScreenWidth() / 2, Raylib.GetScreenHeight() / 2);
        }

        Vector3 forward = OrbitForward();
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));

        if (Raylib.IsMouseButtonDown(MouseButton.Middle))
        {
            Vector2 delta = Raylib.GetMouseDelta();
            float panSpeed = orbitDistance * 0.0015f;
            orbitTarget += (-right * delta.X + up * delta.Y) * panSpeed;
        }

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
            orbitTarget += Vector3.Normalize(move) * moveSpeed;

        camera.Position = orbitTarget - forward * orbitDistance;
        camera.Target = orbitTarget;
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
            LoadModel();
        }

        ImGui.SameLine();
        if (!modelLoaded)
            ImGui.TextDisabled("No model loaded");

        if (loadError != null)
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "Load failed: " + loadError);

        if (ImGui.Button("Reset View"))
        {
            orbitTarget = Vector3.Zero;
            orbitYaw = 0f;
            orbitPitch = 0.3f;
            if (modelLoaded)
            {
                orbitDistance = MathF.Max(model.Radius * 2.5f, 0.5f);
                resetPressed = true;
            }
        }
        ImGui.SameLine();
        ImGui.Checkbox("Show Grid", ref showgrid);
        ImGui.SameLine();
        ImGui.SliderInt("Fly Speed", ref flySpeed, 40, 500);
        ImGui.Text("WASD to move, space and left Ctrl to go up and down\nleft click drag to orbit, right click drag to free-look, middle click drag to pan, mouse wheel to zoom");


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
                    animPlaying = !animPlaying;
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
                resetPressed = false;
            UpdateCamera();
        }


        AdvanceAnimation();

        Raylib.BeginTextureMode(sceneTexture);
        Raylib.ClearBackground(Color.DarkGray);

        Raylib.BeginMode3D(camera);
        Rlgl.SetClipPlanes(clipNear, clipFar);
        if (showgrid)
            Raylib.DrawGrid(200, 10f);

        if (modelLoaded)
            model.Draw();

        Raylib.EndMode3D();
        Raylib.EndTextureMode();

        rlImGui.ImageRenderTexture(sceneTexture);
        ImGui.EndChild();
    }
}