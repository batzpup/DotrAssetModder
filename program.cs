using System.Text.Json;
using Raylib_cs;
using rlImGui_cs;
using Color = Raylib_cs.Color;
namespace DotrAssetModder;

class Program
{
    public static Config config = Config.Load();
    const string CrashLogName = "crash.log";

    static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += WriteCrashLog;

        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.VSyncHint);

        Raylib.InitWindow(1920, 1080, "DOTR Asset Modder");

        GuiContext.UpdateWindowToMonitor();
        Raylib.MaximizeWindow();
        Raylib.SetExitKey(KeyboardKey.Null);
        rlImGui.Setup();
        GuiContext.Init();
        MainWindow main_window = new MainWindow();
        while (!Raylib.WindowShouldClose())
        {
            GuiContext.UpdateWindowSize();
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
    
    static void WriteCrashLog(object sender, UnhandledExceptionEventArgs eventArgs)
    {
        string report = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Crash{Environment.NewLine}{eventArgs.ExceptionObject}{Environment.NewLine}{Environment.NewLine}";
        string[] logPaths =
        {
            Path.Combine(AppContext.BaseDirectory, CrashLogName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DotrAssetModder", CrashLogName),
        };
        foreach (string logPath in logPaths)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, report);
                Console.Error.WriteLine("Crash log written to " + logPath);
                return;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
            }
        }
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
        public string lastMovieReplacePath { get; set; }
        public string lastMusicExtractPath { get; set; }
        public string lastSoundReplacePath { get; set; }
        public int movieVolumePercent { get; set; } = 100;
        // Which MSG file an EU disc's text is read from, by MessageLanguage.Name.
        public string messageLanguage { get; set; }

        
        
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
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UserPath)!);
                File.WriteAllText(UserPath, json);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Failed to save config: " + exception.Message);
            }
        }
    }
}