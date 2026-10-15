using System.Numerics;
using ImGuiNET;
using Raylib_cs;
using rlImGui_cs;
using static DotrAssetModder.GuiContext;

namespace DotrAssetModder;

public partial class ModelImporterWindow
{
    // The Move, Rotate and Pivot gizmos on a View import, each shown while its toggle is on. A
    // handle is 0-2 the X/Y/Z arrow or ring, 3 the centre (Move, Pivot).
    const int GizmoMove = 0;
    const int GizmoRotate = 1;
    const int GizmoPivot = 2;
    int gizmoKind = -1; // the gizmo being dragged, -1 none
    int gizmoDrag = -1; // its handle
    int gizmoHoverKind = -1;
    int gizmoHover = -1;
    Vector2 gizmoStartMouse;
    Vector3 gizmoStartValue; // the dragged values (optMove/optRotate/optPivotAt) when it began
    Vector3 gizmoStartCentre; // the gizmo's centre when it began, world units
    float gizmoLength; // the arrows' length when the drag began
    float gizmoLastAngle; // Rotate: the mouse's angle round the centre last frame
    float gizmoTurned; // Rotate: degrees turned since the drag began

    // keepCamera: a View import redone after an option changed; the camera, the animation and
    // the window stay as they are, so the change shows against the grid.
    void LoadPreview(AnimatedGltfModel model, string name, bool keepCamera)
    {
        if (previewModel != null)
        {
            previewModel.Dispose();
        }
        previewModel = model;
        previewName = name;
        previewLoaded = true;
        previewOpen = true;
        if (!keepCamera || currentAnim >= previewModel.AnimationCount)
        {
            previewShow = !keepCamera;
            currentAnim = 0;
            animTime = 0f;
            animPlaying = previewModel.AnimationCount > 0;
        }

        previewModel.MeasureStanding(IdleAnimIndex(previewModel), out previewLowest, out previewHeight,
            out previewLeft, out previewRight, out previewBack, out previewFront);
        if (!keepCamera)
        {
            // stood on the grid when first viewed; a redo after an option changed keeps it
            standLowest = previewLowest - previewMove.Y;
        }
        previewRadius = viewRadius;

        if (previewModel.AnimationCount > 0)
        {
            previewModel.SetAnimationFrame(currentAnim, animTime);
        }
        else
        {
            previewModel.SetAnimationFrame(-1, 0f);
        }

        PlaceCompare();
        if (!keepCamera)
        {
            FrameStanding();
        }
        RequestCompare();
    }

    void DrawPreview()
    {
        ImGui.Text(previewName);
        if (previewModel.AnimationCount > 0)
        {
            ImGui.SameLine();
            string label = "Play";
            if (animPlaying)
            {
                label = "Pause";
            }
            if (ImGui.Button(label))
            {
                animPlaying = !animPlaying;
            }

            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.5f);
            if (ImGui.Combo("Animation", ref currentAnim, previewModel.AnimationNames, previewModel.AnimationNames.Length))
            {
                animTime = 0f;
                animPlaying = true;
            }
        }
        DrawCompareControls();

        // NoMove: a drag on the canvas orbits the model; only the title bar moves the window
        ImGui.BeginChild("preview_vp", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.NoMove);
        Vector2 imageMin = ImGui.GetCursorScreenPos();
        Vector2 available = ImGui.GetContentRegionAvail();
        int width = Math.Max((int)available.X, 1);
        int height = Math.Max((int)available.Y, 1);

        if (!previewTextureReady || width != previewTextureWidth || height != previewTextureHeight)
        {
            if (previewTextureReady)
            {
                Raylib.UnloadRenderTexture(previewTexture);
            }
            previewTexture = Raylib.LoadRenderTexture(width, height);
            previewTextureWidth = width;
            previewTextureHeight = height;
            previewTextureReady = true;
        }

        bool hovered = ImGui.IsWindowHovered();
        bool gizmoBusy = UpdateGizmos(ImGui.GetMousePos() - imageMin, hovered);
        if (hovered && !gizmoBusy)
        {
            orbitCamera.UpdateMouse();
        }
        AdvanceAnimation();
        AdvanceCompare();
        if (aimAtPivot)
        {
            orbitCamera.Target = PivotWorld();
        }
        orbitCamera.Apply();
        RenderScene();

