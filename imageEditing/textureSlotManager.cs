using Raylib_cs;
using SkiaSharp;

namespace DotrAssetModder;

public enum TextureMrgKind
{
    Rh2,
    Icon
}

public sealed class TextureSlot
{
    public byte[] Bytes;
    public Texture2D Thumbnail;
    public int Width;
    public int Height;
    public TextureMrgKind Kind;
    public int IconIndex;
    // Offset of this slot inside the MRG. Slots whose decode is empty or throws are skipped,
    // so the slot index is not the index of its RH2 match - PatchMrg must use this.
    public int Offset;
    public Rh2Decoder.AlphaTreatment Alpha;

    public TextureSlot(byte[] bytes, Texture2D thumbnail, int width, int height, TextureMrgKind kind, int iconIndex = -1,
        Rh2Decoder.AlphaTreatment alpha = Rh2Decoder.AlphaTreatment.Keep, int offset = 0)
    {
        Bytes = bytes;
        Thumbnail = thumbnail;
        Width = width;
        Height = height;
        Kind = kind;
        IconIndex = iconIndex;
        Alpha = alpha;
        Offset = offset;
    }
}

public static class TextureSlotManager
{
  public static IEnumerable<string> GetTextureMrgs()
{
    return FileLoader.Instance.iso_data_files_supported
        .Select(IsoPaths.FileName)
        .Where(name =>
            name.StartsWith("PICTURE", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("PICMINI", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("PICPACK", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("TEXANM", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("TEXEFF", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("TEXETC", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("TEXEVE", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("TEXSYS", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("MODEL", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("MONSTER", StringComparison.OrdinalIgnoreCase));
}

    public const string IconMrgName = "ICON.MRG";
    public const string PictureMrg = "PICTURE.MRG";
    public const string PictureMiniMrg = "PICMINI.MRG";

    

    public static bool IsoHasFile(string name)
    {
        return FileLoader.Instance.Find(name) != null;
    }

    public static string IsoPath(string name)
    {
        return FileLoader.Instance.Get(name).IsoPath;
    }

    /* Card art and event pictures are drawn by the game with alpha test and blending off,
       so their palette alpha is never seen in-game. Their 5551 CLUTs still carry black
       entries with STP clear (0x0000), which decode as alpha 0 and would punch holes
       through every black pixel of the art. Effect and model sheets do rely on that
       alpha (black STP-clear backgrounds are cut out), so they keep it. */
    internal static Rh2Decoder.AlphaTreatment AlphaTreatFor(string mrgName)
    {
        string fileName = IsoPaths.FileName(mrgName);
        bool drawnOpaque =
            fileName.StartsWith("PICTURE", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("PICMINI", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("PICPACK", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("TEXEVE", StringComparison.OrdinalIgnoreCase);
        return drawnOpaque ? Rh2Decoder.AlphaTreatment.ForceOpaque : Rh2Decoder.AlphaTreatment.Keep;
    }

    // withThumbnails = false skips the Raylib upload, for callers without a window (the harness).
    public static List<TextureSlot> LoadRh2Slots(byte[] mrgBytes, string mrgName, bool withThumbnails = true)
    {
        var alpha = AlphaTreatFor(mrgName);
        var slots = new List<TextureSlot>();
        var offsets = AssetExtractor.FindAllRh2Offsets(mrgBytes);
        for (int i = 0; i < offsets.Count; i++)
        {
            uint mrgOffset = offsets[i];
            try
            {
                var decoded = Rh2Decoder.Decode(mrgBytes, (int)mrgOffset, alpha);
                if (decoded.IsEmpty)
                    continue;
                var thumbnail = default(Texture2D);
                if (withThumbnails)
                {
                    var decodedBitmap = RgbaToBitmap(decoded.Width, decoded.Height, decoded.Rgba);
                    thumbnail = BitmapToTexture(decodedBitmap);
                    decodedBitmap.Dispose();
                }
                // the gap to the next RH2 match, not to the next kept slot
                int nextOffset = i + 1 < offsets.Count ? (int)offsets[i + 1] : mrgBytes.Length;
                var slotBytes = mrgBytes.AsSpan((int)mrgOffset, nextOffset - (int)mrgOffset).ToArray();
                slots.Add(new TextureSlot(slotBytes, thumbnail, decoded.Width, decoded.Height, TextureMrgKind.Rh2,
                    alpha: alpha, offset: (int)mrgOffset));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Slot @ 0x{mrgOffset:X}: {exception.Message}");
            }
        }
        return slots;
    }

    public static List<TextureSlot> LoadIconSlots(byte[] mrgBytes)
    {
        var slots = new List<TextureSlot>();
        for (int i = 0; i < Ps2Icon.TextureOffsets.Length; i++)
        {
            Ps2Icon.CurrentTextureIndex = i;
            int iconOffset = Ps2Icon.TextureOffsets[i];
            int iconLength = i + 1 < Ps2Icon.TextureOffsets.Length
                ? Ps2Icon.TextureOffsets[i + 1] - iconOffset
                : mrgBytes.Length - iconOffset;
            if (iconOffset + iconLength > mrgBytes.Length)
            {
                break;   
            }
             
            try
            {
                var slotBytes = mrgBytes.AsSpan(iconOffset, iconLength).ToArray();
                var iconBitmap = Ps2Icon.ExtractIconTexture(slotBytes);
                var thumbnail = BitmapToTexture(iconBitmap);
                int iconWidth = iconBitmap.Width, iconHeight = iconBitmap.Height;
                iconBitmap.Dispose();
                slots.Add(new TextureSlot(slotBytes, thumbnail, iconWidth, iconHeight, TextureMrgKind.Icon, i));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Icon slot {i}: {exception.Message}");
            }
        }
        return slots;
    }

    public static SKBitmap DecodeSlot(TextureSlot slot)
    {
        if (slot.Kind == TextureMrgKind.Icon)
        {
            Ps2Icon.CurrentTextureIndex = slot.IconIndex;
            return Ps2Icon.ExtractIconTexture(slot.Bytes);
        }
        /* Some textures (e.g. TEXEFF glow/particle sprites) carry their whole visible shape
           in the alpha channel over a flat neutral-grey RGB meant to be alpha-blended over
           the scene. Forcing alpha to 255 would leave plain grey boxes, so only MRGs the
           game draws opaque drop alpha (see AlphaTreatFor). */
        var decoded = Rh2Decoder.Decode(slot.Bytes, 0, slot.Alpha);
        return RgbaToBitmap(decoded.Width, decoded.Height, decoded.Rgba);
    }

    public static byte[] EncodeSlot(TextureSlot slot, SKBitmap bitmap)
    {
        if (slot.Kind == TextureMrgKind.Icon)
        {
            return Ps2Icon.SaveIconToByte(slot.Bytes, bitmap);
        }
        byte[] rgba = BitmapToRgba(bitmap);
        return Rh2Encoder.Encode(slot.Bytes.AsSpan(), rgba, bitmap.Width, bitmap.Height, slot.Alpha);
    }

    public static void PatchMrg(byte[] mrgBytes, List<TextureSlot> slots, int index, byte[] newSlotBytes)
    {
        var slot = slots[index];
        if (slot.Kind == TextureMrgKind.Rh2)
        {
            int slotOffset = slot.Offset;
            int copyLen = Math.Min(newSlotBytes.Length, mrgBytes.Length - slotOffset);
            Buffer.BlockCopy(newSlotBytes, 0, mrgBytes, slotOffset, copyLen);
        }
        else if (slot.Kind == TextureMrgKind.Icon)
        {
            int slotOffset = Ps2Icon.TextureOffsets[slot.IconIndex];
            int copyLen = Math.Min(newSlotBytes.Length, mrgBytes.Length - slotOffset);
            Buffer.BlockCopy(newSlotBytes, 0, mrgBytes, slotOffset, copyLen);
        }
    }

    public static List<SKColor> BuildPalette(SKBitmap bitmap)
    {
        byte[] rgba = BitmapToRgba(bitmap);
        return BuildPalette(rgba, bitmap.Width * bitmap.Height);
    }

    public static List<SKColor> BuildPalette(byte[] rgba, int pixelCount)
    {
        var unique = new HashSet<int>();
        for (int i = 0; i < pixelCount && unique.Count <= 256; i++)
        {
            int key = rgba[i * 4] | (rgba[i * 4 + 1] << 8) | (rgba[i * 4 + 2] << 16) | (rgba[i * 4 + 3] << 24);
            unique.Add(key);
        }

        List<SKColor> paletteColours;
        if (unique.Count <= 256)
        {
            paletteColours = unique.Select(packedRgba => new SKColor((byte)packedRgba, (byte)(packedRgba >> 8), (byte)(packedRgba >> 16), (byte)(packedRgba >> 24))).ToList();
        }
        else
        {
            var quantizer = new Quantizer(256);
            for (int i = 0; i < pixelCount; i++)
            {
                quantizer.AddColor(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]);
            }
            paletteColours = quantizer.GetPalette().Select(colourTuple => new SKColor(colourTuple.Red, colourTuple.Green, colourTuple.Blue, 255)).ToList();
        }

        while (paletteColours.Count < 256)
        {
            paletteColours.Add(SKColors.Black);
        }

        return paletteColours;
    }

    public static List<SKColor> BuildPaletteFromGameTexture()
    {
        var gamePalette = GameImageManager.CurrentTexture.Palette;
        var paletteColours = new List<SKColor>(256);
        for (int i = 0; i < 256; i++)
        {
            uint packedColour = i < gamePalette.Length ? gamePalette[i] : 0xFF000000u;
            paletteColours.Add(new SKColor((byte)(packedColour >> 16), (byte)(packedColour >> 8), (byte)packedColour, (byte)(packedColour >> 24)));
        }
        return paletteColours;
    }

    public static byte[] OctreePreQuantise(byte[] rgba, int pixelCount)
    {
        var quantizer = new Quantizer(256);
        for (int i = 0; i < pixelCount; i++)
        {
            quantizer.AddColor(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]);
        }

        var palette = quantizer.GetPalette();
        var nearestColourCache = new Dictionary<int, (byte Red, byte Green, byte Blue)>();
        var output = new byte[rgba.Length];
        for (int i = 0; i < pixelCount; i++)
        {
            byte red = rgba[i * 4], green = rgba[i * 4 + 1], blue = rgba[i * 4 + 2], alpha = rgba[i * 4 + 3];
            int key = red | (green << 8) | (blue << 16);
            if (!nearestColourCache.TryGetValue(key, out var nearestColour))
            {
                nearestColour = FindNearest(palette, red, green, blue);
                nearestColourCache[key] = nearestColour;
            }
            output[i * 4] = nearestColour.Red;
            output[i * 4 + 1] = nearestColour.Green;
            output[i * 4 + 2] = nearestColour.Blue;
            output[i * 4 + 3] = alpha;
        }
        return output;
    }

    static (byte Red, byte Green, byte Blue) FindNearest(List<(byte Red, byte Green, byte Blue)> palette, byte red, byte green, byte blue)
    {
        var nearestColour = palette[0];
        long bestDistance = long.MaxValue;
        foreach (var (paletteRed, paletteGreen, paletteBlue) in palette)
        {
            long deltaRed = red - paletteRed, deltaGreen = green - paletteGreen, deltaBlue = blue - paletteBlue;
            long distance = deltaRed * deltaRed + deltaGreen * deltaGreen + deltaBlue * deltaBlue;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearestColour = (paletteRed, paletteGreen, paletteBlue);
            }
        }
        return nearestColour;
    }

    public static SKBitmap RgbaToBitmap(int width, int height, byte[] rgba)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        unsafe
        {
            fixed (byte* sourceBytes = rgba) Buffer.MemoryCopy(sourceBytes, (void*)bitmap.GetPixels(), bitmap.ByteCount, Math.Min(rgba.Length, bitmap.ByteCount));
        }
        return bitmap;
    }

    public static byte[] BitmapToRgba(SKBitmap bitmap)
    {
        var rgbaBitmap = bitmap.ColorType == SKColorType.Rgba8888 ? bitmap : bitmap.Copy(SKColorType.Rgba8888);
        var bytes = rgbaBitmap.Bytes.ToArray();
        if (!ReferenceEquals(rgbaBitmap, bitmap))
        {
            rgbaBitmap.Dispose();
        }
        return bytes;
    }

    public static unsafe Texture2D BitmapToTexture(SKBitmap bitmap)
    {
        var rgbaBitmap = bitmap.ColorType == SKColorType.Rgba8888 ? bitmap : bitmap.Copy(SKColorType.Rgba8888);
        var colourImage = Raylib.GenImageColor(rgbaBitmap.Width, rgbaBitmap.Height, Color.Magenta);
        var texture = Raylib.LoadTextureFromImage(colourImage);
        Raylib.UnloadImage(colourImage);
        Raylib.UpdateTexture(texture, (void*)rgbaBitmap.GetPixels());
        Raylib.SetTextureFilter(texture, TextureFilter.Point);
        if (!ReferenceEquals(rgbaBitmap, bitmap))
        {
            rgbaBitmap.Dispose();
        }

        return texture;
    }

    public static void FreeSlots(List<TextureSlot> slots)
    {
        foreach (var slot in slots)
        {
            Raylib.UnloadTexture(slot.Thumbnail);
        }

        slots.Clear();
    }
}