using ImGuiNET;
using static DotrAssetModder.GuiContext;

namespace DotrAssetModder;

/* A monster's sound cues (the RH0 bit-21 table SzModel_ReqSE plays): the Convert window's Sounds
   section, Remove model's keep/silence choice, and the cues played in the preview. Sounds come
   from the card's own monster bank, with the sounds staged in the Sound tab. */
public partial class ModelImporterWindow
{
    // The cues the import or card edit writes, and the card's own as on the ISO (Reset).
    List<SoundCue> optSoundCues = new List<SoundCue>();
    List<SoundCue> cardSoundCues = new List<SoundCue>();
    bool cardHasSoundCues; // false: the block has no cue table, so there is nothing to edit
    int soundCueSlot = -1; // the animation whose cues the section shows; -1 = the first offered
    bool removeModelSilence; // Remove model: empty its cue table too
    // The preview plays cues only while the Sounds tab is shown (this or the last frame) and not muted.
    int soundsTabShownFrame = -10;
    bool muteSoundCues;

    // The player and staged sounds of the Sound tab.
    readonly MusicExtractorWindow soundPlayer;
    // The card's bank for the sound combos and Play, read again when the card or the staged sounds change.
    SoundBank? cueBank;
    bool[] cueBankStaged = new bool[0];
    string[] cueSoundLabels = new string[0];
    int cueBankCard = -1;
    int cueBankChangeCount = -1;
    string? cueBankError;

    const int AttackMotionSlot = 2;
    static readonly System.Numerics.Vector4 PastAnimationEndColour = new System.Numerics.Vector4(0.6f, 0.1f, 0.1f, 1f);
    // Each slot's name in the Animation combo (SlotLabels for the battle slots, the extras by number).
    static readonly string[] SoundCueSlotNames = { "slot_0 Idle", "slot_1", "slot_2 Attack", "slot_3", "slot_4 Death", "slot_5 Win", "slot_6 Hurt", "slot_7" };

    // The card's cues as on the ISO, also as the cues to write (Keep is the default).
    void LoadCardSoundCues(int index)
    {
        cardSoundCues = new List<SoundCue>();
        cardHasSoundCues = false;
        if (source == SourceMonster)
        {
            List<SoundCue>? blockCues = Rh0Assembler.ReadSoundCues(BlockBytes(index));
            if (blockCues != null)
            {
                cardSoundCues = blockCues;
                cardHasSoundCues = true;
            }
        }
        optSoundCues = CloneSoundCues(cardSoundCues);
        soundCueSlot = -1;
    }

    // What staging writes: the edited cues, or null (keep the table as it is) when the card has none.
    List<SoundCue>? SoundCuesToWrite()
    {
        if (!cardHasSoundCues)
        {
            return null;
        }
        return CloneSoundCues(optSoundCues);
    }

    static List<SoundCue> CloneSoundCues(List<SoundCue> cues)
    {
        var copy = new List<SoundCue>(cues.Count);
        foreach (SoundCue cue in cues)
        {
            copy.Add(cue.Clone());
        }
        return copy;
    }

