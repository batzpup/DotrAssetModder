using System.Diagnostics;
using System.Numerics;
using DiscUtils.Iso9660;
using ImGuiNET;
using NativeFileDialogSharp;
namespace DotrAssetModder;

public class MovieExtractorWindow
{
    int currentMovieIndex = 0;
    string movieDestinationPath = string.Empty;


    public void Render()
    {
        ImGui.TextColored(new Vector4(255, 0, 0, 255),"Under Construction");
        ImGui.Text("If you want edit the movies currently please extract the iso with the tool below");
        OpenUrlButton("Ps2 ISO rebuilder", "https://drive.google.com/file/d/10DJ-fYgSx6D7vSdwVCTa9FHFbIWZE7xU/view");
        ImGui.Text("Then seperate the 2 files, edit them and recombine with this tool");
        OpenUrlButton("Ps2 PSS Mux", "https://github.com/wagrenier/PssMux/releases");
        ImGui.Text("Then rebuild the iso with the ISO tool");
        return;
        ImGui.TextColored(new Vector4(255, 0, 0, 255), "This tool just extracts the movies to Mp4");
        ImGui.Text("Movies");
        ImGui.Combo("##Movie List", ref currentMovieIndex, FileLoader.Instance.MovieNames, FileLoader.Instance.MovieNames.Length);
        ImGui.Spacing();
        if (ImGui.Button("Destination Folder"))
        {
            DialogResult dialogResult = Dialog.FolderPicker(Program.config.lastMovieExtractPath);
            if (dialogResult.IsOk)
            {
                movieDestinationPath = dialogResult.Path;
                Program.config.lastMovieExtractPath = movieDestinationPath;
            }
            else
            {
                ImGui.OpenPopup("Error");
            }
        }
        if (ImGui.Button($"Extract Movie"))
        {
            if (movieDestinationPath == string.Empty || !Directory.Exists(movieDestinationPath))
            {
                ImGui.OpenPopup("No Destination");
            }
            else
            {
                Task.Run(async () =>
                {
                    string filePath = FileLoader.Instance.MovieNames[currentMovieIndex];
                    string tempFile = Path.Combine(Path.GetTempPath(), IsoPaths.FileName(filePath));

                    try
                    {
                        CDReader iso = new CDReader(FileLoader.Instance.fileStream, true);
                        using (Stream openFile = iso.OpenFile(filePath, FileMode.Open))
                        using (FileStream output = File.Create(tempFile))
                        {
                            await openFile.CopyToAsync(output);
                        }

                        string outputPath = Path.Combine(movieDestinationPath, IsoPaths.FileNameWithoutExtension(filePath) + ".mp4");


                    }
                    finally
                    {
                        if (File.Exists(tempFile))
                        {
                            File.Delete(tempFile);
                        }

                    }
                });
            }
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("If you want edit the movies please extract the iso with the tool below");
        OpenUrlButton("Ps2 ISO rebuilder", "https://drive.google.com/file/d/10DJ-fYgSx6D7vSdwVCTa9FHFbIWZE7xU/view");
        ImGui.Text("Then seperate the 2 files, edit them and recombine with this tool");
        OpenUrlButton("Ps2 PSS Mux", "https://github.com/wagrenier/PssMux/releases");
        ImGui.Text("Then rebuild the iso with the ISO tool");
    }

    static void OpenUrlButton(string buttonName, string url)
    {
        if (ImGui.Button(buttonName))
        {
            try
            {
                Process.Start(new ProcessStartInfo {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception exception)
            {
                Console.WriteLine("Failed to open URL: " + exception.Message);
            }
        }
    }
}