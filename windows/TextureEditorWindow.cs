using ImGuiNET;
using NativeFileDialogSharp;
using Raylib_cs;
using SkiaSharp;
using System.Numerics;

namespace DotrAssetModder;

public class TextureEditorWindow : IDisposable
{
    enum QuantMode
    {
        MedianCut,
        Octree
    }

    enum PicMiniFilter
    {
        Point,
        Bilinear,
        Trilinear,
    }

    List<TextureSlot> slots = new();
    int currentIndex = -1;
    byte[]? mrgBytes;
    string selectedMrgName = "";

    PaintCanvas? canvas;
    bool isEditMode = false;
    bool cropOnImport = false;
    QuantMode quantMode = QuantMode.MedianCut;

    List<SKColor> palette = new();
    int selectedPaletteIdx = -1;
    SKColor brushColor = SKColors.Black;
    bool wasPainting;

    int sbColCount = 4;
    int sidebarColumns = 1;
    bool scrollToSelected;

    Texture2D viewTexture;
    bool hasViewTexture;
    float zoom = 2f;
    Vector2 pan = Vector2.Zero;
    bool dragging;
    Vector2 dragStartMouse;
    Vector2 dragStartPan;

    bool cropMode = false;
    PaintCanvas? picMiniFullCanvas = null;
    SKPointI cropOrigin = new(0, 0);
    float cropZoom = 1f;
    int cropSrcW = 40;
    int cropSrcH = 32;
    bool centerMouseOnZoom = false;

    PicMiniFilter picMiniFilter = PicMiniFilter.Point;

    public void Render()
    {
        DrawSidebar(glRD.GetScreenPixelsFromPercent(25, 10).X);
        ImGui.SameLine();
        if (isEditMode && canvas != null)
        {
            DrawEditor();
        }
        else
        {
            DrawViewport();
        }
        HandleKeyboard();
    }

    void DrawSidebar(float width)
    {
        ImGui.BeginChild("sidebar", new Vector2(width, 0));

        if (!FileLoader.Instance.IsIsoLoaded)
        {
            ImGui.TextDisabled("Open an ISO to begin.");
            ImGui.EndChild();
            return;
        }

        ImGui.SetNextItemWidth(-1);
        string comboLabel = string.IsNullOrEmpty(selectedMrgName) ? "Pick MRG..." : selectedMrgName;
        if (ImGui.BeginCombo("##mrg", comboLabel))
        {
            foreach (var name in TextureSlotManager.Rh2MrgNames)
            {
                if (!TextureSlotManager.IsoHasFile(name))
                {
                    continue;
                }
                if (ImGui.Selectable(name, selectedMrgName == name))
                {
                    LoadMrg(name, TextureMrgKind.Rh2);
                }
            }
            ImGui.Separator();
            if (TextureSlotManager.IsoHasFile(TextureSlotManager.IconMrgName) &&
                ImGui.Selectable(TextureSlotManager.IconMrgName, selectedMrgName == TextureSlotManager.IconMrgName))
            {
                LoadMrg(TextureSlotManager.IconMrgName, TextureMrgKind.Icon);
            }
            ImGui.EndCombo();
        }

        if (slots.Count > 0)
        {
            bool prevEditMode = isEditMode;
            ImGui.Checkbox("Edit mode", ref isEditMode);
            if (isEditMode && !prevEditMode && currentIndex >= 0)
            {
                OpenCanvas(currentIndex);
            }
            else if (!isEditMode && prevEditMode)
            {
                CloseCanvas();
            }

            if (isEditMode && canvas != null)
            {
                ImGui.SameLine();
                if (ImGui.Button("Save slot"))
                {
                    SaveCurrentSlot();
                }
            }
        }

        ImGui.Separator();
        ImGui.BeginChild("Scrollable_bar");
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float avail = ImGui.GetContentRegionAvail().X;
        float thumb = MathF.Max(1f, (avail - (sbColCount - 1) * spacing) / sbColCount);
        sidebarColumns = sbColCount;

        for (int i = 0; i < slots.Count; i++)
        {
            if (i % sidebarColumns != 0)
            {
                ImGui.SameLine();
            }
            ImGui.PushID(i);
            ImGui.Image((IntPtr)slots[i].Thumbnail.Id, new Vector2(thumb, thumb));
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"Slot {i}  {slots[i].Width}x{slots[i].Height}");
            }
            if (ImGui.IsItemClicked())
            {
                SelectSlot(i);
            }