        rlImGui.ImageRenderTexture(previewTexture);
        ImGui.EndChild();
    }

    // The placement for every preview; Compare only for an import or a staged model.
    void DrawCompareControls()
    {
        ImGui.Checkbox("Aim at pivot", ref aimAtPivot);
        HoveredTooltip(@"The camera looks at node 1 (the Pivot) as it moves with the animation, as the game's duel board
focus camera and the camera that follows a monster after a hit do. Drag to turn, wheel to zoom.
The battle attack and win cameras are the card's own camera paths, set from node 0 instead.");
        ImGui.SameLine();
        if (previewCanCompare)
        {
            if (ImGui.Checkbox("Compare", ref compareOn))
            {
                RequestCompare();
                PlaceCompare();
                FrameStanding();
            }
            HoveredTooltip(@"Stand a second model beside this one to compare sizes.
It is this card's model as it is on the ISO: the model the import replaces.");
            if (compareOn)
            {
                ImGui.SameLine();
                if (ImGui.Checkbox("Dark Magician instead", ref compareDarkMagician))
                {
                    RequestCompare();
                }
                HoveredTooltip("Compare with Dark Magician (card 060): 24.8 tall, about the median retail monster.");
            }
            if (previewIsMonster)
            {
                ImGui.SameLine(); // the View combo follows (monsters only)
            }
        }
        int card = previewIndex;
        string extraTooltip = "";
        if (previewIsImport)
        {
            extraTooltip = @"
Pivot and Position show here; an import keeps the replaced card's map height.
The radius of an import can be set in the Convert window's Advanced options.";
        }
        int heightBefore = modelMrg.Tables.MapLeaderY(card);
        if (viewScene.DrawControls(previewIsMonster, ref card, false, extraTooltip))
        {
            PlaceCompare();
            FrameStanding();
        }
        if (modelMrg.Tables.MapLeaderY(card) != heightBefore)
        {
            mapHeightEdits.Add(card);
        }

        ImGui.TextDisabled(viewScene.StandingText(previewLabel, previewHeight, PreviewRadius()));
        if (!compareOn || !previewCanCompare)
        {
            return;
        }
        if (compareTask != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("   Loading the compare model...");
        }
        else if (compareModel != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("   " + viewScene.StandingText(compareName + " (left)", compareHeight, compareRadius));
        }
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
            float duration = previewModel.Duration(currentAnim);
            if (duration > 0f)
            {
                animTime += Raylib.GetFrameTime();
                animTime %= duration;
            }
        }

        previewModel.SetAnimationFrame(currentAnim, animTime);
    }

    // The compare model plays its idle on its own clock; Pause stops it too.
    void AdvanceCompare()
    {
        if (!CompareShown() || compareAnim < 0)
        {
            return;
        }
        compareModel.AdvanceTextureAnim(Raylib.GetFrameTime());
        if (animPlaying)
        {
            float duration = compareModel.Duration(compareAnim);
            if (duration > 0f)
            {
                compareTime += Raylib.GetFrameTime();
                compareTime %= duration;
            }
        }
        compareModel.SetAnimationFrame(compareAnim, compareTime);
    }

    void RenderScene()
    {
        bool compare = CompareShown();
        if (previewCanCompare)
        {
            PlaceCompare(); // the Radius option may have changed the shrink
        }
        float drawn = viewScene.ModelScale(PreviewRadius()) * PreviewRadius();
        if (compare)
        {
            drawn = MathF.Max(drawn, viewScene.ModelScale(compareRadius) * compareRadius) + MathF.Abs(compareOffsetX);
        }

        Raylib.BeginTextureMode(previewTexture);
        Raylib.ClearBackground(Color.DarkGray);
        viewScene.SetClipPlanes(orbitCamera.Camera, drawn);
        Raylib.BeginMode3D(orbitCamera.Camera);
        bool scenery = viewScene.SceneryShown;
        if (!scenery)
        {
            DrawFloorGrid(compare);
        }
        if (scenery)
        {
            viewScene.DrawScenery();
        }
        // a View import follows the options at once; it is converted again when a change is done
        bool live = ImportPreviewShown();
        if (live)
        {
            Rlgl.PushMatrix();
            ApplyLiveTransform();
        }
        viewScene.DrawStanding(previewModel, 0f, ShownLowest(), previewIndex, PreviewRadius());
        if (live)
        {
            Rlgl.PopMatrix();
        }
        if (compare)
        {
            viewScene.DrawStanding(compareModel, compareOffsetX, compareLowest, compareCard, compareRadius);
        }
#if DEBUG
        if (viewScene.DebugLines && viewScene.Placement == ViewScene.PlaceBoard)
        {
            DrawDebugLines(compare);
        }
#endif
        DrawGizmos();
        Raylib.EndMode3D();
        Raylib.EndTextureMode();
    }

    // The Convert window's View import is on screen: the preview follows the options live and
    // the gizmos can show.
    bool ImportPreviewShown()
    {
        return convertOpen && previewLoaded && previewIsImport && previewIndex == convertIndex;
    }

    // The gizmos show on the View import, each while its toggle is on (Pivot's only where it is
    // edited, PivotEditable).
    bool GizmoShown(int kind)
    {
        if (!ImportPreviewShown())
        {
            return false;
        }
        if (kind == GizmoMove)
        {
            return optMoveOn;
        }
        if (kind == GizmoRotate)
        {
            return optRotateOn;
        }
        return optPivotOn && PivotEditable();
    }

    // The Pivot is edited in Feet on grid only: the board and map stand node 1 over the tile or
    // arena point, so moving it there shifts the body the other way.
    bool PivotEditable()
    {
        return viewScene.Placement == ViewScene.PlaceFeet;
    }

    // Rotate as the converter gets it: zero while its toggle is off.
    Vector3 RotateApplied()
    {
        if (optRotateOn)
        {
            return optRotate;
        }
        return Vector3.Zero;
    }

    /* Live preview. The shown glb was converted (or a card edited) with previewScale/Rotate/Move;
       the options as they stand map its point g to k * T * (g - c0 - previewMove) + LiveShift()
       (both scale and turn about TurnCentre, then move), k the Scale ratio, T the turn from the old
       Rotate to the new. Not exact where the converter does more (a new rest pose), so the
       model is converted again once a change is done. */
    float LiveScale()
    {
        return optScale / previewScale;
    }

    Matrix4 LiveTurn()
    {
        Vector3 rotateNow = RotateApplied();
        Matrix4 turn = Matrix4.EulerAngles(rotateNow.X, rotateNow.Y, rotateNow.Z);
        return turn * Matrix4.EulerAngles(previewRotate.X, previewRotate.Y, previewRotate.Z).Inverse();
    }

    static Vector3 Turn(Matrix4 matrix, Vector3 vector)
    {
        return new Vector3(
            (float)(matrix[0, 0] * vector.X + matrix[0, 1] * vector.Y + matrix[0, 2] * vector.Z),
            (float)(matrix[1, 0] * vector.X + matrix[1, 1] * vector.Y + matrix[1, 2] * vector.Z),
            (float)(matrix[2, 0] * vector.X + matrix[2, 1] * vector.Y + matrix[2, 2] * vector.Z));
    }

    // Node 0 in the shown glb: the origin for a converted one, where the extractor's MrgRoot
    // puts it (-node 1's rest offset) for a card's.
    Vector3 NodeZero()
    {
        return previewModel.RestNodeZero;
    }

    // Where Scale and Rotate turn the body: node 1 where the Pivot puts it, as the game turns and
    // shrinks it about node 1.
    Vector3 TurnCentre()
    {
        return NodeZero() + LivePivot();
    }

    // Where the shown glb's turn centre (its own node 1, c0) lands with the options as they stand:
    // c + Move + S R (c0 - c), c the centre as the options stand.
    Vector3 LiveShift()
    {
        Vector3 centre = TurnCentre();
        Vector3 rotateNow = RotateApplied();
        return centre + MoveApplied() + optScale * Turn(Matrix4.EulerAngles(rotateNow.X, rotateNow.Y, rotateNow.Z), previewModel.RestPivot - centre);
    }

    // Node 1 from node 0 with the options as they stand: on the Pivot while its toggle is on, else
    // where it is without one (Move does not carry it): on node 0 for an import, a card's own.
    Vector3 LivePivot()
    {
        if (!optPivotOn)
        {
            if (convertIsCard)
            {
                return cardPivotRest;
            }
            return Vector3.Zero;
        }
        return optPivotAt;
    }

    // Feet's stand height: set when the model is first viewed (standLowest), so a later Scale,
    // Rotate or Move lifts or sinks it from there instead of standing it back on the grid.
    float ShownLowest()
    {
        return standLowest;
    }

    // The same with the options as they stand (unchanged: the options move the model freely).
    float LiveLowest()
    {
        return standLowest;
    }

    // Where DrawStanding puts the glb's origin for a model whose node 1 rests at `pivot` and
    // whose lowest idle point is `lowest`.
    Vector3 StandAt(Vector3 pivot, float lowest)
    {
        float modelScale = viewScene.ModelScale(PreviewRadius());
        Vector3 shift = viewScene.StandShift(pivot, previewModel.RestNodeZero, modelScale);
        return new Vector3(shift.X, viewScene.StandY(lowest, previewIndex) + shift.Y, shift.Z);
    }

    // Where the preview draws point `glbPoint` of a glb converted with the options as they stand.
    Vector3 LiveWorld(Vector3 glbPoint)
    {
        return StandAt(NodeZero() + LivePivot(), LiveLowest()) + viewScene.ModelScale(PreviewRadius()) * glbPoint;
    }

    // Node 1 where the preview draws it: on the Pivot as the options stand for a View import
    // (a converted model's node 1 does not move), else the shown model's node 1 in its current pose.
    Vector3 PivotWorld()
    {
        if (ImportPreviewShown())
        {
            return LiveWorld(NodeZero() + LivePivot());
        }
        float modelScale = viewScene.ModelScale(PreviewRadius());
        return StandAt(previewModel.RestPivot, ShownLowest()) + modelScale * previewModel.PivotNow();
    }

    /* Before DrawStanding: takes the shown glb's drawn point W(g) to LiveWorld of where the options
       as they stand put g,
       W(g) = A + s g: translate(A' + s LiveShift) * scale(k) * turn * translate(-(A + s (c0 + previewMove))). */
    void ApplyLiveTransform()
    {
        float modelScale = viewScene.ModelScale(PreviewRadius());
        Vector3 fromPoint = StandAt(previewModel.RestPivot, ShownLowest()) + modelScale * (previewModel.RestPivot + previewMove);
        Vector3 toPoint = StandAt(NodeZero() + LivePivot(), LiveLowest()) + modelScale * LiveShift();
        Rlgl.Translatef(toPoint.X, toPoint.Y, toPoint.Z);
        float scaleRatio = LiveScale();
        Rlgl.Scalef(scaleRatio, scaleRatio, scaleRatio);
        Vector3 axis;
        float degrees;
        AxisAngle(LiveTurn(), out axis, out degrees);
        if (degrees != 0f)
        {
            Rlgl.Rotatef(degrees, axis.X, axis.Y, axis.Z);
        }
        Rlgl.Translatef(-fromPoint.X, -fromPoint.Y, -fromPoint.Z);
    }

    // A turn matrix as an axis and an angle in degrees (0 for none).
    static void AxisAngle(Matrix4 matrix, out Vector3 axis, out float degrees)
    {
        double cosine = Math.Clamp((matrix[0, 0] + matrix[1, 1] + matrix[2, 2] - 1.0) * 0.5, -1.0, 1.0);
        double angle = Math.Acos(cosine);
        axis = Vector3.UnitY;
        degrees = 0f;
        if (angle < 1e-5)
        {
            return;
        }
        Vector3 axisVector = new Vector3((float)(matrix[2, 1] - matrix[1, 2]), (float)(matrix[0, 2] - matrix[2, 0]), (float)(matrix[1, 0] - matrix[0, 1]));
        if (Math.Sin(angle) < 1e-3)
        {
            // a half turn: the axis from the diagonal, signs from the largest part
            float x = (float)Math.Sqrt(Math.Max(0.0, (matrix[0, 0] + 1.0) * 0.5));
            float y = (float)Math.Sqrt(Math.Max(0.0, (matrix[1, 1] + 1.0) * 0.5));
            float z = (float)Math.Sqrt(Math.Max(0.0, (matrix[2, 2] + 1.0) * 0.5));
            if (x >= y && x >= z)
            {
                y = MathF.CopySign(y, (float)matrix[0, 1]);
                z = MathF.CopySign(z, (float)matrix[0, 2]);
            }
            else if (y >= z)
            {
                x = MathF.CopySign(x, (float)matrix[0, 1]);
                z = MathF.CopySign(z, (float)matrix[1, 2]);
            }
            else
            {
                x = MathF.CopySign(x, (float)matrix[0, 2]);
                y = MathF.CopySign(y, (float)matrix[1, 2]);
            }
            axisVector = new Vector3(x, y, z);
        }
        if (axisVector.LengthSquared() < 1e-12f)
        {
            return;
        }
        axis = Vector3.Normalize(axisVector);
        degrees = (float)(angle * 180.0 / Math.PI);
    }

    // Rotate's X, Y, Z (degrees, X turned first) for turn matrix `matrix` = Rz * Ry * Rx.
    static Vector3 EulerOf(Matrix4 matrix)
    {
        double y = Math.Asin(Math.Clamp(-matrix[2, 0], -1.0, 1.0));
        double x;
        double z;
        if (Math.Cos(y) > 1e-6)
        {
            x = Math.Atan2(matrix[2, 1], matrix[2, 2]);
            z = Math.Atan2(matrix[1, 0], matrix[0, 0]);
        }
        else
        {
            x = Math.Atan2(-matrix[1, 2], matrix[1, 1]); // straight up or down: all the turn in X
            z = 0.0;
        }
        double degreesPerRadian = 180.0 / Math.PI;
        return new Vector3((float)(x * degreesPerRadian), (float)(y * degreesPerRadian), (float)(z * degreesPerRadian));
    }

    // Each gizmo's centre with the values as they stand: Move on node 0 moved by Move, Rotate on
    // the point the body turns about (TurnCentre) moved by Move, Pivot on node 1.
    Vector3 GizmoCentre(int kind)
    {
        if (kind == GizmoMove)
        {
            return LiveWorld(NodeZero() + MoveApplied());
        }
        if (kind == GizmoRotate)
        {
            return LiveWorld(TurnCentre() + MoveApplied());
        }
        return LiveWorld(NodeZero() + LivePivot());
    }

    // The values a gizmo drags.
    Vector3 GizmoValue(int kind)
    {
        if (kind == GizmoMove)
        {
            return optMove;
        }
        if (kind == GizmoRotate)
        {
            return optRotate;
        }
        return optPivotAt;
    }

    void SetGizmoValue(int kind, Vector3 value)
    {
        if (kind == GizmoMove)
        {
            optMove = value;
        }
        else if (kind == GizmoRotate)
        {
            optRotate = value;
        }
        else
        {
            optPivotAt = value;
        }
    }

    // Pivot's arrows are shorter than Position's, so both show where they share a spot.
    float ArrowLength(int kind)
    {
        if (kind == GizmoPivot)
        {
            return GizmoLength() * 0.6f;
        }
        return GizmoLength();
    }

    float GizmoLength()
    {
        if (gizmoKind >= 0)
        {
            return gizmoLength; // steady while dragging
        }
        return orbitCamera.Distance * 0.15f;
    }

    // Rotate's rings are a little wider than the arrows, so they do not meet the arrow tips.
    float RingRadius()
    {
        return GizmoLength() * 1.25f;
    }

    // A point on the ring about axis `axisIndex`, at angle `angle` (radians).
    static Vector3 RingPoint(Vector3 centre, int axisIndex, float radius, float angle)
    {
        Vector3 firstDirection = Vector3.UnitY;
        Vector3 secondDirection = Vector3.UnitZ;
        if (axisIndex == 1)
        {
            firstDirection = Vector3.UnitZ;
            secondDirection = Vector3.UnitX;
        }
        else if (axisIndex == 2)
        {
            firstDirection = Vector3.UnitX;
            secondDirection = Vector3.UnitY;
        }
        return centre + radius * (MathF.Cos(angle) * firstDirection + MathF.Sin(angle) * secondDirection);
    }

    const int RingSegments = 48;

    // A ring piece on the far side of its centre from the camera: drawn faint, not grabbed.
    bool RingBack(Vector3 centre, Vector3 pieceStart, Vector3 pieceEnd)
    {
        return Vector3.Dot((pieceStart + pieceEnd) * 0.5f - centre, orbitCamera.Camera.Position - centre) < 0f;
    }

    static Vector3 GizmoAxis(int k)
    {
        if (k == 0)
        {
            return Vector3.UnitX;
        }
        if (k == 1)
        {
            return Vector3.UnitY;
        }
        return Vector3.UnitZ;
    }

    Vector2 PreviewToScreen(Vector3 worldPoint)
    {
        return Raylib.GetWorldToScreenEx(worldPoint, orbitCamera.Camera, previewTextureWidth, previewTextureHeight);
    }

    // The gizmo and handle under `mouse` (pixels in the preview image): a centre first, then the
    // nearest arrow or ring. False when none.
    bool GizmoHit(Vector2 mouse, out int kind, out int handle)
    {
        kind = -1;
        handle = -1;
        int[] arrowKinds = { GizmoPivot, GizmoMove };
        foreach (int arrowKind in arrowKinds)
        {
            // Pivot's centre is the smaller one, inside Position's when they share a spot
            float reach = arrowKind == GizmoPivot ? 5f : 9f;
            if (GizmoShown(arrowKind) && Vector2.Distance(mouse, PreviewToScreen(GizmoCentre(arrowKind))) < reach)
            {
                kind = arrowKind;
                handle = 3;
                return true;
            }
        }
        float bestDistance = 7f;
        foreach (int arrowKind in arrowKinds)
        {
            if (!GizmoShown(arrowKind))
            {
                continue;
            }
            Vector3 centre = GizmoCentre(arrowKind);
            Vector2 centreOnScreen = PreviewToScreen(centre);
            for (int k = 0; k < 3; k++)
            {
                float distance = DistanceToSegment(mouse, centreOnScreen, PreviewToScreen(centre + GizmoAxis(k) * ArrowLength(arrowKind)));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    kind = arrowKind;
                    handle = k;
                }
            }
        }
        if (GizmoShown(GizmoRotate))
        {
            Vector3 ringCentre = GizmoCentre(GizmoRotate);
            float ringRadius = RingRadius();
            for (int k = 0; k < 3; k++)
            {
                Vector3 previousPoint = RingPoint(ringCentre, k, ringRadius, 0f);
                for (int i = 1; i <= RingSegments; i++)
                {
                    Vector3 nextPoint = RingPoint(ringCentre, k, ringRadius, i * 2f * MathF.PI / RingSegments);
                    if (!RingBack(ringCentre, previousPoint, nextPoint))
                    {
                        float distance = DistanceToSegment(mouse, PreviewToScreen(previousPoint), PreviewToScreen(nextPoint));
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            kind = GizmoRotate;
                            handle = k;
                        }
                    }
                    previousPoint = nextPoint;
                }
            }
        }
        return kind >= 0;
    }

    static float DistanceToSegment(Vector2 point, Vector2 segmentStart, Vector2 segmentEnd)
    {
        Vector2 segment = segmentEnd - segmentStart;
        float along = 0f;
        if (segment.LengthSquared() > 0f)
        {
            along = Math.Clamp(Vector2.Dot(point - segmentStart, segment) / segment.LengthSquared(), 0f, 1f);
        }
        return Vector2.Distance(point, segmentStart + segment * along);
    }

    // Hover, start and drag the gizmos. True while one has the mouse, so the view does not orbit.
    // The fields and the preview follow the drag; the model is converted again when it is let go.
    bool UpdateGizmos(Vector2 mouse, bool hovered)
    {
        if (gizmoKind >= 0 && !GizmoShown(gizmoKind))
        {
            gizmoKind = -1;
            gizmoDrag = -1;
        }
        if (gizmoKind >= 0)
        {
            if (!Raylib.IsMouseButtonDown(MouseButton.Left))
            {
                if (GizmoValue(gizmoKind) != gizmoStartValue)
                {
                    importDirty = true;
                }
                gizmoKind = -1;
                gizmoDrag = -1;
                return false;
            }
            DragGizmo(mouse);
            return true;
        }
        gizmoHoverKind = -1;
        gizmoHover = -1;
        if (hovered)
        {
            GizmoHit(mouse, out gizmoHoverKind, out gizmoHover);
        }
        if (gizmoHoverKind >= 0 && Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            gizmoStartCentre = GizmoCentre(gizmoHoverKind);
            gizmoLength = GizmoLength();
            gizmoKind = gizmoHoverKind;
            gizmoDrag = gizmoHover;
            gizmoStartMouse = mouse;
            gizmoStartValue = GizmoValue(gizmoKind);
            Vector2 mouseFromCentre = mouse - PreviewToScreen(gizmoStartCentre);
            gizmoLastAngle = MathF.Atan2(mouseFromCentre.Y, mouseFromCentre.X);
            gizmoTurned = 0f;
            return true;
        }
        return false;
    }

    /* An arrow moves along its axis as far as the mouse goes along the arrow on screen; a centre
       moves across the view; a ring turns about its (world) axis as far as the mouse goes round
       the centre. Ctrl snaps moved values to whole units. */
    void DragGizmo(Vector2 mouse)
    {
        Vector3 centre = gizmoStartCentre;
        if (gizmoKind == GizmoRotate)
        {
            Vector2 mouseFromCentre = mouse - PreviewToScreen(centre);
            float angle = MathF.Atan2(mouseFromCentre.Y, mouseFromCentre.X);
            float step = angle - gizmoLastAngle;
            if (step > MathF.PI)
            {
                step -= 2f * MathF.PI;
            }
            else if (step < -MathF.PI)
            {
                step += 2f * MathF.PI;
            }
            gizmoLastAngle = angle;
            // screen Y runs down, so a growing angle is clockwise on screen: a turn the other way
            // about an axis that points at the camera
            Vector3 axis = GizmoAxis(gizmoDrag);
            float sign = 1f;
            if (Vector3.Dot(axis, orbitCamera.Camera.Position - centre) > 0f)
            {
                sign = -1f;
            }
            gizmoTurned += sign * step * 180f / MathF.PI;
            Matrix4 start = Matrix4.EulerAngles(gizmoStartValue.X, gizmoStartValue.Y, gizmoStartValue.Z);
            Matrix4 turned = Matrix4.Rotation(axis.X, axis.Y, axis.Z, gizmoTurned) * start;
            SetGizmoValue(gizmoKind, EulerOf(turned));
            return;
        }
        Vector2 mouseMoved = mouse - gizmoStartMouse;
        Vector3 world;
        if (gizmoDrag == 3)
        {
            float distance = Vector3.Distance(orbitCamera.Camera.Position, centre);
            float perPixel = 2f * distance * MathF.Tan(orbitCamera.Camera.FovY * 0.5f * MathF.PI / 180f) / Math.Max(previewTextureHeight, 1);
            world = (orbitCamera.Right * mouseMoved.X - orbitCamera.Up * mouseMoved.Y) * perPixel;
        }
        else
        {
            Vector3 axis = GizmoAxis(gizmoDrag);
            Vector2 arrowOnScreen = PreviewToScreen(centre + axis * gizmoLength) - PreviewToScreen(centre);
            if (arrowOnScreen.LengthSquared() < 4f)
            {
                return; // the arrow points at the camera
            }
            world = axis * (Vector2.Dot(mouseMoved, arrowOnScreen) / arrowOnScreen.LengthSquared() * gizmoLength);
        }
        float modelScale = viewScene.ModelScale(PreviewRadius());
        Vector3 newValue = gizmoStartValue + world / modelScale;
        if (Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl))
        {
            if (gizmoDrag == 0 || gizmoDrag == 3)
            {
                newValue.X = MathF.Round(newValue.X);
            }
            if (gizmoDrag == 1 || gizmoDrag == 3)
            {
                newValue.Y = MathF.Round(newValue.Y);
            }
            if (gizmoDrag == 2 || gizmoDrag == 3)
            {
                newValue.Z = MathF.Round(newValue.Z);
            }
        }
        SetGizmoValue(gizmoKind, newValue);
    }

    /* Red X, green Y, blue Z, drawn over the model: Move's and Pivot's arrows (Move with a ball
       in the middle; Pivot shorter, square tips, a small white cube) and Rotate's rings (the far
       half faint). The handle under the mouse or being dragged turns yellow. */
    void DrawGizmos()
    {
        if (!GizmoShown(GizmoMove) && !GizmoShown(GizmoRotate) && !GizmoShown(GizmoPivot))
        {
            return;
        }
        Rlgl.DrawRenderBatchActive();
        Rlgl.DisableDepthTest();
        int litKind = gizmoHoverKind;
        int litHandle = gizmoHover;
        if (gizmoKind >= 0)
        {
            litKind = gizmoKind;
            litHandle = gizmoDrag;
        }
        Color[] colors = { new Color(230, 70, 70, 255), new Color(120, 200, 60, 255), new Color(70, 130, 240, 255) };
        float baseLength = GizmoLength();
        if (GizmoShown(GizmoRotate))
        {
            Vector3 ringCentre = GizmoCentre(GizmoRotate);
            if (gizmoKind == GizmoRotate)
            {
                ringCentre = gizmoStartCentre;
            }
            float ringRadius = RingRadius();
            for (int k = 0; k < 3; k++)
            {
                Color colour = colors[k];
                if (litKind == GizmoRotate && litHandle == k)
                {
                    colour = Color.Yellow;
                }
                Color faint = new Color(colour.R, colour.G, colour.B, (byte)60);
                Vector3 previousPoint = RingPoint(ringCentre, k, ringRadius, 0f);
                for (int i = 1; i <= RingSegments; i++)
                {
                    Vector3 nextPoint = RingPoint(ringCentre, k, ringRadius, i * 2f * MathF.PI / RingSegments);
                    Color piece = colour;
                    if (RingBack(ringCentre, previousPoint, nextPoint))
                    {
                        piece = faint;
                    }
                    Raylib.DrawCylinderEx(previousPoint, nextPoint, baseLength * 0.012f, baseLength * 0.012f, 4, piece);
                    previousPoint = nextPoint;
                }
            }
        }
        int[] arrowKinds = { GizmoMove, GizmoPivot };
        foreach (int arrowKind in arrowKinds)
        {
            if (!GizmoShown(arrowKind))
            {
                continue;
            }
            Vector3 gizmoCentre = GizmoCentre(arrowKind);
            for (int k = 0; k < 3; k++)
            {
                Color colour = colors[k];
                if (litKind == arrowKind && litHandle == k)
                {
                    colour = Color.Yellow;
                }
                Vector3 axis = GizmoAxis(k);
                float arrow = ArrowLength(arrowKind);
                if (arrowKind == GizmoPivot)
                {
                    // Pivot: a shorter shaft with a square tip
                    float tipSize = baseLength * 0.07f;
                    Raylib.DrawCylinderEx(gizmoCentre, gizmoCentre + axis * (arrow - tipSize * 0.5f), baseLength * 0.015f, baseLength * 0.015f, 6, colour);
                    Raylib.DrawCube(gizmoCentre + axis * arrow, tipSize, tipSize, tipSize, colour);
                    continue;
                }
                Vector3 neck = gizmoCentre + axis * (arrow * 0.8f);
                Raylib.DrawCylinderEx(gizmoCentre, neck, baseLength * 0.015f, baseLength * 0.015f, 6, colour);
                Raylib.DrawCylinderEx(neck, gizmoCentre + axis * arrow, baseLength * 0.06f, 0f, 8, colour);
            }
            Color centre = Color.White;
            if (litKind == arrowKind && litHandle == 3)
            {
                centre = Color.Yellow;
            }
            float centreSize = baseLength * 0.08f;
            if (arrowKind == GizmoMove)
            {
                Raylib.DrawSphere(gizmoCentre, centreSize * 0.7f, centre);
            }
            else
            {
                Raylib.DrawCube(gizmoCentre, centreSize * 0.6f, centreSize * 0.6f, centreSize * 0.6f, centre);
            }
        }
        Rlgl.DrawRenderBatchActive();
        Rlgl.EnableDepthTest();
    }

    // The grid under the preview and, when shown, the compare model. Fitted to node 0's radius
    // around node 0, not the idle extent, so Rotate does not move or resize it.
    void DrawFloorGrid(bool compare)
    {
        float modelScale = viewScene.ModelScale(PreviewRadius());
        float centre = viewScene.StandShift(previewModel, modelScale).X + modelScale * previewModel.RestNodeZero.X;
        float reach = modelScale * PreviewRadius();
        float left = centre - reach;
        float right = centre + reach;
        float deep = 2f * reach;
        if (compare)
        {
            float compareModelScale = viewScene.ModelScale(compareRadius);
            left = MathF.Min(left, compareOffsetX + compareModelScale * compareLeft);
            deep = MathF.Max(deep, compareModelScale * MathF.Max(compareRight - compareLeft, compareHeight));
        }
        ViewScene.DrawFloorGrid(left, right, deep);
    }

#if DEBUG
    // Duel board debug lines, drawn over everything: each model's tile and its centre, node 1's
    // rest point, node 0's stand point, and the idle box's footprint (see the toggle's tooltip).
    void DrawDebugLines(bool compare)
    {
        Rlgl.DrawRenderBatchActive();
        Rlgl.DisableDepthTest();
        viewScene.DrawDebugViewLines(previewModel, 0f, ShownLowest(), previewIndex, PreviewRadius(),
            new Vector3(previewLeft, previewLowest, previewBack),
            new Vector3(previewRight, previewLowest + previewHeight, previewFront));
        if (compare)
        {
            viewScene.DrawDebugViewLines(compareModel, compareOffsetX, compareLowest, compareCard, compareRadius,
                new Vector3(compareLeft, compareLowest, compareBack),
                new Vector3(compareRight, compareLowest + compareHeight, compareFront));
        }
        Rlgl.DrawRenderBatchActive();
        Rlgl.EnableDepthTest();
    }

#endif
}