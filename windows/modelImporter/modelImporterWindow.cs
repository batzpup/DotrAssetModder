using System.Numerics;
using ImGuiNET;
using NativeFileDialogSharp;
using Raylib_cs;
using rlImGui_cs;
using static DotrAssetModder.GuiContext;
using static DotrAssetModder.Bin;

namespace DotrAssetModder;

/* One importer for both model archives. MONSTER.MRG is 683 fixed 1 MiB card slots; a MODEL.MRG
   block's slot is the gap to the next one. Pick a slot, load a glb, read the fit report, then
   save everything staged to the ISO through ChangeSet. */
public partial class ModelImporterWindow
{
    const int ModelCount = 683;
    const int SourceMonster = 0;
    const int SourceModel = 1;

    int source = SourceMonster;
    string status = string.Empty;
    bool statusSucceeded = true;
    string openedIso = string.Empty;

    // MONSTER.MRG
    string monsterPath;
    byte[] monsterMrg;
    string[] cardNames = new string[ModelCount];
    // GameTables.MessageLanguageChangeCount when cardNames were read.
    int cardNamesLanguageChangeCount;
    Dictionary<int, byte[]> monsterStaged = new Dictionary<int, byte[]>();
    Dictionary<int, string> monsterStagedFrom = new Dictionary<int, string>();
    HashSet<int> mapHeightEdits = new HashSet<int>(); // cards whose Map height was edited, written on Save to ISO
    int selectedMonster = -1;

    // MODEL.MRG
    ModelMrg modelMrg;
    int groupFilter; // 0 = all, else ModelMrg.Groups[groupFilter - 1]
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
    List<string> reportConvert = new List<string>(); // the converter's own report lines

    // The battle motion slots, in the order the importer fills them (RebuildPipeline's
    // SlotFillOrder), what the panel calls them, and the word written after "slot_N_".
    static readonly int[] SlotIds = { 0, 2, 4, 5, 6 };
    static readonly string[] SlotLabels = { "Idle", "Attack", "Death", "Win", "Hurt" };
    static readonly string[] SlotRoleName = { "idle", "attack", "death", "win", "hurt" };

    bool keepStagedAfterSave; // off: a successful Save to ISO clears the staging

    bool convertOpen; // the Convert panel's own window
    bool convertShow; // newly opened: expand it and bring it to the front
    bool convertSelectTransform; // newly opened: its tab bar goes back to Transform
    bool convertClosedByUser; // closed with its own close button: the preview's Show options opens it again
    string convertPath = string.Empty;
    int convertIndex = -1;
    List<string> convertName = new List<string>();
    List<int> convertFrames = new List<int>();
    string[] convertAnimLabels = new string[0]; // "name (N frames)", for the slot combos
    int[] convertSlotAnim = new int[0]; // per slot (SlotIds order), the animation it plays
    List<string> convertAnimsFrom = new List<string>();
    List<string> convertWarnings = new List<string>(); // what reading the source said

    /* The Convert window on a card as it is on the ISO (opened by View): the same Transform and
       Radius options, applied to the card's extracted glb by ExtractedGlbEdit and staged like an
       import. Each kind keeps its own option values. */
    struct ImportOptions
    {
        public float Scale;
        public bool RotateOn;
        public Vector3 Rotate;
        public bool MoveOn;
        public Vector3 Move;
        public bool PivotOn;
        public Vector3 PivotAt;
        public float Radius;
        public Vector3 PivotRest; // a card's: its node 1 rest offset from node 0
        public List<SoundCue>? SoundCues; // null: the card's own (LoadCardSoundCues)
    }

    bool convertIsCard; // the Convert window edits convertIndex's card
    ImportOptions importOptions; // the import options while it does
    Dictionary<int, ImportOptions> cardEdits = new Dictionary<int, ImportOptions>(); // staged edits, by card
    byte[] cardGlb; // the card's glb as on the ISO; null: extract it again
    Vector3 cardPivotRest; // its node 1 rest offset from node 0
    float cardRadiusOwn; // its node 0 radius on the ISO
    bool cardEditable; // false: node 1 draws part of it, Radius only

    // The last Check plan / Save, kept so the report stays on screen after the write.
    ChangeSet.Plan plan;
    ModelMrg.BuildResult planReport;
    bool planIsSaved;

    // View extracts on a worker thread; the preview is built from its glb on the main thread.
    Task<(byte[] Glb, AnimatedGltfModel.Parsed Parsed, float Radius)> viewTask; // read and parsed off the draw thread
    string viewName = string.Empty;

