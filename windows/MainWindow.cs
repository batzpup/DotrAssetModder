using System.Numerics;
using ImGuiNET;
using Raylib_cs;
namespace DotrAssetModder;

public class MainWindow
{
    float title_padding_per = 0.5f;
    Windows current_window = Windows.Extractor;

    ExtractorWindow extract_window;
    TextureEditorWindow texture_window;
    ModelViewerWindow model_window;
    ModelImporterWindow import_window;


    public enum Windows
    {
        Extractor,
        TextureView,
        ModelView,
        ModelImporter,
    }

    public MainWindow()
    {
        glRD.Io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.DpiEnableScaleFonts | ImGuiConfigFlags.DpiEnableScaleViewports;

        extract_window = new();
        texture_window = new();
        model_window = new();
        import_window = new();
    }


    public void Render()
    {
        if (Raylib.IsFileDropped())
        {
            var files = Raylib.GetDroppedFiles();
            var mouse = Raylib.GetMousePosition();

            if (files.Length > 0)
            {
                string ext = Path.GetExtension(files[0]).ToLowerInvariant();
                if (ext == ".iso" && current_window == Windows.Extractor)
                {
                    FileLoader.Instance.OpenIso(files[0]);
                }
                else if (ext is ".glb" or ".gltf")
                {
                    if (current_window == Windows.ModelImporter)
                    {
                        if (FileLoader.Instance.IsIsoLoaded)
                        {
                            import_window.SetupModel(files[0]);
                        }
                    }
                    else if (current_window == Windows.ModelView)
                    {
                        model_window.LoadModel(files[0]);
                    }

                }

            }
        }
        ImGui.PushFont(glRD.MainFont);
        ImGui.SetNextWindowSize(glRD.cur_win_dims);
        ImGui.SetNextWindowPos(Vector2.Zero);

        ImGui.Begin("DOTR Asset Extractor", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse);




        ImGui.PushStyleColor(ImGuiCol.TabSelected, new GuiColour(0, 189, 0).value);
        ImGui.PushStyleColor(ImGuiCol.TabHovered, new GuiColour(128, 128, 0).value);
        if (ImGui.BeginTabBar("Mode"))
        {

            if (ImGui.BeginTabItem("Extractor"))
            {
                current_window = Windows.Extractor;
                extract_window.Render();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Texture Viewer"))
            {
                current_window = Windows.TextureView;
                texture_window.Render();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Model Viewer"))
            {
                current_window = Windows.ModelView;
                model_window.Render();
                ImGui.EndTabItem();

            }
            if (ImGui.BeginTabItem("Model Importer"))
            {
                current_window = Windows.ModelImporter;
                import_window.Render();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        ImGui.PopStyleColor(2);
        ImGui.PopFont();
        ImGui.End();
    }
}