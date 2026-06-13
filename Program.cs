
using Raylib_cs;
using rlImGui_cs;
using Color = Raylib_cs.Color;
namespace DotrAssetModder;

class Program
{
    static void Main()
    {
        
        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.VSyncHint);

        Raylib.InitWindow(1920, 1080, "DOTR Asset Extractor");

        glRD.UpdateWindowToMonitor();
        Raylib.MaximizeWindow();
        Raylib.SetExitKey(KeyboardKey.Null);
        rlImGui.Setup();
        glRD.Init();
        MainWindow main_window = new MainWindow();
        while (!Raylib.WindowShouldClose())
        {
            glRD.UpdateWinDims();
            Raylib.BeginDrawing();
            Raylib.ClearBackground(Color.Gray);
            rlImGui.Begin();
            main_window.Render();
            rlImGui.End();
            Raylib.EndDrawing();
        }
        rlImGui.Shutdown();
        Raylib.CloseWindow();
        FileLoader.Instance.Dispose();
    }
}