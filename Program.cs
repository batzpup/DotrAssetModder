using System.Text.Json;
using Raylib_cs;
using rlImGui_cs;
using Color = Raylib_cs.Color;
namespace DotrAssetModder;

class Program
{
    public static Config config = Config.Load();

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
        config.Save();
        FileLoader.Instance.Dispose();
    }


    public class Config
    {
        public const string configName = "config.json";
        public string lastIsoPath { get; set; }
        public string lastWordlistPath { get; set; } 
        public string lastModelImportPath { get; set; } 
        public string lastExtractPath { get; set; }
        public string lastModelFolderPath { get; set; }

        static string PathName =>
            Path.Combine(AppContext.BaseDirectory, configName);

        public static Config Load()
        {
            try
            {
                if (!File.Exists(PathName)) return new Config();

                string json = File.ReadAllText(PathName);

                return JsonSerializer.Deserialize<Config>(json) ?? new Config();
            }
            catch
            {
                return new Config();
            }
        }


        public void Save()
        {
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions {
                WriteIndented = true
            });

            File.WriteAllText(PathName, json);
        }
    }
}