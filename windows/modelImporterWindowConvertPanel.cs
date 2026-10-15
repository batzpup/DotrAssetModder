using System.Numerics;
using ImGuiNET;
using static DotrAssetModder.GuiContext;

namespace DotrAssetModder;

public partial class ModelImporterWindow
{
    // The converter's options. Window fields, so they hold between imports.
    const float MinScale = 0.1f;
    float optScale = 1f;
    bool optRotateOn; // off: Rotate is not applied (and has no gizmo)
    Vector3 optRotate; // degrees about X, Y, Z
    bool optMoveOn;
    Vector3 optMove; // Translate, in game units
    bool optPivotOn; // off: node 1 on node 0 (and no gizmo)
    Vector3 optPivotAt; // Pivot: node 1, game units from node 0
    bool optRestFromAnim;
    bool optFlipUv;
    float optRadius; // node 0's radius to write; 0 = measured (import, not convert)

    // Called by MainWindow for a .glb, .gltf, .fbx or .dae dropped while this tab is up.
    public void SetupModel(string path)
    {
        BeginImport(path);
    }

    // The source is read once here, only to list its animations, then the Convert panel opens for
    // the options; with more than one animation it also asks which plays in each slot.
    void BeginImport(string path)
    {
        int index = Selected();
        if (index < 0)
        {
            status = "Select a model in the list first.";
            statusSucceeded = false;
            return;
        }
        Program.config.lastModelImportPath = path;
        convertOpen = false;
        PutImportOptionsBack();
        convertPath = path;
        convertIndex = index;
        convertName = new List<string>();
        convertFrames = new List<int>();
        convertAnimLabels = new string[0];
        convertSlotAnim = new int[0];
        convertAnimsFrom = new List<string>();
        convertWarnings = new List<string>();
        ClearReport();
        reportIndex = index;
        Scene scene;
        try
        {
            convertAnimsFrom = ModelConverter.FindMotionFiles(path);
            scene = ModelConverter.ReadSource(path, Warn);
            foreach (string motionFile in convertAnimsFrom)
            {
                Scene motionScene = ModelConverter.ReadSource(motionFile, Warn);
                ModelConverter.AddAnimsFrom(scene, motionScene, Path.GetFileName(motionFile), Warn);
            }
            // the converter sorts the animations by name, so the rows read in the same order
            scene.Anims.Sort((firstAnimation, secondAnimation) => string.CompareOrdinal(firstAnimation.Name, secondAnimation.Name));
            convertAnimLabels = new string[scene.Anims.Count];
            for (int i = 0; i < scene.Anims.Count; i++)
            {
                convertName.Add(scene.Anims[i].Name);
                convertFrames.Add(scene.Anims[i].Frames);
                convertAnimLabels[i] = $"{scene.Anims[i].Name} ({scene.Anims[i].Frames} frames)";
            }
        }
        catch (Exception exception)
        {
            Failed("Read failed: " + exception.Message);
            return;
        }
        if (convertName.Count > 1)
        {
            PrefillSlots();
        }
        convertOpen = true;
        convertShow = true;
        reportWarnings = convertWarnings;
        statusSucceeded = true;
        status = $"{Path.GetFileName(path)}: check the options, then Import.";
    }

    void Warn(string message)
    {
        if (!convertWarnings.Contains(message))
        {
            convertWarnings.Add(message);
        }
    }

    void Failed(string message)
    {
        convertOpen = false;
        reportOk = false;
        statusSucceeded = false;
        report = message;
        status = message;
        reportWarnings = convertWarnings;
    }

