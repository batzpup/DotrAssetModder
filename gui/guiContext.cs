using System.Numerics;
using DotrAssetModder;
using ImGuiNET;
using Raylib_cs;
namespace DotrAssetModder;

public static class GuiContext
{
    public static ImFontPtr MainFont;
    public static ImGuiIOPtr Io;
    public static int MonitorWidth;
    public static int MonitorHeight;
    public static int MonitorIndex;
    public static int FrameRateTarget;
    public static Vector2 MonitorSize;

    public static int WindowWidth;
    public static int WindowHeight;
    public static Vector2 WindowSize;

    public static void Init()
    {
        FontManager.LoadFonts();
        MainFont = FontManager.GetFont(FontManager.FontFamily.NotoSansJP, 32);
        Io = ImGui.GetIO();

        UpdateMonitorSize();
        UpdateWindowSize();
    }
    
    public static void UpdateWindowSize()
    {
        WindowWidth = Raylib.GetScreenWidth();
        WindowHeight = Raylib.GetScreenHeight();
        WindowSize = new Vector2(WindowWidth, WindowHeight);
    }


    // A field's width: a share of the room left on the line, but never under minEm font heights.
    public static float FieldWidth(float share, float minEm)
    {
        return Math.Max(ImGui.GetContentRegionAvail().X * share, ImGui.GetFontSize() * minEm);
    }

    // The tooltip for the item just drawn.
    public static void HoveredTooltip(string text)
    {
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(text);
        }
    }

    public static Vector2 GetScreenPixelsFromPercent(float percentX, float percentY) => new Vector2(WindowWidth * (percentX / 100f), WindowHeight * (percentY / 100f));

    static void UpdateMonitorSize()
    {
        MonitorWidth = Raylib.GetMonitorWidth(MonitorIndex);
        MonitorHeight = Raylib.GetMonitorHeight(MonitorIndex);
        MonitorSize = new Vector2(MonitorWidth, MonitorHeight);
    }

    public static void UpdateWindowToMonitor()
    {
        MonitorIndex = Raylib.GetCurrentMonitor();
        FrameRateTarget = Raylib.GetMonitorRefreshRate(MonitorIndex);
        Raylib.SetWindowSize(Raylib.GetMonitorWidth(MonitorIndex), Raylib.GetMonitorHeight(MonitorIndex));
        Raylib.SetTargetFPS(FrameRateTarget);
    }
}