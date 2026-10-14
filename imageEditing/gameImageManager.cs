using SkiaSharp;
namespace DotrAssetModder;

public static class GameImageManager
{
    public static GameTexture CurrentTexture = new();

    public static void RemapColourInBitmap(SKBitmap bitmap, SKColor oldColour, SKColor newColour)
    {
        unsafe
        {
            uint* pixels = (uint*)bitmap.GetPixels().ToPointer();
            int count = bitmap.Width * bitmap.Height;
            uint oldBgra = (uint)((oldColour.Alpha << 24) | (oldColour.Blue << 16) | (oldColour.Green << 8) | oldColour.Red);
            uint newBgra = (uint)((newColour.Alpha << 24) | (newColour.Blue << 16) | (newColour.Green << 8) | newColour.Red);
            for (int i = 0; i < count; i++)
            {
                if (pixels[i] == oldBgra)
                {
                    pixels[i] = newBgra;
                }
            }
        }
    }

    public static SKBitmap LoadAndPrepareImage(string path, int targetWidth, int targetHeight, bool cropToFit)
    {
        var bitmap = SKBitmap.Decode(path);

        if (bitmap.Width != targetWidth || bitmap.Height != targetHeight)
        {
            var resized = new SKBitmap(targetWidth, targetHeight);
            using var canvas = new SKCanvas(resized);

            SKRect sourceRect, destinationRect;
            if (cropToFit)
            {
                float sourceAspect = (float)bitmap.Width / bitmap.Height;
                float destinationAspect = (float)targetWidth / targetHeight;
                int sourceX, sourceY, sourceWidth, sourceHeight;
                if (Math.Abs(sourceAspect - destinationAspect) < 0.01f)
                {
                    sourceX = 0; sourceY = 0;
                    sourceWidth = bitmap.Width; sourceHeight = bitmap.Height;
                }
                else if (sourceAspect > destinationAspect)
                {
                    sourceHeight = bitmap.Height;
                    sourceWidth = (int)(bitmap.Height * destinationAspect);
                    sourceX = (bitmap.Width - sourceWidth) / 2;
                    sourceY = 0;
                }
                else
                {
                    sourceWidth = bitmap.Width;
                    sourceHeight = (int)(bitmap.Width / destinationAspect);
                    sourceX = 0;
                    sourceY = (bitmap.Height - sourceHeight) / 2;
                }
                sourceRect = new SKRect(sourceX, sourceY, sourceX + sourceWidth, sourceY + sourceHeight);
                destinationRect = new SKRect(0, 0, targetWidth, targetHeight);
            }
            else
            {
                sourceRect = new SKRect(0, 0, bitmap.Width, bitmap.Height);
                destinationRect = new SKRect(0, 0, targetWidth, targetHeight);
            }

            bool isUpscaling = targetWidth > bitmap.Width || targetHeight > bitmap.Height;
            using var paint = new SKPaint
            {
                FilterQuality = isUpscaling ? SKFilterQuality.None : SKFilterQuality.High
            };
            canvas.DrawBitmap(bitmap, sourceRect, destinationRect, paint);
            bitmap.Dispose();
            bitmap = resized;
        }

        if (bitmap.ColorType != SKColorType.Rgba8888)
        {
            var converted = bitmap.Copy(SKColorType.Rgba8888);
            bitmap.Dispose();
            bitmap = converted;
        }

        return bitmap;
    }
}

//To Update
public class GameTexture
{
    public SKBitmap Bitmap  = new SKBitmap();
    public uint[]   Palette = Array.Empty<uint>();
}