    /* Which animation each slot starts with. Each animation is placed by "slot_N" in its name,
       else its role words, else (a 3DS rip) its Motion letter, else the next free slot; a slot
       nothing landed on plays the idle animation (the importer would copy idle there too). */
    void PrefillSlots()
    {
        bool[] used = new bool[8]; // MOT slots 0-7 (ParseSlotId)
        int[] slot = new int[convertName.Count];
        for (int i = 0; i < convertName.Count; i++)
        {
            int slotId = RebuildPipeline.ParseSlotId(convertName[i]);
            if (slotId < 0)
            {
                slotId = RebuildPipeline.RoleSlotId(convertName[i]);
            }
            if (slotId < 0 && convertAnimsFrom.Count > 0)
            {
                slotId = MotionSlotId(convertName[i]);
            }
            if (slotId < 0)
            {
                slot[i] = -2; // nothing in the name: a free slot, below
                continue;
            }
            if (used[slotId])
            {
                slot[i] = -1; // that slot is taken already: leave it out
                continue;
            }
            used[slotId] = true;
            slot[i] = slotId;
        }
        for (int i = 0; i < convertName.Count; i++)
        {
            if (slot[i] != -2)
            {
                continue;
            }
            slot[i] = -1;
            for (int k = 0; k < SlotIds.Length; k++)
            {
                if (!used[SlotIds[k]])
                {
                    slot[i] = SlotIds[k];
                    used[slot[i]] = true;
                    break;
                }
            }
        }
        convertSlotAnim = new int[SlotIds.Length];
        Array.Fill(convertSlotAnim, -1);
        for (int i = 0; i < convertName.Count; i++)
        {
            int slotRow = SlotIndex(slot[i]);
            if (slotRow >= 0 && convertSlotAnim[slotRow] < 0)
            {
                convertSlotAnim[slotRow] = i;
            }
        }
        if (convertSlotAnim[0] < 0)
        {
            convertSlotAnim[0] = 0;
        }
        for (int k = 1; k < SlotIds.Length; k++)
        {
            if (convertSlotAnim[k] < 0)
            {
                convertSlotAnim[k] = convertSlotAnim[0];
            }
        }
    }

    // SlotIds index of a motion slot, -1 for one the panel does not show (the extras 1 and 3)
    static int SlotIndex(int slotId)
    {
        for (int k = 0; k < SlotIds.Length; k++)
        {
            if (SlotIds[k] == slotId)
            {
                return k;
            }
        }
        return -1;
    }


    static int MotionSlotId(string name)
    {
        string lowerName = name.ToLowerInvariant();
        if (lowerName.Contains("motionb"))
        {
            return 0;
        }
        if (lowerName.Contains("motiond"))
        {
            return 2;
        }
        if (lowerName.Contains("motiona"))
        {
            return 5;
        }
        if (lowerName.Contains("motionc"))
        {
            return 6;
        }
        return -1;
    }