    // Extract .glb... writes one block on a worker thread too.
    Task extractTask;
    string extractPath = string.Empty;

    /* The viewed block's card and board scale, taken when View starts and kept with the preview.
       Compare is offered only for an import: View import from the Convert window, or View on a
       staged slot. A plain View of the ISO's model has nothing to compare with. */
    int viewIndex = -1;
    bool viewIsMonster;
    bool viewCanCompare;
    bool viewCompareOn; // Compare's state when that preview opens
    bool viewIsImport; // View import: redone when an option changes
    bool viewOpensCard; // a card's View: the Convert window opens for it
    bool viewKeepCamera; // a redo keeps the camera and animation
    string viewLabel = string.Empty; // the model's name beside its height
    float viewRadius = -1f; // the block's node 0 radius; -1: the importer's measure (View import)
    List<SoundCue>? viewSoundCues; // View of a block: its sound cues (a View import plays the Convert window's)
    List<SoundCue>? previewSoundCues;
    Vector3 viewMove; // View import: the Move it was converted with
    Vector3 viewRotate; // and the Rotate
    float viewScale = 1f; // and the Scale
    int previewIndex = -1;
    bool previewIsMonster;
    bool previewCanCompare;
    bool previewIsImport;
    string previewLabel = string.Empty;
    float previewLowest; // lowest idle point, relative to node 0
    float standLowest; // Feet's stand height, from the first view (before Move)
    float previewHeight; // tallest idle frame
    float previewLeft; // idle X extent, in glb coordinates
    float previewRight;
    float previewBack; // idle Z extent, for the debug footprint
    float previewFront;
    float previewRadius = 1f; // node 0's radius, read from the block or the importer's measure
    Vector3 previewMove; // Move in previewLeft/Right etc. (View import), else 0
    Vector3 previewRotate; // Rotate the preview was converted with, else 0
    float previewScale = 1f; // Scale the preview was converted with, else 1
    bool importDirty; // an option changed under a View import

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
    float compareBack; // idle Z extent, for the debug footprint
    float compareFront;
    float compareOffsetX; // its node 0 stands this far to the left of the preview's
    float compareRadius = 1f; // its block's node 0 radius
    float compareTaskRadius = 1f;
    int compareAnim = -1; // its idle animation
    float compareTime;

    AnimatedGltfModel previewModel;
    bool previewLoaded = false;
    bool previewOpen = false; // the preview's own window; closing it hides it until the next View
    bool previewShow = false; // a new View: expand the window if collapsed and bring it to the front
    string previewName = string.Empty;
    RenderTexture2D previewTexture;
    bool previewTextureReady = false;
    int previewTextureWidth = 1;
    int previewTextureHeight = 1;

    OrbitCamera orbitCamera = new OrbitCamera();
    bool aimAtPivot; // the camera follows node 1, as the game's focus camera does

    int currentAnim = 0;
    float animTime = 0f;
    bool animPlaying = false;

    public ModelImporterWindow(MusicExtractorWindow soundPlayer)
    {
        this.soundPlayer = soundPlayer;
        groupNames = new string[ModelMrg.Groups.Length + 1];
        groupNames[0] = "All";
        for (int i = 0; i < ModelMrg.Groups.Length; i++)
        {
            groupNames[i + 1] = ModelMrg.Groups[i];
        }
    }

