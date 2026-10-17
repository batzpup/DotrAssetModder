using System.Drawing;
using System.Numerics;
using DiscUtils.Iso9660;
using ImGuiNET;
using NAudio.MediaFoundation;
using NativeFileDialogSharp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using static DotrAssetModder.Bin;
namespace DotrAssetModder;


public class MusicExtractorWindow : IDisposable
{
    const float PlaybackVolume = 0.1f;
    // Stop waits this long for NAudio's playback thread to leave the source before disposing it.
    const int PlaybackStopTimeoutMilliseconds = 2000;
    // MP3 holds no 11 kHz-ish rate the encoder accepts; sound effects are resampled to the SPU2's
    // own output rate. (BGM's 24 kHz it takes as it is.)
    const int Mp3SoundEffectSampleRate = 48000;
    static readonly Vector4 ErrorTextColour = new Vector4(1f, 0.4f, 0.4f, 1f);
    // Files "Replace sound..." opens and a drop on the window accepts.
    const string ReplacementFileExtensions = "wav,mp3,m4a,aac,wma,flac";
    // The Replace popup's gain field range, in dB.
    const float LowestReplacementGainDecibels = -24f;
    const float HighestReplacementGainDecibels = 24f;
    // A peak at or above this (dBFS) has hit full scale: the sound clips.
    const double FullScaleDecibels = -0.01;
    const string PeakAndRmsTooltip = "Peak: the loudest single moment. At 0 dBFS the sound clips (distorts).\n"
                                     + "RMS: the average loudness over the whole sound, closest to how loud it sounds.\n"
                                     + "Match RMS to make the new sound about as loud as the original.\n"
                                     + "dBFS: 0 is the loudest a sound can be; lower is quieter.\n"
                                     + "The game also plays each sound at its volume byte.";

    readonly WaveOutEvent waveOut;
    /* The model importer's preview cues: sounds that overlap, as the game's voices do, mixed on a
       second device so they never stop each other or the player above. Started on the first cue. */
    const int CueMixerSampleRate = 48000;
    readonly MixingSampleProvider cueMixer;
    WaveOutEvent? cueOutput;
    // When the last cue sound playing ends (Environment.TickCount64 milliseconds).
    long mixedSoundsEndTick;
    // The source waveOut plays; owned here, disposed by StopMusic.
    WaveStream? playbackSource;
    // Set by NAudio's playback thread when it has left the source (PlaybackStopped).
    readonly ManualResetEventSlim playbackThreadStopped = new ManualResetEventSlim(true);
    // Set on NAudio's thread when a sound or track ended by itself; read by the next frame.
    volatile bool playbackEndedByItself;
    volatile string? playbackThreadError;
    bool isMusicPlaying = false;
    string? playbackError;
    FileLoader fileloader = FileLoader.Instance;

    string playStopButton = "Play";

    CurrentMusicMode currentMode = CurrentMusicMode.BGM;

    // The BGM tab: PCMLIST.BIN read from the ISO, the selected track (a main track or a duel
    // variant), and each main track's id, label and variants (rebuilt when the staging changes).
    BgmTrackTable? bgmTable;
    bool bgmTableLoadAttempted;
    // The ISO the table was read from: another ISO (or one opened after none) reads it again.
    string bgmTableIsoPath = string.Empty;
    string? bgmTableError;
    int selectedBgmTrackId = 1;
    int[] bgmMainTrackIds = [];
    string[] bgmTrackLabels = [];
    int[][] bgmVariantIds = [];
    string[][] bgmVariantLabels = [];
    // Replaced songs and edited loop starts, kept until Save to ISO writes them or the ISO closes.
    readonly Dictionary<int, StagedBgmTrack> stagedBgmTracks = new Dictionary<int, StagedBgmTrack>();
    PendingBgmReplacement? pendingBgmReplacement;
    // The tab's Loop start field while it is being typed in (applied when the edit ends).
    bool bgmLoopStartEditing;
    float bgmLoopStartEditSeconds;
    // The timeline: the track playing, the seek it last made (source bytes, and the device's played
    // bytes at that moment), its length label, and the slider's value while it is dragged.
    BgmTrackStream? playingBgmStream;
    long bgmSeekTargetBytes;
    long bgmPlayedBytesAtSeek;
    string bgmTrackLengthLabel = string.Empty;
    bool bgmTimelineDragging;
    float bgmTimelineDragSeconds;
    string? bgmReplaceError;
    string bgmSaveStatus = string.Empty;
    bool bgmSaveSucceeded;
    List<string> bgmSaveErrors = new List<string>();

    // The bank shown by the Card, Monster or System tab: only its header and ADPCM bytes are kept.
    SoundBank? currentBank;
    bool bankLoadAttempted;
    CurrentMusicMode loadedBankMode;
    int loadedBankCard = -1;
    string? bankError;
    string soundListTitle = "Sounds";
    string[] soundEntryLabels = [];
    int selectedSoundIndex;
    // Decoded PCM of the playing sound effect; grows only when a longer sound needs it.
    byte[] soundPcmBytes = [];

    int selectedMonsterCard = 0;
    string[] monsterCardLabels = [];
    string? monsterNamesError;

    SoundExportFormat exportFormat = SoundExportFormat.Wav;
    SoundExtractionJob? extractionJob;
    int shownBanksDone = -1;
    string extractionProgressLabel = string.Empty;
    string lastRunSummary = string.Empty;
    List<string> lastRunFailures = new List<string>();

    // Replaced sounds per bank, kept until Save to ISO writes them or the ISO closes.
    readonly List<StagedSoundBank> stagedBanks = new List<StagedSoundBank>();
    // The shown bank's staged sounds, or null when it has none.
    StagedSoundBank? currentStagedBank;
    // The file picked by "Replace sound...", while its popup is open.
    PendingSoundReplacement? pendingReplacement;
    int stagedSoundsChangeCount;
    // A file dropped on the window this frame (ReplaceSelectedSoundWith), null when none.
    string? droppedReplacementPath;
    string? replaceError;
    string saveStatus = string.Empty;
    bool saveSucceeded;
    List<string> saveErrors = new List<string>();

    public static string[] BgmTracks = new[] {
        "01-OpeningCutscene",
        "02-MainMenu",
        "03-CustomDuel",
        "04-MapMusic",
        "05-DeckCreation",
        "06-N/A",
        "07-TutorialDuel",
        "08-BattleMusicRed",
        "09-BattleMusicWhite",
        "10-Seto",
        "11-Yugi",
        "12-MFLWhite",
        "13-NormalBattle",
        "14-MeadowBattle",
        "15-MountainBattle",
        "16-SeaBattle",
        "17-ForestBattle",
        "18-WastelandBattle",
        "19-CrushBattle",
        "20-DarkBattle",
        "21-ToonBattle",
        "22-LabBattle",
        "23-Victory",
        "24-NewGame",
        "25-SimonSummoning",
        "26-SetosArrival",
        "27-TalkWithYugi",
        "28-YugiArrivesAtDover",
        "29-PegasusBetrayal",
        "30-DefeatRichardCutscene",
        "31-SetoSummoningRitual1",
        "32-SetoSummongRitual2",
        "33-Summon_MFL_SK",
        "34-FindSetosBook",
        "35-MakoTheme",
        "36-Summon_MFL_C",
        "37-SetoHandsYouBadge",
        "38-EndGameYorkist",
        "39-N/A",
        "40-CustomDuel",
        "41-Trade",
        "42-Defeat",
        "43-Exodia",
        "44-MFLRed"
    };


