using System.Numerics;
using ImGuiNET;
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
        ImGui.PushFont(glRD.MainFont);
        ImGui.SetNextWindowSize(glRD.cur_win_dims);
        ImGui.SetNextWindowPos(Vector2.Zero);

        ImGui.Begin("DOTR Asset Extractor",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse);




        ImGui.PushStyleColor(ImGuiCol.TabSelected, new GuiColour(0, 189, 0).value);
        ImGui.PushStyleColor(ImGuiCol.TabHovered, new GuiColour(128, 128, 0).value);
        if (ImGui.BeginTabBar("Mode"))
        {

            if (ImGui.BeginTabItem("Extractor"))
            {
                extract_window.Render();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Texture Viewer"))
            {
                texture_window.Render();
                ImGui.EndTabItem();
            }
 
            if (ImGui.BeginTabItem("Model Viewer"))
            {
                model_window.Render();
                ImGui.EndTabItem();

            }
            if (ImGui.BeginTabItem("Model Importer"))
            {
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