            if (i == currentIndex)
            {
                var min = ImGui.GetItemRectMin();
                var max = ImGui.GetItemRectMax();
                var winPos = ImGui.GetWindowPos();
                ImGui.GetWindowDrawList().AddRect(min, max, ImGui.GetColorU32(new Vector4(0f, 1f, 1f, 1f)), 0f, ImDrawFlags.None, 3f);
                if (scrollToSelected)
                {
                    float winMaxY = winPos.Y + ImGui.GetWindowHeight();
                    if (min.Y < winPos.Y)
                    {
                        ImGui.SetScrollHereY(0f);
                    }
                    else if (max.Y > winMaxY)
                    {
                        ImGui.SetScrollHereY(1f);
                    }
                    scrollToSelected = false;
                }
            }
            ImGui.PopID();
        }

        ImGui.EndChild();
        ImGui.EndChild();
    }

    void DrawViewport()
    {
        ImGui.BeginChild("viewport", Vector2.Zero);
        if (!hasViewTexture)
        {
            ImGui.TextDisabled("Select a slot to preview.");
            ImGui.EndChild();
            return;
        }

        ImGui.Text($"{selectedMrgName}  slot {currentIndex}  {slots[currentIndex].Width}x{slots[currentIndex].Height}");
        Vector2 region = ImGui.GetContentRegionAvail();
        HandleMouseZoom(region);
        HandleMousePan();
        DrawCheckerboard(region);
        Vector2 sz = new Vector2(viewTexture.Width, viewTexture.Height) * zoom;
        Vector2 pos = region * 0.5f + pan - sz * 0.5f;
        ImGui.SetCursorPos(pos);
        ImGui.Image((IntPtr)viewTexture.Id, sz);
        ImGui.EndChild();
    }

    void HandleMouseZoom(Vector2 region)
    {
        if (!ImGui.IsWindowHovered())
        {
            return;
        }
        float wheel = ImGui.GetIO().MouseWheel;
        if (wheel == 0f)
        {
            return;
        }
        Vector2 mouse = ImGui.GetIO().MousePos - ImGui.GetWindowPos();
        Vector2 world = (mouse - region * 0.5f - pan) / zoom;
        zoom = Math.Clamp(zoom * (1f + wheel * 0.1f), 0.1f, 30f);
        pan = mouse - region * 0.5f - world * zoom;
    }

    void HandleMousePan()
    {
        var io = ImGui.GetIO();
        if (!ImGui.IsWindowHovered())
        {
            return;
        }
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
        {
            dragging = true;
            dragStartMouse = io.MousePos;
            dragStartPan = pan;
        }
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            dragging = false;
        }
        if (dragging)
        {
            pan = dragStartPan + (io.MousePos - dragStartMouse);
        }
    }

    void DrawCheckerboard(Vector2 region)
    {
        const int cell = 16;
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetWindowPos();
        for (int y = 0; y < region.Y; y += cell)
        {
            for (int x = 0; x < region.X; x += cell)
            {
                uint col = ((x / cell + y / cell) % 2) == 0 ? 0xFF2A2A2A : 0xFF3A3A3A;
                draw.AddRectFilled(origin + new Vector2(x, y), origin + new Vector2(x + cell, y + cell), col);
            }
        }
    }

    void DrawEditor()
    {
        ImGui.BeginChild("editor_root", Vector2.Zero);
        canvas!.Update();

        float totalW = ImGui.GetContentRegionAvail().X;
        float halfW = totalW * 0.5f;
        float spacingX = ImGui.GetStyle().ItemSpacing.X;
        float startX = ImGui.GetCursorPosX();

        bool isPicMini = selectedMrgName.Equals(TextureSlotManager.PicMiniMrg, StringComparison.OrdinalIgnoreCase);

        int leftRow1Count = isPicMini ? (cropMode ? 8 : 7) : 6;
        float separationWidth = 7f;
        float leftBtnW = MathF.Max(28f, (halfW - 2 * (separationWidth + spacingX) - spacingX * (leftRow1Count - 1)) / leftRow1Count);

        float row1Y = ImGui.GetCursorPosY();
        float resetZoomX = 0f;

        DrawToolButton("Pencil", PaintCanvas.Tool.Pencil, leftBtnW);
        ImGui.SameLine();
        DrawToolButton("Eraser", PaintCanvas.Tool.Eraser, leftBtnW);
        ImGui.SameLine();
        DrawToolButton("Fill", PaintCanvas.Tool.Fill, leftBtnW);
        ImGui.SameLine();
        DrawToolButton("Eyedrop", PaintCanvas.Tool.Eyedropper, leftBtnW);

        ImGui.SameLine();
        VerticalSeparator();

        if (ImGui.Button("Undo", new Vector2(leftBtnW, 0)) && canvas.CanUndo)
        {
            canvas.Undo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Redo", new Vector2(leftBtnW, 0)) && canvas.CanRedo)
        {
            canvas.Redo();
        }

        if (isPicMini)
        {
            ImGui.SameLine();
            VerticalSeparator();
            if (cropMode)
            {
                resetZoomX = ImGui.GetCursorPosX();
                if (ImGui.Button("Edit PicMini", new Vector2(leftBtnW * 1.5f, 0)))
                {
                    ApplyCurrentCrop();
                    cropMode = false;
                }
                ImGui.SameLine();
                ImGui.TextDisabled($"{cropZoom:0.0}x");
                ImGui.SameLine();
                ImGui.Checkbox("Centre on mouse", ref centerMouseOnZoom);
            }
            else
            {
                if (ImGui.Button("Crop from Picture", new Vector2(leftBtnW * 2f, 0)))
                {
                    OpenCropPicker();
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.GetCursorPosX() < startX + halfW)
        {
            ImGui.SetCursorPosX(startX + halfW);
        }
        VerticalSeparator();

        float rightAvail = totalW - (ImGui.GetCursorPosX() - startX) - spacingX;
        float importBtnW = MathF.Max(60f, rightAvail * 0.5f - spacingX * 0.5f);
        float importStartX = ImGui.GetCursorPosX();

        if (ImGui.Button("Import PNG", new Vector2(importBtnW, 0)))
        {
            ImportPng();
        }
        ImGui.SameLine();
        if (ImGui.Button("Export PNG", new Vector2(importBtnW, 0)))
        {
            canvas.SaveAs();
        }


        ImGui.SetCursorPosX(startX);
        float brushSize = canvas.BrushSize;
        ImGui.Text("Brush Size");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f);
        if (ImGui.SliderFloat("##sz", ref brushSize, 1f, 64f))
        {
            brushSize = MathF.Round(brushSize);
            canvas.BrushSize = brushSize;
        }
        ImGui.SameLine();
        ImGui.Text("Colour");
        ImGui.SameLine();
        Vector4 col = ColorToVec4(brushColor);
        if (ImGui.ColorEdit4("##col", ref col, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
        {
            var newColor = Vec4ToColor(col);
            if (selectedPaletteIdx >= 0 && selectedPaletteIdx < palette.Count)
            {
                GameImageManager.RemapColourInBitmap(canvas.Bitmap, palette[selectedPaletteIdx], newColor);
                palette[selectedPaletteIdx] = newColor;
                canvas.ForceRefresh();
            }
            brushColor = newColor;
        }
        canvas.ActiveColor = brushColor;

        if (canvas.ActiveTool == PaintCanvas.Tool.Eraser)
        {
            ImGui.SameLine();
            ImGui.Text("Opacity");
            ImGui.SameLine();
            float opacity = canvas.Opacity;
            ImGui.SetNextItemWidth(100f);
            if (ImGui.SliderFloat("##op", ref opacity, 0f, 1f))
            {
                canvas.Opacity = opacity;
            }
        }

        ImGui.SetCursorPos(new Vector2(importStartX, ImGui.GetCursorPosY() - ImGui.GetFrameHeightWithSpacing()));
        if (ImGui.Button("Import Options", new Vector2(importBtnW, 0)))
        {
            ImGui.OpenPopup("import_options");
        }
        if (ImGui.BeginPopup("import_options"))
        {
            ImGui.TextDisabled("Quantisation method:");
            if (ImGui.RadioButton("Median-cut", quantMode == QuantMode.MedianCut))
            {
                quantMode = QuantMode.MedianCut;
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Octree", quantMode == QuantMode.Octree))
            {
                quantMode = QuantMode.Octree;
            }
            ImGui.Separator();
            ImGui.Checkbox("Crop to fit", ref cropOnImport);
            ImGui.EndPopup();
        }
        if (isPicMini && isEditMode)
        {
            ImGui.SameLine();
            if (ImGui.Button("Filtering Options", new Vector2(importBtnW, 0)))
            {
                ImGui.OpenPopup("filtering_options");
            }
            if (ImGui.BeginPopup("filtering_options"))
            {
                if (ImGui.RadioButton("Point", picMiniFilter == PicMiniFilter.Point))
                {
                    picMiniFilter = PicMiniFilter.Point;
                }
                if (ImGui.RadioButton("Bilinear", picMiniFilter == PicMiniFilter.Bilinear))
                {
                    picMiniFilter = PicMiniFilter.Bilinear;
                }
                if (ImGui.RadioButton("Trilinear", picMiniFilter == PicMiniFilter.Trilinear))
                {
                    picMiniFilter = PicMiniFilter.Trilinear;
                }
                ImGui.EndPopup();
            }


            canvas.SetTextureFilter(ToTextureFilter(picMiniFilter));
        }

        if (cropMode && resetZoomX > 0)
        {
            ImGui.SetCursorPos(new Vector2(resetZoomX, row1Y + ImGui.GetFrameHeightWithSpacing()));
            if (ImGui.Button("Reset Zoom", new Vector2(leftBtnW * 1.5f, 0)))
            {
                cropZoom = 1f;
                ApplyCurrentCrop();
            }
        }



        Vector2 remaining = ImGui.GetContentRegionAvail();
        float paletteColW = MathF.Floor(remaining.X * 0.2f);
        float canvasColW = remaining.X - paletteColW - spacingX;
        float canvasHeight = remaining.Y * 0.9f;

        ImGui.BeginChild("palette_colour", new Vector2(paletteColW, 0), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar);
        DrawPaletteSwatches();
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("canvas_colour", new Vector2(canvasColW, 0), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (cropMode)
        {
            DrawCropPicker();
        }
        else
        {
            DrawCanvasImageCentred(canvasColW, canvasHeight);
        }
        ImGui.EndChild();

        ImGui.EndChild();
    }

    void DrawToolButton(string label, PaintCanvas.Tool tool, float width = 0)
    {
        bool active = canvas?.ActiveTool == tool;
        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0.6f, 0f, 1f));
        }
        var buttonSize = width > 0 ? new Vector2(width, 0) : Vector2.Zero;
        if (ImGui.Button(label, buttonSize) && canvas != null)
        {
            canvas.ActiveTool = tool;
        }
        if (active)
        {
            ImGui.PopStyleColor();
        }
    }

    void DrawPaletteSwatches()
    {
        float availX = ImGui.GetContentRegionAvail().X;
        const int cols = 8;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float sw = MathF.Max(6f, (availX - spacing * (cols - 1)) / cols);
        var drawList = ImGui.GetWindowDrawList();

        for (int i = 0; i < palette.Count; i++)
        {
            if (i % cols != 0)
            {
                ImGui.SameLine();
            }
            var c = palette[i];
            ImGui.PushID(i);
            ImGui.ColorButton($"##p{i}", ColorToVec4(c),
                ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoBorder,
                new Vector2(sw, sw));

            if (i == selectedPaletteIdx)
            {
                var mn = ImGui.GetItemRectMin();
                var mx = ImGui.GetItemRectMax();
                drawList.AddRect(mn - new Vector2(1, 1), mx + new Vector2(1, 1), 0xFFFFFFFF, 0f, ImDrawFlags.None, 2f);
                drawList.AddRect(mn - new Vector2(2, 2), mx + new Vector2(2, 2), 0xFF000000, 0f, ImDrawFlags.None, 1f);
            }

            if (ImGui.IsItemClicked())
            {
                selectedPaletteIdx = i;
                brushColor = c;
                if (canvas != null)
                {
                    canvas.ActiveColor = c;
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"[{i}] #{c.Red:X2}{c.Green:X2}{c.Blue:X2}{c.Alpha:X2}");
            }
            ImGui.PopID();
        }
    }

    void DrawCanvasImageCentred(float colW, float maxH)
    {
        if (canvas == null)
        {
            return;
        }
        float aspect = (float)canvas.Width / canvas.Height;
        float fitByWidth = colW / aspect;
        float w, h;
        if (fitByWidth <= maxH)
        {
            w = colW;
            h = fitByWidth;
        }
        else
        {
            h = maxH;
            w = maxH * aspect;
        }

        float availH = ImGui.GetContentRegionAvail().Y;
        float padY = MathF.Max(0f, (availH - h) * 0.5f);
        if (padY > 0)
        {
            ImGui.Dummy(new Vector2(1, padY));
        }

        float padX = MathF.Max(0f, (colW - w) * 0.5f);
        if (padX > 0)
        {
            ImGui.Dummy(new Vector2(padX, 1));
            ImGui.SameLine();
        }

        Vector2 imagePos = ImGui.GetCursorScreenPos();
        ImGui.Image(canvas.GetTexturePtr(), new Vector2(w, h));
        HandleCanvasInput(imagePos, new Vector2(w, h));
    }

    void HandleCanvasInput(Vector2 imagePos, Vector2 displaySize)
    {

        if (canvas == null)
        {
            return;
        }
        Vector2 mouse = ImGui.GetMousePos();
        bool hovered = ImGui.IsItemHovered();
        bool down = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        bool clicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);

        if (hovered)
        {
            if (canvas.ActiveTool == PaintCanvas.Tool.Pencil || canvas.ActiveTool == PaintCanvas.Tool.Eraser)
            {
                float circleRadius = canvas.BrushSize * (displaySize.X / canvas.Width) * 0.5f;
                ImGui.GetWindowDrawList().AddCircle(mouse, circleRadius, 0xFFFFFFFF, 32, 1.5f);
                ImGui.GetWindowDrawList().AddCircle(mouse, circleRadius + 1f, 0x88000000, 32, 1f);
                ImGui.SetMouseCursor(ImGuiMouseCursor.None);
            }




        }

        if (!hovered)
        {
            if (wasPainting)
            {
                canvas.EndStroke();
                wasPainting = false;
            }
            return;
        }


        float bx = (mouse.X - imagePos.X) / displaySize.X * canvas.Width;
        float by = (mouse.Y - imagePos.Y) / displaySize.Y * canvas.Height;
        int ibx = (int)Math.Clamp(bx, 0, canvas.Width - 1);
        int iby = (int)Math.Clamp(by, 0, canvas.Height - 1);

        if (down)
        {
            if (clicked)
            {
                switch (canvas.ActiveTool)
                {
                    case PaintCanvas.Tool.Fill:
                        canvas.FloodFill(ibx, iby);
                        wasPainting = true;
                        break;
                    case PaintCanvas.Tool.Eyedropper:
                        var picked = canvas.Bitmap.GetPixel(ibx, iby);
                        brushColor = picked;
                        canvas.ActiveColor = picked;
                        selectedPaletteIdx = palette.FindIndex(c => c == picked);
                        canvas.ActiveTool = PaintCanvas.Tool.Pencil;
                        break;
                    default:
                        canvas.BeginStroke();
                        canvas.PaintAt(bx, by);
                        wasPainting = true;
                        break;
                }
            }
            else if (wasPainting && canvas.ActiveTool is PaintCanvas.Tool.Pencil or PaintCanvas.Tool.Eraser)
            {
                canvas.PaintAt(bx, by);
            }
        }
        else if (wasPainting)
        {
            canvas.EndStroke();
            wasPainting = false;
        }
    }

    void DrawCropPicker()
    {
        if (picMiniFullCanvas == null)
        {
            return;
        }

        var dl = ImGui.GetWindowDrawList();
        int fullW = picMiniFullCanvas.Width;
        int fullH = picMiniFullCanvas.Height;
        var canvasPos = ImGui.GetCursorScreenPos();
        var availSize = ImGui.GetContentRegionAvail();

        cropSrcW = Math.Clamp((int)MathF.Round(40f / cropZoom), 1, fullW);
        cropSrcH = Math.Clamp((int)MathF.Round(32f / cropZoom), 1, fullH);
        cropOrigin.X = Math.Clamp(cropOrigin.X, 0, fullW - cropSrcW);
        cropOrigin.Y = Math.Clamp(cropOrigin.Y, 0, fullH - cropSrcH);

        float imgAspect = (float)fullW / fullH;
        float canvasAspect = availSize.X / availSize.Y;
        Vector2 imageSize = imgAspect > canvasAspect
            ? new Vector2(availSize.X, availSize.X / imgAspect)
            : new Vector2(availSize.Y * imgAspect, availSize.Y);
        Vector2 imagePos = new(canvasPos.X + (availSize.X - imageSize.X) / 2f, canvasPos.Y);

        float scaleX = imageSize.X / fullW;
        float scaleY = imageSize.Y / fullH;

        ImGui.PushClipRect(canvasPos, canvasPos + availSize, true);
        dl.AddImage(picMiniFullCanvas.GetTexturePtr(), imagePos, imagePos + imageSize);
        ImGui.SetCursorScreenPos(canvasPos);
        ImGui.InvisibleButton("##croparea", availSize);

        Vector2 mouse = ImGui.GetMousePos();
        bool mouseInView = ImGui.IsItemHovered();

        if (mouseInView)
        {
            float wheel = ImGui.GetIO().MouseWheel;
            if (wheel != 0f)
            {
                cropZoom = Math.Clamp(cropZoom * MathF.Pow(1.1f, wheel), 0.2f, 16f);
                if (centerMouseOnZoom)
                {
                    CenterCropOnMouse(mouse, imagePos, scaleX, scaleY, fullW, fullH);
                }
                ApplyCurrentCrop();
            }

            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                CenterCropOnMouse(mouse, imagePos, scaleX, scaleY, fullW, fullH);
            }

            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                ApplyCurrentCrop();
            }
        }

        Vector2 cropMin = imagePos + new Vector2(cropOrigin.X * scaleX, cropOrigin.Y * scaleY);
        Vector2 cropMax = cropMin + new Vector2(cropSrcW * scaleX, cropSrcH * scaleY);
        dl.AddRectFilled(cropMin, cropMax, 0x33FFFFFF);
        dl.AddRect(cropMin, cropMax, 0xFFFFFFFF, 0, ImDrawFlags.None, 2f);
        dl.AddRect(cropMin - Vector2.One, cropMax + Vector2.One, 0xFF000000, 0, ImDrawFlags.None, 1f);

        ImGui.PopClipRect();

        if (mouseInView)
        {
            ImGui.SetTooltip($"({cropOrigin.X}, {cropOrigin.Y})  zoom {cropZoom:0.00}x  src {cropSrcW}x{cropSrcH}");
        }
    }

    void CenterCropOnMouse(Vector2 mouse, Vector2 imagePos, float scaleX, float scaleY, int fullW, int fullH)
    {
        int imgX = (int)((mouse.X - imagePos.X) / scaleX);
        int imgY = (int)((mouse.Y - imagePos.Y) / scaleY);
        cropOrigin.X = Math.Clamp(imgX - cropSrcW / 2, 0, fullW - cropSrcW);
        cropOrigin.Y = Math.Clamp(imgY - cropSrcH / 2, 0, fullH - cropSrcH);
    }

    void ApplyCurrentCrop()
    {
        if (picMiniFullCanvas == null || canvas == null)
        {
            return;
        }
        int fullW = picMiniFullCanvas.Width;
        int fullH = picMiniFullCanvas.Height;

        cropSrcW = Math.Clamp((int)MathF.Round(40f / cropZoom), 1, fullW);
        cropSrcH = Math.Clamp((int)MathF.Round(32f / cropZoom), 1, fullH);
        cropOrigin.X = Math.Clamp(cropOrigin.X, 0, fullW - cropSrcW);
        cropOrigin.Y = Math.Clamp(cropOrigin.Y, 0, fullH - cropSrcH);

        using var cropped = new SKBitmap(40, 32, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var sk = new SKCanvas(cropped);
        var srcRect = new SKRect(cropOrigin.X, cropOrigin.Y, cropOrigin.X + cropSrcW, cropOrigin.Y + cropSrcH);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.None };
        sk.DrawBitmap(picMiniFullCanvas.Bitmap, srcRect, new SKRect(0, 0, 40, 32), paint);
        sk.Flush();

        canvas.LoadBitmap(cropped);
        palette = TextureSlotManager.BuildPalette(cropped);
    }

    void LoadMrg(string name, TextureMrgKind kind)
    {
        TextureSlotManager.FreeSlots(slots);
        FreePicCanvas();
        CloseCanvas();
        isEditMode = false;
        cropMode = false;
        currentIndex = -1;
        selectedMrgName = name;
        palette.Clear();

        mrgBytes = FileLoader.Instance.GetFileBytesFromDisk(TextureSlotManager.IsoPath(name));
        slots = kind == TextureMrgKind.Icon
            ? TextureSlotManager.LoadIconSlots(mrgBytes)
            : TextureSlotManager.LoadRh2Slots(mrgBytes);

        if (slots.Count > 0)
        {
            SelectSlot(0);
        }
    }

    void SelectSlot(int index)
    {
        if (index < 0 || index >= slots.Count)
        {
            return;
        }
        currentIndex = index;
        cropMode = false;
        FreePicCanvas();

        if (hasViewTexture)
        {
            Raylib.UnloadTexture(viewTexture);
            hasViewTexture = false;
        }

        try
        {
            var bmp = TextureSlotManager.DecodeSlot(slots[index]);
            palette = slots[index].Kind == TextureMrgKind.Icon
                ? TextureSlotManager.BuildPaletteFromGameTexture()
                : TextureSlotManager.BuildPalette(bmp);

            viewTexture = TextureSlotManager.BitmapToTexture(bmp);
            hasViewTexture = true;
            zoom = 2f;
            pan = Vector2.Zero;

            if (isEditMode)
            {
                OpenCanvas(index, bmp);
            }
            else
            {
                bmp.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SelectSlot {index}: {ex.Message}");
        }

        selectedPaletteIdx = -1;
    }

    void OpenCanvas(int index, SKBitmap? bmp = null)
    {
        if (index < 0 || index >= slots.Count)
        {
            return;
        }
        bool ownBmp = bmp == null;
        if (bmp == null)
        {
            bmp = TextureSlotManager.DecodeSlot(slots[index]);
        }
        if (canvas == null)
        {
            canvas = new PaintCanvas(bmp);
        }
        else
        {
            canvas.LoadBitmap(bmp);
        }
        canvas.ClearStack();
        if (ownBmp)
        {
            bmp.Dispose();
        }
    }

    void OpenCropPicker()
    {
        if (currentIndex < 0 || currentIndex >= slots.Count || !TextureSlotManager.IsoHasFile(TextureSlotManager.PictureMrg))
        {
            return;
        }

        int pictureIndex = currentIndex < 683 ? currentIndex : currentIndex + 171;
        byte[] picBytes = FileLoader.Instance.GetFileBytesFromDisk(TextureSlotManager.IsoPath(TextureSlotManager.PictureMrg));
        var offsets = AssetExtractor.FindAllRh2Offsets(picBytes);
        if (pictureIndex >= offsets.Count)
        {
            pictureIndex = Math.Min(currentIndex, offsets.Count - 1);
        }
        if (pictureIndex < 0)
        {
            return;
        }

        var decodedTex = Rh2Decoder.Decode(picBytes, (int)offsets[pictureIndex]);
        if (decodedTex.IsEmpty)
        {
            return;
        }

        picMiniFullCanvas?.Free();
        var bmp = TextureSlotManager.RgbaToBitmap(decodedTex.Width, decodedTex.Height, decodedTex.Rgba);
        picMiniFullCanvas = new PaintCanvas(bmp);
        bmp.Dispose();

        cropOrigin = new SKPointI(0, 0);
        cropZoom = 1f;
        cropMode = true;
        ApplyCurrentCrop();
    }

    void CloseCanvas()
    {
        canvas?.Free();
        canvas = null;
    }

    void FreePicCanvas()
    {
        picMiniFullCanvas?.Free();
        picMiniFullCanvas = null;
        cropMode = false;
    }

    void ImportPng()
    {
        if (canvas == null || currentIndex < 0)
        {
            return;
        }
        var slot = slots[currentIndex];
        var r = Dialog.FileOpen("png,jpg,jpeg,webp");
        if (!r.IsOk)
        {
            return;
        }

        var imported = GameImageManager.LoadAndPrepareImage(r.Path, slot.Width, slot.Height, cropOnImport);
        byte[] rgba = TextureSlotManager.BitmapToRgba(imported);
        imported.Dispose();
        if (quantMode == QuantMode.Octree)
        {
            rgba = TextureSlotManager.OctreePreQuantise(rgba, slot.Width * slot.Height);
        }
        var quantised = TextureSlotManager.RgbaToBitmap(slot.Width, slot.Height, rgba);
        canvas.LoadBitmap(quantised);
        canvas.ClearStack();
        quantised.Dispose();
        palette = TextureSlotManager.BuildPalette(rgba, slot.Width * slot.Height);
        selectedPaletteIdx = -1;
    }

    void SaveCurrentSlot()
    {
        if (canvas == null || currentIndex < 0 || mrgBytes == null)
        {
            return;
        }

        byte[] newSlotBytes;
        try
        {
            newSlotBytes = TextureSlotManager.EncodeSlot(slots[currentIndex], canvas.Bitmap);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Encode failed: {ex.Message}");
            return;
        }

        TextureSlotManager.PatchMrg(mrgBytes, slots, currentIndex, newSlotBytes);

        var newThumb = TextureSlotManager.BitmapToTexture(canvas.Bitmap);
        Raylib.UnloadTexture(slots[currentIndex].Thumbnail);
        slots[currentIndex].Bytes = newSlotBytes;
        slots[currentIndex].Thumbnail = newThumb;

        try
        {
            long fileBase = FileLoader.Instance.GetStartOfFile(TextureSlotManager.IsoPath(selectedMrgName));
            if (fileBase >= 0)
            {
                FileLoader.Instance.WriteBytes(fileBase, mrgBytes);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Write to ISO failed: {ex.Message}");
        }

        RefreshAfterSave(currentIndex);
    }

    void RefreshAfterSave(int index)
    {
        if (index < 0 || index >= slots.Count)
        {
            return;
        }

        try
        {
            var bmp = TextureSlotManager.DecodeSlot(slots[index]);
            palette = slots[index].Kind == TextureMrgKind.Icon
                ? TextureSlotManager.BuildPaletteFromGameTexture()
                : TextureSlotManager.BuildPalette(bmp);

            if (hasViewTexture)
            {
                Raylib.UnloadTexture(viewTexture);
            }
            viewTexture = TextureSlotManager.BitmapToTexture(bmp);
            hasViewTexture = true;

            if (isEditMode && canvas != null)
            {
                canvas.LoadBitmap(bmp);
            }
            else
            {
                bmp.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RefreshAfterSave {index}: {ex.Message}");
        }

        selectedPaletteIdx = -1;
    }

    void HandleKeyboard()
    {
        if (slots.Count == 0 || sidebarColumns <= 0)
        {
            return;
        }
        int newIndex = currentIndex;
        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow))
        {
            newIndex++;
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
        {
            newIndex--;
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
        {
            newIndex += sidebarColumns;
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
        {
            newIndex -= sidebarColumns;
        }
        newIndex = Math.Clamp(newIndex, 0, slots.Count - 1);
        if (newIndex != currentIndex)
        {
            SelectSlot(newIndex);
            scrollToSelected = true;
        }

        if (!ImGui.IsAnyItemActive())
        {
            bool ctrl = ImGui.GetIO().KeyCtrl;
            if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Z) && canvas?.CanUndo == true)
            {
                canvas.Undo();
            }
            if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Y) && canvas?.CanRedo == true)
            {
                canvas.Redo();
            }
            if (ctrl && ImGui.IsKeyPressed(ImGuiKey.S))
            {
                SaveCurrentSlot();
            }

            if (canvas != null && isEditMode)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.Q)) canvas.ActiveTool = PaintCanvas.Tool.Pencil;
                if (ImGui.IsKeyPressed(ImGuiKey.W)) canvas.ActiveTool = PaintCanvas.Tool.Eraser;
                if (ImGui.IsKeyPressed(ImGuiKey.E)) canvas.ActiveTool = PaintCanvas.Tool.Fill;
                if (ImGui.IsKeyPressed(ImGuiKey.R)) canvas.ActiveTool = PaintCanvas.Tool.Eyedropper;
            }
        }
    }

    static void VerticalSeparator()
    {
        float h = ImGui.GetFrameHeight();
        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(pos, pos + new Vector2(0, h), ImGui.GetColorU32(ImGuiCol.Separator), 1f);
        ImGui.Dummy(new Vector2(6f, h));
        ImGui.SameLine();
    }

    static Vector4 ColorToVec4(SKColor c)
    {
        return new(c.Red / 255f, c.Green / 255f, c.Blue / 255f, c.Alpha / 255f);
    }

    static SKColor Vec4ToColor(Vector4 v)
    {
        return new((byte)(v.X * 255), (byte)(v.Y * 255), (byte)(v.Z * 255), (byte)(v.W * 255));
    }

    static TextureFilter ToTextureFilter(PicMiniFilter mode) => mode switch {
        PicMiniFilter.Point => TextureFilter.Point,
        PicMiniFilter.Bilinear => TextureFilter.Bilinear,
        PicMiniFilter.Trilinear => TextureFilter.Trilinear,
        _ => TextureFilter.Point
    };

    public void Dispose()
    {
        TextureSlotManager.FreeSlots(slots);
        FreePicCanvas();
        CloseCanvas();
        if (hasViewTexture)
        {
            Raylib.UnloadTexture(viewTexture);
        }
    }
}