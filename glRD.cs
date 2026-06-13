using System.Numerics;
using DotrAssetModder;
using ImGuiNET;
using Raylib_cs;
namespace DotrAssetModder;

public static class glRD
{
    public static ImFontPtr MainFont;
    public static ImGuiIOPtr Io;
    public static int cur_mon_w;
    public static int cur_mon_h;
    public static int cur_mon;
    public static int frame_rate_target;
    public static Vector2 cur_mon_dims;

    public static int cur_win_w;
    public static int cur_win_h;
    public static Vector2 cur_win_dims;

    public static void Init()
    {
        FontManager.LoadFonts();
        MainFont = FontManager.GetFont(FontManager.FontFamily.NotoSansJP, 32);
        Io = ImGui.GetIO();

        UpdateMonDims();
        UpdateWinDims();
    }
    
    public static void UpdateWinDims()
    {
        cur_win_w = Raylib.GetScreenWidth();
        cur_win_h = Raylib.GetScreenHeight();
        cur_win_dims = new Vector2(cur_win_w, cur_win_h);
    }


    public static Vector2 P(float x_per, float y_per) => new Vector2(cur_win_w * (x_per / 100f), cur_win_h * (y_per / 100f));


    public static float PW(float per) => cur_win_w * (per / 100f);


    public static float PH(float per) => cur_win_h * (per / 100f);

    public static Vector2 GetScreenPixelsFromPercent(float per_x, float per_y) => P(per_x, per_y);

    static void UpdateMonDims()
    {
        cur_mon_w = Raylib.GetMonitorWidth(cur_mon);
        cur_mon_h = Raylib.GetMonitorHeight(cur_mon);
        cur_mon_dims = new Vector2(cur_mon_w, cur_mon_h);
    }

    public static void UpdateWindowToMonitor()
    {
        cur_mon = Raylib.GetCurrentMonitor();
        frame_rate_target = Raylib.GetMonitorRefreshRate(cur_mon);
        Raylib.SetWindowSize(Raylib.GetMonitorWidth(cur_mon), Raylib.GetMonitorHeight(cur_mon));
        Raylib.SetTargetFPS(frame_rate_target);
    }
}