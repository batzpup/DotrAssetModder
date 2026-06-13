using ImGuiNET;
using NativeFileDialogSharp;
using Raylib_cs;
using System.Numerics;

namespace DotrAssetModder;

public class TextureViewerWindow : IDisposable
{
    TextureCache cache = new();
    Dictionary<string, List<string>> folders = new();
    List<string> currentFolderTextures = new();

    int sidebarColumns = 1;
    int currentIndex = -1;

    Texture2D currentTexture;
    string currentPath;
    bool hasTexture;

    float zoom = 2.0f;
    Vector2 pan = Vector2.Zero;

    bool dragging;
    Vector2 dragStartMouse;
    Vector2 dragStartPan;

    bool scroll_to_selected;
    int sb_col_count = 5;

    public void Render()
    {



        DrawSidebar(glRD.GetScreenPixelsFromPercent(25, 10).X);

        ImGui.SameLine();

        DrawViewport();



        HandleKeyboard();
    }

    void DrawSidebar(float width)
    {
        ImGui.BeginChild("sidebar", new Vector2(width, 0));

        if (ImGui.Button("Load Folder"))
            LoadFolder();

        ImGui.SameLine();

        if (ImGui.Button("Load Image"))
            LoadSingleImage();

        ImGui.Separator();



        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float avail = ImGui.GetContentRegionAvail().X;

        float thumb = (avail - (sb_col_count - 1) * spacing) / sb_col_count;
        thumb = MathF.Max(1, thumb);

        sidebarColumns = sb_col_count;

        int i = 0;

        foreach (var path in currentFolderTextures)
        {
            int col = i % sidebarColumns;
            if (col != 0)
            {
                ImGui.SameLine();
            }

            Texture2D tex = cache.Load(path);
            ImGui.PushID(i);
            Vector2 size = new(thumb, thumb);
            ImGui.Image((IntPtr)tex.Id, size);

            if (ImGui.IsItemClicked())
            {
                SelectTexture(i);
            }

            if (i == currentIndex)
            {
                Vector2 min = ImGui.GetItemRectMin();
                Vector2 max = ImGui.GetItemRectMax();
                Vector2 window_pos = ImGui.GetWindowPos();
                float window_max_y = window_pos.Y + ImGui.GetWindowHeight();
                ImGui.GetWindowDrawList().AddRect(
                    min,
                    max,
                    ImGui.GetColorU32(new Vector4(0f, 1f, 1f, 1f)),
                    0f,
                    ImDrawFlags.None,
                    3f
                );
                if (scroll_to_selected)
                {
                    if (min.Y < window_pos.Y)
                    {
                        ImGui.SetScrollHereY(0.0f);
                    }
                    else if (max.Y > window_max_y)
                    {
                        ImGui.SetScrollHereY(1.0f); // Scroll down — align to bottom
                    }
                    scroll_to_selected = false;
                }
            }
            ImGui.PopID();
            i++;
        }

        ImGui.EndChild();
    }


    void DrawViewport()
    {
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.BeginChild("viewport", Vector2.Zero);

        if (!hasTexture)
        {
            ImGui.Text("No texture loaded");
            ImGui.EndChild();
            return;
        }

        ImGui.Text(Path.GetFileName(currentPath));
        Vector2 region = ImGui.GetContentRegionAvail();

        HandleMouseZoom(region);
        HandleMousePan();

        DrawCheckerboard(region);

        Vector2 size = new Vector2(currentTexture.Width, currentTexture.Height) * zoom;
        Vector2 center = region * 0.5f;
        Vector2 pos = center + pan - (size * 0.5f);

        ImGui.SetCursorPos(pos);
        ImGui.Image((IntPtr)currentTexture.Id, size);

        ImGui.EndChild();
    }


    void HandleMouseZoom(Vector2 region)
    {
        var io = ImGui.GetIO();

        if (!ImGui.IsWindowHovered())
        {
            return;   
        }
        float wheel = io.MouseWheel;
        if (wheel == 0)
        {
            return;
        }
            

        Vector2 mouse = io.MousePos - ImGui.GetWindowPos();
        Vector2 center = region * 0.5f;
        Vector2 worldPoint = (mouse - center - pan) / zoom;
        float newZoom = Math.Clamp(zoom * (1f + wheel * 0.1f), 0.1f, 30f);
        pan = mouse - center - worldPoint * newZoom;
        zoom = newZoom;
    }


