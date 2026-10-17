using System.Numerics;
using ImGuiNET;
using NativeFileDialogSharp;

namespace DotrAssetModder;

public partial class ModelImporterWindow
{
    // The card as on the ISO with its geometry removed (GlbImporter.RemoveModel), staged in place of
    // anything staged for it. The ISO's radius is kept so the board and map treat it as before.
    // silenceSounds: its sound cue table is emptied, else its sounds stay as they are.
    void RemoveModel(int index, bool silenceSounds)
    {
        reportIndex = index;
        reportWarnings = new List<string>();
        try
        {
            byte[] block = BlockBytes(index);
            ImportedModel model = GlbImporter.LoadFromBytes(ModelExtractor.ExtractGlb(block));
            GlbImporter.RemoveModel(model);
            List<SoundCue>? soundCues = null;
            if (silenceSounds)
            {
                soundCues = new List<SoundCue>();
            }
            StageMonster(index, model, cardNames[index] + " (removed)", BlockRadius(block), soundCues);
        }
        catch (Exception exception)
        {
            reportOk = false;
            report = "Remove failed: " + exception.Message;
        }
        statusSucceeded = reportOk;
        status = report;
    }

    // The same conversion (or card edit), written out instead of staged, to check it in another tool.
    void SaveConverted()
    {
        ConvertResult convertResult = new ConvertResult();
        convertWarnings = new List<string>();
        string defaultPath = Path.ChangeExtension(convertPath, ".glb");
        try
        {
            if (convertIsCard)
            {
                convertResult.Glb = CardEditGlb();
                defaultPath = DefaultGlbPath(convertIndex);
            }
            else
            {
                convertResult = ModelConverter.ConvertFile(convertPath, Options(), Warn);
            }
        }
        catch (Exception exception)
        {
            Failed("Convert failed: " + exception.Message);
            return;
        }
        var dialogResult = Dialog.FileSave("glb", defaultPath);
        if (!dialogResult.IsOk)
        {
            return;
        }
        string outPath = dialogResult.Path;
        if (!outPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
        {
            outPath += ".glb";
        }
        File.WriteAllBytes(outPath, convertResult.Glb);
        reportConvert = convertResult.Report;
        reportWarnings = convertWarnings;
        reportIndex = convertIndex;
        reportOk = true;
        statusSucceeded = true;
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
                StageMonster(index, GlbImporter.LoadFromBytes(glb), label, optRadius, SoundCuesToWrite());
            }
            else
            {
                ModelMrg.StageResult stageResult = modelMrg.Stage(index, glb, label, allowReduction);
                reportOk = stageResult.Staged;
                if (stageResult.Staged)
                {
                    report = stageResult.Summary + " - staged.";
                }
                else
                {
                    report = stageResult.Summary + " - not staged.";
                }
                reportWarnings = stageResult.Warnings;
            }
            statusSucceeded = reportOk;
            status = report;
        }
        catch (Exception exception)
        {
            reportOk = false;
            statusSucceeded = false;
            report = "Import failed: " + exception.Message;
            status = report;
        }
    }

    // soundCues: null keeps the card's sound cues as they are, a list replaces them.
    void StageMonster(int index, ImportedModel model, string label, float boundsRadius, List<SoundCue>? soundCues)
    {
        byte[] donor = LoadTemplate(index);
        List<Rh0Assembler.TextureInput> textures = GlbImporter.DecodeGlbTextures(model);

        // What the rebuild asked to reduce, for the fit report when it does not fit.
        bool asked = false;
        Rh0Assembler.OverflowCategory askedCategory = default;
        int askedBytes = 0;
        int askedTotal = 0;
        int askedBudget = 0;

        Rebuilder.ImportResult import;
        try
        {
            import = Rebuilder.Import(model, donor, GameTables.MonsterSlotSize, false,
                (category, categoryBytes, totalBytes, budget) =>
                {
                    asked = true;
                    askedCategory = category;
                    askedBytes = categoryBytes;
                    askedTotal = totalBytes;
                    askedBudget = budget;
                    return allowReduction;
                },
                textures, boundsRadius, soundCues);
        }
        catch (Exception) when (asked)
        {
            double percentOfBudget = 0;
            if (askedBudget > 0)
            {
                percentOfBudget = 100.0 * askedBytes / askedBudget;
            }
            string what = "the texture(s) is";
            if (askedCategory == Rh0Assembler.OverflowCategory.Animation)
            {
                what = "the animations are";
            }
            reportOk = false;
            report = $"card {index:000} is {askedTotal:N0} B, over the {askedBudget:N0} B slot: " +
                     $"{what} {percentOfBudget:0.#}% of it ({askedBytes:N0} B) - not staged.";
            return;
        }

        byte[] bytes = import.Block;
        reportWarnings.AddRange(import.Warnings);
        monsterStaged[index] = bytes;
        monsterStagedFrom[index] = label;
        cardEdits.Remove(index); // StageCardEdit records an edit again
        reportOk = true;
        float radius = BlockRadius(bytes);
        float shrink = ViewScene.BoardShrink(radius);
        report = $"card {index:000}: {bytes.Length:N0} of {GameTables.MonsterSlotSize:N0} B - staged. " +
                 $"Radius {radius:0.#}: board x{shrink:0.##}, map x{ViewScene.MapScale * shrink:0.##}.";
        // battle slots the glb lacked, filled with a copy of another motion (MotWriter)
        if (import.CopiedMotions.Count > 0)
        {
            var copied = new List<string>();
            foreach (var copiedMotion in import.CopiedMotions)
            {
                copied.Add($"slot {copiedMotion.Slot} from slot {copiedMotion.From}");
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
            changes.Add(monsterPath, (long)index * GameTables.MonsterSlotSize, monsterStaged[index], $"model block {index}");
        }
        AddMapHeights(changes);
        return changes;
    }

    // The edited map heights, one Target_Model_Y byte each in the boot ELF.
    void AddMapHeights(ChangeSet changes)
    {
        foreach (int card in mapHeightEdits.OrderBy(k => k))
        {
            byte[] value = { (byte)(sbyte)modelMrg.Tables.MapLeaderY(card) };
            changes.Add(modelMrg.Tables.BootElfIsoPath, modelMrg.Tables.MapLeaderYOffset(card), value, $"map height card {card}");
        }
    }

    void DrawStagedAndSave(FileLoader fileLoader)
    {
        ImGui.Text($"Staged: {StagedCount()}");
        if (source == SourceMonster)
        {
            foreach (int index in monsterStaged.Keys)
            {
                if (cardEdits.ContainsKey(index))
                {
                    ImGui.BulletText($"card {index:000} {cardNames[index]} (edited)  {monsterStaged[index].Length:N0} of {GameTables.MonsterSlotSize:N0} B");
                    continue;
                }
                ImGui.BulletText($"card {index:000} {cardNames[index]}  {monsterStaged[index].Length:N0} of {GameTables.MonsterSlotSize:N0} B  " +
                                 Path.GetFileName(monsterStagedFrom[index]));
            }
        }
        else
        {
            foreach (ModelMrg.StagedBlock stagedBlock in modelMrg.Staged)
            {
                ImGui.BulletText($"{stagedBlock.Block.Name}  {stagedBlock.Bytes.Length:N0} of {stagedBlock.Block.Slot:N0} B  {Path.GetFileName(stagedBlock.SourcePath)}");
            }
        }
        foreach (int card in mapHeightEdits)
        {
            ImGui.BulletText($"card {card:000} {cardNames[card]}  map height {modelMrg.Tables.MapLeaderY(card)}");
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
                    modelMrg.ClearStaged();
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
                plan = MonsterChanges().Validate(fileLoader);
                planReport = null;
            }
            else
            {
                plan = modelMrg.Validate(out planReport);
            }
            planIsSaved = false;
            statusSucceeded = plan.Ok;
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
                ImGui.Text($"Write {mapHeightEdits.Count} map height(s) into {modelMrg.Tables.Addresses.BootElf}?");
            }
            if (ImGui.Button("Yes"))
            {
                ImGui.CloseCurrentPopup();
                Save(fileLoader);
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

    void Save(FileLoader fileLoader)
    {
        try
        {
            if (source == SourceMonster)
            {
                plan = MonsterChanges().Write(fileLoader);
                planReport = null;
                if (plan.Ok)
                {
                    // The staged bytes are now the disc's, so the next donor is the new block, and
                    // an edit's values no longer describe it (the next edit starts from it).
                    foreach (int index in monsterStaged.Keys)
                    {
                        Array.Copy(monsterStaged[index], 0, monsterMrg, (long)index * GameTables.MonsterSlotSize, GameTables.MonsterSlotSize);
                    }
                    cardEdits.Clear();
                    cardGlb = null;
                    if (convertIsCard)
                    {
                        convertOpen = false;
                        convertClosedByUser = false;
                    }
                }
            }
            else
            {
                // The heights first: a MODEL.MRG save re-reads the tables from the disc.
                var heights = new ChangeSet();
                AddMapHeights(heights);
                plan = heights.Write(fileLoader);
                planReport = null;
                if (plan.Ok)
                {
                    plan = modelMrg.Save(out planReport);
                }
            }
            planIsSaved = plan.Ok;
            statusSucceeded = plan.Ok;
            if (plan.Ok)
            {
                mapHeightEdits.Clear(); // on the disc now

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
                        modelMrg.ClearStaged();
                    }
                }
            }
            else
            {
                status = $"Nothing written: {plan.Errors.Count} error(s).";
            }
        }
        catch (Exception exception)
        {
            statusSucceeded = false;
            status = "Save failed: " + exception.Message;
        }
    }
}