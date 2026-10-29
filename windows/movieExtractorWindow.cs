using System.Numerics;
using ImGuiNET;
using NativeFileDialogSharp;
using rlImGui_cs;
namespace DotrAssetModder;

/* The MOVIE folder: extract a movie to MP4, or replace it with any video file (encoded to fit the
   movie's own file on the disc), staged until Save to ISO. Conversions run one at a time on a
   worker thread (MovieConversion). The preview (MoviePreview) plays the disc movie or the staged
   replacement; it loads on the first Play. */
public class MovieExtractorWindow
{
    const string ReplacementFileExtensions = "mp4,mkv,avi,mov,webm,wmv,mpg,mpeg,m4v,flv";
    static readonly Vector4 ErrorTextColour = new Vector4(1f, 0.4f, 0.4f, 1f);

    FileLoader fileloader = FileLoader.Instance;
    int currentMovieIndex = 0;
    MovieConversion? conversion;
    // What the running (or last) conversion is: extracting or replacing.
    bool conversionReplaces;
    string conversionResult = string.Empty;
    string? conversionError;
    // The movie a running replacement is for, and the file it was made from.
    string pendingReplacementPath = string.Empty;
    string pendingReplacementSource = string.Empty;

    // Replaced movies by ISO path, kept until Save to ISO writes them or the ISO closes.
    readonly Dictionary<string, StagedMovie> stagedMovies = new Dictionary<string, StagedMovie>();
    string saveStatus = string.Empty;
    bool saveSucceeded;
    List<string> saveErrors = new List<string>();
    string[] movieLabels = Array.Empty<string>();
    string[]? labelledMovieNames;
    int labelledStagedCount = -1;

    // Preview of the selected movie: from the disc, or its staged replacement.
    MoviePreview? preview;
    Task<MoviePreview>? previewLoad;
    string previewMoviePath = string.Empty;
    byte[]? previewSourceBytes;
    bool previewReplacement;
    string? previewError;
    // The time label, rebuilt only when the shown second changes.
    string previewTimeLabel = string.Empty;
    long previewTimeLabelSecond = -1;
    // Kept across movies and in the config across runs.
    int previewVolumePercent = Math.Clamp(Program.config.movieVolumePercent, 0, 100);

    public MovieExtractorWindow()
    {
        fileloader.OnIsoClosing += OnIsoClosing;
    }