    public void Render()
    {
        var fileLoader = FileLoader.Instance;

        if (!fileLoader.IsIsoLoaded || fileLoader.IsoDataFilesAll == null || string.IsNullOrEmpty(fileLoader.CurrentIsoPath))
        {
            ImGui.TextDisabled("Load an ISO first (Extractor tab).");
            return;
        }
        if (openedIso != fileLoader.CurrentIsoPath)
        {
            if (!Open(fileLoader))
            {
                return;
            }
        }
        if (cardNamesLanguageChangeCount != GameTables.MessageLanguageChangeCount && modelMrg != null)
        {
            modelMrg.Tables.ReadMessages(modelMrg.Disc);
            ReadCardNames();
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
            ImGui.TextColored(new Vector4(1, 0.6f, 0, 1), "MONSTER.MRG: the 683 card models. Each lives in a fixed 1 MiB block.");
        }
        else
        {
            ImGui.TextColored(new Vector4(1, 0.6f, 0, 1),
                "MODEL.MRG: duel table, terrain, arena stages, event scenes, stained-glass windows. " + "A model must fit its existing slot.");
            if (modelMrg.Anomalies.Count > 0)
            {
                ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), $"{modelMrg.Anomalies.Count} parse anomalies");
            }
        }

        DrawFilters(fileLoader);
        ImGui.Separator();
        DrawList();
        ImGui.Separator();
        DrawSelected();
        ImGui.Separator();
        DrawStagedAndSave(fileLoader);

        if (status.Length > 0)
        {
            if (statusSucceeded)
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
            statusSucceeded = false;
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
                convertSelectTransform = true;
            }
            string title = "Convert - " + Path.GetFileName(convertPath);
            if (convertIsCard)
            {
                title = "View - " + cardNames[convertIndex];
            }
            bool convertWindowShown = ImGui.Begin(title + "###importer_convert", ref convertOpen);
            convertClosedByUser = !convertOpen;
            if (convertWindowShown)
            {
                DrawConvertPanel();
            }
            ImGui.End();
        }
    }

    void ReadCardNames()
    {
        cardNamesLanguageChangeCount = GameTables.MessageLanguageChangeCount;
        for (int i = 0; i < ModelCount; i++)
        {
            cardNames[i] = modelMrg.Tables.CardName(i).Trim();
        }
        rowsDirty = true;
    }

    // Reads both archives and the card names from a newly opened ISO.
    bool Open(FileLoader fileLoader)
    {
        try
        {
            monsterPath = fileLoader.Find("MONSTER.MRG")?.IsoPath;
            if (monsterPath == null)
            {
                throw new Exception("MONSTER.MRG not found in ISO.");
            }
            monsterMrg = fileLoader.GetFileBytesFromDisk(monsterPath);
            compareCard = -1; // the compare model came from the old ISO
            viewScene.Unload();
            monsterStaged.Clear();
            monsterStagedFrom.Clear();
            mapHeightEdits.Clear();
            cardEdits.Clear();
            cardGlb = null;
            PutImportOptionsBack();

            if (modelMrg != null)
            {
                modelMrg.Dispose();
            }
            modelMrg = ModelMrg.Open(fileLoader.CurrentIsoPath);
            viewScene.SetSource(modelMrg);
            ReadCardNames();
        }
        catch (Exception exception)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "Could not read the ISO's models: " + exception.Message);
            return false;
        }

        openedIso = fileLoader.CurrentIsoPath;
        selectedMonster = -1;
        selectedModel = -1;
        plan = null;
        planReport = null;
        status = string.Empty;
        convertOpen = false;
        convertClosedByUser = false;
        ClearReport();
        rowsDirty = true;
        return true;
    }

    void SourceChanged()
    {
        convertOpen = false; // its slot index belongs to the other archive
        convertClosedByUser = false;
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
        return modelMrg.IsStaged(index);
    }

    int StagedCount()
    {
        if (source == SourceMonster)
        {
            return monsterStaged.Count;
        }
        return modelMrg.StagedCount;
    }

    void DrawFilters(FileLoader fileLoader)
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
            if (Open(fileLoader))
            {
                status = "Reloaded the models from the ISO.";
                statusSucceeded = true;
            }
        }
        ImGui.SameLine();
        ImGui.Checkbox("Keep staged after save", ref keepStagedAfterSave);
        HoveredTooltip(@"On: staged models stay staged after Save to ISO, so they can be saved again (e.g. to another ISO). 
Off: a successful save clears the staging, since those models are now on the disc.");
    }

    bool Matches(string listName, string displayName)
    {
        if (nameFilter.Length == 0)
        {
            return true;
        }
        return listName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ||
               displayName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase);
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
            foreach (ModelMrg.Block block in modelMrg.Blocks)
            {
                // DUEL01-21 are byte-identical to DUEL00 and saved with it, so only DUEL00 is listed
                if (block.IsMirror)
                {
                    continue;
                }
                if (groupFilter > 0 && block.Group != ModelMrg.Groups[groupFilter - 1])
                {
                    continue;
                }
                if (!Matches(block.Name, block.FriendlyName))
                {
                    continue;
                }
                rowIndex.Add(block.Index);
                rowName.Add(block.Name);
                rowWhat.Add(block.FriendlyName);
                rowGroup.Add(block.Group);
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
                for (int visibleRow = clipper.DisplayStart; visibleRow < clipper.DisplayEnd; visibleRow++)
                {
                    int index = rowIndex[visibleRow];
                    bool staged = IsStaged(index);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    if (staged)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 1f, 0.4f, 1f));
                    }
                    ImGui.PushID(index);
                    if (ImGui.Selectable(rowName[visibleRow], Selected() == index, ImGuiSelectableFlags.SpanAllColumns))
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
                    if (rowWhat[visibleRow].Length > 0)
                    {
                        ImGui.Text(rowWhat[visibleRow]);
                    }
                    else
                    {
                        ImGui.TextDisabled("not identified yet");
                    }
                    ImGui.TableNextColumn();
                    ImGui.Text(rowGroup[visibleRow]);
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
        ModelMrg.Block block = modelMrg.Find(index);
        if (block.FriendlyName.Length > 0)
        {
            return $"{block.Name}  -  {block.FriendlyName}";
        }
        return block.Name;
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
            ImGui.TextDisabled($"slot {GameTables.MonsterSlotSize:N0} B at MONSTER.MRG+0x{(long)index * GameTables.MonsterSlotSize:X}");
        }
        else
        {
            ModelMrg.Block block = modelMrg.Find(index);
            string animated = "no";
            if (block.HasEmbeddedMot)
            {
                animated = "yes";
            }
            ImGui.TextDisabled($"textures {block.TexCount}  animated {animated}  slot {block.Slot:N0} B");
        }

        ImGui.BeginDisabled(viewTask != null);
        if (ImGui.Button("View"))
        {
            View(index, IsStaged(index));
        }
        ImGui.EndDisabled();
        if (IsStaged(index))
        {
            HoveredTooltip("Shows the staged import. Tick Compare in the preview to stand the model it replaces beside it.");
        }
        else
        {
            HoveredTooltip("Shows this model as it is on the ISO.");
        }
        if (viewTask != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Loading " + viewName + "...");
        }
        ImGui.SameLine();
        if (source == SourceMonster)
        {
            if (ImGui.Button("Remove model"))
            {
                removeModelSilence = false;
                ImGui.OpenPopup("Remove model");
            }
            HoveredTooltip("Stages this card as a model the game loads and animates but that draws nothing (one zero-area triangle, a transparent texture). Its nodes, motions and radius stay as they are on the ISO.");
            DrawRemoveModelPopup(index);
        }
        if (ImGui.Button("Load Model"))
        {
            var dialogResult = Dialog.FileOpen(FileDialogFilter.Of("glb,gltf,fbx,dae"),
                Path.GetDirectoryName(Program.config.lastModelImportPath));
            if (dialogResult.IsOk)
            {
                BeginImport(dialogResult.Path);
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
                    modelMrg.Unstage(index);
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
        HoveredTooltip("Save just this model (as it is on the ISO) as a .glb, the same file the Extractor tab writes for it.");
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
            foreach (string warning in reportWarnings)
            {
                ImGui.TextColored(new Vector4(1, 0.8f, 0.3f, 1), warning);
            }
        }
    }

    /* Shows the block (as it is on the ISO, or as staged) in the preview. Only a staged block
       offers Compare, against the ISO's model it replaces; it opens with Compare off. A card
       (as on the ISO, or staged as an edit) opens the Convert window for it, as an import does,
       without Compare. */
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
        viewCanCompare = staged && !card; // no Compare for a card on the ISO (edited or not)
        viewCompareOn = false;
        viewIsImport = card;
        viewOpensCard = card;
        viewKeepCamera = false;
        viewRadius = card ? -1f : BlockRadius(bytes); // a card shows Radius as an import does
        viewMove = Vector3.Zero;
        viewRotate = Vector3.Zero;
        viewScale = 1f;
        if (card && staged)
        {
            // the staged block itself; the preview was edited with the values it was staged with
            ImportOptions cardEdit = cardEdits[index];
            viewLabel = cardNames[index];
            viewMove = cardEdit.MoveOn ? cardEdit.Move : Vector3.Zero;
            viewRotate = cardEdit.RotateOn ? cardEdit.Rotate : Vector3.Zero;
            viewScale = cardEdit.Scale;
        }
        viewSoundCues = null;
        if (source == SourceMonster)
        {
            viewSoundCues = Rh0Assembler.ReadSoundCues(bytes);
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
        viewSoundCues = null; // the preview plays the Convert window's cues
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
            importOptions = CurrentImportOptions();
        }
        convertIsCard = true;
        convertIndex = index;
        convertPath = cardNames[index];
        ImportOptions cardEdit;
        if (cardEdits.TryGetValue(index, out cardEdit))
        {
            cardGlb = null; // the shown glb is the staged block's
        }
        else
        {
            cardGlb = glb;
            cardEdit = new ImportOptions();
            cardEdit.Scale = 1f;
            cardEdit.PivotRest = model.RestPivot - model.RestNodeZero;
            cardEdit.PivotAt = cardEdit.PivotRest;
            cardEdit.Radius = BlockRadius(BlockBytes(index));
        }
        LoadCardSoundCues(index); // a staged edit's own cues replace them in SetImportOptions
        SetImportOptions(cardEdit);
        cardPivotRest = cardEdit.PivotRest;
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
        statusSucceeded = true;
        status = $"{cardNames[index]}: check the options, then Import.";
    }

    // Load Model (or a new ISO) after a card's Convert window: the import options come back.
    void PutImportOptionsBack()
    {
        if (convertIsCard)
        {
            SetImportOptions(importOptions);
            convertIsCard = false;
        }
    }

    ImportOptions CurrentImportOptions()
    {
        ImportOptions windowOptions = new ImportOptions();
        windowOptions.Scale = optScale;
        windowOptions.RotateOn = optRotateOn;
        windowOptions.Rotate = optRotate;
        windowOptions.MoveOn = optMoveOn;
        windowOptions.Move = optMove;
        windowOptions.PivotOn = optPivotOn;
        windowOptions.PivotAt = optPivotAt;
        windowOptions.Radius = optRadius;
        windowOptions.PivotRest = cardPivotRest;
        windowOptions.SoundCues = CloneSoundCues(optSoundCues);
        return windowOptions;
    }

    void SetImportOptions(ImportOptions windowOptions)
    {
        optScale = windowOptions.Scale;
        optRotateOn = windowOptions.RotateOn;
        optRotate = windowOptions.Rotate;
        optMoveOn = windowOptions.MoveOn;
        optMove = windowOptions.Move;
        optPivotOn = windowOptions.PivotOn;
        optPivotAt = windowOptions.PivotAt;
        optRadius = windowOptions.Radius;
        if (windowOptions.SoundCues != null)
        {
            optSoundCues = CloneSoundCues(windowOptions.SoundCues);
        }
    }

    /* The Convert window's View import: the source converted with the options as they stand,
       beside the model on the ISO it would replace. Nothing is staged. A redo after an option
       changed keeps the camera, so Scale and Move show against the grid. */
    void ViewImport(bool redo)
    {
        if (convertIsCard)
        {
            ViewCard(redo);
            return;
        }
        string path = convertPath;
        ConvertOptions convertOptions = Options();
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
        viewSoundCues = null; // the preview plays the Convert window's cues
        viewMove = new Vector3((float)convertOptions.TranslateX, (float)convertOptions.TranslateY, (float)convertOptions.TranslateZ);
        viewRotate = new Vector3((float)convertOptions.RotateX, (float)convertOptions.RotateY, (float)convertOptions.RotateZ);
        viewScale = (float)convertOptions.Scale;
        importDirty = false;
        // the converter's warnings are shown by Import; the preview ignores them
        byte[] donor = BlockBytes(convertIndex);
        viewTask = Task.Run(() => WithParsed(ModelConverter.ConvertFile(path, convertOptions, message => { }).Glb, donor));
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
        ModelMrg.Block block = modelMrg.Find(index);
        foreach (ModelMrg.StagedBlock stagedBlock in modelMrg.Staged)
        {
            if (stagedBlock.Block == block)
            {
                return stagedBlock.SourcePath;
            }
        }
        return block.Name;
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
            bytes = new byte[GameTables.MonsterSlotSize];
            Array.Copy(monsterMrg, (long)card * GameTables.MonsterSlotSize, bytes, 0, GameTables.MonsterSlotSize);
        }
        else
        {
            // a MODEL.MRG entry belongs to the archive it was viewed from
            if (source != SourceModel || modelMrg == null)
            {
                return;
            }
            bytes = modelMrg.BlockBytes(card);
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
            statusSucceeded = false;
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
            compareName = modelMrg.Find(compareCard).Name;
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
        int tree = ReadI32(block, 0x10);
        if (tree <= 0 || tree + 0x40 > block.Length)
        {
            return 1f;
        }
        return ReadF32(block, tree + 0x3C);
    }

    // Stands the compare model to the left of the preview, its idle extent clear of the
    // preview's by a tenth of the taller one's height.
    void PlaceCompare()
    {
        if (!CompareShown())
        {
            return;
        }
        float previewModelScale = viewScene.ModelScale(PreviewRadius());
        float compareModelScale = viewScene.ModelScale(compareRadius);
        float gap = 0.1f * MathF.Max(previewModelScale * previewHeight, compareModelScale * compareHeight);
        // spaced from the body before Move X, so moving the import does not drag this one along
        float x = viewScene.StandShift(previewModel, previewModelScale).X + previewModelScale * (previewLeft - previewMove.X)
                  - viewScene.StandShift(compareModel, compareModelScale).X - compareModelScale * compareRight - gap;
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
        float modelScale = viewScene.ModelScale(PreviewRadius());
        Vector3 shift = viewScene.StandShift(previewModel, modelScale);
        float left = shift.X + modelScale * previewLeft;
        float right = shift.X + modelScale * previewRight;
        float bottom = viewScene.StandY(ShownLowest(), previewIndex) + shift.Y + modelScale * previewLowest;
        float top = bottom + modelScale * previewHeight;
        float back = shift.Z + modelScale * previewBack;
        float front = shift.Z + modelScale * previewFront;
        if (CompareShown())
        {
            float compareModelScale = viewScene.ModelScale(compareRadius);
            Vector3 compareShift = viewScene.StandShift(compareModel, compareModelScale);
            left = compareOffsetX + compareShift.X + compareModelScale * compareLeft;
            float compareBottom = viewScene.StandY(compareLowest, compareCard) + compareShift.Y + compareModelScale * compareLowest;
            bottom = MathF.Min(bottom, compareBottom);
            top = MathF.Max(top, compareBottom + compareModelScale * compareHeight);
            back = MathF.Min(back, compareShift.Z + compareModelScale * compareBack);
            front = MathF.Max(front, compareShift.Z + compareModelScale * compareFront);
        }
        bottom = MathF.Min(bottom, 0f); // keep the grid in view
        float aspect = previewTextureReady ? (float)previewTextureWidth / Math.Max(previewTextureHeight, 1) : 1f;
        orbitCamera.Frame(new Vector3(left, bottom, back), new Vector3(right, top, front), 0.15f, aspect);
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
        var dialogResult = Dialog.FileSave("glb", DefaultGlbPath(index));
        if (!dialogResult.IsOk)
        {
            return;
        }
        string outPath = dialogResult.Path;
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
            name = modelMrg.Find(index).Name;
        }
        foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalidCharacter, '_');
        }
        string directory = string.Empty;
        if (!string.IsNullOrEmpty(Program.config.lastModelImportPath))
        {
            directory = Path.GetDirectoryName(Program.config.lastModelImportPath) ?? string.Empty;
        }
        return Path.Combine(directory, name + ".glb");
    }

    void UpdateExtract()
    {
        if (extractTask == null || !extractTask.IsCompleted)
        {
            return;
        }
        if (extractTask.IsFaulted)
        {
            statusSucceeded = false;
            status = "Extract failed: " + extractTask.Exception.InnerException.Message;
        }
        else
        {
            statusSucceeded = true;
            status = "Wrote " + extractPath + ".";
        }
        extractTask = null;
    }

    /* A View's glb with its SharpGLTF read and scene (the slow part of a load), on the worker:
       only the GPU half is left for the draw thread, so a View import redo does not stall it.
       With a donor block, also the radius an import over it writes (Rebuilder.MeasureRadius,
       the importer's own measure), so the preview and Radius show what the game gets; else -1.
       The measure runs beside the parse. */
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
            statusSucceeded = false;
            status = "View failed: " + viewTask.Exception.InnerException.Message;
        }
        else
        {
            previewIndex = viewIndex;
            previewIsMonster = viewIsMonster;
            previewCanCompare = viewCanCompare;
            previewIsImport = viewIsImport;
            previewSoundCues = viewSoundCues;
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
            return modelMrg.BlockBytes(index);
        }
        byte[] block = new byte[GameTables.MonsterSlotSize];
        Array.Copy(monsterMrg, (long)index * GameTables.MonsterSlotSize, block, 0, GameTables.MonsterSlotSize);
        return block;
    }

    byte[] StagedBytes(int index)
    {
        if (source == SourceModel)
        {
            return modelMrg.StagedBytes(index);
        }
        return monsterStaged[index];
    }
}