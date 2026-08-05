using System.Drawing;
using System.Numerics;
using DiscUtils.Iso9660;
using ImGuiNET;
using NAudio.MediaFoundation;
using NAudio.Wave;
namespace DotrAssetModder;

public class MusicExtractorWindow
{
    WaveOutEvent waveOut;
    Mp3FileReader mp3Reader;
    bool isMusicPlaying = false;
    FileLoader fileloader = FileLoader.Instance;

    string MusicDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Music");
    string playStopButton = "Play";
    int currentBGMIndex;

    ImFontPtr font;

    CurrentMusicMode currentMode = CurrentMusicMode.BGM;

    string bgmPath;
    string pcmPath;

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
        waveOut = new WaveOutEvent();
        waveOut.Volume = 0.1f;
        font = FontManager.GetFont(FontManager.FontFamily.NotoSansJP, 32);

        bgmPath = Path.Combine(MusicDirectory, "BGM");
        pcmPath = Path.Combine(bgmPath, "Original_PCM");

    }

    public void Render()
    {

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
                ImGui.Text("Under Construction");
                currentMode = CurrentMusicMode.Card;
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Monster Sounds"))
            {
                ImGui.Text("Under Construction");
                currentMode = CurrentMusicMode.Monster;
                ImGui.EndTabItem();

            }
            if (ImGui.BeginTabItem("System Sounds"))
            {
                ImGui.Text("Under Construction");
                currentMode = CurrentMusicMode.System;
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
        if (currentMode != oldCurrentMusicMode)
        {
            StopMusic();
        }

    }

    void RenderBGM()
    {
        ImGui.Indent(ImGui.GetContentRegionAvail().X / 160);
        Vector2 windowSize = ImGui.GetContentRegionAvail();
        if (ImGui.BeginChild("Bgm", new Vector2(windowSize.X / 2f, windowSize.Y - ImGui.GetStyle().FramePadding.Y * 2)))
        {
            ImGui.Text("BGM Tracks");
            if (ImGui.ListBox("##MusicTrack", ref currentBGMIndex, BgmTracks, BgmTracks.Length, (int)((ImGui.GetContentRegionAvail().Y / ImGui.GetTextLineHeightWithSpacing() - 1))))
            {
                PlaySelectedTrack(currentMode, currentBGMIndex);
            }
            ImGui.EndChild();
        }

        ImGui.SameLine();

        if (ImGui.BeginChild("##PlayButtons", new Vector2(0, windowSize.Y - ImGui.GetStyle().FramePadding.Y * 2)))
        {
            ImGui.Text("Options");
            if (ImGui.Button(playStopButton, new Vector2(windowSize.X / 3f, windowSize.Y / 8)))
            {
                if (!isMusicPlaying)
                {
                    PlaySelectedTrack(currentMode, currentBGMIndex);
                }
                else
                {
                    StopMusic();
                }
            }
            if (ImGui.Button("Extract Music", new Vector2(windowSize.X / 3f, windowSize.Y / 8)))
            {
                Task.Run(async () => await StartMusicTask());
            }
        }
        ImGui.EndChild();
    }


    public async Task StartMusicTask()
    {
        await Task.Run(() => GetMusicFiles());
    }

    Task GetMusicFiles()
    {

        if (!Directory.Exists(MusicDirectory))
        {
            Directory.CreateDirectory(MusicDirectory);
        }

        MediaFoundationApi.Startup();
        CDReader isoFile = new CDReader(fileloader.fileStream, true);
        // Get the file from inside the ISO
        switch (currentMode)
        {

            case CurrentMusicMode.BGM:
                return ExtractBgm(isoFile);
            case CurrentMusicMode.Card:
                return ExtractCard(isoFile);
            case CurrentMusicMode.Monster:
                return ExtractMonster(isoFile);
                break;
            case CurrentMusicMode.System:
                return ExtractSystem(isoFile);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

    }


    Task ExtractSystem(CDReader isoFile)
    {
        throw new NotImplementedException();
    }

    Task ExtractMonster(CDReader isoFile)
    {
        throw new NotImplementedException();
    }

    Task ExtractCard(CDReader isoFile)
    {
        throw new NotImplementedException();
    }

    Task ExtractBgm(CDReader isoFile)
    {
        for (int i = 0; i < 5; i++)
        {
            var fileEntry = isoFile.GetFiles($"SOUND\\BGM\\0{i}");
            if (fileEntry == null)
            {
                if (ImGui.BeginPopup("resultFailure"))
                {
                    ImGui.Text("File not found in ISO");
                    if (ImGui.Button("Close"))
                    {
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.EndPopup();
                }
                return Task.CompletedTask;
            }


            foreach (var file in fileEntry)
            {
                if (!Path.GetFileName(file).StartsWith("00"))
                    continue;
                string pathFileName = Path.GetFileName(file);

                if (!Directory.Exists(bgmPath))
                {
                    Directory.CreateDirectory(bgmPath);
                }
                if (!Directory.Exists(pcmPath))
                {
                    Directory.CreateDirectory(pcmPath);
                }
                string fileName = pathFileName.Substring(0, pathFileName.Length - 2);
                string extractedFilePath = Path.Combine(pcmPath, fileName);
                RawSourceWaveStream pcmReader;
                using (FileStream fileStream = File.OpenWrite(extractedFilePath))
                {
                    var pcm = isoFile.OpenFile(file, FileMode.Open);
                    pcm.CopyTo(fileStream);

                }
                using (FileStream fileStream = File.OpenRead(extractedFilePath))
                {
                    string mp3FilePath = Path.Combine(bgmPath, Path.GetFileNameWithoutExtension(file) + ".mp3");
                    var desiredBitRate = 0; // ask for lowest available bitrate
                    pcmReader = new RawSourceWaveStream(fileStream, new WaveFormat(48000, 16, 1));
                    MediaFoundationEncoder.EncodeToMp3(pcmReader, mp3FilePath, desiredBitRate);
                }
            }
        }

        CleanUpPcms();
        MediaFoundationApi.Shutdown();
        return Task.CompletedTask;
    }

    void CleanUpPcms()
    {
        string[] pcmFiles = Directory.GetFiles(pcmPath, "*.pcm");
        foreach (string pcmFile in pcmFiles)
        {
            File.Delete(pcmFile);
        }
    }

    void PlaySelectedTrack(CurrentMusicMode currentMode, int trackNumber)
    {
        StopMusic();
        string path = String.Empty;
        switch (currentMode)
        {

            case CurrentMusicMode.BGM:
                path = bgmPath;
                break;
            case CurrentMusicMode.Card:
                break;
            case CurrentMusicMode.Monster:
                break;
            case CurrentMusicMode.System:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(currentMode), currentMode, null);
        }
        string selectedMp3FilePath = Path.Combine(path, "00" + BgmTracks[trackNumber].Substring(0, 2) + ".mp3");
        if (File.Exists(selectedMp3FilePath))
        {
            mp3Reader = new Mp3FileReader(selectedMp3FilePath);
            playStopButton = "Stop";


            // Play or stop the MP3 based on the current state
            waveOut.Init(mp3Reader);
            waveOut.Play();
            isMusicPlaying = true;
        }
    }

    public void StopMusic()
    {
        if (waveOut.PlaybackState != PlaybackState.Stopped)
        {
            waveOut.Stop();
            mp3Reader?.Dispose();
            isMusicPlaying = false;
            playStopButton = "Start";
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