    void HandleMousePan()
    {
        var io = ImGui.GetIO();

        if (!ImGui.IsWindowHovered())
            return;

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
        {
            dragging = true;
            dragStartMouse = io.MousePos;
            dragStartPan = pan;
        }

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Middle))
            dragging = false;

        if (dragging)
        {
            Vector2 delta = io.MousePos - dragStartMouse;
            pan = dragStartPan + delta;
        }
    }


    void LoadSingleImage()
    {
        var result = Dialog.FileOpen("png,jpg,jpeg,bmp,tga");
        if (!result.IsOk || string.IsNullOrWhiteSpace(result.Path))
            return;
        
        currentFolderTextures.Clear();
        SelectExternal(result.Path);
    }

    void SelectExternal(string path)
    {
        currentPath = path;
        currentTexture = cache.Load(path);
        hasTexture = true;

        currentIndex = -1;

        zoom = 2.0f;
        pan = Vector2.Zero;
    }


    void LoadFolder()
    {
        var result = Dialog.FolderPicker();

        if (!result.IsOk || string.IsNullOrWhiteSpace(result.Path))
            return;

        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            ".png", ".jpg", ".jpeg"
        };

        var files = Directory
            .EnumerateFiles(result.Path, "*.*", SearchOption.AllDirectories)
            .Where(f => exts.Contains(Path.GetExtension(f)))
            .OrderBy(f => Path.GetFileNameWithoutExtension(f), NaturalComparer.Instance)
            .ToList();

        string folderName = Path.GetFileName(result.Path.TrimEnd(Path.DirectorySeparatorChar));

        folders[folderName] = files;


        currentFolderTextures = files;

        cache.Clear();

        if (files.Count > 0)
            SelectTexture(0);
    }


    void SelectTexture(int index)
    {
        if (index < 0 || index >= currentFolderTextures.Count)
            return;

        currentIndex = index;
        currentPath = currentFolderTextures[index];

        currentTexture = cache.Load(currentPath);
        hasTexture = true;

        zoom = 2.0f;
        pan = Vector2.Zero;
    }


    void HandleKeyboard()
    {
        if (currentFolderTextures.Count == 0 || sidebarColumns <= 0)
        {

            return;
        }


        int total = currentFolderTextures.Count;
        int cols = sidebarColumns;

        int newIndex = currentIndex;

        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow))
        {
            newIndex = currentIndex + 1;
        }

        else if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
        {
            newIndex = currentIndex - 1;
        }

        else if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
        {
            newIndex = currentIndex + cols;
        }

        else if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
        {
            newIndex = currentIndex - cols;
        }

        newIndex = Math.Clamp(newIndex, 0, total - 1);

        if (newIndex != currentIndex)
        {
            SelectTexture(newIndex);
            scroll_to_selected = true;
        }

    }


    void DrawCheckerboard(Vector2 region)
    {
        const int cell = 16;

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        Vector2 origin = ImGui.GetWindowPos();

        for (int y = 0; y < region.Y; y += cell)
        {
            for (int x = 0; x < region.X; x += cell)
            {
                bool alt = ((x / cell + y / cell) % 2) == 0;

                uint color = alt ? 0xFF2A2A2A : 0xFF3A3A3A;

                draw.AddRectFilled(
                    origin + new Vector2(x, y),
                    origin + new Vector2(x + cell, y + cell),
                    color
                );
            }
        }
    }

    public void Dispose()
    {
        cache.Dispose();
    }
}

static class NaturalComparer
{
    public static readonly IComparer<string> Instance = Comparer<string>.Create(Compare);

    static int Compare(string a, string b)
    {
        int ia = 0, ib = 0;
        while (ia < a.Length && ib < b.Length)
        {
            if (char.IsDigit(a[ia]) && char.IsDigit(b[ib]))
            {
                int sa = ia, sb = ib;
                while (ia < a.Length && char.IsDigit(a[ia])) ia++;
                while (ib < b.Length && char.IsDigit(b[ib])) ib++;
                var na = a.AsSpan(sa, ia - sa).TrimStart('0');
                var nb = b.AsSpan(sb, ib - sb).TrimStart('0');
                if (na.Length != nb.Length) return na.Length - nb.Length;
                int cmp = na.SequenceCompareTo(nb);
                if (cmp != 0) return cmp;
            }
            else
            {
                int cmp = char.ToUpperInvariant(a[ia]).CompareTo(char.ToUpperInvariant(b[ib]));
                if (cmp != 0) return cmp;
                ia++;
                ib++;
            }
        }
        return (a.Length - ia) - (b.Length - ib);
    }
}