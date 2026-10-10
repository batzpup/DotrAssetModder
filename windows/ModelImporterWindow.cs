using System;
using System.IO;
using System.Numerics;
using ImGuiNET;
using NativeFileDialogSharp;
using Raylib_cs;
using rlImGui_cs;
using static DotrAssetModder.glRD;

namespace DotrAssetModder;

// One importer for both model archives. MONSTER.MRG is 683 fixed 1 MiB card slots; a MODEL.MRG
// block's slot is the gap to the next one. Pick a slot, load a glb, read the fit report, then
// save everything staged to the ISO through ChangeSet.
public class ModelImporterWindow
{
    const int ModelCount = 683;
    const int BlockSize = 0x100000;
    const int MaxTextureDim = 512;
    const int SourceMonster = 0;
    const int SourceModel = 1;

    int source = SourceMonster;
    string status = string.Empty;
    bool ok = true;
    string openedIso = string.Empty;

    // MONSTER.MRG
    string monsterPath;
    byte[] monsterMrg;
    string[] cardNames = new string[ModelCount];
    Dictionary<int, byte[]> monsterStaged = new Dictionary<int, byte[]>();
    Dictionary<int, string> monsterStagedFrom = new Dictionary<int, string>();
    HashSet<int> mapHeightEdits = new HashSet<int>();  // cards whose Map height was edited, written on Save to ISO
    int selectedMonster = -1;

    // MODEL.MRG
    ModelMrg imp;
    int groupFilter;                   // 0 = all, else ModelMrg.Groups[groupFilter - 1]
    string[] groupNames;
    int selectedModel = -1;

    // The list rows, rebuilt only when the source, the filter or the archive changes.
    List<int> rowIndex = new List<int>();
    List<string> rowName = new List<string>();
    List<string> rowWhat = new List<string>();
    List<string> rowGroup = new List<string>();
    bool rowsDirty = true;
    string nameFilter = string.Empty;
    bool allowReduction;

    // The fit report for the last Load Model, kept on screen under the selected slot.
    int reportIndex = -1;
    string report = string.Empty;
    bool reportOk;
    List<string> reportWarnings = new List<string>();
    List<string> reportConvert = new List<string>();   // the converter's own report lines

    // The battle motion slots, in the order the importer fills them (RebuildPipeline's
    // SlotFillOrder), what the panel calls them, and the word written after "slot_N_".
    static readonly int[] SlotIds = { 0, 2, 4, 5, 6 };
    static readonly string[] SlotLabels = { "Idle", "Attack", "Death", "Win", "Hurt" };
    static readonly string[] SlotRoleName = { "idle", "attack", "death", "win", "hurt" };

    bool keepStagedAfterSave;                          // off: a successful Save to ISO clears the staging

    bool convertOpen;                                  // the Convert panel's own window
    bool convertShow;                                  // newly opened: expand it and bring it to the front
    string convertPath = string.Empty;
    int convertIndex = -1;
    List<string> convertName = new List<string>();
    List<int> convertFrames = new List<int>();
    string[] convertAnimLabels = new string[0];        // "name (N frames)", for the slot combos
    int[] convertSlotAnim = new int[0];                // per slot (SlotIds order), the animation it plays
    List<string> convertAnimsFrom = new List<string>();
    List<string> convertWarnings = new List<string>();  // what reading the source said

    // The converter's options. Window fields, so they hold between imports.
    const float MinScale = 0.1f;
    float optScale = 1f;
    bool optRotateOn;                                  // off: Rotate is not applied (and has no gizmo)
    Vector3 optRotate;                                 // degrees about X, Y, Z
    bool optMoveOn;
    Vector3 optMove;                                   // Translate, in game units
    bool optPivotOn;                                   // off: node 1 on node 0 (and no gizmo)
    Vector3 optPivotAt;                                // Pivot: node 1, game units from node 0
    bool optRestFromAnim;
    bool optFlipUv;
    float optRadius;                                   // node 0's radius to write; 0 = measured (import, not convert)

    // The Convert window on a card as it is on the ISO (opened by View): the same Transform and
    // Radius options, applied to the card's extracted glb by ExtractedGlbEdit and staged like an
    // import. Each kind keeps its own option values.
    struct Opts
    {
        public float Scale;
        public bool RotateOn;
        public Vector3 Rotate;
        public bool MoveOn;
        public Vector3 Move;
        public bool PivotOn;
        public Vector3 PivotAt;
        public float Radius;
        public Vector3 PivotRest;                      // a card's: its node 1 rest offset from node 0
    }
    bool convertIsCard;                                // the Convert window edits convertIndex's card
    Opts importOpts;                                   // the import options while it does
    Dictionary<int, Opts> cardEdits = new Dictionary<int, Opts>();  // staged edits, by card
    byte[] cardGlb;                                    // the card's glb as on the ISO; null: extract it again
    Vector3 cardPivotRest;                             // its node 1 rest offset from node 0
    float cardRadiusOwn;                               // its node 0 radius on the ISO
    bool cardEditable;                                 // false: node 1 draws part of it, Radius only

    // The last Check plan / Save, kept so the report stays on screen after the write.
    ChangeSet.Plan plan;
    ModelMrg.BuildResult planReport;
    bool planIsSaved;

    // View extracts on a worker thread; the preview is built from its glb on the main thread.
    Task<(byte[] Glb, AnimatedGltfModel.Parsed Parsed, float Radius)> viewTask;   // read and parsed off the draw thread
    string viewName = string.Empty;

    // Extract .glb... writes one block on a worker thread too.
    Task extractTask;
    string extractPath = string.Empty;

    // The viewed block's card and board scale, taken when View starts and kept with the preview.
    // Compare is offered only for an import: View import from the Convert window, or View on a
    // staged slot. A plain View of the ISO's model has nothing to compare with.
    int viewIndex = -1;
    bool viewIsMonster;
    bool viewCanCompare;
    bool viewCompareOn;                                // Compare's state when that preview opens
    bool viewIsImport;                                 // View import: redone when an option changes
    bool viewOpensCard;                                // a card's View: the Convert window opens for it
    bool viewKeepCamera;                               // a redo keeps the camera and animation
    string viewLabel = string.Empty;                   // the model's name beside its height
    float viewRadius = -1f;                            // the block's node 0 radius; -1: the importer's measure (View import)
    Vector3 viewMove;                                  // View import: the Move it was converted with
    Vector3 viewRotate;                                // and the Rotate
    float viewScale = 1f;                              // and the Scale
    int previewIndex = -1;
    bool previewIsMonster;
    bool previewCanCompare;
    bool previewIsImport;
    string previewLabel = string.Empty;
    float previewLowest;                               // lowest idle point, relative to node 0
    float standLowest;                                 // Feet's stand height, from the first view (before Move)
    float previewHeight;                               // tallest idle frame
    float previewLeft;                                 // idle X extent, in glb coordinates
    float previewRight;
    float previewBack;                                 // idle Z extent, for the debug footprint
    float previewFront;
    float previewRadius = 1f;                          // node 0's radius, read from the block or the importer's measure
    Vector3 previewMove;                               // Move in previewLeft/Right etc. (View import), else 0
    Vector3 previewRotate;                             // Rotate the preview was converted with, else 0
    float previewScale = 1f;                           // Scale the preview was converted with, else 1
    bool importDirty;                                  // an option changed under a View import

    ViewScene viewScene = new ViewScene();

    // Compare: a second model beside the preview, both with their feet on the grid, for size.
    // It is the viewed card's block as on the ISO (the model an import replaces) or Dark Magician.
    const int DarkMagicianCard = 60;
    bool compareOn;
    bool compareDarkMagician;
    Task<byte[]> compareTask;
    int compareTaskCard = -1;
    bool compareTaskIsMonster;
    AnimatedGltfModel compareModel;
    int compareCard = -1;
    bool compareIsMonster;
    string compareName = string.Empty;
    float compareLowest;
    float compareHeight;
    float compareLeft;
    float compareRight;
    float compareBack;                                 // idle Z extent, for the debug footprint
    float compareFront;
    float compareOffsetX;                              // its node 0 stands this far to the left of the preview's
    float compareRadius = 1f;                          // its block's node 0 radius
    float compareTaskRadius = 1f;
    int compareAnim = -1;                              // its idle animation
    float compareTime;

    AnimatedGltfModel previewModel;
    bool previewLoaded = false;
    bool previewOpen = false; // the preview's own window; closing it hides it until the next View
    bool previewShow = false; // a new View: expand the window if collapsed and bring it to the front
    string previewName = string.Empty;
    RenderTexture2D previewTex;
    bool previewTexReady = false;
    int previewW = 1;
    int previewH = 1;

    OrbitCamera orbit = new OrbitCamera();
    bool aimAtPivot;                                   // the camera follows node 1, as the game's focus camera does

    // The Move, Rotate and Pivot gizmos on a View import, each shown while its toggle is on. A
    // handle is 0-2 the X/Y/Z arrow or ring, 3 the centre (Move, Pivot).
    const int GizmoMove = 0;
    const int GizmoRotate = 1;
    const int GizmoPivot = 2;
    int gizmoKind = -1;                                // the gizmo being dragged, -1 none
    int gizmoDrag = -1;                                // its handle
    int gizmoHoverKind = -1;
    int gizmoHover = -1;
    Vector2 gizmoStartMouse;
    Vector3 gizmoStartValue;                           // the dragged values (optMove/optRotate/optPivotAt) when it began
    Vector3 gizmoStartCentre;                          // the gizmo's centre when it began, world units
    float gizmoLength;                                 // the arrows' length when the drag began
    float gizmoLastAngle;                              // Rotate: the mouse's angle round the centre last frame
    float gizmoTurned;                                 // Rotate: degrees turned since the drag began

    int currentAnim = 0;
    float animTime = 0f;
    bool animPlaying = false;

    public ModelImporterWindow()
    {
        groupNames = new string[ModelMrg.Groups.Length + 1];
        groupNames[0] = "All";
        for (int i = 0; i < ModelMrg.Groups.Length; i++)
        {
            groupNames[i + 1] = ModelMrg.Groups[i];
        }
    }

