using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using ImGuiNET;
using rlImGui_cs;
namespace DotrAssetModder;

public static class FontManager
{
    public static Dictionary<FontFamily, Dictionary<int, ImFontPtr>> Fonts = new();

    public enum FontFamily
    {
        NotoSansJP
    }

    public static Dictionary<FontFamily, string> FontNames = new() {
        { FontFamily.NotoSansJP, "NotoSansJP-Regular.ttf" }
    };

    public static void LoadFonts()
    {
        int[] sizes = { 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32 };
        foreach (var familyEntry in FontNames)
        {

            var fontsBySize = new Dictionary<int, ImFontPtr>();
            foreach (int size in sizes)
            {
                ImFontPtr font = LoadCustomFont(familyEntry.Value, size);
                fontsBySize[size] = font;
            }
            Fonts[familyEntry.Key] = fontsBySize;
        }
        ImGui.GetIO().Fonts.Build();
        rlImGui.ReloadFonts();
    }

    public static ImFontPtr GetFont(FontFamily family, int requestedSize)
    {
        if (!Fonts.TryGetValue(family, out var fontsBySize))
            return IntPtr.Zero;

        int closest = fontsBySize.Keys.OrderBy(availableSize => Math.Abs(availableSize - requestedSize)).First();
        return fontsBySize[closest];
    }

    public static ImFontPtr LoadCustomFont(string fontPath = "NotoSansJP-Regular.ttf", int pixelSize = 32)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string resourceName = $"{assembly.GetName().Name}.fonts.{fontPath}";
        ImFontPtr font;
        using (Stream? stream = assembly.GetManifestResourceStream(resourceName))
        {

            if (stream is null)
            {
                Console.Error.WriteLine($"No resource exists with the name {resourceName}");
                font = ImGui.GetIO().Fonts.AddFontDefault();
                font.ConfigData.SizePixels = pixelSize;
                return font;
            }
            byte[] fontData = new byte[stream.Length];
            int bytesRead = stream.Read(fontData);
            if (bytesRead == stream.Length)
            {
                // Allocate unmanaged memory that persists for the application lifetime
                IntPtr unmanagedPtr = Marshal.AllocHGlobal(bytesRead);
                Marshal.Copy(fontData, 0, unmanagedPtr, bytesRead);

                font = ImGui.GetIO().Fonts.AddFontFromMemoryTTF(unmanagedPtr, bytesRead, pixelSize);
            }
            else
            {
                font = ImGui.GetIO().Fonts.AddFontDefault();
                font.ConfigData.SizePixels = pixelSize;
            }
        }
        return font;
    }
}