    public void Render()
    {
        string[] movieNames = fileloader.MovieNames ?? Array.Empty<string>();
        if (movieNames.Length == 0)
        {
            ImGui.Text("No movies on this ISO.");
            return;
        }
        currentMovieIndex = Math.Clamp(currentMovieIndex, 0, movieNames.Length - 1);
        UpdateMovieLabels(movieNames);
        FinishConversionIfDone();

        Vector2 windowSize = ImGui.GetContentRegionAvail();
        var buttonSize = new Vector2(windowSize.X / 3f, Math.Max(windowSize.Y / 8, ImGui.GetFrameHeight()));
        bool conversionRunning = conversion != null;
        string moviePath = movieNames[currentMovieIndex];

        if (ImGui.BeginChild("##MovieControls", new Vector2(windowSize.X / 3f + ImGui.GetStyle().WindowPadding.X * 2, 0)))
        {
            RenderControls(moviePath, buttonSize, conversionRunning);
        }
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("##MoviePreview", Vector2.Zero))
        {
            RenderPreview(moviePath);
        }
        ImGui.EndChild();
    }

    void RenderControls(string moviePath, Vector2 buttonSize, bool conversionRunning)
    {
        ImGui.Text("Movies");
        if (conversionRunning)
        {
            ImGui.BeginDisabled();
        }
        ImGui.Combo("##Movie List", ref currentMovieIndex, movieLabels, movieLabels.Length);
        ImGui.Spacing();
        if (ImGui.Button("Extract Movie (MP4)", buttonSize))
        {
            StartExtraction(moviePath);
        }
        if (ImGui.Button("Replace movie...", buttonSize))
        {
            StartReplacement(moviePath);
        }
        bool selectedIsReplaced = stagedMovies.ContainsKey(moviePath);
        if (!selectedIsReplaced)
        {
            ImGui.BeginDisabled();
        }
        if (ImGui.Button("Revert", buttonSize))
        {
            stagedMovies.Remove(moviePath);
        }
        if (!selectedIsReplaced)
        {
            ImGui.EndDisabled();
        }
        bool canSave = stagedMovies.Count > 0;
        if (!canSave)
        {
            ImGui.BeginDisabled();
        }
        if (ImGui.Button("Save to ISO", buttonSize))
        {
            ImGui.OpenPopup("Confirm movie save");
        }
        if (!canSave)
        {
            ImGui.EndDisabled();
        }
        if (conversionRunning)
        {
            ImGui.EndDisabled();
        }
        // Inside ##MovieControls, as OpenPopup is: the popup ID includes the child's ID stack.
        RenderSaveConfirmPopup();

        // Looked up again: Revert above may have just removed it.
        if (stagedMovies.TryGetValue(moviePath, out StagedMovie? stagedMovie))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted($"Replaced with {stagedMovie.SourceName}: {stagedMovie.Summary}");
            ImGui.PopTextWrapPos();
        }
        RenderConversionStatus(buttonSize.X);
        RenderSaveStatus();
    }

    // ---- preview ------------------------------------------------------------------------------

    void RenderPreview(string moviePath)
    {
        ImGui.Text("Preview");
        bool selectedIsReplaced = stagedMovies.ContainsKey(moviePath);
        if (selectedIsReplaced)
        {
            if (ImGui.RadioButton("Disc", !previewReplacement))
            {
                previewReplacement = false;
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Replacement", previewReplacement))
            {
                previewReplacement = true;
            }
        }
        else
        {
            previewReplacement = false;
        }
        byte[]? sourceBytes = null;
        if (previewReplacement)
        {
            sourceBytes = stagedMovies[moviePath].FileBytes;
        }
        // A selected movie (or source) loads at once and waits, paused, on its first picture.
        if (previewMoviePath != moviePath || !ReferenceEquals(previewSourceBytes, sourceBytes))
        {
            ClosePreview();
            StartPreviewLoad(moviePath, sourceBytes);
        }
        FinishPreviewLoadIfDone();

        bool loading = previewLoad != null;
        string playLabel = "Play##MoviePlay";
        if (preview != null && preview.IsPlaying)
        {
            playLabel = "Pause##MoviePlay";
        }
        bool canControl = preview != null;
        if (!canControl)
        {
            ImGui.BeginDisabled();
        }
        if (ImGui.Button(playLabel) && preview != null)
        {
            if (preview.IsPlaying)
            {
                preview.Pause();
            }
            else
            {
                preview.Play();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Stop##MovieStop"))
        {
            preview?.Stop();
        }
        if (!canControl)
        {
            ImGui.EndDisabled();
        }
        ImGui.SameLine();
        
           ImGui.SetNextItemWidth(ImGui.GetFontSize() * 8);
        if (ImGui.SliderInt("Volume##MovieVolume", ref previewVolumePercent, 0, 100, "%d%%", ImGuiSliderFlags.AlwaysClamp))
        {
            Program.config.movieVolumePercent = previewVolumePercent;
            if (preview != null)
            {
                preview.Volume = previewVolumePercent / 100f;
            }
        }
        
        if (loading)
        {
            ImGui.TextUnformatted("Loading...");
        }
        else if (preview != null)
        {
            UpdateTimeLabel(preview);
            ImGui.TextUnformatted(previewTimeLabel);
        }
        ImGui.SameLine();
     

        if (preview != null)
        {
            // 4:3 as the console showed it, as large as the space under the seek bar allows; the
            // seek bar is as wide as the picture.
            Vector2 space = ImGui.GetContentRegionAvail();
            float spaceForPicture = space.Y - ImGui.GetFrameHeightWithSpacing();
            if (preview.Error != null)
            {
                spaceForPicture -= ImGui.GetTextLineHeightWithSpacing();
            }
            float width = space.X;
            float height = width * 3f / 4f;
            if (height > spaceForPicture)
            {
                height = spaceForPicture;
                width = height * 4f / 3f;
            }
            float seekSeconds = (float)preview.PositionSeconds;
            ImGui.SetNextItemWidth(Math.Max(width, ImGui.GetFontSize() * 8));
            if (ImGui.SliderFloat("##MovieSeek", ref seekSeconds, 0f, (float)preview.DurationSeconds, string.Empty))
            {
                preview.Seek(seekSeconds);
            }
            if (preview.Error != null)
            {
                ImGui.TextColored(ErrorTextColour, preview.Error);
            }
            Raylib_cs.Texture2D texture = preview.Update();
            if (width > 0 && height > 0)
            {
                rlImGui.ImageSize(texture, new Vector2(width, height));
            }
        }
        else if (previewError != null)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(ErrorTextColour, previewError);
            ImGui.PopTextWrapPos();
        }
    }

    void UpdateTimeLabel(MoviePreview moviePreview)
    {
        long second = (long)moviePreview.PositionSeconds;
        if (second == previewTimeLabelSecond)
        {
            return;
        }
        previewTimeLabelSecond = second;
        long durationSeconds = (long)Math.Ceiling(moviePreview.DurationSeconds);
        previewTimeLabel = $"Time: {second / 60}:{second % 60:00} / {durationSeconds / 60}:{durationSeconds % 60:00}";
    }

    void StartPreviewLoad(string moviePath, byte[]? sourceBytes)
    {
        previewMoviePath = moviePath;
        previewSourceBytes = sourceBytes;
        FileLoader.FileEntry? movieFile = fileloader.Find(moviePath);
        if (sourceBytes == null && movieFile == null)
        {
            previewError = $"{moviePath} is not on the disc.";
            return;
        }
        previewError = null;
        string movieName = IsoPaths.FileName(moviePath);
        previewLoad = Task.Run(() =>
        {
            byte[] pssBytes = sourceBytes ?? fileloader.ReadFile(movieFile!);
            return MoviePreview.Load(movieName, pssBytes);
        });
    }

    
    void FinishPreviewLoadIfDone()
    {
        if (previewLoad == null || !previewLoad.IsCompleted)
        {
            return;
        }
        Task<MoviePreview> finished = previewLoad;
        previewLoad = null;
        if (finished.IsFaulted)
        {
            previewError = $"Preview: {finished.Exception?.GetBaseException().Message}";
            return;
        }
        preview = finished.Result;
        preview.Volume = previewVolumePercent / 100f;
        previewTimeLabelSecond = -1;
    }

    // Leaving the movie folder pauses the sound.
    public void PausePreview()
    {
        preview?.Pause();
    }

    /* A load still running is left to finish and dispose its result, so changing movie does not
       wait for it; when the ISO closes (waitForLoad) nothing may read it afterwards, so it waits. */
    void ClosePreview(bool waitForLoad = false)
    {
        if (previewLoad != null)
        {
            Task<MoviePreview> abandonedLoad = previewLoad;
            previewLoad = null;
            Task disposal = abandonedLoad.ContinueWith(finishedLoad =>
            {
                if (finishedLoad.Status == TaskStatus.RanToCompletion)
                {
                    finishedLoad.Result.Dispose();
                }
            });
            if (waitForLoad)
            {
                disposal.Wait();
            }
        }
        preview?.Dispose();
        preview = null;
        previewMoviePath = string.Empty;
        previewSourceBytes = null;
        previewError = null;
    }

    void UpdateMovieLabels(string[] movieNames)
    {
        if (labelledMovieNames == movieNames && labelledStagedCount == stagedMovies.Count)
        {
            return;
        }
        movieLabels = new string[movieNames.Length];
        for (int i = 0; i < movieNames.Length; i++)
        {
            movieLabels[i] = IsoPaths.FileName(movieNames[i]);
            if (stagedMovies.ContainsKey(movieNames[i]))
            {
                movieLabels[i] += " (replaced)";
            }
        }
        labelledMovieNames = movieNames;
        labelledStagedCount = stagedMovies.Count;
    }

    // ---- conversions --------------------------------------------------------------------------

    void StartExtraction(string moviePath)
    {
        DialogResult dialogResult = Dialog.FolderPicker(Program.config.lastMovieExtractPath);
        if (!dialogResult.IsOk)
        {
            return;
        }
        Program.config.lastMovieExtractPath = dialogResult.Path;
        string outputPath = Path.Combine(dialogResult.Path, IsoPaths.FileNameWithoutExtension(moviePath) + ".mp4");
        FileLoader.FileEntry? movieFile = fileloader.Find(moviePath);
        if (movieFile == null)
        {
            conversionError = $"{moviePath} is not on the disc.";
            return;
        }
        var job = new MovieConversion();
        job.MovieName = IsoPaths.FileName(moviePath);
        StartConversion(job, false, () =>
        {
            job.ExtractToMp4(fileloader.ReadFile(movieFile), outputPath);
        });
    }

    void StartReplacement(string moviePath)
    {
        DialogResult dialogResult = Dialog.FileOpen(FileDialogFilter.Of(ReplacementFileExtensions), Path.GetDirectoryName(Program.config.lastMovieReplacePath));
        if (!dialogResult.IsOk)
        {
            return;
        }
        Program.config.lastMovieReplacePath = dialogResult.Path;
        FileLoader.FileEntry? movieFile = fileloader.Find(moviePath);
        if (movieFile == null)
        {
            conversionError = $"{moviePath} is not on the disc.";
            return;
        }
        string sourcePath = dialogResult.Path;
        var job = new MovieConversion();
        job.MovieName = IsoPaths.FileName(moviePath);
        StartConversion(job, true, () =>
        {
            job.EncodeReplacement(sourcePath, movieFile.Size);
        });
        pendingReplacementPath = moviePath;
        pendingReplacementSource = Path.GetFileName(sourcePath);
    }

    void StartConversion(MovieConversion job, bool replaces, Action work)
    {
        conversionReplaces = replaces;
        conversionResult = string.Empty;
        conversionError = null;
        saveStatus = string.Empty;
        saveErrors = new List<string>();
        conversion = job;
        job.Task = Task.Run(() =>
        {
            try
            {
                work();
            }
            catch (OperationCanceledException)
            {
                job.Error = "Cancelled.";
            }
            catch (Exception exception)
            {
                job.Error = exception.Message;
            }
        });
    }

    // UI thread: takes a finished conversion's result.
    void FinishConversionIfDone()
    {
        if (conversion == null || conversion.Task == null || !conversion.Task.IsCompleted)
        {
            return;
        }
        MovieConversion finished = conversion;
        conversion = null;
        finished.Cancellation.Dispose();
        if (finished.Error != null)
        {
            conversionError = $"{finished.MovieName}: {finished.Error}";
            return;
        }
        if (conversionReplaces && finished.ReplacementBytes != null)
        {
            var stagedMovie = new StagedMovie();
            stagedMovie.IsoPath = pendingReplacementPath;
            stagedMovie.FileBytes = finished.ReplacementBytes;
            stagedMovie.SourceName = pendingReplacementSource;
            stagedMovie.Summary = finished.Summary;
            stagedMovies[pendingReplacementPath] = stagedMovie;
            conversionResult = $"Staged {finished.MovieName}: {finished.Summary}";
        }
        else
        {
            conversionResult = $"Extracted {finished.Summary}";
        }
    }

    void RenderConversionStatus(float barWidth)
    {
        if (conversion != null)
        {
            ImGui.TextUnformatted($"{conversion.MovieName}: {conversion.Stage}");
            ImGui.ProgressBar(conversion.Progress, new Vector2(barWidth, 0));
            if (conversion.Cancellation.IsCancellationRequested)
            {
                ImGui.TextDisabled("Cancelling...");
            }
            else if (ImGui.Button("Cancel"))
            {
                conversion.Cancellation.Cancel();
            }
            return;
        }
        ImGui.PushTextWrapPos();
        if (conversionError != null)
        {
            ImGui.TextColored(ErrorTextColour, conversionError);
        }
        else if (conversionResult.Length > 0)
        {
            ImGui.TextUnformatted(conversionResult);
        }
        ImGui.PopTextWrapPos();
    }

    // FileLoader is about to close or replace the image: nothing may read it after this returns.
    void OnIsoClosing()
    {
        if (conversion != null)
        {
            conversion.Cancellation.Cancel();
            try
            {
                conversion.Task?.Wait();
            }
            catch (AggregateException)
            {
            }
            conversion.Cancellation.Dispose();
            conversion = null;
        }
        ClosePreview(waitForLoad: true);
        // Staged movies belong to the image being closed.
        stagedMovies.Clear();
        conversionResult = string.Empty;
        conversionError = null;
        saveStatus = string.Empty;
        saveErrors = new List<string>();
        labelledMovieNames = null;
    }

    // ---- saving -------------------------------------------------------------------------------

    void RenderSaveConfirmPopup()
    {
        bool open = true;
        if (!ImGui.BeginPopupModal("Confirm movie save", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }
        ImGui.Text($"Write {stagedMovies.Count} replaced movie(s) into the ISO in place?");
        foreach (StagedMovie stagedMovie in stagedMovies.Values)
        {
            ImGui.BulletText($"{IsoPaths.FileName(stagedMovie.IsoPath)}: {stagedMovie.SourceName}");
        }
        ImGui.TextDisabled("Nothing is written if any movie does not fit.");
        if (ImGui.Button("Yes"))
        {
            ImGui.CloseCurrentPopup();
            SaveStagedMovies();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    // One ChangeSet for every staged movie: each replaces its whole file, same size.
    void SaveStagedMovies()
    {
        saveErrors = new List<string>();
        var changes = new ChangeSet();
        try
        {
            foreach (StagedMovie stagedMovie in stagedMovies.Values)
            {
                changes.AddWholeFile(stagedMovie.IsoPath, stagedMovie.FileBytes, IsoPaths.FileName(stagedMovie.IsoPath));
            }
            ChangeSet.Plan plan = changes.Write(fileloader);
            saveSucceeded = plan.Ok;
            if (plan.Ok)
            {
                saveStatus = $"Saved {stagedMovies.Count} movie(s): {plan.BytesToWrite:N0} B written.";
                stagedMovies.Clear();
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

    void RenderSaveStatus()
    {
        if (saveStatus.Length == 0)
        {
            return;
        }
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
}

// One movie with a new video, as Save to ISO will write it: the whole file, its original size.
sealed class StagedMovie
{
    public string IsoPath = string.Empty;
    public byte[] FileBytes = Array.Empty<byte>();
    public string SourceName = string.Empty;
    public string Summary = string.Empty;
}
