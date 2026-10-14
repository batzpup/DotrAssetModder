using System.Diagnostics;
using System.Numerics;
using DiscUtils.Iso9660;
using ImGuiNET;
using NativeFileDialogSharp;
namespace DotrAssetModder;

public class ExtractorWindow
{
    string pathTemplate = "Current path: ";
    string dataDestinationPath = string.Empty;
    string dataWordlistPath = string.Empty;
    int currentDataFileIndex = 0;

    CurrentExtractor CurrentExtractor;
    int currentFolderIndex;
    ExtractionJob? activeJob;
    ExtractResult lastExtractResult;
    string? jobError;

    bool openResultsNext;
    bool openUnsupportedNext;

    MusicExtractorWindow musicExtractor = new MusicExtractorWindow();
    MovieExtractorWindow movieExtractor = new MovieExtractorWindow();

    public void Render()
    {
        ImGui.PushFont(GuiContext.MainFont);
        ImGui.Text(pathTemplate + FileLoader.Instance.CurrentIsoPath);
        ImGui.SameLine();

        if (ImGui.Button("Load ISO"))
        {
            DialogResult isoDialogResult;
            isoDialogResult = Dialog.FileOpen(FileDialogFilter.Of("iso"),Path.GetDirectoryName(Program.config.lastIsoPath));
            if (isoDialogResult.IsOk)
            {
                FileLoader.Instance.OpenIso(isoDialogResult.Path);
                Program.config.lastIsoPath = isoDialogResult.Path;
            }
            else
            {
                ImGui.OpenPopup("Error");
            }
        }
        if (FileLoader.Instance.IsIsoLoaded)
        {
            ImGui.Text("Folder To Extract from");
            if (ImGui.Combo("##Folder", ref currentFolderIndex, FileLoader.Instance.folderNames, FileLoader.Instance.folderNames.Length))
            {
                CurrentExtractor = (CurrentExtractor)currentFolderIndex;
            }

            ImGui.Text("Current extractor: " + CurrentExtractor);
            ImGui.Separator();
            switch (CurrentExtractor)
            {
                case CurrentExtractor.Data:
                    RenderDataExtractor();
                    break;
                case CurrentExtractor.Movie:
                    movieExtractor.Render();
                    break;
                case CurrentExtractor.Sound:
                    musicExtractor.Render();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            if (CurrentExtractor != CurrentExtractor.Sound)
            {
                musicExtractor.StopMusic();
            }
            if (ImGui.BeginPopupModal("No Destination", ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.Text("Please select a destination folder");

                if (ImGui.Button("Close"))
                {
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }
            if (ImGui.BeginPopupModal("UnsupportedFile", ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.Text("Currently unsupported file type");

                if (ImGui.Button("Close"))
                {
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }
            if (ImGui.BeginPopupModal("Results", ImGuiWindowFlags.AlwaysAutoResize))
            {
                if (jobError != null)
                {
                    ImGui.Text("Extraction failed:");
                    ImGui.TextWrapped(jobError);
                }
                else
                {
                    ImGui.Text($"Results: {lastExtractResult.Total} {lastExtractResult.Kind.ToString()} files found \nSucceed: {lastExtractResult.Written}\nFailed: {lastExtractResult.Failed}");
                    if (lastExtractResult.Failures.Count > 0)
                    {
                        ImGui.Separator();
                        ImGui.Text("Failures:");
                        if (ImGui.BeginChild("failures", new System.Numerics.Vector2(420, 160)))
                        {
                            foreach (var failure in lastExtractResult.Failures)
                                ImGui.TextWrapped($"slot {failure.Slot} ({failure.Name}): {failure.Reason}");
                        }
                        ImGui.EndChild();
                    }
                }

                if (ImGui.Button("Close"))
                {
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }
        }
        ImGui.PopFont();
    }

    void StartDataExtraction(string[] fileList, int index)
    {
        jobError = null;
        string currentFile = fileList[index];
        byte[] fileBytes = FileLoader.Instance.GetFileBytesFromDisk(currentFile);
        string fileNameStem = IsoPaths.FileNameWithoutExtension(currentFile);
        IList<string>? wordlistNames = LoadWordlist();
        activeJob = ExtractionJob.Start(fileBytes, dataDestinationPath, fileNameStem, wordlistNames);
    }

    IList<string>? LoadWordlist()
    {
        if (string.IsNullOrEmpty(dataWordlistPath) || !File.Exists(dataWordlistPath))
        {
            return null;
        }

        try
        {
            var lines = File.ReadAllLines(dataWordlistPath);
            var trimmed = new List<string>(lines.Length);
            foreach (var line in lines)
            {
                trimmed.Add(line.Trim());
            }

            return trimmed;
        }
        catch
        {
            return null;
        }
    }

    void DrawProgressModal()
    {
        if (activeJob == null) return;

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new System.Numerics.Vector2(0.5f, 0.5f));

        bool open = true;
        if (ImGui.BeginPopupModal("Extracting", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var progress = activeJob.Progress;
            int totalCount = progress.Total;
            int doneCount = progress.Done;
            float fraction = progress.Fraction;

            ImGui.Text(totalCount > 0 ? $"Extracting {doneCount}/{totalCount}" : "Preparing...");
            ImGui.ProgressBar(fraction, new System.Numerics.Vector2(300, 0));

            if (progress.IsCancellationRequested)
            {
                ImGui.TextDisabled("Cancelling...");
            }
            
            if (ImGui.Button("Cancel"))
            {
                activeJob.RequestCancel();
            }
            
            if (activeJob.IsCompleted)
            {
                if (activeJob.IsFaulted)
                {
                    jobError = activeJob.Error?.Message ?? "Unknown error";
                    openResultsNext = true;
                }
                else
                {
                    lastExtractResult = activeJob.Result ?? new ExtractResult(AssetKind.Unknown, 0, 0, 0);
                    if (lastExtractResult.Kind == AssetKind.Unknown)
                        openUnsupportedNext = true;
                    else
                        openResultsNext = true;
                }
                activeJob = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }


    void RenderDataExtractor()
    {
        ImGui.Combo("##data", ref currentDataFileIndex, FileLoader.Instance.iso_data_files_supported, FileLoader.Instance.iso_data_files_supported.Length);

        if (ImGui.Button("Destination Folder"))
        {
            DialogResult dialogResult = Dialog.FolderPicker(Program.config.lastDataExtractPath);
            if (dialogResult.IsOk)
            {
                dataDestinationPath = dialogResult.Path;
                Program.config.lastDataExtractPath = dataDestinationPath;
            }
            else
            {
                ImGui.OpenPopup("Error");
            }
        }
        ImGui.SameLine();
        ImGui.Text(dataDestinationPath);

        if (ImGui.Button("Load Wordlist"))
        {
            DialogResult dialogResult = Dialog.FileOpen(FileDialogFilter.Of("txt"),Path.GetDirectoryName(Program.config.lastWordlistPath));

            if (dialogResult.IsOk)
            {
                dataWordlistPath = dialogResult.Path;
                Program.config.lastWordlistPath = dataWordlistPath;
            }
            else
            {
                ImGui.OpenPopup("Error");
            }


        }
        ImGui.SameLine();
        ImGui.Text(dataWordlistPath);

        bool busy = activeJob != null;
        if (busy) ImGui.BeginDisabled();
        if (ImGui.Button("Extract"))
        {
            if (dataDestinationPath != string.Empty && Directory.Exists(dataDestinationPath))
            {
                StartDataExtraction(FileLoader.Instance.iso_data_files_supported, currentDataFileIndex);
                ImGui.OpenPopup("Extracting");
            }
            else
            {
                ImGui.OpenPopup("No Destination");
            }

        }
        if (busy) ImGui.EndDisabled();


        DrawProgressModal();
        if (openResultsNext)
        {
            openResultsNext = false;
            ImGui.OpenPopup("Results");
        }

        if (openUnsupportedNext)
        {
            openUnsupportedNext = false;
            ImGui.OpenPopup("UnsupportedFile");
        }

        if (ImGui.BeginPopupModal("Error", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Please select a valid file/folder");

            if (ImGui.Button("Close"))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }


    }
}

enum CurrentExtractor
{
    Data,
    Movie,
    Sound
}