    void DrawConvertPanel()
    {
        ImGui.Text("Into " + SlotName(convertIndex));

        // With none or one animation there is nothing to choose: the one motion goes to idle and
        // the importer copies it into the slots the model lacks. A card keeps its own motions.
        if (convertIsCard)
        {
            ImGui.BeginDisabled();
            DrawSlotTable();
            ImGui.EndDisabled();
        }
        else if (convertName.Count == 0)
        {
            ImGui.TextDisabled("No animations: the model is imported standing still.");
        }
        else if (convertName.Count == 1)
        {
            ImGui.TextDisabled("One animation, " + convertAnimLabels[0] + ": it plays in every battle slot.");
        }
        else
        {
            DrawSlotTable();
        }

        if (ImGui.CollapsingHeader("Advanced"))
        {
            DrawAdvanced();
        }

        if (ImGui.Button("Import"))
        {
            Import();
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(viewTask != null);
        if (ImGui.Button("View import"))
        {
            ViewImport(false);
        }
        ImGui.EndDisabled();
        HoveredTooltip(@"Preview the model converted with these options, beside the model it would replace.
Nothing is staged; press it again after changing an option.");
        ImGui.SameLine();
        if (ImGui.Button("Save .glb..."))
        {
            SaveConverted();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            convertOpen = false;
            ClearReport();
            status = "Import cancelled.";
        }
    }

    // One row per battle slot, each picking the animation it plays; one animation in two
    // slots goes in as a copy. Column edges can be dragged; the combo fills its column.
    void DrawSlotTable()
    {
        float labelWidth = 0;
        foreach (string slotLabel in SlotLabels)
        {
            labelWidth = Math.Max(labelWidth, ImGui.CalcTextSize(slotLabel).X);
        }
        if (ImGui.BeginTable("convert_slots", 2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersV | ImGuiTableFlags.BordersOuterH))
        {
            ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, labelWidth + ImGui.GetStyle().CellPadding.X * 2);
            ImGui.TableSetupColumn("Animation", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();
            for (int k = 0; k < SlotIds.Length; k++)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.AlignTextToFramePadding();
                ImGui.Text(SlotLabels[k]);
                ImGui.TableSetColumnIndex(1);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##slot" + k, ref convertSlotAnim[k], convertAnimLabels, convertAnimLabels.Length))
                {
                    importDirty = true;
                }
            }
            ImGui.EndTable();
        }
    }

    /* The converter's options, the same ones tools/toglb.py takes. They hold between imports.
       Fields are dragged (or double-clicked to type); the View import preview follows a drag at
       once and is converted again when it is let go. */
    void DrawAdvanced()
    {
        bool changed = false;
        // Rows of toggle, name, then the fields, the names and fields lined up.
        ImGuiStylePtr style = ImGui.GetStyle();
        float nameAt = ImGui.GetCursorPosX() + ImGui.GetFrameHeight() + style.ItemSpacing.X;
        float valuesAt = nameAt + ImGui.CalcTextSize("Position").X + style.ItemSpacing.X * 2f;

        ImGui.SeparatorText("Transform");
        bool locked = convertIsCard && !cardEditable;
        if (locked)
        {
            ImGui.TextDisabled("Node 1 draws part of this card's model and turns in every motion, so the body cannot be edited.");
        }
        ImGui.BeginDisabled(locked);
        ImGui.SetCursorPosX(nameAt);
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Scale");
        ImGui.SameLine(valuesAt);
        ImGui.SetNextItemWidth(FieldWidth(0.2f, 5f));
        ImGui.DragFloat("##scale", ref optScale, 0.01f, MinScale, 10000f, "%.3f");
        changed |= ImGui.IsItemDeactivatedAfterEdit();
        if (!(optScale >= MinScale))
        {
            optScale = MinScale; // 0 collapses the model (and NaN fails the !(>=) too)
        }
        HoveredTooltip(@"Multiplies the model's size in all directions
Drag to change it, double-click to type.");
        if (optScale != 1f)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset##scale"))
            {
                optScale = 1f;
                changed = true;
            }
            HoveredTooltip("Reset scale back to 1x.");
        }
        if (ScaleHiddenByShrink())
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(the board/map shrinks it to radius 40: Grid shows Scale)");
            HoveredTooltip(@"A bigger Scale grows the radius with it, so the game draws it the same size
until the radius passes 133 (x0.3). Set Radius to keep the shrink fixed.");
        }

        changed |= AxisRow("Rotate", "rotate", ref optRotateOn, ref optRotate, 0.5f, "Reset", nameAt, valuesAt,
            "Rotation is in degrees (Euler angles) and Y is up",
            new string[] { });

        changed |= AxisRow("Position", "move", ref optMoveOn, ref optMove, 0.1f, "Reset", nameAt, valuesAt,
            "",
            new string[] { "" });

        // the board and map stand node 1 over a fixed point, so the Pivot is edited in Feet on grid only
        ImGui.BeginDisabled(!PivotEditable());
        // one starting point: an import's node 1 starts on node 0 ("Origin"), a card's at its own
        string pivotReset = "Origin";
        Vector3 pivotStart = Vector3.Zero;
        if (convertIsCard)
        {
            pivotReset = "Reset";
            pivotStart = cardPivotRest;
        }
        changed |= AxisRow("Pivot", "pivot", ref optPivotOn, ref optPivotAt, 0.1f, pivotReset, nameAt, valuesAt,
            "Where node 1 sits: the point the game scales and turns the model about and where the camera focuses",
            new string[] { }, pivotStart);
        ImGui.EndDisabled();
        ImGui.EndDisabled();

        ImGui.SeparatorText("Source fixes");
        ImGui.BeginDisabled(convertIsCard); // a card has no source to fix
        changed |= ImGui.Checkbox("Bind at first frame", ref optRestFromAnim);
        HoveredTooltip(@"Bind the skin at the first animation's first frame instead of the file's bind pose.
Only for rips whose bind pose lies flat or is never shown;
vertices pulled by several bones then move slightly differently.");

        changed |= ImGui.Checkbox("Flip UV", ref optFlipUv);
        HoveredTooltip(@"Turn this on if the textures land upside down: the source's UVs are already top-left.
Done by itself for FBX files exported by Unity Studio.");
        ImGui.EndDisabled();
        if (changed)
        {
            importDirty = true;
        }
        if (source == SourceMonster)
        {
            ImGui.SeparatorText("Game");
            DrawRadius(nameAt, valuesAt);
        }
    }

    /* One Advanced row: the toggle, `name`, then X/Y/Z fields dragged by `speed` (greyed while
       off), and a `resetLabel` button that puts them back at `resetTo` (where the row started).
       True when a change is done (a drag let go, a value typed, the toggle or the button). */
    bool AxisRow(string name, string imguiId, ref bool enabled, ref Vector3 value, float speed, string resetLabel,
        float nameAt, float valuesAt, string tooltip, string[] axisTips, Vector3 resetTo = default)
    {
        bool changed = ImGui.Checkbox("##on" + imguiId, ref enabled);
        HoveredTooltip($@"On: {name} is applied, and its gizmo shows in the View import preview.
Off: it is not applied and hides gizmos.");
        ImGui.SameLine(nameAt);
        changed |= AxisFields(name, imguiId, enabled, ref value, speed, resetLabel, valuesAt, tooltip, axisTips, resetTo);
        return changed;
    }

    /* `name`, then X/Y/Z fields dragged by `speed` (greyed unless `enabled`), and a `resetLabel`
       button that puts them back at `resetTo`. True when a change is done (a drag let go, a
       value typed, the button). */
    bool AxisFields(string name, string imguiId, bool enabled, ref Vector3 value, float speed, string resetLabel,
        float valuesAt, string tooltip, string[] axisTips, Vector3 resetTo)
    {
        bool changed = false;
        ImGui.AlignTextToFramePadding();
        ImGui.Text(name);
        HoveredTooltip(tooltip);
        ImGui.SameLine(valuesAt);
        ImGui.BeginDisabled(!enabled);
        string[] axes = { "X", "Y", "Z" };
        for (int k = 0; k < 3; k++)
        {
            if (k > 0)
            {
                ImGui.SameLine();
            }
            ImGui.Text(axes[k]);
            ImGui.SameLine();
            float axisValue = Axis(value, k);
            ImGui.SetNextItemWidth(FieldWidth(0.15f, 4f));
            if (ImGui.DragFloat("##" + imguiId + axes[k], ref axisValue, speed, 0f, 0f, "%.3f"))
            {
                value = WithAxis(value, k, axisValue);
            }
            changed |= ImGui.IsItemDeactivatedAfterEdit();
            string axisTip = k < axisTips.Length && axisTips[k].Length > 0 ? axisTips[k] + "\n" : string.Empty;
            HoveredTooltip(axisTip + "Drag to change it, double-click to type.");
        }
        if (value != resetTo)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(resetLabel + "##" + imguiId))
            {
                value = resetTo;
                changed = true;
            }
            HoveredTooltip($"Back to {resetTo.X:0.###}, {resetTo.Y:0.###}, {resetTo.Z:0.###}.");
        }
        ImGui.EndDisabled();
        return changed;
    }

    static float Axis(Vector3 vector, int axisIndex)
    {
        if (axisIndex == 0)
        {
            return vector.X;
        }
        if (axisIndex == 1)
        {
            return vector.Y;
        }
        return vector.Z;
    }

    static Vector3 WithAxis(Vector3 vector, int axisIndex, float value)
    {
        if (axisIndex == 0)
        {
            vector.X = value;
        }
        else if (axisIndex == 1)
        {
            vector.Y = value;
        }
        else
        {
            vector.Z = value;
        }
        return vector;
    }

    // Move as the converter gets it: zero while its toggle is off.
    Vector3 MoveApplied()
    {
        if (optMoveOn)
        {
            return optMove;
        }
        return Vector3.Zero;
    }

    // True while the shown View import stands on the board or map already shrunk by its radius,
    // which grows with Scale (Radius 0 = measured), so a bigger Scale draws the same size there.
    bool ScaleHiddenByShrink()
    {
        if (!previewLoaded || !previewIsImport || previewIndex != convertIndex || viewScene.Placement == ViewScene.PlaceFeet)
        {
            return false;
        }
        if (previewIsMonster && optRadius > 0f)
        {
            return false;
        }
        float shrink = ViewScene.BoardShrink(PreviewRadius());
        return shrink < 1f && shrink > 0.3f;
    }

    // Node 0's radius, which only sets how much the board and map shrink the monster. Written by
    // the import, not the converter, so it needs no new convert: the preview follows at once.
    void DrawRadius(float nameAt, float valuesAt)
    {
        bool shown = previewLoaded && previewIsImport && previewIndex == convertIndex;
        // until it is changed, the field shows the measured radius and follows it (Scale, Move)
        float radius = optRadius;
        if (radius <= 0f && shown)
        {
            radius = previewRadius;
        }
        ImGui.SetCursorPosX(nameAt);
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Radius");
        ImGui.SameLine(valuesAt);
        ImGui.SetNextItemWidth(FieldWidth(0.2f, 5f));
        if (ImGui.DragFloat("##radius", ref radius, 0.1f, 0f, 100000f, "%.3f"))
        {
            optRadius = radius;
            if (optRadius < 0f || float.IsNaN(optRadius))
            {
                optRadius = 0f;
            }
        }
        HoveredTooltip(@"The model's bounding radius the import writes (node 0). Measured from the model until changed
(a card on the ISO: its own radius).
On the duel board the game shrinks a monster to fit radius 40:
the map draws it at half that. Battles use real size.
A radius much smaller than the model may let the game cull it before it leaves the screen.
View import shows the result in View: Duel board / Map leader.
Drag to change it, double-click to type.");
        // one starting point: an import's radius starts measured, a card's at its own
        if (convertIsCard && optRadius != cardRadiusOwn)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset##radius"))
            {
                optRadius = cardRadiusOwn;
            }
            HoveredTooltip($"Back to the card's own radius, {cardRadiusOwn:0.###}.");
        }
        else if (!convertIsCard && optRadius > 0f)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Measured##radius"))
            {
                optRadius = 0f;
            }
            HoveredTooltip("Go back to the measured radius.");
        }
        ImGui.SameLine();
        if (!shown)
        {
            ImGui.TextDisabled("(View import measures it)");
            return;
        }
        float shrink = ViewScene.BoardShrink(PreviewRadius());
        ImGui.TextDisabled($"measured {previewRadius:0.#}; board x{shrink:0.##}, map x{ViewScene.MapScale * shrink:0.##}");
    }

    ConvertOptions Options()
    {
        ConvertOptions convertOptions = new ConvertOptions();
        convertOptions.Scale = optScale;
        if (optRotateOn)
        {
            convertOptions.RotateX = optRotate.X;
            convertOptions.RotateY = optRotate.Y;
            convertOptions.RotateZ = optRotate.Z;
        }
        Vector3 move = MoveApplied();
        convertOptions.TranslateX = move.X;
        convertOptions.TranslateY = move.Y;
        convertOptions.TranslateZ = move.Z;
        Vector3 pivot = LivePivot();
        convertOptions.PivotPoint = new double[] { pivot.X, pivot.Y, pivot.Z };
        convertOptions.RestFromAnim = optRestFromAnim;
        convertOptions.FlipUv = optFlipUv;
        foreach (string motionFile in convertAnimsFrom)
        {
            convertOptions.AnimsFrom.Add(motionFile);
        }
        convertOptions.Animations = AnimsOption();
        return convertOptions;
    }

    /* "name=slot_N_role,..." for every slot, in the order the slots are filled; an animation in
       two slots is named twice and the converter copies it. Null with one animation or
       none (no slot rows): one animation goes to idle on its own. */
    string? AnimsOption()
    {
        List<string> parts = new List<string>();
        for (int k = 0; k < convertSlotAnim.Length; k++)
        {
            parts.Add(convertName[convertSlotAnim[k]] + "=slot_" + SlotIds[k] + "_" + SlotRoleName[k]);
        }
        if (parts.Count == 0)
        {
            return null;
        }
        return string.Join(",", parts);
    }

    // Convert the source with the options as they stand and stage the glb in the selected slot.
    void Import()
    {
        if (convertIsCard)
        {
            StageCardEdit();
            return;
        }
        ConvertResult convertResult;
        convertWarnings = new List<string>();
        try
        {
            convertResult = ModelConverter.ConvertFile(convertPath, Options(), Warn);
        }
        catch (Exception exception)
        {
            Failed("Convert failed: " + exception.Message);
            return;
        }
        convertOpen = false;
        StageConverted(convertIndex, convertResult.Glb, convertPath);
        reportConvert = convertResult.Report;
        foreach (string warning in convertWarnings)
        {
            reportWarnings.Add(warning);
        }
    }

    // A card edited with the options as they stand, staged in place of anything staged for it.
    void StageCardEdit()
    {
        int index = convertIndex;
        byte[] glb;
        try
        {
            glb = CardEditGlb();
        }
        catch (Exception exception)
        {
            Failed("Edit failed: " + exception.Message);
            return;
        }
        convertOpen = false;
        StageConverted(index, glb, cardNames[index] + " (edited)");
        if (reportOk)
        {
            cardEdits[index] = CurrentImportOptions();
        }
    }
}