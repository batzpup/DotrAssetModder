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
    int selectedPaletteIndex = -1;
    SKColor brushColor = SKColors.Black;
    bool wasPainting;

    int sidebarColumnCount = 4;
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
    int cropSourceWidth = 40;
    int cropSourceHeight = 32;
    bool centerMouseOnZoom = false;

    PicMiniFilter picMiniFilter = PicMiniFilter.Point;

    public TextureEditorWindow()
    {
        
    }
    public void Render()
    {
        DrawSidebar(GuiContext.GetScreenPixelsFromPercent(25, 10).X);
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
            foreach (var name in TextureSlotManager.GetTextureMrgs())
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
        float availableWidth = ImGui.GetContentRegionAvail().X;
        float thumbnailSize = MathF.Max(1f, (availableWidth - (sidebarColumnCount - 1) * spacing) / sidebarColumnCount);
        sidebarColumns = sidebarColumnCount;

        for (int i = 0; i < slots.Count; i++)
        {
            if (i % sidebarColumns != 0)
            {
                ImGui.SameLine();
            }
            ImGui.PushID(i);
            ImGui.Image((IntPtr)slots[i].Thumbnail.Id, new Vector2(thumbnailSize, thumbnailSize));
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
                var itemRectMin = ImGui.GetItemRectMin();
                var itemRectMax = ImGui.GetItemRectMax();
                var windowPosition = ImGui.GetWindowPos();
                ImGui.GetWindowDrawList().AddRect(itemRectMin, itemRectMax, ImGui.GetColorU32(new Vector4(0f, 1f, 1f, 1f)), 0f, ImDrawFlags.None, 3f);
                if (scrollToSelected)
                {
                    float windowBottomY = windowPosition.Y + ImGui.GetWindowHeight();
                    if (itemRectMin.Y < windowPosition.Y)
                    {
                        ImGui.SetScrollHereY(0f);
                    }
                    else if (itemRectMax.Y > windowBottomY)
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
        Vector2 textureSize = new Vector2(viewTexture.Width, viewTexture.Height) * zoom;
        Vector2 texturePosition = region * 0.5f + pan - textureSize * 0.5f;
        ImGui.SetCursorPos(texturePosition);
        ImGui.Image((IntPtr)viewTexture.Id, textureSize);
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
        var imGuiIo = ImGui.GetIO();
        if (!ImGui.IsWindowHovered())
        {
            return;
        }
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
        {
            dragging = true;
            dragStartMouse = imGuiIo.MousePos;
            dragStartPan = pan;
        }
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            dragging = false;
        }
        if (dragging)
        {
            pan = dragStartPan + (imGuiIo.MousePos - dragStartMouse);
        }
    }

    void DrawCheckerboard(Vector2 region)
    {
        const int cell = 16;
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetWindowPos();
        for (int y = 0; y < region.Y; y += cell)
        {
            for (int x = 0; x < region.X; x += cell)
            {
                uint cellColour = ((x / cell + y / cell) % 2) == 0 ? 0xFF2A2A2A : 0xFF3A3A3A;
                drawList.AddRectFilled(origin + new Vector2(x, y), origin + new Vector2(x + cell, y + cell), cellColour);
            }
        }
    }

    void DrawEditor()
    {
        ImGui.BeginChild("editor_root", Vector2.Zero);
        canvas!.Update();

        float totalWidth = ImGui.GetContentRegionAvail().X;
        float halfWidth = totalWidth * 0.5f;
        float spacingX = ImGui.GetStyle().ItemSpacing.X;
        float startX = ImGui.GetCursorPosX();

        bool isPicMini = selectedMrgName.Equals(TextureSlotManager.PictureMiniMrg, StringComparison.OrdinalIgnoreCase);

        int leftRow1Count = isPicMini ? (cropMode ? 8 : 7) : 6;
        float separationWidth = 7f;
        float leftButtonWidth = MathF.Max(28f, (halfWidth - 2 * (separationWidth + spacingX) - spacingX * (leftRow1Count - 1)) / leftRow1Count);

        float firstRowY = ImGui.GetCursorPosY();
        float resetZoomX = 0f;

        DrawToolButton("Pencil", PaintCanvas.Tool.Pencil, leftButtonWidth);
        ImGui.SameLine();
        DrawToolButton("Eraser", PaintCanvas.Tool.Eraser, leftButtonWidth);
        ImGui.SameLine();
        DrawToolButton("Fill", PaintCanvas.Tool.Fill, leftButtonWidth);
        ImGui.SameLine();
        DrawToolButton("Eyedrop", PaintCanvas.Tool.Eyedropper, leftButtonWidth);

        ImGui.SameLine();
        VerticalSeparator();

        if (ImGui.Button("Undo", new Vector2(leftButtonWidth, 0)) && canvas.CanUndo)
        {
            canvas.Undo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Redo", new Vector2(leftButtonWidth, 0)) && canvas.CanRedo)
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
                if (ImGui.Button("Edit PicMini", new Vector2(leftButtonWidth * 1.5f, 0)))
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
                if (ImGui.Button("Crop from Picture", new Vector2(leftButtonWidth * 2f, 0)))
                {
                    OpenCropPicker();
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.GetCursorPosX() < startX + halfWidth)
        {
            ImGui.SetCursorPosX(startX + halfWidth);
        }
        VerticalSeparator();

        float rightAvailableWidth = totalWidth - (ImGui.GetCursorPosX() - startX) - spacingX;
        float importButtonWidth = MathF.Max(60f, rightAvailableWidth * 0.5f - spacingX * 0.5f);
        float importStartX = ImGui.GetCursorPosX();

        if (ImGui.Button("Import PNG", new Vector2(importButtonWidth, 0)))
        {
            ImportPng();
        }
        ImGui.SameLine();
        if (ImGui.Button("Export PNG", new Vector2(importButtonWidth, 0)))
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
        Vector4 brushColourVector = ColorToVec4(brushColor);
        if (ImGui.ColorEdit4("##col", ref brushColourVector, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
        {
            var newColor = Vec4ToColor(brushColourVector);
            if (selectedPaletteIndex >= 0 && selectedPaletteIndex < palette.Count)
            {
                GameImageManager.RemapColourInBitmap(canvas.Bitmap, palette[selectedPaletteIndex], newColor);
                palette[selectedPaletteIndex] = newColor;
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
        if (ImGui.Button("Import Options", new Vector2(importButtonWidth, 0)))
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
            if (ImGui.Button("Filtering Options", new Vector2(importButtonWidth, 0)))
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
            ImGui.SetCursorPos(new Vector2(resetZoomX, firstRowY + ImGui.GetFrameHeightWithSpacing()));
            if (ImGui.Button("Reset Zoom", new Vector2(leftButtonWidth * 1.5f, 0)))
            {
                cropZoom = 1f;
                ApplyCurrentCrop();
            }
        }



        Vector2 remaining = ImGui.GetContentRegionAvail();
        float paletteColumnWidth = MathF.Floor(remaining.X * 0.2f);
        float canvasColumnWidth = remaining.X - paletteColumnWidth - spacingX;
        float canvasHeight = remaining.Y * 0.9f;

        ImGui.BeginChild("palette_colour", new Vector2(paletteColumnWidth, 0), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar);
        DrawPaletteSwatches();
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("canvas_colour", new Vector2(canvasColumnWidth, 0), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (cropMode)
        {
            DrawCropPicker();
        }
        else
        {
            DrawCanvasImageCentred(canvasColumnWidth, canvasHeight);
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
        float availableWidth = ImGui.GetContentRegionAvail().X;
        const int columnCount = 8;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float swatchSize = MathF.Max(6f, (availableWidth - spacing * (columnCount - 1)) / columnCount);
        var drawList = ImGui.GetWindowDrawList();

        for (int i = 0; i < palette.Count; i++)
        {
            if (i % columnCount != 0)
            {
                ImGui.SameLine();
            }
            var swatchColour = palette[i];
            ImGui.PushID(i);
            ImGui.ColorButton($"##p{i}", ColorToVec4(swatchColour),
                ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoBorder,
                new Vector2(swatchSize, swatchSize));

            if (i == selectedPaletteIndex)
            {
                var swatchRectMin = ImGui.GetItemRectMin();
                var swatchRectMax = ImGui.GetItemRectMax();
                drawList.AddRect(swatchRectMin - new Vector2(1, 1), swatchRectMax + new Vector2(1, 1), 0xFFFFFFFF, 0f, ImDrawFlags.None, 2f);
                drawList.AddRect(swatchRectMin - new Vector2(2, 2), swatchRectMax + new Vector2(2, 2), 0xFF000000, 0f, ImDrawFlags.None, 1f);
            }

            if (ImGui.IsItemClicked())
            {
                selectedPaletteIndex = i;
                brushColor = swatchColour;
                if (canvas != null)
                {
                    canvas.ActiveColor = swatchColour;
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"[{i}] #{swatchColour.Red:X2}{swatchColour.Green:X2}{swatchColour.Blue:X2}{swatchColour.Alpha:X2}");
            }
            ImGui.PopID();
        }
    }

    void DrawCanvasImageCentred(float columnWidth, float maxHeight)
    {
        if (canvas == null)
        {
            return;
        }
        float aspect = (float)canvas.Width / canvas.Height;
        float fitByWidth = columnWidth / aspect;
        float displayWidth, displayHeight;
        if (fitByWidth <= maxHeight)
        {
            displayWidth = columnWidth;
            displayHeight = fitByWidth;
        }
        else
        {
            displayHeight = maxHeight;
            displayWidth = maxHeight * aspect;
        }

        float availableHeight = ImGui.GetContentRegionAvail().Y;
        float padY = MathF.Max(0f, (availableHeight - displayHeight) * 0.5f);
        if (padY > 0)
        {
            ImGui.Dummy(new Vector2(1, padY));
        }

        float padX = MathF.Max(0f, (columnWidth - displayWidth) * 0.5f);
        if (padX > 0)
        {
            ImGui.Dummy(new Vector2(padX, 1));
            ImGui.SameLine();
        }

        Vector2 imagePosition = ImGui.GetCursorScreenPos();
        ImGui.Image(canvas.GetTexturePtr(), new Vector2(displayWidth, displayHeight));
        HandleCanvasInput(imagePosition, new Vector2(displayWidth, displayHeight));
    }

    void HandleCanvasInput(Vector2 imagePosition, Vector2 displaySize)
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


        float bitmapX = (mouse.X - imagePosition.X) / displaySize.X * canvas.Width;
        float bitmapY = (mouse.Y - imagePosition.Y) / displaySize.Y * canvas.Height;
        int bitmapPixelX = (int)Math.Clamp(bitmapX, 0, canvas.Width - 1);
        int bitmapPixelY = (int)Math.Clamp(bitmapY, 0, canvas.Height - 1);

        if (down)
        {
            if (clicked)
            {
                switch (canvas.ActiveTool)
                {
                    case PaintCanvas.Tool.Fill:
                        canvas.FloodFill(bitmapPixelX, bitmapPixelY);
                        wasPainting = true;
                        break;
                    case PaintCanvas.Tool.Eyedropper:
                        var picked = canvas.Bitmap.GetPixel(bitmapPixelX, bitmapPixelY);
                        brushColor = picked;
                        canvas.ActiveColor = picked;
                        selectedPaletteIndex = palette.FindIndex(paletteColour => paletteColour == picked);
                        canvas.ActiveTool = PaintCanvas.Tool.Pencil;
                        break;
                    default:
                        canvas.BeginStroke();
                        canvas.PaintAt(bitmapX, bitmapY);
                        wasPainting = true;
                        break;
                }
            }
            else if (wasPainting && canvas.ActiveTool is PaintCanvas.Tool.Pencil or PaintCanvas.Tool.Eraser)
            {
                canvas.PaintAt(bitmapX, bitmapY);
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

        var drawList = ImGui.GetWindowDrawList();
        int fullWidth = picMiniFullCanvas.Width;
        int fullHeight = picMiniFullCanvas.Height;
        var canvasPosition = ImGui.GetCursorScreenPos();
        var availableSize = ImGui.GetContentRegionAvail();

        cropSourceWidth = Math.Clamp((int)MathF.Round(40f / cropZoom), 1, fullWidth);
        cropSourceHeight = Math.Clamp((int)MathF.Round(32f / cropZoom), 1, fullHeight);
        cropOrigin.X = Math.Clamp(cropOrigin.X, 0, fullWidth - cropSourceWidth);
        cropOrigin.Y = Math.Clamp(cropOrigin.Y, 0, fullHeight - cropSourceHeight);

        float imageAspect = (float)fullWidth / fullHeight;
        float canvasAspect = availableSize.X / availableSize.Y;
        Vector2 imageSize = imageAspect > canvasAspect
            ? new Vector2(availableSize.X, availableSize.X / imageAspect)
            : new Vector2(availableSize.Y * imageAspect, availableSize.Y);
        Vector2 imagePosition = new(canvasPosition.X + (availableSize.X - imageSize.X) / 2f, canvasPosition.Y);

        float scaleX = imageSize.X / fullWidth;
        float scaleY = imageSize.Y / fullHeight;

        ImGui.PushClipRect(canvasPosition, canvasPosition + availableSize, true);
        drawList.AddImage(picMiniFullCanvas.GetTexturePtr(), imagePosition, imagePosition + imageSize);
        ImGui.SetCursorScreenPos(canvasPosition);
        ImGui.InvisibleButton("##croparea", availableSize);

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
                    CenterCropOnMouse(mouse, imagePosition, scaleX, scaleY, fullWidth, fullHeight);
                }
                ApplyCurrentCrop();
            }

            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                CenterCropOnMouse(mouse, imagePosition, scaleX, scaleY, fullWidth, fullHeight);
            }

            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                ApplyCurrentCrop();
            }
        }

        Vector2 cropMin = imagePosition + new Vector2(cropOrigin.X * scaleX, cropOrigin.Y * scaleY);
        Vector2 cropMax = cropMin + new Vector2(cropSourceWidth * scaleX, cropSourceHeight * scaleY);
        drawList.AddRectFilled(cropMin, cropMax, 0x33FFFFFF);
        drawList.AddRect(cropMin, cropMax, 0xFFFFFFFF, 0, ImDrawFlags.None, 2f);
        drawList.AddRect(cropMin - Vector2.One, cropMax + Vector2.One, 0xFF000000, 0, ImDrawFlags.None, 1f);

        ImGui.PopClipRect();

        if (mouseInView)
        {
            ImGui.SetTooltip($"({cropOrigin.X}, {cropOrigin.Y})  zoom {cropZoom:0.00}x  src {cropSourceWidth}x{cropSourceHeight}");
        }
    }

    void CenterCropOnMouse(Vector2 mouse, Vector2 imagePosition, float scaleX, float scaleY, int fullWidth, int fullHeight)
    {
        int imageX = (int)((mouse.X - imagePosition.X) / scaleX);
        int imageY = (int)((mouse.Y - imagePosition.Y) / scaleY);
        cropOrigin.X = Math.Clamp(imageX - cropSourceWidth / 2, 0, fullWidth - cropSourceWidth);
        cropOrigin.Y = Math.Clamp(imageY - cropSourceHeight / 2, 0, fullHeight - cropSourceHeight);
    }

    void ApplyCurrentCrop()
    {
        if (picMiniFullCanvas == null || canvas == null)
        {
            return;
        }
        int fullWidth = picMiniFullCanvas.Width;
        int fullHeight = picMiniFullCanvas.Height;

        cropSourceWidth = Math.Clamp((int)MathF.Round(40f / cropZoom), 1, fullWidth);
        cropSourceHeight = Math.Clamp((int)MathF.Round(32f / cropZoom), 1, fullHeight);
        cropOrigin.X = Math.Clamp(cropOrigin.X, 0, fullWidth - cropSourceWidth);
        cropOrigin.Y = Math.Clamp(cropOrigin.Y, 0, fullHeight - cropSourceHeight);

        using var cropped = new SKBitmap(40, 32, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var croppedCanvas = new SKCanvas(cropped);
        var sourceRect = new SKRect(cropOrigin.X, cropOrigin.Y, cropOrigin.X + cropSourceWidth, cropOrigin.Y + cropSourceHeight);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.None };
        croppedCanvas.DrawBitmap(picMiniFullCanvas.Bitmap, sourceRect, new SKRect(0, 0, 40, 32), paint);
        croppedCanvas.Flush();

        canvas.LoadBitmap(cropped);
        palette = TextureSlotManager.BuildPalette(cropped);
    }

    void LoadMrg(string mrgName, TextureMrgKind kind)
    {
        TextureSlotManager.FreeSlots(slots);
        FreePicCanvas();
        CloseCanvas();
        isEditMode = false;
        cropMode = false;
        currentIndex = -1;
        selectedMrgName = mrgName;
        palette.Clear();

        mrgBytes = FileLoader.Instance.GetFileBytesFromDisk(TextureSlotManager.IsoPath(mrgName));
        slots = kind == TextureMrgKind.Icon
            ? TextureSlotManager.LoadIconSlots(mrgBytes)
            : TextureSlotManager.LoadRh2Slots(mrgBytes, mrgName);

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
            var slotBitmap = TextureSlotManager.DecodeSlot(slots[index]);
            palette = slots[index].Kind == TextureMrgKind.Icon
                ? TextureSlotManager.BuildPaletteFromGameTexture()
                : TextureSlotManager.BuildPalette(slotBitmap);

            viewTexture = TextureSlotManager.BitmapToTexture(slotBitmap);
            hasViewTexture = true;
            zoom = 2f;
            pan = Vector2.Zero;

            if (isEditMode)
            {
                OpenCanvas(index, slotBitmap);
            }
            else
            {
                slotBitmap.Dispose();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SelectSlot {index}: {exception.Message}");
        }

        selectedPaletteIndex = -1;
    }

    void OpenCanvas(int index, SKBitmap? bitmap = null)
    {
        if (index < 0 || index >= slots.Count)
        {
            return;
        }
        bool ownsBitmap = bitmap == null;
        if (bitmap == null)
        {
            bitmap = TextureSlotManager.DecodeSlot(slots[index]);
        }
        if (canvas == null)
        {
            canvas = new PaintCanvas(bitmap);
        }
        else
        {
            canvas.LoadBitmap(bitmap);
        }
        canvas.ClearStack();
        if (ownsBitmap)
        {
            bitmap.Dispose();
        }
    }

    void OpenCropPicker()
    {
        if (currentIndex < 0 || currentIndex >= slots.Count || !TextureSlotManager.IsoHasFile(TextureSlotManager.PictureMrg))
        {
            return;
        }

        int pictureIndex = currentIndex < 683 ? currentIndex : currentIndex + 171;
        byte[] pictureMrgBytes = FileLoader.Instance.GetFileBytesFromDisk(TextureSlotManager.IsoPath(TextureSlotManager.PictureMrg));
        var offsets = AssetExtractor.FindAllRh2Offsets(pictureMrgBytes);
        if (pictureIndex >= offsets.Count)
        {
            pictureIndex = Math.Min(currentIndex, offsets.Count - 1);
        }
        if (pictureIndex < 0)
        {
            return;
        }

        var decodedTexture = Rh2Decoder.Decode(pictureMrgBytes, (int)offsets[pictureIndex]);
        if (decodedTexture.IsEmpty)
        {
            return;
        }

        picMiniFullCanvas?.Free();
        var fullBitmap = TextureSlotManager.RgbaToBitmap(decodedTexture.Width, decodedTexture.Height, decodedTexture.Rgba);
        picMiniFullCanvas = new PaintCanvas(fullBitmap);
        fullBitmap.Dispose();

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
        var openResult = Dialog.FileOpen(FileDialogFilter.Of("png,jpg,jpeg,webp"));
        if (!openResult.IsOk)
        {
            return;
        }

        var imported = GameImageManager.LoadAndPrepareImage(openResult.Path, slot.Width, slot.Height, cropOnImport);
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
        selectedPaletteIndex = -1;
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
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Encode failed: {exception.Message}");
            return;
        }

        TextureSlotManager.PatchMrg(mrgBytes, slots, currentIndex, newSlotBytes);

        var newThumbnail = TextureSlotManager.BitmapToTexture(canvas.Bitmap);
        Raylib.UnloadTexture(slots[currentIndex].Thumbnail);
        slots[currentIndex].Bytes = newSlotBytes;
        slots[currentIndex].Thumbnail = newThumbnail;

        try
        {
            var changes = new ChangeSet();
            changes.AddWholeFile(TextureSlotManager.IsoPath(selectedMrgName), mrgBytes, $"{selectedMrgName} slot {currentIndex}");
            var plan = changes.Write(FileLoader.Instance);
            if (!plan.Ok)
            {
                foreach (string errorMessage in plan.Errors) Console.Error.WriteLine($"Write to ISO refused: {errorMessage}");
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Write to ISO failed: {exception.Message}");
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
            var slotBitmap = TextureSlotManager.DecodeSlot(slots[index]);
            palette = slots[index].Kind == TextureMrgKind.Icon
                ? TextureSlotManager.BuildPaletteFromGameTexture()
                : TextureSlotManager.BuildPalette(slotBitmap);

            if (hasViewTexture)
            {
                Raylib.UnloadTexture(viewTexture);
            }
            viewTexture = TextureSlotManager.BitmapToTexture(slotBitmap);
            hasViewTexture = true;

            if (isEditMode && canvas != null)
            {
                canvas.LoadBitmap(slotBitmap);
            }
            else
            {
                slotBitmap.Dispose();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"RefreshAfterSave {index}: {exception.Message}");
        }

        selectedPaletteIndex = -1;
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
            bool isCtrlHeld = ImGui.GetIO().KeyCtrl;
            if (isCtrlHeld && ImGui.IsKeyPressed(ImGuiKey.Z) && canvas?.CanUndo == true)
            {
                canvas.Undo();
            }
            if (isCtrlHeld && ImGui.IsKeyPressed(ImGuiKey.Y) && canvas?.CanRedo == true)
            {
                canvas.Redo();
            }
            if (isCtrlHeld && ImGui.IsKeyPressed(ImGuiKey.S))
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
        float frameHeight = ImGui.GetFrameHeight();
        var cursorScreenPosition = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(cursorScreenPosition, cursorScreenPosition + new Vector2(0, frameHeight), ImGui.GetColorU32(ImGuiCol.Separator), 1f);
        ImGui.Dummy(new Vector2(6f, frameHeight));
        ImGui.SameLine();
    }

    static Vector4 ColorToVec4(SKColor color)
    {
        return new(color.Red / 255f, color.Green / 255f, color.Blue / 255f, color.Alpha / 255f);
    }

    static SKColor Vec4ToColor(Vector4 colorVector)
    {
        return new((byte)(colorVector.X * 255), (byte)(colorVector.Y * 255), (byte)(colorVector.Z * 255), (byte)(colorVector.W * 255));
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