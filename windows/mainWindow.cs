using System.Numerics;
using ImGuiNET;
using Raylib_cs;
namespace DotrAssetModder;

public class MainWindow
{
    Windows currentWindow = Windows.Extractor;

    ExtractorWindow extractWindow;
    TextureEditorWindow textureWindow;
    ModelViewerWindow modelWindow;
    ModelImporterWindow importWindow;


    public enum Windows
    {
        Extractor,
        TextureView,
        ModelView,
        ModelImporter,
        Music
    }

    public MainWindow()
    {
        GuiContext.Io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.DpiEnableScaleFonts | ImGuiConfigFlags.DpiEnableScaleViewports;

        extractWindow = new();
        textureWindow = new();
        modelWindow = new();
        importWindow = new(extractWindow.MusicExtractor);
    }


    public void Render()
    {
        if (Raylib.IsFileDropped())
        {
            var files = Raylib.GetDroppedFiles();
            var mouse = Raylib.GetMousePosition();

            if (files.Length > 0)
            {
                string extension = Path.GetExtension(files[0]).ToLowerInvariant();
                if (extension == ".iso" && currentWindow == Windows.Extractor)
                {
                    FileLoader.Instance.OpenIso(files[0]);
                }
                else if (MusicExtractorWindow.IsReplacementFile(files[0]) && currentWindow == Windows.Extractor)
                {
                    extractWindow.DropSoundFile(files[0]);
                }
                else if (extension is ".glb" or ".gltf")
                {
                    if (currentWindow == Windows.ModelImporter)
                    {
                        if (FileLoader.Instance.IsIsoLoaded)
                        {
                            importWindow.SetupModel(files[0]);
                        }
                    }
                    else if (currentWindow == Windows.ModelView)
                    {
                        modelWindow.LoadModel(files[0]);
                    }

                }
                else if (extension is ".fbx" or ".dae")
                {
                    // Converted to a glb first, by the importer and by the viewer.
                    if (currentWindow == Windows.ModelImporter)
                    {
                        if (FileLoader.Instance.IsIsoLoaded)
                        {
                            importWindow.SetupModel(files[0]);
                        }
                    }
                    else if (currentWindow == Windows.ModelView)
                    {
                        modelWindow.LoadModel(files[0]);
                    }
                }

            }
        }
        
        ImGui.PushFont(GuiContext.MainFont);
        ImGui.SetNextWindowSize(GuiContext.WindowSize);
        ImGui.SetNextWindowPos(Vector2.Zero);

        // NoBringToFrontOnFocus: the full-screen main window stays behind floating windows (the
        // importer's preview), so clicking it never buries them
        ImGui.Begin("DOTR Asset Extractor", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBringToFrontOnFocus);




        ImGui.PushStyleColor(ImGuiCol.TabSelected, new GuiColour(0, 189, 0).Value);
        ImGui.PushStyleColor(ImGuiCol.TabHovered, new GuiColour(128, 128, 0).Value);
        if (ImGui.BeginTabBar("Mode"))
        {

            if (ImGui.BeginTabItem("Extractor"))
            {
                currentWindow = Windows.Extractor;
                extractWindow.Render();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Texture Viewer"))
            {
                currentWindow = Windows.TextureView;
                textureWindow.Render();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Model Viewer"))
            {
                currentWindow = Windows.ModelView;
                modelWindow.Render();
                ImGui.EndTabItem();

            }
            if (ImGui.BeginTabItem("Model Importer"))
            {
                currentWindow = Windows.ModelImporter;
                importWindow.Render();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
        if (currentWindow != Windows.Extractor)
        {
            extractWindow.PauseMoviePreview();
        }
        

        ImGui.PopStyleColor(2);
        ImGui.PopFont();
        ImGui.End();
        
    }
}