    public void Render()
    {
        var fl = FileLoader.Instance;

        if (!fl.IsIsoLoaded || fl.iso_data_files_all == null || string.IsNullOrEmpty(fl.CurrentIsoPath))
        {
            ImGui.TextDisabled("Load an ISO first (Extractor tab).");
            return;
        }
        if (openedIso != fl.CurrentIsoPath)
        {
            if (!Open(fl))
            {
                return;
            }
        }

        ImGui.Text("Source:");
        ImGui.SameLine();
        if (ImGui.RadioButton("MONSTER.MRG card", ref source, SourceMonster))
        {
            SourceChanged();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("MODEL.MRG model", ref source, SourceModel))
        {
            SourceChanged();
        }

        if (source == SourceMonster)
        {
            //Orange
            ImGui.TextColored(new Vector4(1, 0.6f, 0, 1),
                "MONSTER.MRG: the 683 card models. Each lives in a fixed 1 MiB block.");
        }
        else
        {
            ImGui.TextColored(new Vector4(1, 0.6f, 0, 1),
                "MODEL.MRG: duel table, terrain, arena stages, event scenes, stained-glass windows. " +
                "A model must fit its existing slot.");
            if (imp.Anomalies.Count > 0)
            {
                ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), $"{imp.Anomalies.Count} parse anomalies");
            }
        }

        DrawFilters(fl);
        ImGui.Separator();
        DrawList();
        ImGui.Separator();
        DrawSelected();
        ImGui.Separator();
        DrawStagedAndSave(fl);

        if (status.Length > 0)
        {
            if (ok)
            {
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), status);
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), status);
            }
        }

        UpdateView();
        UpdateCompare();
        UpdateImportPreview();
        viewScene.Update();
        if (viewScene.Error != null)
        {
            ok = false;
            status = viewScene.Error;
            viewScene.Error = null;
        }
        UpdateExtract();
        if (previewLoaded && previewOpen)
        {
            ImGui.SetNextWindowSize(new Vector2(640, 560), ImGuiCond.FirstUseEver);
            if (previewShow)
            {
                ImGui.SetNextWindowCollapsed(false);
                ImGui.SetNextWindowFocus();
                previewShow = false;
            }
            if (ImGui.Begin("Preview - " + previewName + "###importer_preview", ref previewOpen))
            {
                DrawPreview();
            }
            ImGui.End();
        }

        if (convertOpen)
        {
            ImGui.SetNextWindowSize(new Vector2(560, 420), ImGuiCond.FirstUseEver);
            if (convertShow)
            {
                ImGui.SetNextWindowCollapsed(false);
                ImGui.SetNextWindowFocus();
                convertShow = false;
            }
            string title = Path.GetFileName(convertPath);
            if (convertIsCard)
            {
                title = cardNames[convertIndex];
            }
            if (ImGui.Begin("Convert - " + title + "###importer_convert", ref convertOpen))
            {
                DrawConvertPanel();
            }
            ImGui.End();
        }
    }

    // Reads both archives and the card names from a newly opened ISO.
    bool Open(FileLoader fl)
    {
        try
        {
            monsterPath = fl.iso_data_files_all.FirstOrDefault(f => IsoPaths.NameEquals(f, "MONSTER.MRG"));
            if (monsterPath == null)
            {
                throw new Exception("MONSTER.MRG not found in ISO.");
            }
            monsterMrg = fl.GetFileBytesFromDisk(monsterPath);
            compareCard = -1;                          // the compare model came from the old ISO
            viewScene.Unload();
            monsterStaged.Clear();
            monsterStagedFrom.Clear();
            mapHeightEdits.Clear();
            cardEdits.Clear();
            cardGlb = null;
            PutImportOptionsBack();

            if (imp != null)
            {
                imp.Dispose();
            }
            imp = ModelMrg.Open(fl.CurrentIsoPath);
            viewScene.SetSource(imp);

            for (int i = 0; i < ModelCount; i++)
            {
                cardNames[i] = imp.Tables.CardName(i).Trim();
            }
        }
        catch (Exception e)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "Could not read the ISO's models: " + e.Message);
            return false;
        }

        openedIso = fl.CurrentIsoPath;
        selectedMonster = -1;
        selectedModel = -1;
        plan = null;
        planReport = null;
        status = string.Empty;
        convertOpen = false;
        ClearReport();
        rowsDirty = true;
        return true;
    }

    void SourceChanged()
    {
        convertOpen = false;                           // its slot index belongs to the other archive
        ClearReport();
        plan = null;
        planReport = null;
        rowsDirty = true;
    }

    void ClearReport()
    {
        reportIndex = -1;
        report = string.Empty;
        reportWarnings = new List<string>();
        reportConvert = new List<string>();
    }

    int Selected()
    {
        if (source == SourceMonster)
        {
            return selectedMonster;
        }
        return selectedModel;
    }

    void Select(int index)
    {
        if (source == SourceMonster)
        {
            selectedMonster = index;
        }
        else
        {
            selectedModel = index;
        }
    }

    bool IsStaged(int index)
    {
        if (source == SourceMonster)
        {
            return monsterStaged.ContainsKey(index);
        }
        return imp.IsStaged(index);
    }

    int StagedCount()
    {
        if (source == SourceMonster)
        {
            return monsterStaged.Count;
        }
        return imp.StagedCount;
    }

    void DrawFilters(FileLoader fl)
    {
        if (source == SourceModel)
        {
            ImGui.SetNextItemWidth(160);
            if (ImGui.Combo("Group", ref groupFilter, groupNames, groupNames.Length))
            {
                rowsDirty = true;
            }
            ImGui.SameLine();
        }
        ImGui.Text("Name");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputText("##Name", ref nameFilter, 64))
        {
            rowsDirty = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Reload from ISO"))
        {
            openedIso = string.Empty;
            if (Open(fl))
            {
                status = "Reloaded the models from the ISO.";
                ok = true;
            }
        }
        ImGui.SameLine();
        ImGui.Checkbox("Keep staged after save", ref keepStagedAfterSave);
        Tip("On: staged models stay staged after Save to ISO, so they can be saved again (e.g. to another ISO). " +
            "\nOff: a successful save clears the staging, since those models are now on the disc.");
    }

    bool Matches(string a, string b)
    {
        if (nameFilter.Length == 0)
        {
            return true;
        }
        return a.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ||
               b.Contains(nameFilter, StringComparison.OrdinalIgnoreCase);
    }

    void BuildRows()
    {
        rowIndex.Clear();
        rowName.Clear();
        rowWhat.Clear();
        rowGroup.Clear();
        if (source == SourceMonster)
        {
            for (int i = 0; i < ModelCount; i++)
            {
                string name = "card " + i.ToString("000");
                if (!Matches(name, cardNames[i]))
                {
                    continue;
                }
                rowIndex.Add(i);
                rowName.Add(name);
                rowWhat.Add(cardNames[i]);
                rowGroup.Add("MONSTER");
            }
        }
        else
        {
            foreach (ModelMrg.Block b in imp.Blocks)
            {
                // DUEL01-21 are byte-identical to DUEL00 and saved with it, so only DUEL00 is listed
                if (b.IsMirror)
                {
                    continue;
                }
                if (groupFilter > 0 && b.Group != ModelMrg.Groups[groupFilter - 1])
                {
                    continue;
                }
                if (!Matches(b.Name, b.FriendlyName))
                {
                    continue;
                }
                rowIndex.Add(b.Index);
                rowName.Add(b.Name);
                rowWhat.Add(b.FriendlyName);
                rowGroup.Add(b.Group);
            }
        }
        rowsDirty = false;
    }

    unsafe void DrawList()
    {
        if (rowsDirty)
        {
            BuildRows();
        }

        ImGui.BeginChild("mrg_list", new Vector2(0, 220));
        if (ImGui.BeginTable("mrg_entries", 3,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Model", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 2.5f);
            ImGui.TableSetupColumn("Group", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableHeadersRow();

            // Only the rows on screen are drawn.
            var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clipper.Begin(rowIndex.Count);
            while (clipper.Step())
            {
                for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                {
                    int index = rowIndex[row];
                    bool staged = IsStaged(index);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    if (staged)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 1f, 0.4f, 1f));
                    }
                    ImGui.PushID(index);
                    if (ImGui.Selectable(rowName[row], Selected() == index, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        Select(index);
                        ClearReport();
                    }
                    ImGui.PopID();
                    if (staged)
                    {
                        ImGui.PopStyleColor();
                    }
                    ImGui.TableNextColumn();
                    if (rowWhat[row].Length > 0)
                    {
                        ImGui.Text(rowWhat[row]);
                    }
                    else
                    {
                        ImGui.TextDisabled("not identified yet");
                    }
                    ImGui.TableNextColumn();
                    ImGui.Text(rowGroup[row]);
                }
            }
            clipper.End();
            clipper.Destroy();
            ImGui.EndTable();
        }
        ImGui.EndChild();
    }

    string SlotName(int index)
    {
        if (source == SourceMonster)
        {
            return $"card {index:000}  -  {cardNames[index]}";
        }
        ModelMrg.Block b = imp.Find(index);
        if (b.FriendlyName.Length > 0)
        {
            return $"{b.Name}  -  {b.FriendlyName}";
        }
        return b.Name;
    }

    void DrawSelected()
    {
        int index = Selected();
        if (index < 0)
        {
            ImGui.TextDisabled("Select a model, then drop a .glb, .gltf, .fbx or .dae on the window or use Load Model.");
            return;
        }

        ImGui.Text(SlotName(index));
        if (source == SourceMonster)
        {
            ImGui.TextDisabled($"slot {BlockSize:N0} B at MONSTER.MRG+0x{(long)index * BlockSize:X}");
        }
        else
        {
            ModelMrg.Block b = imp.Find(index);
            string animated = "no";
            if (b.HasEmbeddedMot)
            {
                animated = "yes";
            }
            ImGui.TextDisabled($"textures {b.TexCount}  animated {animated}  slot {b.Slot:N0} B");
        }

        ImGui.BeginDisabled(viewTask != null);
        if (ImGui.Button("View"))
        {
            View(index, IsStaged(index));
        }
        ImGui.EndDisabled();
        if (IsStaged(index))
        {
            Tip("Shows the staged import. Tick Compare in the preview to stand the model it replaces beside it.");
        }
        else
        {
            Tip("Shows this model as it is on the ISO.");
        }
        if (viewTask != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Loading " + viewName + "...");
        }

        if (ImGui.Button("Load Model"))
        {
            var r = Dialog.FileOpen(FileDialogFilter.Of("glb,gltf,fbx,dae"),
                Path.GetDirectoryName(Program.config.lastModelImportPath));
            if (r.IsOk)
            {
                BeginImport(r.Path);
            }
        }
        ImGui.SameLine();
        ImGui.Checkbox("Allow texture/animation reduction to fit", ref allowReduction);

        if (IsStaged(index))
        {
            ImGui.SameLine();
            if (ImGui.Button("Unstage"))
            {
                if (source == SourceMonster)
                {
                    monsterStaged.Remove(index);
                    monsterStagedFrom.Remove(index);
                    cardEdits.Remove(index);
                }
                else
                {
                    imp.Unstage(index);
                }
                ClearReport();
            }
        }

        ImGui.BeginDisabled(extractTask != null);
        if (ImGui.Button("Extract .glb..."))
        {
            ExtractOne(index);
        }
        ImGui.EndDisabled();
        Tip("Save just this model (as it is on the ISO) as a .glb, the same file the Extractor tab writes for it.");
        if (extractTask != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Extracting " + Path.GetFileName(extractPath) + "...");
        }

        if (reportIndex == index)
        {
            foreach (string line in reportConvert)
            {
                ImGui.TextUnformatted(line);
            }
        }

        if (reportIndex == index && report.Length > 0)
        {
            if (reportOk)
            {
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), report);
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), report);
                ImGui.TextWrapped("Nothing was staged. Simplify the mesh, or let the reduction checkbox shrink its textures.");
            }
            foreach (string w in reportWarnings)
            {
                ImGui.TextColored(new Vector4(1, 0.8f, 0.3f, 1), w);
            }
        }
    }

    // Shows the block (as it is on the ISO, or as staged) in the preview. Only a staged block
    // offers Compare, against the ISO's model it replaces; it opens with Compare off. A card
    // (as on the ISO, or staged as an edit) opens the Convert window for it, as an import does,
    // without Compare.
    void View(int index, bool staged)
    {
        byte[] bytes;
        bool card = source == SourceMonster && (!staged || cardEdits.ContainsKey(index));
        if (staged)
        {
            bytes = StagedBytes(index);
            viewName = SlotName(index) + " (staged)";
            viewLabel = Path.GetFileNameWithoutExtension(StagedFrom(index));
        }
        else
        {
            bytes = BlockBytes(index);
            viewName = SlotName(index);
            viewLabel = viewName;
        }
        viewIndex = index;
        viewIsMonster = source == SourceMonster;
        viewCanCompare = staged && !card;                  // no Compare for a card on the ISO (edited or not)
        viewCompareOn = false;
        viewIsImport = card;
        viewOpensCard = card;
        viewKeepCamera = false;
        viewRadius = card ? -1f : BlockRadius(bytes);      // a card shows Radius as an import does
        viewMove = Vector3.Zero;
        viewRotate = Vector3.Zero;
        viewScale = 1f;
        if (card && staged)
        {
            // the staged block itself; the preview was edited with the values it was staged with
            Opts e = cardEdits[index];
            viewLabel = cardNames[index];
            viewMove = e.MoveOn ? e.Move : Vector3.Zero;
            viewRotate = e.RotateOn ? e.Rotate : Vector3.Zero;
            viewScale = e.Scale;
        }
        byte[] donor = null;
        if (card)
        {
            donor = BlockBytes(index);
        }
        viewTask = Task.Run(() => WithParsed(ModelExtractor.ExtractGlb(bytes), donor));
    }

    // The Convert window's View import for a card: its glb as on the ISO, edited with the options
    // as they stand. A redo after an option changed keeps the camera.
    void ViewCard(bool redo)
    {
        int index = convertIndex;
        viewName = SlotName(index);
        viewLabel = cardNames[index];
        viewIndex = index;
        viewIsMonster = true;
        viewCanCompare = false;
        viewCompareOn = false;
        viewIsImport = true;
        viewOpensCard = false;
        viewKeepCamera = redo;
        viewRadius = -1f;
        viewMove = MoveApplied();
        viewRotate = RotateApplied();
        viewScale = optScale;
        importDirty = false;
        byte[] glb = cardGlb;
        byte[] block = BlockBytes(index);
        float scale = optScale;
        Vector3 rotate = RotateApplied();
        Vector3 move = MoveApplied();
        Vector3 pivot = LivePivot() - cardPivotRest;
        viewTask = Task.Run(() => WithParsed(ExtractedGlbEdit.Apply(glb ?? ModelExtractor.ExtractGlb(block), scale, rotate, move, pivot), block));
    }

    // The convert card edited with the options as they stand, for staging or saving.
    byte[] CardEditGlb()
    {
        byte[] glb = cardGlb ?? ModelExtractor.ExtractGlb(BlockBytes(convertIndex));
        return ExtractedGlbEdit.Apply(glb, optScale, RotateApplied(), MoveApplied(), LivePivot() - cardPivotRest);
    }

    // The Convert window for a card View just loaded: the values of its staged edit, else none
    // (Pivot at its own node 1, Radius its own). The import options wait for the next Load Model.
    void OpenCardConvert(int index, AnimatedGltfModel model, byte[] glb)
    {
        if (!convertIsCard)
        {
            importOpts = CurrentOpts();
        }
        convertIsCard = true;
        convertIndex = index;
        convertPath = cardNames[index];
        Opts e;
        if (cardEdits.TryGetValue(index, out e))
        {
            cardGlb = null;                            // the shown glb is the staged block's
        }
        else
        {
            cardGlb = glb;
            e = new Opts();
            e.Scale = 1f;
            e.PivotRest = model.RestPivot - model.RestNodeZero;
            e.PivotAt = e.PivotRest;
            e.Radius = BlockRadius(BlockBytes(index));
        }
        SetOpts(e);
        cardPivotRest = e.PivotRest;
        cardRadiusOwn = BlockRadius(BlockBytes(index));
        cardEditable = ExtractedGlbEdit.Editable(glb);
        // the card's own motions, in the slot rows, greyed: an edit keeps them as they are
        convertName = new List<string>(model.AnimationNames);
        convertFrames = new List<int>();
        convertAnimLabels = new string[convertName.Count];
        for (int i = 0; i < convertName.Count; i++)
        {
            convertFrames.Add((int)MathF.Round(model.Duration(i) * GlbImporter.AnimFps) + 1);
            convertAnimLabels[i] = $"{convertName[i]} ({convertFrames[i]} frames)";
        }
        convertSlotAnim = new int[SlotIds.Length];
        for (int k = 0; k < SlotIds.Length; k++)
        {
            convertSlotAnim[k] = convertName.IndexOf("slot_" + SlotIds[k]);
        }
        convertAnimsFrom = new List<string>();
        convertWarnings = new List<string>();
        ClearReport();
        reportIndex = index;
        convertOpen = true;
        convertShow = true;
        ok = true;
        status = $"{cardNames[index]}: check the options, then Import.";
    }

    // Load Model (or a new ISO) after a card's Convert window: the import options come back.
    void PutImportOptionsBack()
    {
        if (convertIsCard)
        {
            SetOpts(importOpts);
            convertIsCard = false;
        }
    }

    Opts CurrentOpts()
    {
        Opts o = new Opts();
        o.Scale = optScale;
        o.RotateOn = optRotateOn;
        o.Rotate = optRotate;
        o.MoveOn = optMoveOn;
        o.Move = optMove;
        o.PivotOn = optPivotOn;
        o.PivotAt = optPivotAt;
        o.Radius = optRadius;
        o.PivotRest = cardPivotRest;
        return o;
    }

    void SetOpts(Opts o)
    {
        optScale = o.Scale;
        optRotateOn = o.RotateOn;
        optRotate = o.Rotate;
        optMoveOn = o.MoveOn;
        optMove = o.Move;
        optPivotOn = o.PivotOn;
        optPivotAt = o.PivotAt;
        optRadius = o.Radius;
    }

    // The Convert window's View import: the source converted with the options as they stand,
    // beside the model on the ISO it would replace. Nothing is staged. A redo after an option
    // changed keeps the camera, so Scale and Move show against the grid.
    void ViewImport(bool redo)
    {
        if (convertIsCard)
        {
            ViewCard(redo);
            return;
        }
        string path = convertPath;
        ConvertOptions o = Options();
        viewName = Path.GetFileName(path) + " -> " + SlotName(convertIndex);
        viewLabel = Path.GetFileNameWithoutExtension(path);
        viewIndex = convertIndex;
        viewIsMonster = source == SourceMonster;
        viewCanCompare = true;
        if (redo)
        {
            viewCompareOn = compareOn;
        }
        else
        {
            viewCompareOn = false;
        }
        viewIsImport = true;
        viewOpensCard = false;
        viewKeepCamera = redo;
        viewRadius = -1f;
        viewMove = new Vector3((float)o.TranslateX, (float)o.TranslateY, (float)o.TranslateZ);
        viewRotate = new Vector3((float)o.RotateX, (float)o.RotateY, (float)o.RotateZ);
        viewScale = (float)o.Scale;
        importDirty = false;
        // the converter's warnings are shown by Import; the preview ignores them
        byte[] donor = BlockBytes(convertIndex);
        viewTask = Task.Run(() => WithParsed(ModelConverter.ConvertFile(path, o, w => { }).Glb, donor));
    }

    // Redoes a shown View import once an option has changed and the last convert is done.
    void UpdateImportPreview()
    {
        if (!importDirty || viewTask != null)
        {
            return;
        }
        if (!convertOpen || !previewLoaded || !previewOpen || !previewIsImport || previewIndex != convertIndex)
        {
            importDirty = false;
            return;
        }
        ViewImport(true);
    }

    // The file a staged block was imported from.
    string StagedFrom(int index)
    {
        if (source == SourceMonster)
        {
            return monsterStagedFrom[index];
        }
        ModelMrg.Block b = imp.Find(index);
        foreach (ModelMrg.StagedBlock s in imp.Staged)
        {
            if (s.Block == b)
            {
                return s.SourcePath;
            }
        }
        return b.Name;
    }

    // Starts loading the compare model if the one wanted is not already shown or loading.
    void RequestCompare()
    {
        if (!compareOn || !previewLoaded || !previewCanCompare || compareTask != null)
        {
            return;
        }
        int card = previewIndex;
        bool isMonster = previewIsMonster;
        if (compareDarkMagician)
        {
            card = DarkMagicianCard;
            isMonster = true;
        }
        if (compareModel != null && compareCard == card && compareIsMonster == isMonster)
        {
            return;
        }
        byte[] bytes;
        if (isMonster)
        {
            if (monsterMrg == null || card < 0 || card >= ModelCount)
            {
                return;
            }
            bytes = new byte[BlockSize];
            Array.Copy(monsterMrg, (long)card * BlockSize, bytes, 0, BlockSize);
        }
        else
        {
            // a MODEL.MRG entry belongs to the archive it was viewed from
            if (source != SourceModel || imp == null)
            {
                return;
            }
            bytes = imp.BlockBytes(card);
        }
        compareTaskCard = card;
        compareTaskIsMonster = isMonster;
        compareTaskRadius = BlockRadius(bytes);
        compareTask = Task.Run(() => ModelExtractor.ExtractGlb(bytes));
    }

    void UpdateCompare()
    {
        if (compareTask == null || !compareTask.IsCompleted)
        {
            return;
        }
        if (compareTask.IsFaulted)
        {
            ok = false;
            status = "Compare model failed: " + compareTask.Exception.InnerException.Message;
            compareTask = null;
            return;
        }
        if (compareModel != null)
        {
            compareModel.Dispose();
        }
        compareModel = AnimatedGltfModel.Load(compareTask.Result);
        compareTask = null;
        compareCard = compareTaskCard;
        compareIsMonster = compareTaskIsMonster;
        compareRadius = compareTaskRadius;
        if (compareIsMonster)
        {
            compareName = cardNames[compareCard];
        }
        else
        {
            compareName = imp.Find(compareCard).Name;
        }
        compareAnim = IdleAnimIndex(compareModel);
        compareTime = 0f;
        compareModel.MeasureStanding(compareAnim, out compareLowest, out compareHeight, out compareLeft, out compareRight,
            out compareBack, out compareFront);
        compareModel.SetAnimationFrame(compareAnim, 0f);
        PlaceCompare();
        FrameStanding();
        // the toggle may have changed while this one loaded
        RequestCompare();
    }

    bool CompareShown()
    {
        return compareOn && previewCanCompare && compareModel != null && previewLoaded;
    }

    // The radius the preview's model would have in the game: the Radius option for an import
    // when set, else the one read from the block or measured.
    float PreviewRadius()
    {
        if (previewIsImport && previewIsMonster && optRadius > 0f)
        {
            return optRadius;
        }
        return previewRadius;
    }

    // Node 0's bounding radius in an RH0 block (node tree at +0x10, sphere radius at +0x3C).
    static float BlockRadius(byte[] block)
    {
        if (block.Length < 0x14)
        {
            return 1f;
        }
        int tree = BitConverter.ToInt32(block, 0x10);
        if (tree <= 0 || tree + 0x40 > block.Length)
        {
            return 1f;
        }
        return BitConverter.ToSingle(block, tree + 0x3C);
    }

    // Stands the compare model to the left of the preview, its idle extent clear of the
    // preview's by a tenth of the taller one's height.
    void PlaceCompare()
    {
        if (!CompareShown())
        {
            return;
        }
        float sp = viewScene.ModelScale(PreviewRadius());
        float sc = viewScene.ModelScale(compareRadius);
        float gap = 0.1f * MathF.Max(sp * previewHeight, sc * compareHeight);
        // spaced from the body before Move X, so moving the import does not drag this one along
        float x = viewScene.StandShift(previewModel, sp).X + sp * (previewLeft - previewMove.X)
            - viewScene.StandShift(compareModel, sc).X - sc * compareRight - gap;
        if (viewScene.Placement == ViewScene.PlaceBoard)
        {
            // the tile left of the centre one, as two units stand in a duel (big ones may overlap)
            x = -ViewScene.TilePitch;
        }
        compareOffsetX = x;
    }

    // Fits the camera to the standing models (an import or staged preview): the preview, and
    // the compare model when shown.
    void FrameStanding()
    {
        float s = viewScene.ModelScale(PreviewRadius());
        Vector3 shift = viewScene.StandShift(previewModel, s);
        float left = shift.X + s * previewLeft;
        float right = shift.X + s * previewRight;
        float bottom = viewScene.StandY(ShownLowest(), previewIndex) + shift.Y + s * previewLowest;
        float top = bottom + s * previewHeight;
        if (CompareShown())
        {
            float sc = viewScene.ModelScale(compareRadius);
            Vector3 cs = viewScene.StandShift(compareModel, sc);
            left = compareOffsetX + cs.X + sc * compareLeft;
            float cb = viewScene.StandY(compareLowest, compareCard) + cs.Y + sc * compareLowest;
            bottom = MathF.Min(bottom, cb);
            top = MathF.Max(top, cb + sc * compareHeight);
        }
        bottom = MathF.Min(bottom, 0f);                // keep the grid in view
        orbit.Frame(left, right, bottom, top, 0.15f);
    }

    // The idle motion: "slot_0" in an extracted block, "slot_0_idle" in a converted source;
    // otherwise the first animation, or -1.
    public static int IdleAnimIndex(AnimatedGltfModel model)
    {
        for (int i = 0; i < model.AnimationCount; i++)
        {
            string name = model.AnimationNames[i];
            if (name == "slot_0" || name.StartsWith("slot_0_", StringComparison.Ordinal))
            {
                return i;
            }
        }
        if (model.AnimationCount > 0)
        {
            return 0;
        }
        return -1;
    }

    // One block through the Extractor tab's own conversion, written where the user picks.
    void ExtractOne(int index)
    {
        var r = Dialog.FileSave("glb", DefaultGlbPath(index));
        if (!r.IsOk)
        {
            return;
        }
        string outPath = r.Path;
        if (!outPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
        {
            outPath += ".glb";
        }
        byte[] bytes = BlockBytes(index);
        extractPath = outPath;
        extractTask = Task.Run(() => File.WriteAllBytes(outPath, ModelExtractor.ExtractGlb(bytes)));
    }

    // The save dialog's start for a block's glb: its card or model name, beside the last import.
    string DefaultGlbPath(int index)
    {
        string name;
        if (source == SourceMonster)
        {
            name = cardNames[index];
            if (name.Length == 0)
            {
                name = $"MONSTER_{index:D3}";
            }
        }
        else
        {
            name = imp.Find(index).Name;
        }
        foreach (char ch in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(ch, '_');
        }
        string dir = string.Empty;
        if (!string.IsNullOrEmpty(Program.config.lastModelImportPath))
        {
            dir = Path.GetDirectoryName(Program.config.lastModelImportPath) ?? string.Empty;
        }
        return Path.Combine(dir, name + ".glb");
    }

    void UpdateExtract()
    {
        if (extractTask == null || !extractTask.IsCompleted)
        {
            return;
        }
        if (extractTask.IsFaulted)
        {
            ok = false;
            status = "Extract failed: " + extractTask.Exception.InnerException.Message;
        }
        else
        {
            ok = true;
            status = "Wrote " + extractPath + ".";
        }
        extractTask = null;
    }

    // A View's glb with its SharpGLTF read and scene (the slow part of a load), on the worker:
    // only the GPU half is left for the draw thread, so a View import redo does not stall it.
    // With a donor block, also the radius an import over it writes (Rebuilder.MeasureRadius,
    // the importer's own measure), so the preview and Radius show what the game gets; else -1.
    // The measure runs beside the parse.
    static (byte[] Glb, AnimatedGltfModel.Parsed Parsed, float Radius) WithParsed(byte[] glb, byte[] donor)
    {
        Task<float> radius = Task.FromResult(-1f);
        if (donor != null)
        {
            radius = Task.Run(() => Rebuilder.MeasureRadius(GlbImporter.LoadFromBytes(glb), donor));
        }
        AnimatedGltfModel.Parsed parsed = AnimatedGltfModel.Parse(glb);
        return (glb, parsed, radius.GetAwaiter().GetResult());
    }

    void UpdateView()
    {
        if (viewTask == null || !viewTask.IsCompleted)
        {
            return;
        }
        if (viewTask.IsFaulted)
        {
            ok = false;
            status = "View failed: " + viewTask.Exception.InnerException.Message;
        }
        else
        {
            previewIndex = viewIndex;
            previewIsMonster = viewIsMonster;
            previewCanCompare = viewCanCompare;
            previewIsImport = viewIsImport;
            previewMove = viewMove;
            previewRotate = viewRotate;
            previewScale = viewScale;
            previewLabel = viewLabel;
            if (previewCanCompare)
            {
                compareOn = viewCompareOn;
            }
            if (viewRadius < 0f)
            {
                viewRadius = viewTask.Result.Radius;
            }
            AnimatedGltfModel model = AnimatedGltfModel.Load(viewTask.Result.Parsed);
            if (viewOpensCard)
            {
                OpenCardConvert(viewIndex, model, viewTask.Result.Glb);
            }
            LoadPreview(model, viewName, viewKeepCamera);
        }
        viewTask = null;
    }

    byte[] BlockBytes(int index)
    {
        if (source == SourceModel)
        {
            return imp.BlockBytes(index);
        }
        byte[] block = new byte[BlockSize];
        Array.Copy(monsterMrg, (long)index * BlockSize, block, 0, BlockSize);
        return block;
    }

    byte[] StagedBytes(int index)
    {
        if (source == SourceModel)
        {
            return imp.StagedBytes(index);
        }
        return monsterStaged[index];
    }

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
            ok = false;
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
        Scene sc;
        try
        {
            convertAnimsFrom = ModelConverter.FindMotionFiles(path);
            sc = ModelConverter.ReadSource(path, Warn);
            foreach (string f in convertAnimsFrom)
            {
                Scene es = ModelConverter.ReadSource(f, Warn);
                ModelConverter.AddAnimsFrom(sc, es, Path.GetFileName(f), Warn);
            }
            // the converter sorts the animations by name, so the rows read in the same order
            sc.Anims.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            convertAnimLabels = new string[sc.Anims.Count];
            for (int i = 0; i < sc.Anims.Count; i++)
            {
                convertName.Add(sc.Anims[i].Name);
                convertFrames.Add(sc.Anims[i].Frames);
                convertAnimLabels[i] = $"{sc.Anims[i].Name} ({sc.Anims[i].Frames} frames)";
            }
        }
        catch (Exception ex)
        {
            Failed("Read failed: " + ex.Message);
            return;
        }
        if (convertName.Count > 1)
        {
            PrefillSlots();
        }
        convertOpen = true;
        convertShow = true;
        reportWarnings = convertWarnings;
        ok = true;
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
        ok = false;
        report = message;
        status = message;
        reportWarnings = convertWarnings;
    }

    // Which animation each slot starts with. Each animation is placed by "slot_N" in its name,
    // else its role words, else (a 3DS rip) its Motion letter, else the next free slot; a slot
    // nothing landed on plays the idle animation (the importer would copy idle there too).
    void PrefillSlots()
    {
        bool[] used = new bool[8];                    // MOT slots 0-7 (ParseSlotId)
        int[] slot = new int[convertName.Count];
        for (int i = 0; i < convertName.Count; i++)
        {
            int s = RebuildPipeline.ParseSlotId(convertName[i]);
            if (s < 0)
            {
                s = RebuildPipeline.RoleSlotId(convertName[i]);
            }
            if (s < 0 && convertAnimsFrom.Count > 0)
            {
                s = MotionSlotId(convertName[i]);
            }
            if (s < 0)
            {
                slot[i] = -2;                 // nothing in the name: a free slot, below
                continue;
            }
            if (used[s])
            {
                slot[i] = -1;                 // that slot is taken already: leave it out
                continue;
            }
            used[s] = true;
            slot[i] = s;
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
            int k = SlotIndex(slot[i]);
            if (k >= 0 && convertSlotAnim[k] < 0)
            {
                convertSlotAnim[k] = i;
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
        string n = name.ToLowerInvariant();
        if (n.Contains("motionb"))
        {
            return 0;
        }
        if (n.Contains("motiond"))
        {
            return 2;
        }
        if (n.Contains("motiona"))
        {
            return 5;
        }
        if (n.Contains("motionc"))
        {
            return 6;
        }
        return -1;
    }

    // The animations panel: one row per animation with the slot it goes to, the converter's own
    // options under Advanced, and the Import / Save / Cancel buttons.
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
        Tip("Preview the model converted with these options, beside the model it would replace." +
            "\nNothing is staged; press it again after changing an option.");
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
        foreach (string s in SlotLabels)
        {
            labelWidth = Math.Max(labelWidth, ImGui.CalcTextSize(s).X);
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

    // The converter's options, the same ones tools/toglb.py takes. They hold between imports.
    // Fields are dragged (or double-clicked to type); the View import preview follows a drag at
    // once and is converted again when it is let go.
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
            optScale = MinScale;                       // 0 collapses the model (and NaN fails the !(>=) too)
        }
        Tip("Multiplies the source's size (in metres; 1 game unit = 1 metre). 1 keeps the model as exported." +
            "\nThe size it comes out at is in the convert report. At least " + MinScale + "." +
            "\nDrag to change it, double-click to type.");
        if (optScale != 1f)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset##scale"))
            {
                optScale = 1f;
                changed = true;
            }
            Tip("Back to 1.");
        }
        if (ScaleHiddenByShrink())
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(the board/map shrinks it to radius 40: Feet on grid shows Scale)");
            Tip("lSzModel_LoadCB: x clamp(40 / radius, 0.3, 1) on the board, half that on the map." +
                "\nA bigger Scale grows the radius with it, so the game draws it the same size" +
                "\nuntil the radius passes 133 (x0.3). Set Radius to keep the shrink fixed.");
        }

        changed |= AxisRow("Rotate", "rotate", ref optRotateOn, ref optRotate, 0.5f, "Reset", nameAt, valuesAt,
            "Extra turn in degrees, X applied first, for a model that does not stand upright facing forward " +
            "(up is +Y, facing +Z), about node 1 (Pivot).\nY 180 turns it round. In the View import preview, drag a ring to turn about its axis.",
            new string[] { "Turn about the side axis, in degrees.", "Turn about the up axis, in degrees.",
                "Turn about the forward axis, in degrees." });

        changed |= AxisRow("Position", "move", ref optMoveOn, ref optMove, 0.1f, "Reset", nameAt, valuesAt,
            "Moves the whole model off node 0, in game units (after Scale and the turn)." +
            "\nNode 0, where the game stands the unit, stays put, so this is where the body sits around it." +
            "\nNode 1 (Pivot) stays put too." +
            "\nIn the View import preview, drag an arrow to move it along that axis, or the centre ball across the view.",
            new string[] { "Right (+) or left (-), in game units.",
                "Up (+) or down (-), in game units. The battle floor is 50 below node 0.",
                "Forward (+) or back (-), in game units." });

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
            "Where node 1 sits: the point the game scales and turns the model about and the battle camera" +
            "\naims at, in game units from node 0 (the model's own origin). Scale and Rotate turn the body about it;" +
            "\nwith neither set the body stays where it is. Position does not change it. Off: node 1 on node 0, as the model is imported (a card on the ISO: its own node 1)." +
            "\nIn the View import preview, drag an arrow to move it along that axis, or the centre square across the view." +
            "\nEdited in View: Feet on grid only; the duel board and map stand node 1 over the tile or arena point.",
            new string[] { "Right (+) or left (-), in game units.", "Up (+) or down (-), in game units.",
                "Forward (+) or back (-), in game units." }, pivotStart);
        ImGui.EndDisabled();
        ImGui.EndDisabled();

        ImGui.SeparatorText("Source fixes");
        ImGui.BeginDisabled(convertIsCard);            // a card has no source to fix
        changed |= ImGui.Checkbox("Bind at first frame", ref optRestFromAnim);
        Tip("Bind the skin at the first animation's first frame instead of the file's bind pose." +
            "\nOnly for rips whose bind pose lies flat or is never shown;" +
            "\nvertices pulled by several bones then move slightly differently.");

        changed |= ImGui.Checkbox("Flip UV", ref optFlipUv);
        Tip("Turn this on if the textures land upside down: the source's UVs are already top-left." +
            "\nDone by itself for FBX files exported by Unity Studio.");
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

    // One Advanced row: the toggle, `name`, then X/Y/Z fields dragged by `speed` (greyed while
    // off), and a `resetLabel` button that puts them back at `resetTo` (where the row started).
    // True when a change is done (a drag let go, a value typed, the toggle or the button).
    bool AxisRow(string name, string id, ref bool on, ref Vector3 value, float speed, string resetLabel,
        float nameAt, float valuesAt, string tip, string[] axisTips, Vector3 resetTo = default)
    {
        bool changed = ImGui.Checkbox("##on" + id, ref on);
        Tip("On: " + name + " is applied, and its gizmo shows in the View import preview." +
            "\nOff: it is not applied (the values are kept).");
        ImGui.SameLine(nameAt);
        changed |= AxisFields(name, id, on, ref value, speed, resetLabel, valuesAt, tip, axisTips, resetTo);
        return changed;
    }

    // `name`, then X/Y/Z fields dragged by `speed` (greyed unless `on`), and a `resetLabel`
    // button that puts them back at `resetTo`. True when a change is done (a drag let go, a
    // value typed, the button).
    bool AxisFields(string name, string id, bool on, ref Vector3 value, float speed, string resetLabel,
        float valuesAt, string tip, string[] axisTips, Vector3 resetTo)
    {
        bool changed = false;
        ImGui.AlignTextToFramePadding();
        ImGui.Text(name);
        Tip(tip);
        ImGui.SameLine(valuesAt);
        ImGui.BeginDisabled(!on);
        string[] axes = { "X", "Y", "Z" };
        for (int k = 0; k < 3; k++)
        {
            if (k > 0)
            {
                ImGui.SameLine();
            }
            ImGui.Text(axes[k]);
            ImGui.SameLine();
            float v = Axis(value, k);
            ImGui.SetNextItemWidth(FieldWidth(0.15f, 4f));
            if (ImGui.DragFloat("##" + id + axes[k], ref v, speed, 0f, 0f, "%.3f"))
            {
                value = WithAxis(value, k, v);
            }
            changed |= ImGui.IsItemDeactivatedAfterEdit();
            Tip(axisTips[k] + "\nDrag to change it, double-click to type.");
        }
        if (value != resetTo)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(resetLabel + "##" + id))
            {
                value = resetTo;
                changed = true;
            }
            Tip($"Back to {resetTo.X:0.###}, {resetTo.Y:0.###}, {resetTo.Z:0.###}.");
        }
        ImGui.EndDisabled();
        return changed;
    }

    static float Axis(Vector3 v, int k)
    {
        if (k == 0)
        {
            return v.X;
        }
        if (k == 1)
        {
            return v.Y;
        }
        return v.Z;
    }

    static Vector3 WithAxis(Vector3 v, int k, float value)
    {
        if (k == 0)
        {
            v.X = value;
        }
        else if (k == 1)
        {
            v.Y = value;
        }
        else
        {
            v.Z = value;
        }
        return v;
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
        Tip("The model's bounding radius the import writes (node 0). Measured from the model until changed" +
            "\n(a card on the ISO: its own radius)." +
            "\nOn the duel board the game shrinks a monster to fit radius 40: x clamp(40 / radius, 0.3, 1);" +
            "\nthe map draws it at half that. Battle ignores it (real size)." +
            "\nA radius much smaller than the model may let the game cull it before it leaves the screen." +
            "\nView import shows the result in View: Duel board / Map leader." +
            "\nDrag to change it, double-click to type.");
        // one starting point: an import's radius starts measured, a card's at its own
        if (convertIsCard && optRadius != cardRadiusOwn)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset##radius"))
            {
                optRadius = cardRadiusOwn;
            }
            Tip($"Back to the card's own radius, {cardRadiusOwn:0.###}.");
        }
        else if (!convertIsCard && optRadius > 0f)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Measured##radius"))
            {
                optRadius = 0f;
            }
            Tip("Go back to the measured radius.");
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
        ConvertOptions o = new ConvertOptions();
        o.Scale = optScale;
        if (optRotateOn)
        {
            o.RotateX = optRotate.X;
            o.RotateY = optRotate.Y;
            o.RotateZ = optRotate.Z;
        }
        Vector3 move = MoveApplied();
        o.TranslateX = move.X;
        o.TranslateY = move.Y;
        o.TranslateZ = move.Z;
        Vector3 pivot = LivePivot();
        o.PivotPoint = new double[] { pivot.X, pivot.Y, pivot.Z };
        o.RestFromAnim = optRestFromAnim;
        o.FlipUv = optFlipUv;
        foreach (string f in convertAnimsFrom)
        {
            o.AnimsFrom.Add(f);
        }
        o.Anims = AnimsOption();
        return o;
    }

    // "name=slot_N_role,..." for every slot, in the order the slots are filled; an animation in
    // two slots is named twice and the converter copies it. Null with one animation or
    // none (no slot rows): one animation goes to idle on its own.
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
        ConvertResult res;
        convertWarnings = new List<string>();
        try
        {
            res = ModelConverter.ConvertFile(convertPath, Options(), Warn);
        }
        catch (Exception ex)
        {
            Failed("Convert failed: " + ex.Message);
            return;
        }
        convertOpen = false;
        StageConverted(convertIndex, res.Glb, convertPath);
        reportConvert = res.Report;
        foreach (string w in convertWarnings)
        {
            reportWarnings.Add(w);
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
        catch (Exception ex)
        {
            Failed("Edit failed: " + ex.Message);
            return;
        }
        convertOpen = false;
        StageConverted(index, glb, cardNames[index] + " (edited)");
        if (reportOk)
        {
            cardEdits[index] = CurrentOpts();
        }
    }

    // The same conversion (or card edit), written out instead of staged, to check it in another tool.
    void SaveConverted()
    {
        ConvertResult res = new ConvertResult();
        convertWarnings = new List<string>();
        string defaultPath = Path.ChangeExtension(convertPath, ".glb");
        try
        {
            if (convertIsCard)
            {
                res.Glb = CardEditGlb();
                defaultPath = DefaultGlbPath(convertIndex);
            }
            else
            {
                res = ModelConverter.ConvertFile(convertPath, Options(), Warn);
            }
        }
        catch (Exception ex)
        {
            Failed("Convert failed: " + ex.Message);
            return;
        }
        var r = Dialog.FileSave("glb", defaultPath);
        if (!r.IsOk)
        {
            return;
        }
        string outPath = r.Path;
        if (!outPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
        {
            outPath += ".glb";
        }
        File.WriteAllBytes(outPath, res.Glb);
        reportConvert = res.Report;
        reportWarnings = convertWarnings;
        reportIndex = convertIndex;
        reportOk = true;
        ok = true;
        report = "Wrote " + outPath + ".";
        status = report;
    }

    void StageConverted(int index, byte[] glb, string label)
    {
        reportIndex = index;
        reportWarnings = new List<string>();
        try
        {
            if (source == SourceMonster)
            {
                StageMonster(index, glb, label);
            }
            else
            {
                ModelMrg.StageResult s = imp.Stage(index, glb, label, allowReduction);
                reportOk = s.Staged;
                if (s.Staged)
                {
                    report = s.Summary + " - staged.";
                }
                else
                {
                    report = s.Summary + " - not staged.";
                }
                reportWarnings = s.Warnings;
            }
            ok = reportOk;
            status = report;
        }
        catch (Exception ex)
        {
            reportOk = false;
            ok = false;
            report = "Import failed: " + ex.Message;
            status = report;
        }
    }

    void StageMonster(int index, byte[] glb, string label)
    {
        byte[] donor = LoadTemplate(index);
        ImportedModel model = GlbImporter.LoadFromBytes(glb);
        List<Rh0Assembler.TextureInput> textures = GlbImporter.DecodeGlbTextures(model);

        // What the rebuild asked to reduce, for the fit report when it does not fit.
        bool asked = false;
        Rh0Assembler.OverflowCategory askedCategory = default;
        int askedBytes = 0;
        int askedTotal = 0;
        int askedBudget = 0;

        byte[] bytes;
        try
        {
            bytes = Rebuilder.Import(model, donor, BlockSize, false,
                (category, categoryBytes, totalBytes, budget) =>
                {
                    asked = true;
                    askedCategory = category;
                    askedBytes = categoryBytes;
                    askedTotal = totalBytes;
                    askedBudget = budget;
                    return allowReduction;
                },
                reportWarnings, MaxTextureDim, textures, optRadius);
        }
        catch (Exception) when (asked)
        {
            double pct = 0;
            if (askedBudget > 0)
            {
                pct = 100.0 * askedBytes / askedBudget;
            }
            string what = "the texture(s) is";
            if (askedCategory == Rh0Assembler.OverflowCategory.Animation)
            {
                what = "the animations are";
            }
            reportOk = false;
            report = $"card {index:000} is {askedTotal:N0} B, over the {askedBudget:N0} B slot: " +
                     $"{what} {pct:0.#}% of it ({askedBytes:N0} B) - not staged.";
            return;
        }

        monsterStaged[index] = bytes;
        monsterStagedFrom[index] = label;
        cardEdits.Remove(index);                       // StageCardEdit records an edit again
        reportOk = true;
        float radius = BlockRadius(bytes);
        float shrink = ViewScene.BoardShrink(radius);
        report = $"card {index:000}: {bytes.Length:N0} of {BlockSize:N0} B - staged. " +
                 $"Radius {radius:0.#}: board x{shrink:0.##}, map x{ViewScene.MapScale * shrink:0.##}.";
        // battle slots the glb lacked, filled with a copy of another motion (MotWriter)
        if (MotWriter.LastClones != null && MotWriter.LastClones.Count > 0)
        {
            var copied = new List<string>();
            foreach (var c in MotWriter.LastClones)
            {
                copied.Add($"slot {c.Slot} from slot {c.From}");
            }
            reportWarnings.Add("Missing motions copied: " + string.Join(", ", copied) +
                               " (4 death, 5 win, 6 damage; a copied idle never shows a fall).");
        }
    }

    // The donor block: the card's own bytes.
    byte[] LoadTemplate(int index)
    {
        byte[] block = BlockBytes(index);
        Rh0Assembler.ValidateRh0Block(block, "template");
        return block;
    }

    // A card's slot is its 1 MiB block, so the change is just that run of bytes in MONSTER.MRG.
    ChangeSet MonsterChanges()
    {
        var changes = new ChangeSet();
        foreach (int index in monsterStaged.Keys.OrderBy(k => k))
        {
            changes.Add(monsterPath, (long)index * BlockSize, monsterStaged[index], $"model block {index}");
        }
        AddMapHeights(changes);
        return changes;
    }

    // The edited map heights, one Target_Model_Y byte each in the boot ELF.
    void AddMapHeights(ChangeSet changes)
    {
        foreach (int card in mapHeightEdits.OrderBy(k => k))
        {
            byte[] value = { (byte)(sbyte)imp.Tables.MapLeaderY(card) };
            changes.Add(imp.Tables.BootElfIsoPath, imp.Tables.MapLeaderYOffset(card), value, $"map height card {card}");
        }
    }

    void DrawStagedAndSave(FileLoader fl)
    {
        ImGui.Text($"Staged: {StagedCount()}");
        if (source == SourceMonster)
        {
            foreach (int index in monsterStaged.Keys)
            {
                if (cardEdits.ContainsKey(index))
                {
                    ImGui.BulletText($"card {index:000} {cardNames[index]} (edited)  {monsterStaged[index].Length:N0} of {BlockSize:N0} B");
                    continue;
                }
                ImGui.BulletText($"card {index:000} {cardNames[index]}  {monsterStaged[index].Length:N0} of {BlockSize:N0} B  " +
                                 Path.GetFileName(monsterStagedFrom[index]));
            }
        }
        else
        {
            foreach (ModelMrg.StagedBlock s in imp.Staged)
            {
                ImGui.BulletText($"{s.Block.Name}  {s.Bytes.Length:N0} of {s.Block.Slot:N0} B  {Path.GetFileName(s.SourcePath)}");
            }
        }
        foreach (int card in mapHeightEdits)
        {
            ImGui.BulletText($"card {card:000} {cardNames[card]}  map height {imp.Tables.MapLeaderY(card)}");
        }

        if (StagedCount() > 0)
        {
            if (ImGui.Button("Clear staged"))
            {
                if (source == SourceMonster)
                {
                    monsterStaged.Clear();
                    monsterStagedFrom.Clear();
                    cardEdits.Clear();
                }
                else
                {
                    imp.ClearStaged();
                }
                plan = null;
                planReport = null;
            }
            ImGui.SameLine();
        }

        if (ImGui.Button("Check plan"))
        {
            if (source == SourceMonster)
            {
                plan = MonsterChanges().Validate(fl);
                planReport = null;
            }
            else
            {
                plan = imp.Validate(out planReport);
            }
            planIsSaved = false;
            ok = plan.Ok;
            status = $"Plan: {plan.Writes.Count} writes ({plan.BytesToWrite:N0} B), {plan.NoOps.Count} no-ops, {plan.Errors.Count} errors.";
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(StagedCount() == 0 && mapHeightEdits.Count == 0);
        if (ImGui.Button("Save to ISO"))
        {
            ImGui.OpenPopup("Confirm save");
        }
        ImGui.EndDisabled();

        bool open = true;
        if (ImGui.BeginPopupModal("Confirm save", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (source == SourceMonster)
            {
                ImGui.Text($"Write {StagedCount()} staged model(s) into DATA/MONSTER.MRG in place?");
                ImGui.TextDisabled("Nothing is written if any block does not fit its 1 MiB slot.");
            }
            else
            {
                ImGui.Text($"Write {StagedCount()} staged model(s) into DATA/MODEL.MRG in place?");
                ImGui.TextDisabled("Identical packs are mirrored. Nothing is written if any model does not fit.");
            }
            if (mapHeightEdits.Count > 0)
            {
                ImGui.Text($"Write {mapHeightEdits.Count} map height(s) into {imp.Tables.Addresses.BootElf}?");
            }
            if (ImGui.Button("Yes"))
            {
                ImGui.CloseCurrentPopup();
                Save(fl);
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (plan != null)
        {
            if (planIsSaved)
            {
                ImGui.Text("Last save:");
            }
            else
            {
                ImGui.Text("Last plan (nothing written):");
            }
            if (planReport != null)
            {
                ImGui.Text($"  blocks: {planReport.Written.Count} written, {planReport.Mirrored.Count} mirrored, " +
                           $"{planReport.Unchanged.Count} byte-identical to the disc");
            }
            ImGui.Text($"  writer: {plan.Writes.Count} writes, {plan.NoOps.Count} no-ops, {plan.Errors.Count} errors");
            for (int i = 0; i < plan.Errors.Count && i < 12; i++)
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "  " + plan.Errors[i]);
            }
        }
    }

    void Save(FileLoader fl)
    {
        try
        {
            if (source == SourceMonster)
            {
                plan = MonsterChanges().Write(fl);
                planReport = null;
                if (plan.Ok)
                {
                    // The staged bytes are now the disc's, so the next donor is the new block, and
                    // an edit's values no longer describe it (the next edit starts from it).
                    foreach (int index in monsterStaged.Keys)
                    {
                        Array.Copy(monsterStaged[index], 0, monsterMrg, (long)index * BlockSize, BlockSize);
                    }
                    cardEdits.Clear();
                    cardGlb = null;
                    if (convertIsCard)
                    {
                        convertOpen = false;
                    }
                }
            }
            else
            {
                // The heights first: a MODEL.MRG save re-reads the tables from the disc.
                var heights = new ChangeSet();
                AddMapHeights(heights);
                plan = heights.Write(fl);
                planReport = null;
                if (plan.Ok)
                {
                    plan = imp.Save(out planReport);
                }
            }
            planIsSaved = plan.Ok;
            ok = plan.Ok;
            if (plan.Ok)
            {
                mapHeightEdits.Clear();                // on the disc now

                status = $"Saved: {plan.Writes.Count} writes ({plan.BytesToWrite:N0} B), {plan.NoOps.Count} unchanged.";
                if (!keepStagedAfterSave)
                {
                    // committed to the disc: nothing is left to stage (the save report stays shown)
                    if (source == SourceMonster)
                    {
                        monsterStaged.Clear();
                        monsterStagedFrom.Clear();
                    }
                    else
                    {
                        imp.ClearStaged();
                    }
                }
            }
            else
            {
                status = $"Nothing written: {plan.Errors.Count} error(s).";
            }
        }
        catch (Exception ex)
        {
            ok = false;
            status = "Save failed: " + ex.Message;
        }
    }

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

        bool hovered = ImGui.IsWindowHovered();
        bool gizmoBusy = UpdateGizmos(ImGui.GetMousePos() - imageMin, hovered);
        if (hovered && !gizmoBusy)
        {
            orbit.UpdateMouse();
        }
        AdvanceAnimation();
        AdvanceCompare();
        if (aimAtPivot)
        {
            orbit.Target = PivotWorld();
        }
        orbit.Apply();
        RenderScene();

        rlImGui.ImageRenderTexture(previewTex);
        ImGui.EndChild();
    }

    // The placement for every preview; Compare only for an import or a staged model.
    void DrawCompareControls()
    {
        ImGui.Checkbox("Aim at pivot", ref aimAtPivot);
        Tip("The camera looks at node 1 (the Pivot) as it moves with the animation, as the game's duel board" +
            "\nfocus camera and the camera that follows a monster after a hit do. Drag to turn, wheel to zoom." +
            "\nThe battle attack and win cameras are the card's own camera paths, set from node 0 instead.");
        ImGui.SameLine();
        if (previewCanCompare)
        {
            if (ImGui.Checkbox("Compare", ref compareOn))
            {
                RequestCompare();
                PlaceCompare();
                FrameStanding();
            }
            Tip("Stand a second model beside this one to compare sizes." +
                "\nIt is this card's model as it is on the ISO: the model the import replaces.");
            if (compareOn)
            {
                ImGui.SameLine();
                if (ImGui.Checkbox("Dark Magician instead", ref compareDarkMagician))
                {
                    RequestCompare();
                }
                Tip("Compare with Dark Magician (card 060): 24.8 tall, about the median retail monster.");
            }
            if (previewIsMonster)
            {
                ImGui.SameLine();                      // the View combo follows (monsters only)
            }
        }
        int card = previewIndex;
        string tip = "";
        if (previewIsImport)
        {
            tip = "\nPivot and Position show here; an import keeps the replaced card's map height." +
                "\nThe radius of an import can be set in the Convert window's Advanced options.";
        }
        int heightBefore = imp.Tables.MapLeaderY(card);
        if (viewScene.DrawControls(previewIsMonster, ref card, false, tip))
        {
            PlaceCompare();
            FrameStanding();
        }
        if (imp.Tables.MapLeaderY(card) != heightBefore)
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
            float dur = previewModel.Duration(currentAnim);
            if (dur > 0f)
            {
                animTime += Raylib.GetFrameTime();
                animTime %= dur;
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
            float dur = compareModel.Duration(compareAnim);
            if (dur > 0f)
            {
                compareTime += Raylib.GetFrameTime();
                compareTime %= dur;
            }
        }
        compareModel.SetAnimationFrame(compareAnim, compareTime);
    }

    void RenderScene()
    {
        bool compare = CompareShown();
        if (previewCanCompare)
        {
            PlaceCompare();                            // the Radius option may have changed the shrink
        }
        float drawn = viewScene.ModelScale(PreviewRadius()) * PreviewRadius();
        if (compare)
        {
            drawn = MathF.Max(drawn, viewScene.ModelScale(compareRadius) * compareRadius) + MathF.Abs(compareOffsetX);
        }

        Raylib.BeginTextureMode(previewTex);
        Raylib.ClearBackground(Color.DarkGray);
        viewScene.SetClipPlanes(orbit.Camera, drawn);
        Raylib.BeginMode3D(orbit.Camera);
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

    // Live preview. The shown glb was converted (or a card edited) with previewScale/Rotate/Move;
    // the options as they stand map its point g to k * T * (g - c0 - previewMove) + LiveShift()
    // (both scale and turn about TurnCentre, then move), k the Scale ratio, T the turn from the old
    // Rotate to the new. Not exact where the converter does more (a new rest pose), so the
    // model is converted again once a change is done.
    float LiveScale()
    {
        return optScale / previewScale;
    }

    Mat4 LiveTurn()
    {
        Vector3 now = RotateApplied();
        Mat4 turn = Mat4.Euler(now.X, now.Y, now.Z);
        return turn * Mat4.Euler(previewRotate.X, previewRotate.Y, previewRotate.Z).Inverse();
    }

    static Vector3 Turn(Mat4 m, Vector3 v)
    {
        return new Vector3(
            (float)(m[0, 0] * v.X + m[0, 1] * v.Y + m[0, 2] * v.Z),
            (float)(m[1, 0] * v.X + m[1, 1] * v.Y + m[1, 2] * v.Z),
            (float)(m[2, 0] * v.X + m[2, 1] * v.Y + m[2, 2] * v.Z));
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

    // The part of LiveGlb that does not depend on g: where the shown glb's turn centre (its own
    // node 1, c0) lands, c + Move + S R (c0 - c), c the centre as the options stand.
    Vector3 LiveShift()
    {
        Vector3 c = TurnCentre();
        Vector3 now = RotateApplied();
        return c + MoveApplied() + optScale * Turn(Mat4.Euler(now.X, now.Y, now.Z), previewModel.RestPivot - c);
    }

    // A point of the shown glb where the options as they stand put it: the shown glb was turned
    // about its own node 1 (c0), so g = c0 + A0 (x - c0) + previewMove for source point x.
    Vector3 LiveGlb(Vector3 g)
    {
        Vector3 c0 = previewModel.RestPivot;
        return LiveScale() * Turn(LiveTurn(), g - c0 - previewMove) + LiveShift();
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
        float s = viewScene.ModelScale(PreviewRadius());
        Vector3 shift = viewScene.StandShift(pivot, previewModel.RestNodeZero, s);
        return new Vector3(shift.X, viewScene.StandY(lowest, previewIndex) + shift.Y, shift.Z);
    }

    // Where the preview draws point `g` of a glb converted with the options as they stand.
    Vector3 LiveWorld(Vector3 g)
    {
        return StandAt(NodeZero() + LivePivot(), LiveLowest()) + viewScene.ModelScale(PreviewRadius()) * g;
    }

    // Node 1 where the preview draws it: on the Pivot as the options stand for a View import
    // (a converted model's node 1 does not move), else the shown model's node 1 in its current pose.
    Vector3 PivotWorld()
    {
        if (ImportPreviewShown())
        {
            return LiveWorld(NodeZero() + LivePivot());
        }
        float s = viewScene.ModelScale(PreviewRadius());
        return StandAt(previewModel.RestPivot, ShownLowest()) + s * previewModel.PivotNow();
    }

    // Before DrawStanding: takes the shown glb's drawn point W(g) to LiveWorld(LiveGlb(g)),
    // W(g) = A + s g: translate(A' + s LiveShift) * scale(k) * turn * translate(-(A + s (c0 + previewMove))).
    void ApplyLiveTransform()
    {
        float s = viewScene.ModelScale(PreviewRadius());
        Vector3 from = StandAt(previewModel.RestPivot, ShownLowest()) + s * (previewModel.RestPivot + previewMove);
        Vector3 to = StandAt(NodeZero() + LivePivot(), LiveLowest()) + s * LiveShift();
        Rlgl.Translatef(to.X, to.Y, to.Z);
        float k = LiveScale();
        Rlgl.Scalef(k, k, k);
        Vector3 axis;
        float degrees;
        AxisAngle(LiveTurn(), out axis, out degrees);
        if (degrees != 0f)
        {
            Rlgl.Rotatef(degrees, axis.X, axis.Y, axis.Z);
        }
        Rlgl.Translatef(-from.X, -from.Y, -from.Z);
    }

    // A turn matrix as an axis and an angle in degrees (0 for none).
    static void AxisAngle(Mat4 m, out Vector3 axis, out float degrees)
    {
        double cos = Math.Clamp((m[0, 0] + m[1, 1] + m[2, 2] - 1.0) * 0.5, -1.0, 1.0);
        double angle = Math.Acos(cos);
        axis = Vector3.UnitY;
        degrees = 0f;
        if (angle < 1e-5)
        {
            return;
        }
        Vector3 a = new Vector3((float)(m[2, 1] - m[1, 2]), (float)(m[0, 2] - m[2, 0]), (float)(m[1, 0] - m[0, 1]));
        if (Math.Sin(angle) < 1e-3)
        {
            // a half turn: the axis from the diagonal, signs from the largest part
            float x = (float)Math.Sqrt(Math.Max(0.0, (m[0, 0] + 1.0) * 0.5));
            float y = (float)Math.Sqrt(Math.Max(0.0, (m[1, 1] + 1.0) * 0.5));
            float z = (float)Math.Sqrt(Math.Max(0.0, (m[2, 2] + 1.0) * 0.5));
            if (x >= y && x >= z)
            {
                y = MathF.CopySign(y, (float)m[0, 1]);
                z = MathF.CopySign(z, (float)m[0, 2]);
            }
            else if (y >= z)
            {
                x = MathF.CopySign(x, (float)m[0, 1]);
                z = MathF.CopySign(z, (float)m[1, 2]);
            }
            else
            {
                x = MathF.CopySign(x, (float)m[0, 2]);
                y = MathF.CopySign(y, (float)m[1, 2]);
            }
            a = new Vector3(x, y, z);
        }
        if (a.LengthSquared() < 1e-12f)
        {
            return;
        }
        axis = Vector3.Normalize(a);
        degrees = (float)(angle * 180.0 / Math.PI);
    }

    // Rotate's X, Y, Z (degrees, X turned first) for turn matrix `m` = Rz * Ry * Rx.
    static Vector3 EulerOf(Mat4 m)
    {
        double y = Math.Asin(Math.Clamp(-m[2, 0], -1.0, 1.0));
        double x;
        double z;
        if (Math.Cos(y) > 1e-6)
        {
            x = Math.Atan2(m[2, 1], m[2, 2]);
            z = Math.Atan2(m[1, 0], m[0, 0]);
        }
        else
        {
            x = Math.Atan2(-m[1, 2], m[1, 1]);     // straight up or down: all the turn in X
            z = 0.0;
        }
        double d = 180.0 / Math.PI;
        return new Vector3((float)(x * d), (float)(y * d), (float)(z * d));
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
            return gizmoLength;                        // steady while dragging
        }
        return orbit.Distance * 0.15f;
    }

    // Rotate's rings are a little wider than the arrows, so they do not meet the arrow tips.
    float RingRadius()
    {
        return GizmoLength() * 1.25f;
    }

    // A point on the ring about axis `k`, at angle `t` (radians).
    static Vector3 RingPoint(Vector3 centre, int k, float radius, float t)
    {
        Vector3 u = Vector3.UnitY;
        Vector3 v = Vector3.UnitZ;
        if (k == 1)
        {
            u = Vector3.UnitZ;
            v = Vector3.UnitX;
        }
        else if (k == 2)
        {
            u = Vector3.UnitX;
            v = Vector3.UnitY;
        }
        return centre + radius * (MathF.Cos(t) * u + MathF.Sin(t) * v);
    }

    const int RingSegments = 48;

    // A ring piece on the far side of its centre from the camera: drawn faint, not grabbed.
    bool RingBack(Vector3 centre, Vector3 a, Vector3 b)
    {
        return Vector3.Dot((a + b) * 0.5f - centre, orbit.Camera.Position - centre) < 0f;
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

    Vector2 PreviewToScreen(Vector3 p)
    {
        return Raylib.GetWorldToScreenEx(p, orbit.Camera, previewW, previewH);
    }

    // The gizmo and handle under `mouse` (pixels in the preview image): a centre first, then the
    // nearest arrow or ring. False when none.
    bool GizmoHit(Vector2 mouse, out int kind, out int handle)
    {
        kind = -1;
        handle = -1;
        int[] arrowKinds = { GizmoPivot, GizmoMove };
        foreach (int g in arrowKinds)
        {
            // Pivot's centre is the smaller one, inside Position's when they share a spot
            float reach = g == GizmoPivot ? 5f : 9f;
            if (GizmoShown(g) && Vector2.Distance(mouse, PreviewToScreen(GizmoCentre(g))) < reach)
            {
                kind = g;
                handle = 3;
                return true;
            }
        }
        float bestDist = 7f;
        foreach (int g in arrowKinds)
        {
            if (!GizmoShown(g))
            {
                continue;
            }
            Vector3 o = GizmoCentre(g);
            Vector2 p0 = PreviewToScreen(o);
            for (int k = 0; k < 3; k++)
            {
                float dist = DistanceToSegment(mouse, p0, PreviewToScreen(o + GizmoAxis(k) * ArrowLength(g)));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    kind = g;
                    handle = k;
                }
            }
        }
        if (GizmoShown(GizmoRotate))
        {
            Vector3 c = GizmoCentre(GizmoRotate);
            float r = RingRadius();
            for (int k = 0; k < 3; k++)
            {
                Vector3 prev = RingPoint(c, k, r, 0f);
                for (int i = 1; i <= RingSegments; i++)
                {
                    Vector3 next = RingPoint(c, k, r, i * 2f * MathF.PI / RingSegments);
                    if (!RingBack(c, prev, next))
                    {
                        float dist = DistanceToSegment(mouse, PreviewToScreen(prev), PreviewToScreen(next));
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            kind = GizmoRotate;
                            handle = k;
                        }
                    }
                    prev = next;
                }
            }
        }
        return kind >= 0;
    }

    static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 d = b - a;
        float t = 0f;
        if (d.LengthSquared() > 0f)
        {
            t = Math.Clamp(Vector2.Dot(p - a, d) / d.LengthSquared(), 0f, 1f);
        }
        return Vector2.Distance(p, a + d * t);
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
            Vector2 m = mouse - PreviewToScreen(gizmoStartCentre);
            gizmoLastAngle = MathF.Atan2(m.Y, m.X);
            gizmoTurned = 0f;
            return true;
        }
        return false;
    }

    // An arrow moves along its axis as far as the mouse goes along the arrow on screen; a centre
    // moves across the view; a ring turns about its (world) axis as far as the mouse goes round
    // the centre. Ctrl snaps moved values to whole units.
    void DragGizmo(Vector2 mouse)
    {
        Vector3 o = gizmoStartCentre;
        if (gizmoKind == GizmoRotate)
        {
            Vector2 m = mouse - PreviewToScreen(o);
            float angle = MathF.Atan2(m.Y, m.X);
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
            if (Vector3.Dot(axis, orbit.Camera.Position - o) > 0f)
            {
                sign = -1f;
            }
            gizmoTurned += sign * step * 180f / MathF.PI;
            Mat4 start = Mat4.Euler(gizmoStartValue.X, gizmoStartValue.Y, gizmoStartValue.Z);
            Mat4 turned = Mat4.Rotation(axis.X, axis.Y, axis.Z, gizmoTurned) * start;
            SetGizmoValue(gizmoKind, EulerOf(turned));
            return;
        }
        Vector2 md = mouse - gizmoStartMouse;
        Vector3 world;
        if (gizmoDrag == 3)
        {
            float dist = Vector3.Distance(orbit.Camera.Position, o);
            float perPixel = 2f * dist * MathF.Tan(orbit.Camera.FovY * 0.5f * MathF.PI / 180f) / Math.Max(previewH, 1);
            world = (orbit.Right * md.X - orbit.Up * md.Y) * perPixel;
        }
        else
        {
            Vector3 axis = GizmoAxis(gizmoDrag);
            Vector2 d = PreviewToScreen(o + axis * gizmoLength) - PreviewToScreen(o);
            if (d.LengthSquared() < 4f)
            {
                return;                                // the arrow points at the camera
            }
            world = axis * (Vector2.Dot(md, d) / d.LengthSquared() * gizmoLength);
        }
        float s = viewScene.ModelScale(PreviewRadius());
        Vector3 at = gizmoStartValue + world / s;
        if (Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl))
        {
            if (gizmoDrag == 0 || gizmoDrag == 3)
            {
                at.X = MathF.Round(at.X);
            }
            if (gizmoDrag == 1 || gizmoDrag == 3)
            {
                at.Y = MathF.Round(at.Y);
            }
            if (gizmoDrag == 2 || gizmoDrag == 3)
            {
                at.Z = MathF.Round(at.Z);
            }
        }
        SetGizmoValue(gizmoKind, at);
    }

    // Red X, green Y, blue Z, drawn over the model: Move's and Pivot's arrows (Move with a ball
    // in the middle; Pivot shorter, square tips, a small white cube) and Rotate's rings (the far half faint). The handle
    // under the mouse or being dragged turns yellow.
    void DrawGizmos()
    {
        if (!GizmoShown(GizmoMove) && !GizmoShown(GizmoRotate) && !GizmoShown(GizmoPivot))
        {
            return;
        }
        Rlgl.DrawRenderBatchActive();
        Rlgl.DisableDepthTest();
        int litKind = gizmoHoverKind;
        int lit = gizmoHover;
        if (gizmoKind >= 0)
        {
            litKind = gizmoKind;
            lit = gizmoDrag;
        }
        Color[] colors = { new Color(230, 70, 70, 255), new Color(120, 200, 60, 255), new Color(70, 130, 240, 255) };
        float len = GizmoLength();
        if (GizmoShown(GizmoRotate))
        {
            Vector3 c = GizmoCentre(GizmoRotate);
            if (gizmoKind == GizmoRotate)
            {
                c = gizmoStartCentre;
            }
            float r = RingRadius();
            for (int k = 0; k < 3; k++)
            {
                Color col = colors[k];
                if (litKind == GizmoRotate && lit == k)
                {
                    col = Color.Yellow;
                }
                Color faint = new Color(col.R, col.G, col.B, (byte)60);
                Vector3 prev = RingPoint(c, k, r, 0f);
                for (int i = 1; i <= RingSegments; i++)
                {
                    Vector3 next = RingPoint(c, k, r, i * 2f * MathF.PI / RingSegments);
                    Color piece = col;
                    if (RingBack(c, prev, next))
                    {
                        piece = faint;
                    }
                    Raylib.DrawCylinderEx(prev, next, len * 0.012f, len * 0.012f, 4, piece);
                    prev = next;
                }
            }
        }
        int[] arrowKinds = { GizmoMove, GizmoPivot };
        foreach (int g in arrowKinds)
        {
            if (!GizmoShown(g))
            {
                continue;
            }
            Vector3 o = GizmoCentre(g);
            for (int k = 0; k < 3; k++)
            {
                Color col = colors[k];
                if (litKind == g && lit == k)
                {
                    col = Color.Yellow;
                }
                Vector3 axis = GizmoAxis(k);
                float arrow = ArrowLength(g);
                if (g == GizmoPivot)
                {
                    // Pivot: a shorter shaft with a square tip
                    float tip = len * 0.07f;
                    Raylib.DrawCylinderEx(o, o + axis * (arrow - tip * 0.5f), len * 0.015f, len * 0.015f, 6, col);
                    Raylib.DrawCube(o + axis * arrow, tip, tip, tip, col);
                    continue;
                }
                Vector3 neck = o + axis * (arrow * 0.8f);
                Raylib.DrawCylinderEx(o, neck, len * 0.015f, len * 0.015f, 6, col);
                Raylib.DrawCylinderEx(neck, o + axis * arrow, len * 0.06f, 0f, 8, col);
            }
            Color centre = Color.White;
            if (litKind == g && lit == 3)
            {
                centre = Color.Yellow;
            }
            float box = len * 0.08f;
            if (g == GizmoMove)
            {
                Raylib.DrawSphere(o, box * 0.7f, centre);
            }
            else
            {
                Raylib.DrawCube(o, box * 0.6f, box * 0.6f, box * 0.6f, centre);
            }
        }
        Rlgl.DrawRenderBatchActive();
        Rlgl.EnableDepthTest();
    }

    // The grid under the preview and, when shown, the compare model. Fitted to node 0's radius
    // around node 0, not the idle extent, so Rotate does not move or resize it.
    void DrawFloorGrid(bool compare)
    {
        float s = viewScene.ModelScale(PreviewRadius());
        float centre = viewScene.StandShift(previewModel, s).X + s * previewModel.RestNodeZero.X;
        float reach = s * PreviewRadius();
        float left = centre - reach;
        float right = centre + reach;
        float deep = 2f * reach;
        if (compare)
        {
            float sc = viewScene.ModelScale(compareRadius);
            left = MathF.Min(left, compareOffsetX + sc * compareLeft);
            deep = MathF.Max(deep, sc * MathF.Max(compareRight - compareLeft, compareHeight));
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