    public MusicExtractorWindow()
    {
        cueMixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(CueMixerSampleRate, 1));
        // Silence between sounds, so the device keeps running and each new sound joins at once.
        cueMixer.ReadFully = true;
        waveOut = new WaveOutEvent();
        waveOut.Volume = PlaybackVolume;
        // Subscribed once here, removed in Dispose; never per play.
        waveOut.PlaybackStopped += OnPlaybackStopped;
        fileloader.OnIsoClosing += OnIsoClosing;
    }

    public void Render()
    {
        if (playbackEndedByItself)
        {
            playbackEndedByItself = false;
            playbackError = playbackThreadError;
            StopMusic();
        }
        if (extractionJob != null && extractionJob.Task.IsCompleted)
        {
            FinishExtraction();
        }

        CurrentMusicMode oldCurrentMusicMode = currentMode;
        if (ImGui.BeginTabBar("Music Mode"))
        {

            if (ImGui.BeginTabItem("Background Music"))
            {
                currentMode = CurrentMusicMode.BGM;
                RenderBGM();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Card Sounds"))
            {
                currentMode = CurrentMusicMode.Card;
                RenderSoundBankTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Monster Sounds"))
            {
                currentMode = CurrentMusicMode.Monster;
                RenderSoundBankTab();
                ImGui.EndTabItem();

            }
            if (ImGui.BeginTabItem("System Sounds"))
            {
                currentMode = CurrentMusicMode.System;
                RenderSoundBankTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
        if (currentMode != oldCurrentMusicMode)
        {
            StopMusic();
        }
 
        droppedReplacementPath = null;

    }


    void RenderSoundBankTab()
    {
        float indent = ImGui.GetContentRegionAvail().X / 160;
        ImGui.Indent(indent);
        if (currentMode == CurrentMusicMode.Monster)
        {
            RenderMonsterPicker();
        }
        SelectBank(currentMode, selectedMonsterCard);

        Vector2 windowSize = ImGui.GetContentRegionAvail();
        float childHeight = windowSize.Y - ImGui.GetStyle().FramePadding.Y * 2;
        if (ImGui.BeginChild("Sounds", new Vector2(windowSize.X / 2f, childHeight)))
        {
            ImGui.Text(soundListTitle);
            if (ImGui.BeginListBox("##SoundEntries", ImGui.GetContentRegionAvail()))
            {
                for (int entryIndex = 0; entryIndex < soundEntryLabels.Length; entryIndex++)
                {
                    if (ImGui.Selectable(soundEntryLabels[entryIndex], entryIndex == selectedSoundIndex))
                    {
                        selectedSoundIndex = entryIndex;
                        PlaySoundEntry(entryIndex);
                    }
                }
                ImGui.EndListBox();
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();

        if (ImGui.BeginChild("##SoundOptions", new Vector2(0, childHeight)))
        {
            ImGui.Text("Options");
            var buttonSize = new Vector2(windowSize.X / 3f, Math.Max(windowSize.Y / 8, ImGui.GetFrameHeight()));
            bool hasSounds = soundEntryLabels.Length > 0;
            bool extractionRunning = extractionJob != null;

            if (!hasSounds)
            {
                ImGui.BeginDisabled();
            }
            if (ImGui.Button(playStopButton, buttonSize))
            {
                if (!isMusicPlaying)
                {
                    PlaySoundEntry(selectedSoundIndex);
                }
                else
                {
                    StopMusic();
                }
            }
            if (!hasSounds)
            {
                ImGui.EndDisabled();
            }

            RenderExtractFormatChoice();

            if (extractionRunning)
            {
                ImGui.BeginDisabled();
            }
            if (!hasSounds)
            {
                ImGui.BeginDisabled();
            }
            if (ImGui.Button("Extract sound", buttonSize))
            {
                StartExtraction(SoundExtractionScope.Sound);
            }
            if (ImGui.Button("Extract bank", buttonSize))
            {
                StartExtraction(SoundExtractionScope.Bank);
            }
            if (!hasSounds)
            {
                ImGui.EndDisabled();
            }
            if (ImGui.Button("Extract all", buttonSize))
            {
                StartExtraction(SoundExtractionScope.All);
            }
            if (extractionRunning)
            {
                ImGui.EndDisabled();
            }

            RenderExtractionStatus(buttonSize.X);

            ImGui.Separator();
            RenderSoundReplacing(buttonSize, hasSounds, extractionRunning);
            if (bankError != null)
            {
                ImGui.PushTextWrapPos();
                ImGui.TextColored(ErrorTextColour, bankError);
                ImGui.PopTextWrapPos();
            }
            if (monsterNamesError != null && currentMode == CurrentMusicMode.Monster)
            {
                ImGui.PushTextWrapPos();
                ImGui.TextColored(ErrorTextColour, monsterNamesError);
                ImGui.PopTextWrapPos();
            }
            RenderPlaybackError();
        }
        ImGui.EndChild();
        ImGui.Unindent(indent);
    }

    void RenderMonsterPicker()
    {
        if (monsterCardLabels.Length == 0)
        {
            LoadMonsterCardLabels();
        }
        if (monsterCardLabels.Length == 0)
        {
            return;
        }
        selectedMonsterCard = Math.Clamp(selectedMonsterCard, 0, monsterCardLabels.Length - 1);
        ImGui.Text("Monster:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(GuiContext.FieldWidth(0.5f, 20f));
        if (ImGui.BeginCombo("##Monster", monsterCardLabels[selectedMonsterCard]))
        {
            for (int card = 0; card < monsterCardLabels.Length; card++)
            {
                bool isSelected = card == selectedMonsterCard;
                if (ImGui.Selectable(monsterCardLabels[card], isSelected))
                {
                    selectedMonsterCard = card;
                }
                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }
            ImGui.EndCombo();
        }
    }

    // WAV or MP3 for every Extract (sound tabs and BGM).
    void RenderExtractFormatChoice()
    {
        ImGui.Text("Extract Format:");
        ImGui.SameLine();
        if (ImGui.RadioButton("WAV", exportFormat == SoundExportFormat.Wav))
        {
            exportFormat = SoundExportFormat.Wav;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("MP3", exportFormat == SoundExportFormat.Mp3))
        {
            exportFormat = SoundExportFormat.Mp3;
        }
    }

    // The running extraction's progress, then the last run's summary and failures.
    void RenderExtractionStatus(float barWidth)
    {
        if (extractionJob != null)
        {
            RenderExtractionProgress(extractionJob, barWidth);
        }
        if (lastRunSummary.Length > 0)
        {
            ImGui.TextWrapped(lastRunSummary);
            foreach (string failure in lastRunFailures)
            {
                ImGui.TextColored(ErrorTextColour, failure);
            }
        }
    }

    void RenderExtractionProgress(SoundExtractionJob job, float barWidth)
    {
        int banksDone = Volatile.Read(ref job.BanksDone);
        // Rebuilt only when the count moves, not every frame.
        if (banksDone != shownBanksDone)
        {
            shownBanksDone = banksDone;
            extractionProgressLabel = $"{banksDone} / {job.BankCount}";
        }
        ImGui.ProgressBar(banksDone / (float)job.BankCount, new Vector2(barWidth, 0), extractionProgressLabel);
        ImGui.SameLine();
        if (job.Cancellation.IsCancellationRequested)
        {
            ImGui.TextDisabled("Cancelling...");
        }
        else if (ImGui.Button("Cancel"))
        {
            job.Cancellation.Cancel();
        }
    }

    void RenderPlaybackError()
    {
        if (playbackError != null)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(ErrorTextColour, playbackError);
            ImGui.PopTextWrapPos();
        }
    }

    // ---- banks --------------------------------------------------------------------------------

    /* Shows the bank of `mode` (for Monster, card `monsterCard`'s slot), reading it from the ISO if
       another one is loaded. A bank that fails to read is reported once, not retried every frame. */
    void SelectBank(CurrentMusicMode mode, int monsterCard)
    {
        currentMode = mode;
        selectedMonsterCard = monsterCard;
        int wantedCard = -1;
        if (mode == CurrentMusicMode.Monster)
        {
            wantedCard = monsterCard;
        }
        if (bankLoadAttempted && loadedBankMode == mode && loadedBankCard == wantedCard)
        {
            return;
        }

        StopMusic();
        UnloadBank();
        bankLoadAttempted = true;
        loadedBankMode = mode;
        loadedBankCard = wantedCard;
        try
        {
            currentBank = ReadBank(fileloader, mode, wantedCard);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
        {
            bankError = $"{BankDisplayName(mode, wantedCard)}: {exception.Message}";
            return;
        }

        currentStagedBank = FindStagedBank(mode, wantedCard);
        RefreshSoundEntryLabels();
        soundListTitle = $"Sounds ({soundEntryLabels.Length})";
        selectedSoundIndex = 0;
    }

    // Rebuilt when the bank or its staged sounds change, not every frame.
    void RefreshSoundEntryLabels()
    {
        SoundBank? playableBank = PlayableBank();
        if (playableBank == null)
        {
            soundEntryLabels = [];
            return;
        }
        soundEntryLabels = new string[playableBank.Entries.Length];
        for (int entryIndex = 0; entryIndex < soundEntryLabels.Length; entryIndex++)
        {
            string label = $"{entryIndex:00}   {playableBank.Entries[entryIndex].Seconds:0.00} s";
            if (currentStagedBank != null && currentStagedBank.Replacements[entryIndex] != null)
            {
                label += "   (replaced)";
            }
            soundEntryLabels[entryIndex] = label;
        }
    }

    // What Play plays: the shown bank with its staged sounds, or as it is on the disc.
    SoundBank? PlayableBank()
    {
        if (currentStagedBank != null)
        {
            return currentStagedBank.PreviewBank;
        }
        return currentBank;
    }

    /* A card's monster bank as the Monster Sounds tab plays it: with the sounds staged there, else
       as on the disc; stagedEntries marks the staged sounds. For the model importer's sound cues.
       Throws (InvalidDataException, IOException, InvalidOperationException) when it cannot be read. */
    public SoundBank ReadMonsterBankWithStaged(int monsterCard, out bool[] stagedEntries)
    {
        StagedSoundBank? stagedBank = FindStagedBank(CurrentMusicMode.Monster, monsterCard);
        if (stagedBank != null)
        {
            stagedEntries = new bool[stagedBank.Replacements.Length];
            for (int entryIndex = 0; entryIndex < stagedEntries.Length; entryIndex++)
            {
                stagedEntries[entryIndex] = stagedBank.Replacements[entryIndex] != null;
            }
            return stagedBank.PreviewBank;
        }
        SoundBank bank = ReadBank(fileloader, CurrentMusicMode.Monster, monsterCard);
        stagedEntries = new bool[bank.Entries.Length];
        return bank;
    }

    // Goes up whenever staged sounds change (staged, reverted, saved, dropped with the ISO), so the
    // model importer knows to read a card's bank again.
    public int StagedSoundsChangeCount => stagedSoundsChangeCount;

    void UnloadBank()
    {
        currentBank = null;
        currentStagedBank = null;
        bankLoadAttempted = false;
        loadedBankCard = -1;
        bankError = null;
        soundEntryLabels = [];
        soundListTitle = "Sounds";
    }

    static SoundBank ReadBank(FileLoader disc, CurrentMusicMode mode, int monsterCard)
    {
        switch (mode)
        {
            case CurrentMusicMode.System:
            {
                FileLoader.FileEntry bankFile = disc.Get(SoundBank.SystemBankPath);
                return SoundBank.Read(disc, bankFile.ImageOffset, bankFile.Size);
            }
            case CurrentMusicMode.Card:
            {
                FileLoader.FileEntry bankFile = disc.Get(SoundBank.CardBankPath);
                return SoundBank.Read(disc, bankFile.ImageOffset, bankFile.Size);
            }
            case CurrentMusicMode.Monster:
            {
                FileLoader.FileEntry bankFile = disc.Get(SoundBank.MonsterBanksPath);
                long slotOffset = (long)monsterCard * SoundBank.MonsterSlotSize;
                if (monsterCard < 0 || slotOffset >= bankFile.Size)
                {
                    throw new InvalidDataException($"{bankFile.Name} has no slot {monsterCard}");
                }
                long slotBytes = Math.Min(SoundBank.MonsterSlotSize, bankFile.Size - slotOffset);
                return SoundBank.Read(disc, bankFile.ImageOffset + slotOffset, slotBytes);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    static int MonsterSlotCount(FileLoader disc)
    {
        return (int)(disc.Get(SoundBank.MonsterBanksPath).Size / SoundBank.MonsterSlotSize);
    }

    string BankDisplayName(CurrentMusicMode mode, int monsterCard)
    {
        if (mode == CurrentMusicMode.Monster)
        {
            if (monsterCard >= 0 && monsterCard < monsterCardLabels.Length)
            {
                return monsterCardLabels[monsterCard];
            }
            return $"Monster {monsterCard:000}";
        }
        return mode + " sounds";
    }

    // One label per SEGRP.MRG slot: card id and the name the model importer shows (GameTables).
    void LoadMonsterCardLabels()
    {
        monsterNamesError = null;
        int slotCount;
        try
        {
            slotCount = MonsterSlotCount(fileloader);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            bankError = exception.Message;
            return;
        }

        GameTables? tables = null;
        try
        {
            tables = GameTables.Load(fileloader);
        }
        catch (Exception exception)
        {
            monsterNamesError = "Card names unavailable: " + exception.Message;
        }
        var labels = new string[slotCount];
        for (int card = 0; card < slotCount; card++)
        {
            if (tables != null)
            {
                labels[card] = $"{card:000} {tables.CardName(card).Trim()}";
            }
            else
            {
                labels[card] = card.ToString("000");
            }
        }
        monsterCardLabels = labels;
    }

    // ---- playback -----------------------------------------------------------------------------

    // A replaced track plays its staged song; any other streams from the disc.
    void PlayBgmTrack(int trackId)
    {
        StopMusic();
        playbackError = null;
        if (stagedBgmTracks.TryGetValue(trackId, out StagedBgmTrack? stagedTrack) && stagedTrack.AudioBytes != null)
        {
            StartBgmPlayback(new BgmTrackStream(stagedTrack.AudioBytes));
            return;
        }
        string trackFileName = BgmTrackTable.TrackFileName(trackId);
        FileLoader.FileEntry? trackFile = fileloader.Find(trackFileName);
        if (trackFile == null)
        {
            playbackError = $"{trackFileName} is not on the disc.";
            return;
        }
        BgmTrackStream trackStream;
        try
        {
            trackStream = new BgmTrackStream(fileloader, trackFile);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
        {
            playbackError = $"{trackFileName}: {exception.Message}";
            return;
        }
        StartBgmPlayback(trackStream);
    }

    // Plays a track and shows it on the timeline.
    void StartBgmPlayback(BgmTrackStream trackStream)
    {
        StartPlayback(trackStream);
        if (playbackSource != trackStream)
        {
            return;
        }
        playingBgmStream = trackStream;
        bgmPlayedBytesAtSeek = 0;
        bgmSeekTargetBytes = 0;
        bgmTrackLengthLabel = $"/ {trackStream.TotalTime.TotalSeconds:0.0} s";
    }

    /* The timeline: where the track is being heard (the device's played bytes since the last seek,
       not the source's read position, which runs a buffer ahead), and a drag seeks there. */
    void RenderBgmTimeline(float width)
    {
        BgmTrackStream? trackStream = playingBgmStream;
        if (trackStream == null || playbackSource != trackStream)
        {
            ImGui.BeginDisabled();
            float stoppedSeconds = 0f;
            ImGui.SetNextItemWidth(width);
            ImGui.SliderFloat("##bgmTimeline", ref stoppedSeconds, 0f, 1f, "-");
            ImGui.EndDisabled();
            return;
        }
        float lengthSeconds = (float)trackStream.TotalTime.TotalSeconds;
        float heardSeconds = bgmTimelineDragSeconds;
        if (!bgmTimelineDragging)
        {
            long heardBytes = bgmSeekTargetBytes + (waveOut.GetPosition() - bgmPlayedBytesAtSeek);
            heardSeconds = Math.Min(heardBytes / (float)trackStream.WaveFormat.AverageBytesPerSecond, lengthSeconds);
        }
        ImGui.SetNextItemWidth(width);
        bool moved = ImGui.SliderFloat("##bgmTimeline", ref heardSeconds, 0f, lengthSeconds, "%.1f s");
        bgmTimelineDragging = ImGui.IsItemActive();
        bgmTimelineDragSeconds = heardSeconds;
        if (moved)
        {
            long targetBytes = (long)(heardSeconds * trackStream.WaveFormat.AverageBytesPerSecond);
            trackStream.Position = targetBytes;
            bgmSeekTargetBytes = trackStream.Position;
            bgmPlayedBytesAtSeek = waveOut.GetPosition();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(bgmTrackLengthLabel);
    }

    void PlaySoundEntry(int entryIndex)
    {
        PlayBankSound(PlayableBank(), entryIndex);
    }

    // Plays one sound of `bank` (the model importer plays a card's sound cues through this too).
    public void PlayBankSound(SoundBank? playableBank, int entryIndex)
    {
        // Stopped first: the device may still be reading soundPcmBytes.
        StopMusic();
        playbackError = null;
        if (playableBank == null || entryIndex < 0 || entryIndex >= playableBank.Entries.Length)
        {
            return;
        }
        SoundEntry entry = playableBank.Entries[entryIndex];
        if (soundPcmBytes.Length < entry.PcmByteCount)
        {
            soundPcmBytes = new byte[entry.PcmByteCount];
        }
        int pcmByteCount = playableBank.Decode(entry, soundPcmBytes);
        StartPlayback(new RawSourceWaveStream(soundPcmBytes, 0, pcmByteCount, new WaveFormat(entry.SampleRate, 16, 1)));
    }

    /* Plays one sound of `bank` on top of whatever cue sounds still play (the model importer's
       preview cues). Each sound is decoded into its own buffer, since several can play at once. */
    public void PlayBankSoundMixed(SoundBank? playableBank, int entryIndex)
    {
        if (playableBank == null || entryIndex < 0 || entryIndex >= playableBank.Entries.Length)
        {
            return;
        }
        if (cueOutput == null)
        {
            var output = new WaveOutEvent();
            output.Volume = PlaybackVolume;
            try
            {
                output.Init(cueMixer);
                output.Play();
            }
            catch (Exception exception)
            {
                output.Dispose();
                playbackError = "Could not play: " + exception.Message;
                return;
            }
            cueOutput = output;
        }
        SoundEntry entry = playableBank.Entries[entryIndex];
        byte[] pcmBytes = new byte[entry.PcmByteCount];
        int pcmByteCount = playableBank.Decode(entry, pcmBytes);
        var soundStream = new RawSourceWaveStream(pcmBytes, 0, pcmByteCount, new WaveFormat(entry.SampleRate, 16, 1));
        cueMixer.AddMixerInput(new WdlResamplingSampleProvider(soundStream.ToSampleProvider(), CueMixerSampleRate));
        long soundEndTick = Environment.TickCount64 + (long)(entry.Seconds * 1000.0);
        mixedSoundsEndTick = Math.Max(mixedSoundsEndTick, soundEndTick);
    }

    // Stops every cue sound still playing (the device keeps running, silent).
    public void StopMixedSounds()
    {
        cueMixer.RemoveAllMixerInputs();
        mixedSoundsEndTick = 0;
    }

    // A cue sound is still playing.
    public bool MixedSoundsPlaying
    {
        get { return Environment.TickCount64 < mixedSoundsEndTick; }
    }

    // Takes ownership of `source`: it is disposed when playback stops, or here if it cannot start.
    void StartPlayback(WaveStream source)
    {
        StopMusic();
        playbackThreadStopped.Reset();
        try
        {
            waveOut.Init(source);
            waveOut.Play();
        }
        catch (Exception exception)
        {
            // NAudio raises MmException (no device, unsupported format) and others from the driver.
            playbackThreadStopped.Set();
            source.Dispose();
            playbackError = "Could not play: " + exception.Message;
            return;
        }
        playbackSource = source;
        isMusicPlaying = true;
        playStopButton = "Stop";
    }

    /* Stops the device, waits for its playback thread to leave the source, then disposes the source.
       Called every frame while another extractor is shown, so it does nothing when idle. */
    public void StopMusic()
    {
        if (playbackSource == null)
        {
            return;
        }
        waveOut.Stop();
        playbackThreadStopped.Wait(PlaybackStopTimeoutMilliseconds);
        // The handler has run by now; its "ended" flag belongs to this stop, not to the next play.
        playbackEndedByItself = false;
        playbackSource.Dispose();
        playbackSource = null;
        playingBgmStream = null;
        isMusicPlaying = false;
        playStopButton = "Play";
    }

    // NAudio's playback thread: no ImGui here, only flags the next frame reads.
    void OnPlaybackStopped(object? sender, StoppedEventArgs eventArgs)
    {
        playbackThreadError = eventArgs.Exception?.Message;
        playbackEndedByItself = true;
        playbackThreadStopped.Set();
    }

    // FileLoader is about to close or replace the image: nothing may read it after this returns.
    void OnIsoClosing()
    {
        StopMusic();
        StopMixedSounds();
        CancelExtractionAndWait();
        UnloadBank();
        monsterCardLabels = [];
        monsterNamesError = null;
        // Staged sounds belong to the image being closed.
        stagedBanks.Clear();
        stagedSoundsChangeCount++;
        pendingReplacement = null;
        replaceError = null;
        stagedBgmTracks.Clear();
        pendingBgmReplacement = null;
        bgmReplaceError = null;
        bgmSaveStatus = string.Empty;
        bgmSaveErrors = new List<string>();
        bgmTableLoadAttempted = false;
    }

    public void Dispose()
    {
        fileloader.OnIsoClosing -= OnIsoClosing;
        StopMusic();
        CancelExtractionAndWait();
        waveOut.PlaybackStopped -= OnPlaybackStopped;
        waveOut.Dispose();
        StopMixedSounds();
        cueOutput?.Dispose();
        playbackThreadStopped.Dispose();
    }

    // ---- sound replacing ----------------------------------------------------------------------

    void RenderSoundReplacing(Vector2 buttonSize, bool hasSounds, bool extractionRunning)
    {
        if (!hasSounds)
        {
            ImGui.BeginDisabled();
        }
        if (ImGui.Button("Replace sound...", buttonSize))
        {
            PickReplacementFile(selectedSoundIndex);
        }
        if (droppedReplacementPath != null && hasSounds && pendingReplacement == null)
        {
            StartReplacement(selectedSoundIndex, droppedReplacementPath);
        }
        bool selectedIsReplaced = currentStagedBank != null && selectedSoundIndex < currentStagedBank.Replacements.Length
                                  && currentStagedBank.Replacements[selectedSoundIndex] != null;
        if (!selectedIsReplaced)
        {
            ImGui.BeginDisabled();
        }
        if (ImGui.Button("Revert", buttonSize))
        {
            RevertReplacement(selectedSoundIndex);
        }
        if (!selectedIsReplaced)
        {
            ImGui.EndDisabled();
        }
        if (!hasSounds)
        {
            ImGui.EndDisabled();
        }

        bool canSave = stagedBanks.Count > 0 && !extractionRunning;
        if (!canSave)
        {
            ImGui.BeginDisabled();
        }
        if (ImGui.Button("Save to ISO", buttonSize))
        {
            ImGui.OpenPopup("Confirm sound save");
        }
        if (!canSave)
        {
            ImGui.EndDisabled();
        }

        if (replaceError != null && pendingReplacement == null)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(ErrorTextColour, replaceError);
            ImGui.PopTextWrapPos();
        }
        if (saveStatus.Length > 0)
        {
            ImGui.PushTextWrapPos();
            if (saveSucceeded)
            {
                ImGui.TextUnformatted(saveStatus);
            }
            else
            {
                ImGui.TextColored(ErrorTextColour, saveStatus);
            }
            foreach (string saveError in saveErrors)
            {
                ImGui.TextColored(ErrorTextColour, saveError);
            }
            ImGui.PopTextWrapPos();
        }

        RenderReplacePopup();
        RenderSaveConfirmPopup();
    }

    void PickReplacementFile(int entryIndex)
    {
        replaceError = null;
        if (currentBank == null || entryIndex < 0 || entryIndex >= currentBank.Entries.Length)
        {
            return;
        }
        DialogResult dialogResult = Dialog.FileOpen(FileDialogFilter.Of(ReplacementFileExtensions), Path.GetDirectoryName(Program.config.lastSoundReplacePath));
        if (!dialogResult.IsOk)
        {
            return;
        }
        Program.config.lastSoundReplacePath = dialogResult.Path;
        StartReplacement(entryIndex, dialogResult.Path);
    }

    // A file dropped on the window (MainWindow): replaces the selected sound when a Card, Monster or
    // System tab is shown this frame, else it is ignored.
    public void ReplaceSelectedSoundWith(string filePath)
    {
        droppedReplacementPath = filePath;
    }

    public static bool IsReplacementFile(string filePath)
    {
        string extension = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        return extension.Length > 0 && ReplacementFileExtensions.Split(',').Contains(extension);
    }

    // Reads `filePath` and opens the Replace popup for `entryIndex`.
    void StartReplacement(int entryIndex, string filePath)
    {
        droppedReplacementPath = null;
        replaceError = null;
        if (currentBank == null || entryIndex < 0 || entryIndex >= currentBank.Entries.Length)
        {
            return;
        }
        var pending = new PendingSoundReplacement();
        pending.EntryIndex = entryIndex;
        pending.FileName = Path.GetFileName(filePath);
        try
        {
            pending.MonoSamples = ReadSamples(filePath, 1, out pending.SourceRate, out _);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
                                              or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            replaceError = $"{pending.FileName}: {exception.Message}";
            return;
        }
        pending.Rate = Math.Clamp(pending.SourceRate, 1, SoundBank.HighestSampleRate);
        pending.Loops = currentBank.Entries[entryIndex].Loops;
        pending.Volume = currentBank.Entries[entryIndex].Volume;
        SoundEntry originalEntry = currentBank.Entries[entryIndex];
        byte[] originalPcmBytes = new byte[originalEntry.PcmByteCount];
        int originalPcmByteCount = currentBank.Decode(originalEntry, originalPcmBytes);
        ReadOnlySpan<short> originalSamples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(originalPcmBytes.AsSpan(0, originalPcmByteCount));
        MeasureLevels(originalSamples, out pending.OriginalPeakDecibels, out pending.OriginalRmsDecibels);
        pending.OtherSamplesByteCount = SampleDataSizeWithout(entryIndex);
        UpdatePendingRate(pending);
        pendingReplacement = pending;
        ImGui.OpenPopup("Replace sound");
    }

    // The shown bank's sample data size with every staged sound kept but `entryIndex`'s sound left out.
    int SampleDataSizeWithout(int entryIndex)
    {
        SoundReplacement?[] replacements = CurrentReplacementsCopy();
        // A one-block placeholder stands in for the new sound, then is taken off again.
        var placeholder = new SoundReplacement();
        placeholder.AdpcmBytes = new byte[SoundBank.AdpcmBlockSize];
        replacements[entryIndex] = placeholder;
        return currentBank!.SampleDataSize(replacements) - SoundBank.AdpcmBlockSize;
    }

    SoundReplacement?[] CurrentReplacementsCopy()
    {
        if (currentStagedBank != null)
        {
            return (SoundReplacement?[])currentStagedBank.Replacements.Clone();
        }
        return new SoundReplacement?[currentBank!.Entries.Length];
    }

    /* Recomputes what the chosen rate gives: the nearest pitch, the rate the game plays it at, the
       bytes the sound will take, and when it does not fit, the highest rate that does. */
    void UpdatePendingRate(PendingSoundReplacement pending)
    {
        int roomByteCount = BankSampleDataLimit(currentMode, loadedBankCard) - pending.OtherSamplesByteCount;
        pending.RoomByteCount = roomByteCount;
        pending.Pitch = SoundBank.PitchOfSampleRate(pending.Rate);
        pending.PlayRate = SoundBank.SampleRateOfPitch(pending.Pitch);
        UpdatePendingLevels(pending);
        pending.SoundByteCount = EncodedByteCount(pending, pending.PlayRate);
        pending.Fits = pending.SoundByteCount <= roomByteCount;
        pending.FittingRate = 0;
        if (pending.Fits)
        {
            return;
        }
        int largestSoundBlockCount = roomByteCount / SoundBank.AdpcmBlockSize - 2;
        if (largestSoundBlockCount < 1)
        {
            return;
        }
        double largestSampleCount = largestSoundBlockCount * (double)SoundBank.SamplesPerAdpcmBlock;
        int tryRate = (int)Math.Floor(largestSampleCount * pending.SourceRate / pending.MonoSamples.Length);
        while (tryRate >= 1)
        {
            int candidateRate = SoundBank.SampleRateOfPitch(SoundBank.PitchOfSampleRate(tryRate));
            if (candidateRate <= tryRate && EncodedByteCount(pending, candidateRate) <= roomByteCount)
            {
                pending.FittingRate = candidateRate;
                return;
            }
            tryRate = Math.Min(tryRate, candidateRate) - 1;
        }
    }

    // Measures the new sound as it will be encoded: resampled to PlayRate with the gain applied.
    static void UpdatePendingLevels(PendingSoundReplacement pending)
    {
        float gain = MathF.Pow(10f, pending.GainDecibels / 20f);
        short[] samples = ResampleToShorts(pending.MonoSamples, pending.SourceRate, pending.PlayRate, gain);
        MeasureLevels(samples, out pending.NewPeakDecibels, out pending.NewRmsDecibels);
    }

    // Peak and RMS of 16-bit samples in dBFS (0 = full scale); negative infinity when silent.
    static void MeasureLevels(ReadOnlySpan<short> samples, out double peakDecibels, out double rmsDecibels)
    {
        int peakMagnitude = 0;
        double sumOfSquares = 0;
        foreach (short sample in samples)
        {
            int magnitude = Math.Abs((int)sample);
            if (magnitude > peakMagnitude)
            {
                peakMagnitude = magnitude;
            }
            sumOfSquares += (double)sample * sample;
        }
        peakDecibels = DecibelsOfFullScale(peakMagnitude);
        if (samples.Length > 0)
        {
            rmsDecibels = DecibelsOfFullScale(Math.Sqrt(sumOfSquares / samples.Length));
        }
        else
        {
            rmsDecibels = double.NegativeInfinity;
        }
    }

    static double DecibelsOfFullScale(double magnitude)
    {
        if (magnitude <= 0)
        {
            return double.NegativeInfinity;
        }
        return 20.0 * Math.Log10(magnitude / short.MaxValue);
    }

    static string LevelText(double decibels)
    {
        if (double.IsNegativeInfinity(decibels))
        {
            return "silent";
        }
        return $"{decibels:0.0} dBFS";
    }

    // The ADPCM size of the pending sound once resampled to `playRate` (silent + terminator blocks included).
    static int EncodedByteCount(PendingSoundReplacement pending, int playRate)
    {
        long sampleCount = ResampledSampleCount(pending.MonoSamples.Length, pending.SourceRate, playRate);
        long soundBlockCount = (sampleCount + SoundBank.SamplesPerAdpcmBlock - 1) / SoundBank.SamplesPerAdpcmBlock;
        return (int)Math.Min(int.MaxValue, (soundBlockCount + 2) * SoundBank.AdpcmBlockSize);
    }

    static long ResampledSampleCount(int sourceSampleCount, int sourceRate, int playRate)
    {
        return (long)Math.Ceiling(sourceSampleCount * (double)playRate / sourceRate);
    }

    void RenderReplacePopup()
    {
        if (!ImGui.BeginPopupModal("Replace sound", ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }
        PendingSoundReplacement? pending = pendingReplacement;
        if (pending == null || currentBank == null || pending.EntryIndex >= currentBank.Entries.Length)
        {
            pendingReplacement = null;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        SoundEntry entry = currentBank.Entries[pending.EntryIndex];
        ImGui.Text($"Replace sound {pending.EntryIndex:00} of {BankDisplayName(currentMode, loadedBankCard)}");
        ImGui.Text($"{pending.FileName}: {pending.MonoSamples.Length / (double)pending.SourceRate:0.00} s at {pending.SourceRate:N0} Hz");

        ImGui.SetNextItemWidth(GuiContext.FieldWidth(0.15f, 10f));
        if (ImGui.InputInt("Rate (Hz)", ref pending.Rate, 100, 1000))
        {
            pending.Rate = Math.Clamp(pending.Rate, 1, SoundBank.HighestSampleRate);
            UpdatePendingRate(pending);
        }
        ImGui.SameLine();
        if (ImGui.Button($"Use entry's rate ({entry.SampleRate:N0} Hz)"))
        {
            pending.Rate = entry.SampleRate;
            UpdatePendingRate(pending);
        }
        ImGui.Text($"Plays at {pending.PlayRate:N0} Hz (pitch 0x{pending.Pitch:X4})");
        ImGui.SetNextItemWidth(GuiContext.FieldWidth(0.15f, 10f));
        if (ImGui.InputInt($"Volume (0-{SoundBank.MaxVolume}, entry's is {entry.Volume})", ref pending.Volume, 1, 10))
        {
            pending.Volume = Math.Clamp(pending.Volume, 0, SoundBank.MaxVolume);
        }
        ImGui.SetNextItemWidth(GuiContext.FieldWidth(0.15f, 10f));
        if (ImGui.InputFloat("Gain (dB)", ref pending.GainDecibels, 1f, 6f, "%.1f"))
        {
            pending.GainDecibels = Math.Clamp(pending.GainDecibels, LowestReplacementGainDecibels, HighestReplacementGainDecibels);
            UpdatePendingLevels(pending);
        }
        ImGui.SameLine();
        bool canMatchLoudness = double.IsFinite(pending.OriginalRmsDecibels) && double.IsFinite(pending.NewRmsDecibels);
        if (!canMatchLoudness)
        {
            ImGui.BeginDisabled();
        }
        if (ImGui.Button("Match loudness"))
        {
            float matchedGainDecibels = pending.GainDecibels + (float)(pending.OriginalRmsDecibels - pending.NewRmsDecibels);
            pending.GainDecibels = Math.Clamp(matchedGainDecibels, LowestReplacementGainDecibels, HighestReplacementGainDecibels);
            UpdatePendingLevels(pending);
        }
        if (!canMatchLoudness)
        {
            ImGui.EndDisabled();
        }
        GuiContext.HoveredTooltip("Sets Gain so the new sound's RMS matches the original's.");

        ImGui.Text($"Original: peak {LevelText(pending.OriginalPeakDecibels)}, RMS {LevelText(pending.OriginalRmsDecibels)}, volume {entry.Volume}");
        GuiContext.HoveredTooltip(PeakAndRmsTooltip);
        ImGui.Text($"New: peak {LevelText(pending.NewPeakDecibels)}, RMS {LevelText(pending.NewRmsDecibels)}, volume {pending.Volume}");
        GuiContext.HoveredTooltip(PeakAndRmsTooltip);
        if (pending.NewPeakDecibels >= FullScaleDecibels)
        {
            ImGui.SameLine();
            ImGui.TextColored(ErrorTextColour, "(clipping)");
        }
        ImGui.Checkbox("Loop", ref pending.Loops);
        ImGui.Text($"Needs about {pending.SoundByteCount:N0} bytes; the bank has {Math.Max(0, pending.RoomByteCount):N0} free.");
        if (!pending.Fits)
        {
            ImGui.TextColored(ErrorTextColour, "Does not fit in this bank at this rate.");
            if (pending.FittingRate > 0)
            {
                if (ImGui.Button($"Use {pending.FittingRate:N0} Hz (fits)"))
                {
                    pending.Rate = pending.FittingRate;
                    UpdatePendingRate(pending);
                }
            }
            else
            {
                ImGui.TextColored(ErrorTextColour, "No rate fits: the bank has no room left.");
            }
        }
        if (replaceError != null)
        {
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
            ImGui.TextColored(ErrorTextColour, replaceError);
            ImGui.PopTextWrapPos();
        }

        if (!pending.Fits)
        {
            ImGui.BeginDisabled();
        }
        if (ImGui.Button("Stage"))
        {
            if (StagePendingReplacement(pending))
            {
                pendingReplacement = null;
                ImGui.CloseCurrentPopup();
            }
        }
        if (!pending.Fits)
        {
            ImGui.EndDisabled();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            pendingReplacement = null;
            replaceError = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    // Resamples, encodes and stages the pending sound; false (with replaceError set) if it cannot.
    bool StagePendingReplacement(PendingSoundReplacement pending)
    {
        replaceError = null;
        float gain = MathF.Pow(10f, pending.GainDecibels / 20f);
        short[] samples = ResampleToShorts(pending.MonoSamples, pending.SourceRate, pending.PlayRate, gain);
        var replacement = new SoundReplacement();
        replacement.AdpcmBytes = SoundBank.Encode(samples, samples.Length, pending.Loops);
        replacement.Pitch = pending.Pitch;
        replacement.Volume = (byte)pending.Volume;

        SoundReplacement?[] replacements = CurrentReplacementsCopy();
        replacements[pending.EntryIndex] = replacement;
        StopMusic();
        try
        {
            if (currentStagedBank == null)
            {
                StagedSoundBank stagedBank = CreateStagedBank(currentMode, loadedBankCard, currentBank!);
                stagedBank.SetReplacements(replacements);
                stagedBanks.Add(stagedBank);
                currentStagedBank = stagedBank;
            }
            else
            {
                currentStagedBank.SetReplacements(replacements);
            }
        }
        catch (InvalidDataException exception)
        {
            replaceError = exception.Message;
            return false;
        }
        stagedSoundsChangeCount++;
        saveStatus = string.Empty;
        saveErrors = new List<string>();
        RefreshSoundEntryLabels();
        return true;
    }

    void RevertReplacement(int entryIndex)
    {
        if (currentStagedBank == null)
        {
            return;
        }
        StopMusic();
        SoundReplacement?[] replacements = CurrentReplacementsCopy();
        replacements[entryIndex] = null;
        bool anyLeft = false;
        foreach (SoundReplacement? replacement in replacements)
        {
            if (replacement != null)
            {
                anyLeft = true;
            }
        }
        if (anyLeft)
        {
            // Fewer bytes than before, so it always fits.
            currentStagedBank.SetReplacements(replacements);
        }
        else
        {
            stagedBanks.Remove(currentStagedBank);
            currentStagedBank = null;
        }
        stagedSoundsChangeCount++;
        RefreshSoundEntryLabels();
    }

    StagedSoundBank? FindStagedBank(CurrentMusicMode mode, int monsterCard)
    {
        foreach (StagedSoundBank stagedBank in stagedBanks)
        {
            if (stagedBank.Mode == mode && stagedBank.MonsterCard == monsterCard)
            {
                return stagedBank;
            }
        }
        return null;
    }

    StagedSoundBank CreateStagedBank(CurrentMusicMode mode, int monsterCard, SoundBank originalBank)
    {
        var stagedBank = new StagedSoundBank();
        stagedBank.Mode = mode;
        stagedBank.MonsterCard = monsterCard;
        stagedBank.DisplayName = BankDisplayName(mode, monsterCard);
        stagedBank.OriginalBank = originalBank;
        stagedBank.SampleDataLimit = BankSampleDataLimit(mode, monsterCard);
        if (mode == CurrentMusicMode.System)
        {
            stagedBank.FilePath = SoundBank.SystemBankPath;
        }
        else if (mode == CurrentMusicMode.Card)
        {
            stagedBank.FilePath = SoundBank.CardBankPath;
        }
        else
        {
            stagedBank.FilePath = SoundBank.MonsterBanksPath;
            stagedBank.FileOffset = (long)monsterCard * SoundBank.MonsterSlotSize;
        }
        return stagedBank;
    }

    /* The sample data a bank may hold: a .GRP bank its file's size on the disc (an in-place write
       cannot grow a file); a monster bank its SPU RAM room (SoundBank.MonsterSampleDataLimit). */
    int BankSampleDataLimit(CurrentMusicMode mode, int monsterCard)
    {
        if (mode == CurrentMusicMode.System)
        {
            return (int)(fileloader.Get(SoundBank.SystemBankPath).Size - SoundBank.HeaderSize);
        }
        if (mode == CurrentMusicMode.Card)
        {
            return (int)(fileloader.Get(SoundBank.CardBankPath).Size - SoundBank.HeaderSize);
        }
        FileLoader.FileEntry monsterFile = fileloader.Get(SoundBank.MonsterBanksPath);
        long slotBytes = Math.Min(SoundBank.MonsterSlotSize, monsterFile.Size - (long)monsterCard * SoundBank.MonsterSlotSize);
        return (int)Math.Min(SoundBank.MonsterSampleDataLimit, slotBytes - SoundBank.HeaderSize);
    }

    void RenderSaveConfirmPopup()
    {
        bool open = true;
        if (!ImGui.BeginPopupModal("Confirm sound save", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }
        ImGui.Text($"Write {stagedBanks.Count} staged sound bank(s) into the ISO in place?");
        foreach (StagedSoundBank stagedBank in stagedBanks)
        {
            ImGui.BulletText($"{stagedBank.DisplayName}: {stagedBank.ReplacedCount()} sound(s)");
        }
        ImGui.TextDisabled("Nothing is written if any bank does not fit.");
        if (ImGui.Button("Yes"))
        {
            ImGui.CloseCurrentPopup();
            SaveStagedBanks();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    // One ChangeSet for every staged bank: header + sample data at the bank's place, nothing after it.
    void SaveStagedBanks()
    {
        StopMusic();
        saveErrors = new List<string>();
        var changes = new ChangeSet();
        try
        {
            foreach (StagedSoundBank stagedBank in stagedBanks)
            {
                changes.Add(stagedBank.FilePath, stagedBank.FileOffset, stagedBank.BankBytes, stagedBank.DisplayName);
            }
            ChangeSet.Plan plan = changes.Write(fileloader);
            saveSucceeded = plan.Ok;
            if (plan.Ok)
            {
                saveStatus = $"Saved {stagedBanks.Count} bank(s): {plan.Writes.Count} writes ({plan.BytesToWrite:N0} B), {plan.NoOps.Count} unchanged.";
                stagedBanks.Clear();
                stagedSoundsChangeCount++;
                // Read again from the disc on the next frame.
                UnloadBank();
            }
            else
            {
                saveStatus = "Nothing written:";
                saveErrors.AddRange(plan.Errors);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            saveSucceeded = false;
            saveStatus = "Nothing written: " + exception.Message;
        }
    }

    /* Reads any file Windows Media Foundation decodes (WAV, MP3, AAC/M4A, WMA, FLAC) as interleaved
       samples with `outputChannelCount` channels: 1 mixes every channel down (a sound effect plays on
       one SPU voice); 2 keeps the first two (a mono file on both). */
    static float[] ReadSamples(string filePath, int outputChannelCount, out int sourceRate, out int sourceChannelCount)
    {
        using var reader = new MediaFoundationReader(filePath);
        ISampleProvider sampleProvider = reader.ToSampleProvider();
        int channelCount = sampleProvider.WaveFormat.Channels;
        sourceChannelCount = channelCount;
        sourceRate = sampleProvider.WaveFormat.SampleRate;
        var outputSamples = new List<float>();
        float[] readBuffer = new float[4096 * channelCount];
        int samplesRead;
        while ((samplesRead = sampleProvider.Read(readBuffer, 0, readBuffer.Length)) > 0)
        {
            for (int frameStart = 0; frameStart + channelCount <= samplesRead; frameStart += channelCount)
            {
                if (outputChannelCount == 1)
                {
                    float frameSum = 0f;
                    for (int channel = 0; channel < channelCount; channel++)
                    {
                        frameSum += readBuffer[frameStart + channel];
                    }
                    outputSamples.Add(frameSum / channelCount);
                }
                else
                {
                    for (int channel = 0; channel < outputChannelCount; channel++)
                    {
                        outputSamples.Add(readBuffer[frameStart + Math.Min(channel, channelCount - 1)]);
                    }
                }
            }
        }
        if (outputSamples.Count == 0)
        {
            throw new InvalidDataException("the file has no audio");
        }
        return outputSamples.ToArray();
    }

    // Mono samples resampled to playRate and scaled by gain; anything past full scale is clipped.
    static short[] ResampleToShorts(float[] monoSamples, int sourceRate, int playRate, float gain)
    {
        return ToShorts(Resample(monoSamples, 1, sourceRate, playRate), gain);
    }

    // Interleaved samples with `channelCount` channels from sourceRate to playRate.
    static float[] Resample(float[] samples, int channelCount, int sourceRate, int playRate)
    {
        if (playRate == sourceRate)
        {
            return samples;
        }
        byte[] floatBytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, floatBytes, 0, floatBytes.Length);
        using var floatStream = new RawSourceWaveStream(floatBytes, 0, floatBytes.Length, WaveFormat.CreateIeeeFloatWaveFormat(sourceRate, channelCount));
        var resampler = new WdlResamplingSampleProvider(floatStream.ToSampleProvider(), playRate);
        var resampledSamples = new List<float>((int)ResampledSampleCount(samples.Length, sourceRate, playRate));
        float[] readBuffer = new float[4096 * channelCount];
        int samplesRead;
        while ((samplesRead = resampler.Read(readBuffer, 0, readBuffer.Length)) > 0)
        {
            for (int i = 0; i < samplesRead; i++)
            {
                resampledSamples.Add(readBuffer[i]);
            }
        }
        return resampledSamples.ToArray();
    }

    // Float samples scaled by gain to 16 bits; anything past full scale is clipped.
    static short[] ToShorts(float[] samples, float gain)
    {
        var shortSamples = new short[Math.Max(1, samples.Length)];
        for (int i = 0; i < samples.Length; i++)
        {
            shortSamples[i] = (short)Math.Clamp(Math.Round(samples[i] * gain * 32767.0), short.MinValue, short.MaxValue);
        }
        return shortSamples;
    }

    // ---- sound extraction ---------------------------------------------------------------------

    // Opens at the last folder extracted to (shared by BGM and sounds); null when cancelled.
    static string? PickExtractFolder()
    {
        DialogResult dialogResult = Dialog.FolderPicker(Program.config.lastMusicExtractPath);
        if (!dialogResult.IsOk)
        {
            return null;
        }
        Program.config.lastMusicExtractPath = dialogResult.Path;
        return dialogResult.Path;
    }

    void StartExtraction(SoundExtractionScope scope)
    {
        string? destinationFolder = PickExtractFolder();
        if (destinationFolder == null)
        {
            return;
        }
        var job = new SoundExtractionJob();
        job.Disc = fileloader;
        job.Mode = currentMode;
        job.Format = exportFormat;
        job.OutputRoot = destinationFolder;
        if (currentMode == CurrentMusicMode.BGM)
        {
            SetBgmExtractionTracks(job, scope);
            lastRunSummary = string.Empty;
            lastRunFailures = new List<string>();
            shownBanksDone = -1;
            extractionJob = job;
            job.Task = Task.Run(() => RunExtraction(job));
            return;
        }
        if (scope == SoundExtractionScope.Sound)
        {
            job.SingleEntryIndex = selectedSoundIndex;
        }

        if (currentMode == CurrentMusicMode.Monster)
        {
            if (monsterCardLabels.Length == 0)
            {
                return;
            }
            if (scope == SoundExtractionScope.All)
            {
                job.Cards = new int[monsterCardLabels.Length];
                for (int card = 0; card < job.Cards.Length; card++)
                {
                    job.Cards[card] = card;
                }
            }
            else
            {
                job.Cards = new[] { selectedMonsterCard };
            }
            job.MonsterFolderNames = new string[monsterCardLabels.Length];
            for (int card = 0; card < monsterCardLabels.Length; card++)
            {
                job.MonsterFolderNames[card] = FileSystemSafeName(monsterCardLabels[card]);
            }
        }
        else
        {
            job.Cards = new[] { -1 };
        }
        job.BankCount = job.Cards.Length;

        lastRunSummary = string.Empty;
        lastRunFailures = new List<string>();
        shownBanksDone = -1;
        extractionJob = job;
        job.Task = Task.Run(() => RunExtraction(job));
    }

    // Worker thread: reads banks through FileLoader.ReadAt (locked), writes files, no ImGui.
    static void RunExtraction(SoundExtractionJob job)
    {
        CancellationToken cancellationToken = job.Cancellation.Token;
        bool mediaFoundationStarted = false;
        try
        {
            if (job.Format == SoundExportFormat.Mp3)
            {
                MediaFoundationApi.Startup();
                mediaFoundationStarted = true;
            }
            // This job's own decode buffer: the window's is the playing sound's.
            byte[] pcmBytes = [];
            string fileExtension = ".wav";
            if (job.Format == SoundExportFormat.Mp3)
            {
                fileExtension = ".mp3";
            }
            if (job.Mode == CurrentMusicMode.BGM)
            {
                ExtractBgmTracks(job, fileExtension);
                return;
            }

            foreach (int card in job.Cards)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                string bankFolder = job.BankFolder(card);
                SoundBank bank;
                try
                {
                    bank = ReadBank(job.Disc, job.Mode, card);
                    Directory.CreateDirectory(bankFolder);
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    job.Failures.Add($"{Path.GetFileName(bankFolder)}: {exception.Message}");
                    Interlocked.Increment(ref job.BanksDone);
                    continue;
                }

                int firstEntry = 0;
                int entryEnd = bank.Entries.Length;
                if (job.SingleEntryIndex >= 0)
                {
                    firstEntry = job.SingleEntryIndex;
                    entryEnd = Math.Min(job.SingleEntryIndex + 1, bank.Entries.Length);
                }
                for (int entryIndex = firstEntry; entryIndex < entryEnd; entryIndex++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    SoundEntry entry = bank.Entries[entryIndex];
                    string outputPath = Path.Combine(bankFolder, entryIndex.ToString("00") + fileExtension);
                    try
                    {
                        if (pcmBytes.Length < entry.PcmByteCount)
                        {
                            pcmBytes = new byte[entry.PcmByteCount];
                        }
                        int pcmByteCount = bank.Decode(entry, pcmBytes);
                        WriteSound(pcmBytes, pcmByteCount, entry.SampleRate, outputPath, job.Format);
                        job.SoundsWritten++;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
                    {
                        job.Failures.Add($"{Path.GetFileName(bankFolder)} sound {entryIndex:00}: {exception.Message}");
                    }
                }
                Interlocked.Increment(ref job.BanksDone);
            }
        }
        finally
        {
            if (mediaFoundationStarted)
            {
                MediaFoundationApi.Shutdown();
            }
        }
    }

    // One mono 16-bit sound to a .wav at its own rate, or an .mp3 resampled to 48 kHz.
    static void WriteSound(byte[] pcmBytes, int pcmByteCount, int sampleRate, string outputPath, SoundExportFormat format)
    {
        using var pcmStream = new RawSourceWaveStream(pcmBytes, 0, pcmByteCount, new WaveFormat(sampleRate, 16, 1));
        WriteAudio(pcmStream, outputPath, format, Mp3SoundEffectSampleRate);
    }

    // 16-bit audio to a .wav as it is, or an .mp3 at mp3SampleRate (lowest available bitrate).
    static void WriteAudio(WaveStream pcmStream, string outputPath, SoundExportFormat format, int mp3SampleRate)
    {
        if (format == SoundExportFormat.Wav)
        {
            WaveFileWriter.CreateWaveFile(outputPath, pcmStream);
        }
        else if (pcmStream.WaveFormat.SampleRate == mp3SampleRate)
        {
            MediaFoundationEncoder.EncodeToMp3(pcmStream, outputPath, 0);
        }
        else
        {
            var resampled = new WdlResamplingSampleProvider(pcmStream.ToSampleProvider(), mp3SampleRate);
            MediaFoundationEncoder.EncodeToMp3(resampled.ToWaveProvider16(), outputPath, 0);
        }
    }

    void FinishExtraction()
    {
        SoundExtractionJob job = extractionJob!;
        extractionJob = null;
        lastRunFailures = job.Failures;
        string outcome = "Last run";
        if (job.Task.IsFaulted)
        {
            lastRunFailures.Add(job.Task.Exception!.GetBaseException().Message);
        }
        if (job.Cancellation.IsCancellationRequested)
        {
            outcome = "Last run (cancelled)";
        }
        lastRunSummary = $"{outcome}: {job.SoundsWritten} written, {lastRunFailures.Count} failed. Folder: {job.OutputRoot}";
        job.Cancellation.Dispose();
    }

    void CancelExtractionAndWait()
    {
        if (extractionJob == null)
        {
            return;
        }
        extractionJob.Cancellation.Cancel();
        try
        {
            extractionJob.Task.Wait();
        }
        catch (AggregateException)
        {
            // Reported by FinishExtraction from the task itself.
        }
        FinishExtraction();
    }

    static string FileSystemSafeName(string name)
    {
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        var safeName = new System.Text.StringBuilder(name.Length);
        foreach (char character in name)
        {
            if (Array.IndexOf(invalidCharacters, character) >= 0)
            {
                safeName.Append('_');
            }
            else
            {
                safeName.Append(character);
            }
        }
        // Windows drops trailing dots and spaces from folder names.
        return safeName.ToString().TrimEnd('.', ' ');
    }

    // ---- BGM ----------------------------------------------------------------------------------

    // Reads PCMLIST.BIN from the ISO once (again after a save or on another ISO), then the list.
    void LoadBgmTable()
    {
        if (bgmTableLoadAttempted && bgmTableIsoPath == fileloader.CurrentIsoPath)
        {
            return;
        }
        bgmTableLoadAttempted = true;
        bgmTableIsoPath = fileloader.CurrentIsoPath;
        bgmTable = null;
        bgmTableError = null;
        try
        {
            bgmTable = BgmTrackTable.Parse(fileloader.ReadFile(BgmTrackTable.TablePath));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
        {
            bgmTableError = "Track table unavailable: " + exception.Message;
        }
        RefreshBgmTrackLabels();
    }

    // Each main track's id, label and duel variants (the table's ids N + 1000 * level).
    void RefreshBgmTrackLabels()
    {
        bgmMainTrackIds = new int[BgmTracks.Length];
        bgmTrackLabels = new string[BgmTracks.Length];
        bgmVariantIds = new int[BgmTracks.Length][];
        bgmVariantLabels = new string[BgmTracks.Length][];
        for (int mainIndex = 0; mainIndex < BgmTracks.Length; mainIndex++)
        {
            int mainTrackId = int.Parse(BgmTracks[mainIndex].Substring(0, 2));
            bgmMainTrackIds[mainIndex] = mainTrackId;
            bgmTrackLabels[mainIndex] = BgmTracks[mainIndex] + BgmStagedSuffix(mainTrackId);
            var variantIds = new List<int>();
            if (bgmTable != null)
            {
                foreach (BgmTrackEntry entry in bgmTable.Entries)
                {
                    if (entry.TrackId >= 1000 && entry.TrackId % 1000 == mainTrackId)
                    {
                        variantIds.Add(entry.TrackId);
                    }
                }
            }
            variantIds.Sort();
            bgmVariantIds[mainIndex] = variantIds.ToArray();
            bgmVariantLabels[mainIndex] = new string[variantIds.Count];
            for (int variantIndex = 0; variantIndex < variantIds.Count; variantIndex++)
            {
                int variantTrackId = variantIds[variantIndex];
                bgmVariantLabels[mainIndex][variantIndex] = $"      {variantTrackId:0000} (variant {variantTrackId / 1000}){BgmStagedSuffix(variantTrackId)}";
            }
        }
    }

    string BgmStagedSuffix(int trackId)
    {
        if (!stagedBgmTracks.TryGetValue(trackId, out StagedBgmTrack? stagedTrack))
        {
            return string.Empty;
        }
        if (stagedTrack.AudioBytes != null)
        {
            return "  (replaced)";
        }
        return "  (loop edited)";
    }

    // "40-CustomDuel", or "40-CustomDuel variant 1" for a duel variant.
    string BgmTrackDisplayName(int trackId)
    {
        string mainName = (trackId % 1000).ToString("00");
        for (int mainIndex = 0; mainIndex < bgmMainTrackIds.Length; mainIndex++)
        {
            if (bgmMainTrackIds[mainIndex] == trackId % 1000)
            {
                mainName = BgmTracks[mainIndex];
            }
        }
        if (trackId >= 1000)
        {
            return $"{mainName} variant {trackId / 1000}";
        }
        return mainName;
    }

    // The values the game would use now: the staged ones, else the table's (null: not in the table).
    BgmTrackEntry? CurrentBgmEntry(int trackId)
    {
        if (stagedBgmTracks.TryGetValue(trackId, out StagedBgmTrack? stagedTrack))
        {
            return stagedTrack.Entry;
        }
        return bgmTable?.Find(trackId);
    }

    // The track's file and its values as on the disc (its own header when the table has no entry);
    // null when the disc has no such file. Throws InvalidDataException on a short file.
    BgmTrackEntry? OriginalBgmEntry(int trackId, out FileLoader.FileEntry? trackFile)
    {
        trackFile = fileloader.Find(BgmTrackTable.TrackFileName(trackId));
        if (trackFile == null)
        {
            return null;
        }
        BgmTrackEntry? tableEntry = bgmTable?.Find(trackId);
        if (tableEntry != null)
        {
            return tableEntry.Clone();
        }
        return BgmTrackTable.ReadHeaderEntry(fileloader, trackFile, trackId);
    }

    void SelectBgmTrack(int trackId)
    {
        selectedBgmTrackId = trackId;
        bgmLoopStartEditing = false;
        PlayBgmTrack(trackId);
    }

    void RenderBGM()
    {
        float indent = ImGui.GetContentRegionAvail().X / 160;
        ImGui.Indent(indent);
        LoadBgmTable();
        Vector2 windowSize = ImGui.GetContentRegionAvail();
        float childHeight = windowSize.Y - ImGui.GetStyle().FramePadding.Y * 2;
        if (ImGui.BeginChild("Bgm", new Vector2(windowSize.X / 2f, childHeight)))
        {
            ImGui.Text("BGM Tracks");
            if (ImGui.BeginListBox("##MusicTrack", ImGui.GetContentRegionAvail()))
            {
                for (int mainIndex = 0; mainIndex < bgmTrackLabels.Length; mainIndex++)
                {
                    int mainTrackId = bgmMainTrackIds[mainIndex];
                    if (ImGui.Selectable(bgmTrackLabels[mainIndex], selectedBgmTrackId == mainTrackId))
                    {
                        SelectBgmTrack(mainTrackId);
                    }
                    // a track's duel variants show under it while it or one of them is selected
                    if (selectedBgmTrackId % 1000 != mainTrackId)
                    {
                        continue;
                    }
                    for (int variantIndex = 0; variantIndex < bgmVariantIds[mainIndex].Length; variantIndex++)
                    {
                        int variantTrackId = bgmVariantIds[mainIndex][variantIndex];
                        if (ImGui.Selectable(bgmVariantLabels[mainIndex][variantIndex], selectedBgmTrackId == variantTrackId))
                        {
                            SelectBgmTrack(variantTrackId);
                        }
                    }
                }
                ImGui.EndListBox();
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();

        if (ImGui.BeginChild("##PlayButtons", new Vector2(0, childHeight)))
        {
            ImGui.Text("Options");
            var buttonSize = new Vector2(windowSize.X / 3f, Math.Max(windowSize.Y / 8, ImGui.GetFrameHeight()));
            bool extractionRunning = extractionJob != null;
            if (ImGui.Button(playStopButton, buttonSize))
            {
                if (!isMusicPlaying)
                {
                    PlayBgmTrack(selectedBgmTrackId);
                }
                else
                {
                    StopMusic();
                }
            }
            RenderBgmTimeline(buttonSize.X);

            RenderExtractFormatChoice();
            if (extractionRunning)
            {
                ImGui.BeginDisabled();
            }
            if (ImGui.Button("Extract song", buttonSize))
            {
                StartExtraction(SoundExtractionScope.Sound);
            }
            if (ImGui.Button("Extract all", buttonSize))
            {
                StartExtraction(SoundExtractionScope.All);
            }
            if (extractionRunning)
            {
                ImGui.EndDisabled();
            }
            RenderExtractionStatus(buttonSize.X);

            ImGui.Separator();
            RenderBgmReplacing(buttonSize, extractionRunning);
            if (bgmTableError != null)
            {
                ImGui.PushTextWrapPos();
                ImGui.TextColored(ErrorTextColour, bgmTableError);
                ImGui.PopTextWrapPos();
            }
            RenderPlaybackError();
        }
        ImGui.EndChild();
        ImGui.Unindent(indent);
    }

    // Extract song: the selected track; Extract all: every main track and variant on the disc.
    void SetBgmExtractionTracks(SoundExtractionJob job, SoundExtractionScope scope)
    {
        var trackIds = new List<int>();
        if (scope == SoundExtractionScope.Sound)
        {
            trackIds.Add(selectedBgmTrackId);
        }
        else
        {
            for (int mainIndex = 0; mainIndex < bgmMainTrackIds.Length; mainIndex++)
            {
                if (fileloader.Find(BgmTrackTable.TrackFileName(bgmMainTrackIds[mainIndex])) != null)
                {
                    trackIds.Add(bgmMainTrackIds[mainIndex]);
                }
                trackIds.AddRange(bgmVariantIds[mainIndex]);
            }
        }
        job.BgmTrackIds = trackIds.ToArray();
        job.BgmTrackFileNames = new string[trackIds.Count];
        for (int trackIndex = 0; trackIndex < trackIds.Count; trackIndex++)
        {
            job.BgmTrackFileNames[trackIndex] = FileSystemSafeName(BgmTrackDisplayName(trackIds[trackIndex]));
        }
        job.BankCount = trackIds.Count;
    }

    // Worker thread: each track as the disc holds it (24 kHz stereo), into <folder>/BGM.
    static void ExtractBgmTracks(SoundExtractionJob job, string fileExtension)
    {
        CancellationToken cancellationToken = job.Cancellation.Token;
        string bgmFolder = job.BankFolder(-1);
        for (int trackIndex = 0; trackIndex < job.BgmTrackIds.Length; trackIndex++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            string trackFileName = BgmTrackTable.TrackFileName(job.BgmTrackIds[trackIndex]);
            try
            {
                FileLoader.FileEntry? trackFile = job.Disc.Find(trackFileName);
                if (trackFile == null)
                {
                    throw new FileNotFoundException("not on the disc");
                }
                Directory.CreateDirectory(bgmFolder);
                using var trackStream = new BgmTrackStream(job.Disc, trackFile);
                WriteAudio(trackStream, Path.Combine(bgmFolder, job.BgmTrackFileNames[trackIndex] + fileExtension), job.Format, BgmTrackTable.SampleRate);
                job.SoundsWritten++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException
                                                  or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                job.Failures.Add($"{trackFileName}: {exception.Message}");
            }
            Interlocked.Increment(ref job.BanksDone);
        }
    }

    // ---- BGM replacing ------------------------------------------------------------------------

    void RenderBgmReplacing(Vector2 buttonSize, bool extractionRunning)
    {
        if (ImGui.Button("Replace song...", buttonSize))
        {
            PickBgmReplacementFile();
        }
        if (droppedReplacementPath != null && pendingBgmReplacement == null)
        {
            StartBgmReplacement(selectedBgmTrackId, droppedReplacementPath);
        }
        RenderBgmLoopStart();

        bool selectedIsStaged = stagedBgmTracks.ContainsKey(selectedBgmTrackId);
        if (!selectedIsStaged)
        {
            ImGui.BeginDisabled();
        }
        if (ImGui.Button("Revert##bgm", buttonSize))
        {
            RevertBgmTrack(selectedBgmTrackId);
        }
        if (!selectedIsStaged)
        {
            ImGui.EndDisabled();
        }

        bool canSave = stagedBgmTracks.Count > 0 && !extractionRunning;
        if (!canSave)
        {
            ImGui.BeginDisabled();
        }
        if (ImGui.Button("Save to ISO##bgm", buttonSize))
        {
            ImGui.OpenPopup("Confirm BGM save");
        }
        if (!canSave)
        {
            ImGui.EndDisabled();
        }

        if (bgmReplaceError != null && pendingBgmReplacement == null)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(ErrorTextColour, bgmReplaceError);
            ImGui.PopTextWrapPos();
        }
        if (bgmSaveStatus.Length > 0)
        {
            ImGui.PushTextWrapPos();
            if (bgmSaveSucceeded)
            {
                ImGui.TextUnformatted(bgmSaveStatus);
            }
            else
            {
                ImGui.TextColored(ErrorTextColour, bgmSaveStatus);
            }
            foreach (string saveError in bgmSaveErrors)
            {
                ImGui.TextColored(ErrorTextColour, saveError);
            }
            ImGui.PopTextWrapPos();
        }

        RenderBgmReplacePopup();
        RenderBgmSaveConfirmPopup();
    }

    // The selected track's loop start, applied (staged) when an edit ends.
    void RenderBgmLoopStart()
    {
        BgmTrackEntry? entry = CurrentBgmEntry(selectedBgmTrackId);
        if (entry == null || entry.TableIndex < 0)
        {
            ImGui.TextDisabled("Loop start: not in the game's track table.");
            return;
        }
        if (!entry.Loops)
        {
            ImGui.TextDisabled("Plays once (no loop).");
            return;
        }
        float loopStartSeconds = entry.LoopStart / (float)BgmTrackTable.SampleRate;
        if (bgmLoopStartEditing)
        {
            loopStartSeconds = bgmLoopStartEditSeconds;
        }
        ImGui.SetNextItemWidth(GuiContext.FieldWidth(0.15f, 10f));
        bool changed = ImGui.InputFloat("Loop start (s)", ref loopStartSeconds, 0.1f, 1f, "%.3f");
        bgmLoopStartEditing = ImGui.IsItemActive();
        bgmLoopStartEditSeconds = loopStartSeconds;
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"Where the song jumps back to when it reaches its end ({entry.Frames / (float)BgmTrackTable.SampleRate:0.000} s).\nRounded down to 4 frames (1/6000 s), as the game does.");
        }
        if ((changed && !bgmLoopStartEditing) || ImGui.IsItemDeactivatedAfterEdit())
        {
            SetBgmLoopStart(selectedBgmTrackId, loopStartSeconds);
        }
    }

    // `seconds` as a loop start frame: rounded down to 4 frames, inside the song.
    static uint BgmLoopStartFrame(float seconds, uint frameCount)
    {
        long frame = (long)Math.Round(seconds * (double)BgmTrackTable.SampleRate);
        frame = Math.Clamp(frame, 0, Math.Max(0, (long)frameCount - BgmTrackTable.FrameStep));
        frame -= frame % BgmTrackTable.FrameStep;
        return (uint)frame;
    }

    void SetBgmLoopStart(int trackId, float seconds)
    {
        StagedBgmTrack? stagedTrack = StagedBgmTrackFor(trackId);
        if (stagedTrack == null)
        {
            return;
        }
        stagedTrack.Entry.LoopStart = BgmLoopStartFrame(seconds, stagedTrack.Entry.Frames);
        if (stagedTrack.AudioBytes == null && stagedTrack.Entry.LoopStart == stagedTrack.Original.LoopStart)
        {
            stagedBgmTracks.Remove(trackId);
        }
        bgmSaveStatus = string.Empty;
        bgmSaveErrors = new List<string>();
        RefreshBgmTrackLabels();
    }

    // The track's staged change, created (unchanged) from the disc when it has none; null on error.
    StagedBgmTrack? StagedBgmTrackFor(int trackId)
    {
        if (stagedBgmTracks.TryGetValue(trackId, out StagedBgmTrack? existingTrack))
        {
            return existingTrack;
        }
        BgmTrackEntry? original;
        FileLoader.FileEntry? trackFile;
        try
        {
            original = OriginalBgmEntry(trackId, out trackFile);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
        {
            bgmReplaceError = $"{BgmTrackTable.TrackFileName(trackId)}: {exception.Message}";
            return null;
        }
        if (original == null || trackFile == null)
        {
            bgmReplaceError = $"{BgmTrackTable.TrackFileName(trackId)} is not on the disc.";
            return null;
        }
        var stagedTrack = new StagedBgmTrack();
        stagedTrack.TrackId = trackId;
        stagedTrack.DisplayName = BgmTrackDisplayName(trackId);
        stagedTrack.IsoPath = trackFile.IsoPath;
        stagedTrack.FileSize = trackFile.Size;
        stagedTrack.Original = original;
        stagedTrack.Entry = original.Clone();
        stagedBgmTracks[trackId] = stagedTrack;
        return stagedTrack;
    }

    void RevertBgmTrack(int trackId)
    {
        StopMusic();
        stagedBgmTracks.Remove(trackId);
        bgmLoopStartEditing = false;
        RefreshBgmTrackLabels();
    }

    void PickBgmReplacementFile()
    {
        bgmReplaceError = null;
        DialogResult dialogResult = Dialog.FileOpen(FileDialogFilter.Of(ReplacementFileExtensions), Path.GetDirectoryName(Program.config.lastSoundReplacePath));
        if (!dialogResult.IsOk)
        {
            return;
        }
        Program.config.lastSoundReplacePath = dialogResult.Path;
        StartBgmReplacement(selectedBgmTrackId, dialogResult.Path);
    }

    // Reads `filePath` at 24 kHz stereo and opens the Replace song popup for `trackId`.
    void StartBgmReplacement(int trackId, string filePath)
    {
        droppedReplacementPath = null;
        bgmReplaceError = null;
        var pending = new PendingBgmReplacement();
        pending.TrackId = trackId;
        pending.TrackName = BgmTrackDisplayName(trackId);
        pending.FileName = Path.GetFileName(filePath);
        BgmTrackEntry? original;
        FileLoader.FileEntry? trackFile;
        try
        {
            original = OriginalBgmEntry(trackId, out trackFile);
            if (original == null || trackFile == null)
            {
                bgmReplaceError = $"{BgmTrackTable.TrackFileName(trackId)} is not on the disc.";
                return;
            }
            float[] sourceSamples = ReadSamples(filePath, BgmTrackTable.ChannelCount, out pending.SourceRate, out pending.SourceChannelCount);
            pending.SourceSeconds = sourceSamples.Length / (double)BgmTrackTable.ChannelCount / pending.SourceRate;
            pending.StereoSamples = Resample(sourceSamples, BgmTrackTable.ChannelCount, pending.SourceRate, BgmTrackTable.SampleRate);
            // the original's levels, from its audio on the disc
            byte[] originalAudioBytes = new byte[original.Size];
            fileloader.ReadAt(trackFile.ImageOffset + BgmTrackTable.TrackHeaderSize, originalAudioBytes, 0, originalAudioBytes.Length);
            ReadOnlySpan<short> originalSamples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(originalAudioBytes);
            MeasureLevels(originalSamples, out pending.OriginalPeakDecibels, out pending.OriginalRmsDecibels);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
                                              or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            bgmReplaceError = $"{pending.FileName}: {exception.Message}";
            return;
        }
        long roomFrames = (trackFile.Size - BgmTrackTable.TrackHeaderSize) / BgmTrackTable.BytesPerFrame;
        pending.RoomFrames = roomFrames - roomFrames % BgmTrackTable.FrameStep;
        long frameCount = pending.StereoSamples.Length / BgmTrackTable.ChannelCount;
        pending.FrameCount = frameCount - frameCount % BgmTrackTable.FrameStep;
        pending.Fits = pending.FrameCount > 0 && pending.FrameCount <= pending.RoomFrames;
        pending.Loops = original.Loops;
        pending.LoopStartSeconds = Math.Min(original.LoopStart, pending.FrameCount) / (float)BgmTrackTable.SampleRate;
        UpdatePendingBgmLevels(pending);
        pendingBgmReplacement = pending;
        ImGui.OpenPopup("Replace song");
    }

    static void UpdatePendingBgmLevels(PendingBgmReplacement pending)
    {
        float gain = MathF.Pow(10f, pending.GainDecibels / 20f);
        MeasureLevels(ToShorts(pending.StereoSamples, gain), out pending.NewPeakDecibels, out pending.NewRmsDecibels);
    }

    void RenderBgmReplacePopup()
    {
        if (!ImGui.BeginPopupModal("Replace song", ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }
        PendingBgmReplacement? pending = pendingBgmReplacement;
        if (pending == null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        double newSeconds = pending.FrameCount / (double)BgmTrackTable.SampleRate;
        double roomSeconds = pending.RoomFrames / (double)BgmTrackTable.SampleRate;
        ImGui.Text($"Replace {pending.TrackName}");
        ImGui.Text($"{pending.FileName}: {pending.SourceSeconds:0.00} s, {pending.SourceChannelCount} channel(s) at {pending.SourceRate:N0} Hz");
        ImGui.TextDisabled($"Written as {BgmTrackTable.SampleRate:N0} Hz stereo, as the game plays it.");

        ImGui.SetNextItemWidth(GuiContext.FieldWidth(0.15f, 10f));
        if (ImGui.InputFloat("Gain (dB)", ref pending.GainDecibels, 1f, 6f, "%.1f"))
        {
            pending.GainDecibels = Math.Clamp(pending.GainDecibels, LowestReplacementGainDecibels, HighestReplacementGainDecibels);
            UpdatePendingBgmLevels(pending);
        }
        ImGui.SameLine();
        bool canMatchLoudness = double.IsFinite(pending.OriginalRmsDecibels) && double.IsFinite(pending.NewRmsDecibels);
        if (!canMatchLoudness)
        {
            ImGui.BeginDisabled();
        }
        if (ImGui.Button("Match loudness"))
        {
            float matchedGainDecibels = pending.GainDecibels + (float)(pending.OriginalRmsDecibels - pending.NewRmsDecibels);
            pending.GainDecibels = Math.Clamp(matchedGainDecibels, LowestReplacementGainDecibels, HighestReplacementGainDecibels);
            UpdatePendingBgmLevels(pending);
        }
        if (!canMatchLoudness)
        {
            ImGui.EndDisabled();
        }
        GuiContext.HoveredTooltip("Sets Gain so the new song's RMS matches the original's.");
        ImGui.Text($"Original: peak {LevelText(pending.OriginalPeakDecibels)}, RMS {LevelText(pending.OriginalRmsDecibels)}");
        GuiContext.HoveredTooltip(PeakAndRmsTooltip);
        ImGui.Text($"New: peak {LevelText(pending.NewPeakDecibels)}, RMS {LevelText(pending.NewRmsDecibels)}");
        GuiContext.HoveredTooltip(PeakAndRmsTooltip);
        if (pending.NewPeakDecibels >= FullScaleDecibels)
        {
            ImGui.SameLine();
            ImGui.TextColored(ErrorTextColour, "(clipping)");
        }

        ImGui.Checkbox("Loop", ref pending.Loops);
        GuiContext.HoveredTooltip("On: at its end the song jumps back to Loop start. Off: it plays once.");
        if (pending.Loops)
        {
            ImGui.SetNextItemWidth(GuiContext.FieldWidth(0.15f, 10f));
            if (ImGui.InputFloat("Loop start (s)##replace", ref pending.LoopStartSeconds, 0.1f, 1f, "%.3f"))
            {
                pending.LoopStartSeconds = Math.Clamp(pending.LoopStartSeconds, 0f, (float)newSeconds);
            }
            ImGui.TextDisabled($"Loop end: the end of the song ({newSeconds:0.000} s).");
        }

        ImGui.Text($"The song is {newSeconds:0.00} s; the track has room for {roomSeconds:0.00} s.");
        if (!pending.Fits)
        {
            ImGui.TextColored(ErrorTextColour, $"Does not fit: cut the song to {roomSeconds:0.00} s or less.");
        }

        if (!pending.Fits)
        {
            ImGui.BeginDisabled();
        }
        if (ImGui.Button("Stage"))
        {
            StagePendingBgmReplacement(pending);
            pendingBgmReplacement = null;
            ImGui.CloseCurrentPopup();
        }
        if (!pending.Fits)
        {
            ImGui.EndDisabled();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            pendingBgmReplacement = null;
            bgmReplaceError = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    void StagePendingBgmReplacement(PendingBgmReplacement pending)
    {
        StagedBgmTrack? stagedTrack = StagedBgmTrackFor(pending.TrackId);
        if (stagedTrack == null)
        {
            return;
        }
        StopMusic();
        float gain = MathF.Pow(10f, pending.GainDecibels / 20f);
        short[] samples = ToShorts(pending.StereoSamples, gain);
        int audioByteCount = (int)pending.FrameCount * BgmTrackTable.BytesPerFrame;
        byte[] audioBytes = new byte[audioByteCount];
        Buffer.BlockCopy(samples, 0, audioBytes, 0, audioByteCount);
        stagedTrack.AudioBytes = audioBytes;
        stagedTrack.Entry.Size = (uint)audioByteCount;
        if (pending.Loops)
        {
            stagedTrack.Entry.LoopStart = BgmLoopStartFrame(pending.LoopStartSeconds, stagedTrack.Entry.Frames);
            stagedTrack.Entry.LoopEnd = stagedTrack.Entry.Frames;
        }
        else
        {
            stagedTrack.Entry.LoopStart = 0;
            stagedTrack.Entry.LoopEnd = 0;
        }
        bgmLoopStartEditing = false;
        bgmSaveStatus = string.Empty;
        bgmSaveErrors = new List<string>();
        RefreshBgmTrackLabels();
    }

    void RenderBgmSaveConfirmPopup()
    {
        bool open = true;
        if (!ImGui.BeginPopupModal("Confirm BGM save", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }
        ImGui.Text($"Write {stagedBgmTracks.Count} staged track(s) into the ISO in place?");
        foreach (StagedBgmTrack stagedTrack in stagedBgmTracks.Values)
        {
            string change = "loop start";
            if (stagedTrack.AudioBytes != null)
            {
                change = "new song";
            }
            ImGui.BulletText($"{stagedTrack.DisplayName}: {change}");
        }
        ImGui.TextDisabled("Nothing is written if any track does not fit.");
        if (ImGui.Button("Yes"))
        {
            ImGui.CloseCurrentPopup();
            SaveStagedBgmTracks();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    /* One ChangeSet for every staged track: its PCMLIST.BIN entry (what the game reads), the first
       16 bytes of its header (the copy tools read), and for a new song the whole file: header,
       audio, zeros to the file's end (the file keeps its size). */
    void SaveStagedBgmTracks()
    {
        StopMusic();
        bgmSaveErrors = new List<string>();
        var changes = new ChangeSet();
        try
        {
            foreach (StagedBgmTrack stagedTrack in stagedBgmTracks.Values)
            {
                if (stagedTrack.Entry.TableIndex >= 0)
                {
                    changes.Add(BgmTrackTable.TablePath, (long)stagedTrack.Entry.TableIndex * BgmTrackTable.EntrySize,
                        BgmTrackTable.EntryBytes(stagedTrack.Entry), stagedTrack.DisplayName + " table entry");
                }
                byte[] headerStart = BgmTrackTable.TrackHeaderStart(stagedTrack.Entry);
                if (stagedTrack.AudioBytes == null)
                {
                    changes.Add(stagedTrack.IsoPath, 0, headerStart, stagedTrack.DisplayName + " header");
                }
                else
                {
                    byte[] fileBytes = new byte[stagedTrack.FileSize];
                    Array.Copy(headerStart, fileBytes, headerStart.Length);
                    Array.Copy(stagedTrack.AudioBytes, 0, fileBytes, BgmTrackTable.TrackHeaderSize, stagedTrack.AudioBytes.Length);
                    changes.Add(stagedTrack.IsoPath, 0, fileBytes, stagedTrack.DisplayName);
                }
            }
            ChangeSet.Plan plan = changes.Write(fileloader);
            bgmSaveSucceeded = plan.Ok;
            if (plan.Ok)
            {
                bgmSaveStatus = $"Saved {stagedBgmTracks.Count} track(s): {plan.Writes.Count} writes ({plan.BytesToWrite:N0} B), {plan.NoOps.Count} unchanged.";
                stagedBgmTracks.Clear();
                // the table is read again from the disc
                bgmTableLoadAttempted = false;
            }
            else
            {
                bgmSaveStatus = "Nothing written:";
                bgmSaveErrors.AddRange(plan.Errors);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            bgmSaveSucceeded = false;
            bgmSaveStatus = "Nothing written: " + exception.Message;
        }
        RefreshBgmTrackLabels();
    }
}

/* One BGM track streamed straight from the disc image, chunk by chunk as NAudio asks: each Read
   copies the next bytes of the track's extent into NAudio's own buffer through FileLoader.ReadAt
   (locked), so nothing is held but the position and nothing is allocated per Read.

   SOUND/BGM/0x/*.PCM: a 0x800-byte header { u32 dataSize, u32 frameCount, u32 loopStartFrame,
   u32 loopEndFrame } (SOUND/BGM/PCMLIST.BIN lists the same size and loop points per track id),
   then 16-bit stereo samples interleaved L/R per sample at 24 kHz: _iDecodeLPCM @ 0x10A780 (SLUS)
   writes every source sample twice into the SPU2's 48 kHz AutoDMA input. Played once; the game's
   loop points are not used. A staged song plays through the same class from its bytes in memory.
   Position may be set from the UI thread (the timeline) while NAudio's thread reads: both lock. */
public sealed class BgmTrackStream : WaveStream
{
    const int HeaderSize = BgmTrackTable.TrackHeaderSize;
    const int SampleRate = BgmTrackTable.SampleRate;
    const int ChannelCount = BgmTrackTable.ChannelCount;
    const int BytesPerFrame = BgmTrackTable.BytesPerFrame;

    readonly FileLoader? disc;
    readonly long dataImageOffset;
    // A staged song's audio, read from memory instead of the disc (null for a disc track).
    readonly byte[]? audioBytes;
    readonly long dataByteCount;
    readonly WaveFormat waveFormat = new WaveFormat(SampleRate, 16, ChannelCount);
    readonly object positionLock = new object();
    long position;

    public BgmTrackStream(FileLoader disc, FileLoader.FileEntry trackFile)
    {
        if (trackFile.Size < HeaderSize)
        {
            throw new InvalidDataException($"{trackFile.Size} bytes, shorter than the {HeaderSize}-byte header");
        }
        var headerBytes = new byte[8];
        disc.ReadAt(trackFile.ImageOffset, headerBytes, 0, headerBytes.Length);
        uint dataSize = ReadU32(headerBytes, 0);
        uint frameCount = ReadU32(headerBytes, 4);
        if (dataSize > trackFile.Size - HeaderSize || dataSize != (long)frameCount * BytesPerFrame)
        {
            throw new InvalidDataException($"header says {dataSize} bytes in {frameCount} frames; the file holds {trackFile.Size - HeaderSize} bytes after the header");
        }
        this.disc = disc;
        dataImageOffset = trackFile.ImageOffset + HeaderSize;
        dataByteCount = dataSize;
    }

    // A staged song: 16-bit stereo at 24 kHz, as written to the disc.
    public BgmTrackStream(byte[] audioBytes)
    {
        this.audioBytes = audioBytes;
        dataByteCount = audioBytes.Length - audioBytes.Length % BytesPerFrame;
    }

    public override WaveFormat WaveFormat => waveFormat;

    public override long Length => dataByteCount;

    public override long Position
    {
        get
        {
            lock (positionLock)
            {
                return position;
            }
        }
        set
        {
            lock (positionLock)
            {
                position = Math.Clamp(value - value % BytesPerFrame, 0, dataByteCount);
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (positionLock)
        {
            long bytesLeft = dataByteCount - position;
            int bytesToRead = (int)Math.Min(count - count % BytesPerFrame, bytesLeft);
            if (bytesToRead <= 0)
            {
                return 0;
            }
            if (audioBytes != null)
            {
                Buffer.BlockCopy(audioBytes, (int)position, buffer, offset, bytesToRead);
            }
            else
            {
                disc!.ReadAt(dataImageOffset + position, buffer, offset, bytesToRead);
            }
            position += bytesToRead;
            return bytesToRead;
        }
    }
}

enum CurrentMusicMode
{
    BGM,
    Card,
    Monster,
    System,
}

enum SoundExportFormat
{
    Wav,
    Mp3,
}

enum SoundExtractionScope
{
    Sound,
    Bank,
    All,
}

// One extraction run: what to write, set on the UI thread; progress and results, set by the worker.
sealed class SoundExtractionJob
{
    public FileLoader Disc = FileLoader.Instance;
    public CurrentMusicMode Mode;
    public SoundExportFormat Format;
    public string OutputRoot = string.Empty;
    public int[] Cards = [];                    // monster cards, or { -1 } for the System/Card bank
    public string[] MonsterFolderNames = [];    // per card: "<id> <name>", safe for the file system
    public int SingleEntryIndex = -1;           // >= 0: only this sound
    public int[] BgmTrackIds = [];              // BGM: the tracks to write (the progress counts tracks)
    public string[] BgmTrackFileNames = [];     // BGM: per track, the output name without extension
    public int BankCount;
    public int BanksDone;                       // Interlocked by the worker, Volatile.Read by the UI
    public int SoundsWritten;                   // read by the UI only after the task completed
    public List<string> Failures = new List<string>();
    public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
    public Task Task = Task.CompletedTask;

    public string BankFolder(int card)
    {
        switch (Mode)
        {
            case CurrentMusicMode.System:
                return Path.Combine(OutputRoot, "System");
            case CurrentMusicMode.Card:
                return Path.Combine(OutputRoot, "Card");
            case CurrentMusicMode.Monster:
                return Path.Combine(OutputRoot, "Monster", MonsterFolderNames[card]);
            case CurrentMusicMode.BGM:
                return Path.Combine(OutputRoot, "BGM");
            default:
                throw new ArgumentOutOfRangeException(nameof(Mode), Mode, null);
        }
    }
}

// One bank with replaced sounds, as Save to ISO will write it.
sealed class StagedSoundBank
{
    public CurrentMusicMode Mode;
    public int MonsterCard = -1;
    public string DisplayName = string.Empty;
    public string FilePath = string.Empty;
    // Where the bank starts in FilePath (the SEGRP.MRG slot; 0 for a .GRP file).
    public long FileOffset;
    public int SampleDataLimit;
    // The bank as it is on the disc; the replacements are applied to it.
    public SoundBank OriginalBank = new SoundBank();
    public SoundReplacement?[] Replacements = [];
    // Header + sample data to write, and the same parsed for Play.
    public byte[] BankBytes = [];
    public SoundBank PreviewBank = new SoundBank();

    // Builds the bank with `replacements`; throws (and keeps the previous ones) if it does not fit.
    public void SetReplacements(SoundReplacement?[] replacements)
    {
        byte[] bankBytes = OriginalBank.Build(replacements, SampleDataLimit);
        PreviewBank = SoundBank.Parse(bankBytes.AsSpan(0, SoundBank.HeaderSize).ToArray(), bankBytes.AsSpan(SoundBank.HeaderSize).ToArray());
        BankBytes = bankBytes;
        Replacements = replacements;
    }

    public int ReplacedCount()
    {
        int replacedCount = 0;
        foreach (SoundReplacement? replacement in Replacements)
        {
            if (replacement != null)
            {
                replacedCount++;
            }
        }
        return replacedCount;
    }
}

// The file picked to replace one sound, and what the rate chosen in the popup gives.
sealed class PendingSoundReplacement
{
    public int EntryIndex;
    public string FileName = string.Empty;
    public float[] MonoSamples = [];
    public int SourceRate;
    // The rate field; PlayRate is what the nearest pitch really plays at.
    public int Rate;
    public ushort Pitch;
    public int PlayRate;
    public bool Loops;
    // The entry's volume byte (m_cVolumeL) to write, 0-127.
    public int Volume;
    // Gain applied to the file's samples before encoding.
    public float GainDecibels;
    // Levels in dBFS (MeasureLevels): the entry's sound on the disc, and the new one as it will be encoded.
    public double OriginalPeakDecibels;
    public double OriginalRmsDecibels;
    public double NewPeakDecibels;
    public double NewRmsDecibels;
    // The bank's sample data without this sound, the room left for it, and its size at PlayRate.
    public int OtherSamplesByteCount;
    public int RoomByteCount;
    public int SoundByteCount;
    public bool Fits;
    // When it does not fit: the highest rate that does (0 = none).
    public int FittingRate;
}

// One BGM track with a new song and/or loop start, as Save to ISO will write it.
sealed class StagedBgmTrack
{
    public int TrackId;
    public string DisplayName = string.Empty;
    public string IsoPath = string.Empty;
    // The file's size on the disc: a new song fills it (header, audio, zeros) and never grows it.
    public long FileSize;
    // The values as on the disc, and the ones to write (TableIndex -1: the track is not in the table).
    public BgmTrackEntry Original = new BgmTrackEntry();
    public BgmTrackEntry Entry = new BgmTrackEntry();
    // The new song, 16-bit stereo at 24 kHz; null when only the loop start changed.
    public byte[]? AudioBytes;
}

// The file picked to replace one BGM track, while the Replace song popup is open.
sealed class PendingBgmReplacement
{
    public int TrackId;
    public string TrackName = string.Empty;
    public string FileName = string.Empty;
    public int SourceRate;
    public int SourceChannelCount;
    public double SourceSeconds;
    // The file at 24 kHz stereo, interleaved, before Gain.
    public float[] StereoSamples = [];
    public float GainDecibels;
    // Levels in dBFS (MeasureLevels): the track on the disc, and the new song with Gain.
    public double OriginalPeakDecibels;
    public double OriginalRmsDecibels;
    public double NewPeakDecibels;
    public double NewRmsDecibels;
    public bool Loops;
    public float LoopStartSeconds;
    // Stereo frames: the new song's (rounded down to 4) and the most the track's file holds.
    public long FrameCount;
    public long RoomFrames;
    public bool Fits;
}