    // Reads `card`'s bank (with staged sounds) when it is not the one held.
    void RefreshCueBank(int card)
    {
        if (cueBankCard == card && cueBankChangeCount == soundPlayer.StagedSoundsChangeCount)
        {
            return;
        }
        cueBankCard = card;
        cueBankChangeCount = soundPlayer.StagedSoundsChangeCount;
        cueBank = null;
        cueBankStaged = new bool[0];
        cueSoundLabels = new string[0];
        cueBankError = null;
        try
        {
            cueBank = soundPlayer.ReadMonsterBankWithStaged(card, out cueBankStaged);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
        {
            cueBankError = "Sound bank unavailable: " + exception.Message;
            return;
        }
        cueSoundLabels = new string[cueBank.Entries.Length];
        for (int entryIndex = 0; entryIndex < cueSoundLabels.Length; entryIndex++)
        {
            cueSoundLabels[entryIndex] = SoundLabel(entryIndex);
        }
    }

    string SoundLabel(int soundIndex)
    {
        if (cueBank == null || soundIndex >= cueBank.Entries.Length)
        {
            return $"{soundIndex:00} (not in the bank)";
        }
        string label = $"{soundIndex:00}   {cueBank.Entries[soundIndex].Seconds:0.00} s";
        if (cueBankStaged[soundIndex])
        {
            label += "  (staged)";
        }
        return label;
    }

    // The slots the Animation combo offers: the battle slots, the extras the source has, and any
    // slot that holds cues.
    List<int> SoundCueSlots()
    {
        var slots = new List<int>();
        for (int slot = 0; slot <= Rh0Assembler.MaxSoundCueSlot; slot++)
        {
            bool offered = SlotIndex(slot) >= 0;
            foreach (string animationName in convertName)
            {
                if (RebuildPipeline.ParseSlotId(animationName) == slot)
                {
                    offered = true;
                }
            }
            foreach (SoundCue cue in optSoundCues)
            {
                if (cue.MotionSlot == slot)
                {
                    offered = true;
                }
            }
            if (offered)
            {
                slots.Add(slot);
            }
        }
        return slots;
    }

    /* The last frame of the animation `slot` plays (its frame count - 1), -1 when unknown: the slot
       table's pick, else an animation named for the slot, else the one animation (it plays in
       every slot); a model with no animation stands on frame 0. */
    int SoundCueLastFrame(int slot)
    {
        if (convertName.Count == 0)
        {
            return 0;
        }
        int animationIndex = -1;
        int slotRow = SlotIndex(slot);
        if (slotRow >= 0 && slotRow < convertSlotAnim.Length)
        {
            animationIndex = convertSlotAnim[slotRow];
        }
        if (animationIndex < 0)
        {
            for (int i = 0; i < convertName.Count; i++)
            {
                if (RebuildPipeline.ParseSlotId(convertName[i]) == slot)
                {
                    animationIndex = i;
                }
            }
        }
        if (animationIndex < 0 && convertName.Count == 1)
        {
            animationIndex = 0;
        }
        if (animationIndex < 0 || animationIndex >= convertFrames.Count)
        {
            return -1;
        }
        return Math.Max(0, convertFrames[animationIndex] - 1);
    }

    int SoundCueCount(int slot)
    {
        int count = 0;
        foreach (SoundCue cue in optSoundCues)
        {
            if (cue.MotionSlot == slot)
            {
                count++;
            }
        }
        return count;
    }

    void DrawSoundCues()
    {
        if (!cardHasSoundCues)
        {
            ImGui.TextDisabled("This card's block has no sound cue table.");
            return;
        }
        RefreshCueBank(convertIndex);
        if (cueBankError != null)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f), cueBankError);
        }
        else if (cueBank != null)
        {
            int stagedCount = 0;
            foreach (bool staged in cueBankStaged)
            {
                if (staged)
                {
                    stagedCount++;
                }
            }
            ImGui.AlignTextToFramePadding();
            ImGui.Text($"Sounds from {cardNames[convertIndex].Trim()}'s bank ({cueBank.Entries.Length} sounds, {stagedCount} staged)");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Reset to card's##sounds"))
        {
            optSoundCues = CloneSoundCues(cardSoundCues);
        }
        HoveredTooltip("Back to this card's own sound cues as they are on the ISO.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Remove all##sounds"))
        {
            optSoundCues.Clear();
        }
        HoveredTooltip("No sounds on any animation: the model is silent in battle.");
        if (ImGui.Checkbox("Mute sounds", ref muteSoundCues) && muteSoundCues)
        {
            soundPlayer.StopMixedSounds();
        }
        HoveredTooltip("While this tab is shown, the preview plays each cue as its animation reaches it, unless muted.");

        List<int> slots = SoundCueSlots();
        if (!slots.Contains(soundCueSlot))
        {
            soundCueSlot = slots[0];
        }
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Animation");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(FieldWidth(0.2f, 10f));
        if (ImGui.BeginCombo("##soundCueSlot", $"{SoundCueSlotNames[soundCueSlot]} ({SoundCueCount(soundCueSlot)})"))
        {
            foreach (int slot in slots)
            {
                if (ImGui.Selectable($"{SoundCueSlotNames[slot]} ({SoundCueCount(slot)})", slot == soundCueSlot))
                {
                    soundCueSlot = slot;
                }
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{SoundCueCount(soundCueSlot)} cues here, {optSoundCues.Count} in all (max {Rh0Assembler.MaxSoundCues})");

        // A frame may go past its animation's last frame; the box goes red. Such a cue never plays,
        // except on the attack slot (the effect's clock).
        int frameLimit = Rh0Assembler.MaxSoundCueFrame;
        string frameTooltip = "The frame the sound starts on, 30 frames per second.";
        int lastFrame = SoundCueLastFrame(soundCueSlot);
        if (lastFrame >= 0)
        {
            frameTooltip += $"\nThis animation's last frame is {lastFrame}; red = past it.";
        }
        if (soundCueSlot == AttackMotionSlot)
        {
            frameTooltip += "\nAttack cues past it still play (they count on the attack effect's frames).";
        }
        else
        {
            frameTooltip += "\nA cue past it never plays in the game.";
        }

        int deleteIndex = -1;
        // Resizable: dragging a column's edge sets its width; the Sound combo fills its column.
        if (ImGui.BeginTable("sound_cues", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuterH | ImGuiTableFlags.BordersInnerV
            | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable | ImGuiTableFlags.NoHostExtendX))
        {
            ImGui.TableSetupColumn("Frame");
            ImGui.TableSetupColumn("Sound", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 12f);
            ImGui.TableSetupColumn("No restart");
            ImGui.TableSetupColumn("##delete");
            ImGui.TableHeadersRow();
            for (int cueIndex = 0; cueIndex < optSoundCues.Count; cueIndex++)
            {
                SoundCue cue = optSoundCues[cueIndex];
                if (cue.MotionSlot != soundCueSlot)
                {
                    continue;
                }
                ImGui.PushID(cueIndex);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.SetNextItemWidth(FieldWidth(0.08f, 5f));
                bool pastAnimationEnd = lastFrame >= 0 && cue.Frame > lastFrame;
                if (pastAnimationEnd)
                {
                    ImGui.PushStyleColor(ImGuiCol.FrameBg, PastAnimationEndColour);
                }
                if (ImGui.InputInt("##frame", ref cue.Frame, 1, 10))
                {
                    cue.Frame = Math.Clamp(cue.Frame, 0, frameLimit);
                }
                if (pastAnimationEnd)
                {
                    ImGui.PopStyleColor();
                }
                HoveredTooltip(frameTooltip);
                ImGui.TableSetColumnIndex(1);
                float playButtonWidth = ImGui.CalcTextSize("Play").X + ImGui.GetStyle().FramePadding.X * 2f;
                ImGui.SetNextItemWidth(Math.Max(ImGui.GetContentRegionAvail().X - playButtonWidth - ImGui.GetStyle().ItemSpacing.X, ImGui.GetFontSize() * 3f));
                if (ImGui.BeginCombo("##sound", SoundLabel(cue.SoundIndex)))
                {
                    for (int entryIndex = 0; entryIndex < cueSoundLabels.Length; entryIndex++)
                    {
                        if (ImGui.Selectable(cueSoundLabels[entryIndex], entryIndex == cue.SoundIndex))
                        {
                            cue.SoundIndex = entryIndex;
                        }
                    }
                    ImGui.EndCombo();
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Play"))
                {
                    soundPlayer.PlayBankSound(cueBank, cue.SoundIndex);
                }
                ImGui.TableSetColumnIndex(2);
                ImGui.Checkbox("##noRestart", ref cue.NoRestart);
                HoveredTooltip("On: if this sound is still playing when the cue comes again, it is not started over.");
                ImGui.TableSetColumnIndex(3);
                if (ImGui.SmallButton("Delete"))
                {
                    deleteIndex = cueIndex;
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (deleteIndex >= 0)
        {
            optSoundCues.RemoveAt(deleteIndex);
        }

        ImGui.BeginDisabled(optSoundCues.Count >= Rh0Assembler.MaxSoundCues);
        if (ImGui.Button("Add cue"))
        {
            var cue = new SoundCue();
            cue.MotionSlot = soundCueSlot;
            optSoundCues.Add(cue);
        }
        ImGui.EndDisabled();
        if (soundCueSlot == AttackMotionSlot)
        {
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 40f);
            ImGui.TextDisabled("Attack cues count on the attack effect's frames (30 per second), which can run past the end of the animation; the preview plays them on the animation's frames.");
            ImGui.PopTextWrapPos();
        }
    }

    // Remove model's popup: keep the card's sounds or silence them, then stage.
    void DrawRemoveModelPopup(int index)
    {
        if (!ImGui.BeginPopupModal("Remove model", ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }
        ImGui.Text($"Stage {SlotName(index)} with its model removed?");
        if (ImGui.RadioButton("Keep card's sounds", !removeModelSilence))
        {
            removeModelSilence = false;
        }
        if (ImGui.RadioButton("Silence sounds", removeModelSilence))
        {
            removeModelSilence = true;
        }
        if (ImGui.Button("Remove"))
        {
            ImGui.CloseCurrentPopup();
            RemoveModel(index, removeModelSilence);
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    // The cues the preview plays: the Convert window's for its View import, else the viewed block's.
    List<SoundCue>? PreviewSoundCuesNow()
    {
        if (!previewIsMonster)
        {
            return null;
        }
        if (previewIsImport && convertOpen && previewIndex == convertIndex && cardHasSoundCues)
        {
            return optSoundCues;
        }
        return previewSoundCues;
    }

    // The preview plays cues: the Sounds tab is shown (this or the last frame) and not muted.
    bool PreviewSoundCuesHeard()
    {
        return !muteSoundCues && ImGui.GetFrameCount() - soundsTabShownFrame <= 1;
    }

    /* Plays each cue of the shown animation's slot whose frame the animation passed this frame,
       from `previousTime` to `nowTime` (seconds; `wrapped`: it reached the end and looped back to
       the start between, which stops the sounds still playing as a new start does). A cue past the
       animation's end never plays here. */
    void PlayPreviewSoundCues(float previousTime, float nowTime, bool wrapped)
    {
        if (wrapped)
        {
            soundPlayer.StopMixedSounds();
        }
        if (!PreviewSoundCuesHeard())
        {
            return;
        }
        List<SoundCue>? cues = PreviewSoundCuesNow();
        if (cues == null || cues.Count == 0)
        {
            return;
        }
        int slot = RebuildPipeline.ParseSlotId(previewModel.AnimationNames[currentAnim]);
        if (slot < 0)
        {
            return;
        }
        float previousFrame = previousTime * GlbImporter.AnimFps;
        float nowFrame = nowTime * GlbImporter.AnimFps;
        foreach (SoundCue cue in cues)
        {
            if (cue.MotionSlot != slot)
            {
                continue;
            }
            bool passed;
            if (wrapped)
            {
                // the cues before the end would be stopped by the restart at once
                passed = cue.Frame <= nowFrame;
            }
            else
            {
                passed = cue.Frame > previousFrame && cue.Frame <= nowFrame;
            }
            if (passed)
            {
                RefreshCueBank(previewIndex);
                soundPlayer.PlayBankSoundMixed(cueBank, cue.SoundIndex);
            }
        }
    }
}
