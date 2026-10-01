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
        public string lastDataExtractPath { get; set; }
        public string lastModelFolderPath { get; set; }
        public string lastMovieExtractPath { get; set; }

        
        
        static string PortablePath =>
            Path.Combine(AppContext.BaseDirectory, configName);

        static string UserPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DotrAssetModder", configName);

        public static Config Load()
        {
            try
            {
                string? path = new[] { PortablePath, UserPath }
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (path == null) return new Config();

                string json = File.ReadAllText(path);

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

            try
            {
                File.WriteAllText(PortablePath, json);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UserPath)!);
                File.WriteAllText(UserPath, json);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Failed to save config: " + ex.Message);
            }
        }
    }
}