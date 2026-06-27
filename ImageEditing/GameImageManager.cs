using SkiaSharp;
namespace DotrAssetModder;

public static class GameImageManager
{
    public static GameTexture CurrentTexture = new();

    public static void RemapColourInBitmap(SKBitmap bitmap, SKColor oldCol, SKColor newCol)
    {
        unsafe
        {
            uint* pixels = (uint*)bitmap.GetPixels().ToPointer();
            int count = bitmap.Width * bitmap.Height;
            uint oldBgra = (uint)((oldCol.Alpha << 24) | (oldCol.Blue << 16) | (oldCol.Green << 8) | oldCol.Red);
            uint newBgra = (uint)((newCol.Alpha << 24) | (newCol.Blue << 16) | (newCol.Green << 8) | newCol.Red);
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

            SKRect srcRect, dstRect;
            if (cropToFit)
            {
                float srcRatio = (float)bitmap.Width / bitmap.Height;
                float dstRatio = (float)targetWidth / targetHeight;
                int srcX, srcY, srcW, srcH;
                if (Math.Abs(srcRatio - dstRatio) < 0.01f)
                {
                    srcX = 0; srcY = 0;
                    srcW = bitmap.Width; srcH = bitmap.Height;
                }
                else if (srcRatio > dstRatio)
                {
                    srcH = bitmap.Height;
                    srcW = (int)(bitmap.Height * dstRatio);
                    srcX = (bitmap.Width - srcW) / 2;
                    srcY = 0;
                }
                else
                {
                    srcW = bitmap.Width;
                    srcH = (int)(bitmap.Width / dstRatio);
                    srcX = 0;
                    srcY = (bitmap.Height - srcH) / 2;
                }
                srcRect = new SKRect(srcX, srcY, srcX + srcW, srcY + srcH);
                dstRect = new SKRect(0, 0, targetWidth, targetHeight);
            }
            else
            {
                srcRect = new SKRect(0, 0, bitmap.Width, bitmap.Height);
                dstRect = new SKRect(0, 0, targetWidth, targetHeight);
            }

            bool isUpscaling = targetWidth > bitmap.Width || targetHeight > bitmap.Height;
            using var paint = new SKPaint
            {
                FilterQuality = isUpscaling ? SKFilterQuality.None : SKFilterQuality.High
            };
            canvas.DrawBitmap(bitmap, srcRect, dstRect, paint);
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

public class GameTexture
{
    public SKBitmap Bitmap  = new SKBitmap();
    public uint[]   Palette = Array.Empty<uint>();
}