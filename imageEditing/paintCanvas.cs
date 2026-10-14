using System.Runtime.InteropServices;
using NativeFileDialogSharp;
using Raylib_cs;
using SkiaSharp;
namespace DotrAssetModder;

public class PaintCanvas
{
    public enum Tool
    {
        Pencil,
        Eraser,
        Fill,
        Eyedropper
    }

    public SKBitmap Bitmap { get; private set; }
    public SKColor ActiveColor { get; set; } = SKColors.Black;
    public float BrushSize { get; set; } = 4f;
    public float Opacity { get; set; } = 1f;
    public Tool ActiveTool { get; set; } = Tool.Pencil;

    Texture2D texture;
    bool isDirty;
    bool textureCreated;
    TextureFilter textureFilter = TextureFilter.Point;
    SKPoint? lastPaintPosition;

    readonly Stack<byte[]> undoStack = new();
    readonly Stack<byte[]> redoStack = new();
    const int MaxUndoSteps = 30;

    public void ForceRefresh() => isDirty = true;

    public void ClearStack()
    {
        undoStack.Clear();
        redoStack.Clear();
    }

    public PaintCanvas(SKBitmap bitmap)
    {
        LoadBitmap(bitmap);
    }

    public void LoadBitmap(SKBitmap bitmap)
    {
        Bitmap = bitmap.ColorType != SKColorType.Rgba8888
            ? bitmap.Copy(SKColorType.Rgba8888)
            : bitmap.Copy();
        CreateTexture();
    }

    void CreateTexture()
    {
        if (textureCreated)
        {
            Raylib.UnloadTexture(texture);
            textureCreated = false;
        }
        texture = TextureSlotManager.BitmapToTexture(Bitmap);
        Raylib.SetTextureFilter(texture, textureFilter);
        textureCreated = true;
    }

    public void SetTextureFilter(TextureFilter filter)
    {
        textureFilter = filter;
        if (textureCreated)
        {
            Raylib.SetTextureFilter(texture, textureFilter);
        }
    }

    public void Update()
    {
        if (isDirty)
        {
            UploadToTexture();
            isDirty = false;
        }
    }

    unsafe void UploadToTexture()
    {
        var pixels = Bitmap.GetPixels(out _);
        Raylib.UpdateTexture(texture, (void*)pixels);
    }

    public nint GetTexturePtr() => (nint)texture.Id;
    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;

    public void BeginStroke()
    {
        lastPaintPosition = null;
        PushUndo();
    }

    public void PaintAt(float bitmapX, float bitmapY)
    {
        using var canvas = new SKCanvas(Bitmap);
        using var paint = MakePaint();
        var position = new SKPoint(bitmapX, bitmapY);
        if (ActiveTool == Tool.Pencil || ActiveTool == Tool.Eraser)
        {
            if (lastPaintPosition.HasValue)
            {
                canvas.DrawLine(lastPaintPosition.Value, position, paint);
            }
            else
            {
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawCircle(position, BrushSize / 2f, paint);
            }
        }
        lastPaintPosition = position;
        isDirty = true;
    }

    public void EndStroke()
    {
        lastPaintPosition = null;
    }

    public void FloodFill(int x, int y)
    {
        PushUndo();
        var targetColor = Bitmap.GetPixel(x, y);
        var fillColor = new SKColor(ActiveColor.Red, ActiveColor.Green, ActiveColor.Blue, ActiveColor.Alpha);
        if (targetColor == fillColor)
        {
            return;
        }

        var pendingPixels = new Stack<(int x, int y)>();
        pendingPixels.Push((x, y));
        int bitmapWidth = Bitmap.Width, bitmapHeight = Bitmap.Height;
        while (pendingPixels.Count > 0)
        {
            var (pixelX, pixelY) = pendingPixels.Pop();
            if (pixelX < 0 || pixelX >= bitmapWidth || pixelY < 0 || pixelY >= bitmapHeight)
            {
                continue;
            }
            if (Bitmap.GetPixel(pixelX, pixelY) != targetColor)
            {
                continue;
            }
            Bitmap.SetPixel(pixelX, pixelY, fillColor);
            pendingPixels.Push((pixelX + 1, pixelY));
            pendingPixels.Push((pixelX - 1, pixelY));
            pendingPixels.Push((pixelX, pixelY + 1));
            pendingPixels.Push((pixelX, pixelY - 1));
        }
        isDirty = true;
    }

    SKPaint MakePaint()
    {
        var paint = new SKPaint {
            IsAntialias = false,
            StrokeWidth = BrushSize,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Style = SKPaintStyle.Stroke,
        };

        if (ActiveTool == Tool.Eraser)
        {
            paint.BlendMode = Opacity >= 1f ? SKBlendMode.Clear : SKBlendMode.DstOut;
            paint.Color = SKColors.White.WithAlpha((byte)(255 * Opacity * 0.1f));
        }
        else
        {
            paint.Color = ActiveColor;
            paint.BlendMode = ActiveColor.Alpha >= 255 ? SKBlendMode.Src : SKBlendMode.SrcOver;
        }
        return paint;
    }

    void PushUndo()
    {
        undoStack.Push(Bitmap.Bytes.ToArray());
        redoStack.Clear();
        if (undoStack.Count > MaxUndoSteps)
        {
            var undoSnapshots = undoStack.ToArray();
            undoStack.Clear();
            foreach (var snapshot in undoSnapshots.Take(MaxUndoSteps - 1).Reverse())
            {
                undoStack.Push(snapshot);
            }
        }
    }

    public bool CanUndo => undoStack.Count > 0;
    public bool CanRedo => redoStack.Count > 0;

    public void Undo()
    {
        if (!CanUndo)
        {
            return;
        }
        redoStack.Push(Bitmap.Bytes.ToArray());
        RestoreBitmap(undoStack.Pop());
        isDirty = true;
    }

    public void Redo()
    {
        if (!CanRedo)
        {
            return;
        }
        undoStack.Push(Bitmap.Bytes.ToArray());
        RestoreBitmap(redoStack.Pop());
        isDirty = true;
    }

    void RestoreBitmap(byte[] bitmapBytes)
    {
        var pixelCopy = bitmapBytes.ToArray();
        var handle = GCHandle.Alloc(pixelCopy, GCHandleType.Pinned);
        Bitmap.InstallPixels(
            Bitmap.Info,
            handle.AddrOfPinnedObject(),
            Bitmap.RowBytes,
            (pixelAddress, handleContext) => ((GCHandle)handleContext).Free(),
            (object)handle);
    }

    public void SaveAs()
    {
        DialogResult result = Dialog.FileSave(FileDialogFilter.Of("png,jpg,jpeg,webp"));
        if (!result.IsOk)
        {
            return;
        }

        string path = result.Path;
        string extension = Path.GetExtension(path).ToLowerInvariant();
        SKEncodedImageFormat format = extension switch {
            ".webp" => SKEncodedImageFormat.Webp,
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            _ => SKEncodedImageFormat.Png,
        };
        if (format == SKEncodedImageFormat.Png && !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            path += ".png";
        }

        using var stream = File.OpenWrite(path);
        Bitmap.Encode(stream, format, 100);
    }

    public void Free()
    {
        if (textureCreated)
        {
            Raylib.UnloadTexture(texture);
            textureCreated = false;
        }
        Bitmap.Dispose();
    }
}