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
    public Texture2D Thumb;
    public int Width;
    public int Height;
    public TextureMrgKind Kind;
    public int IconIndex;

    public TextureSlot(byte[] bytes, Texture2D thumb, int width, int height, TextureMrgKind kind, int iconIndex = -1)
    {
        Bytes = bytes;
        Thumb = thumb;
        Width = width;
        Height = height;
        Kind = kind;
        IconIndex = iconIndex;
    }
}

public static class TextureSlotManager
{
    static readonly string[] Rh2MrgNames = {
        "PICTURE.MRG", "PICMINI.MRG", "PICPACK.MRG",
        "TEXANM.MRG", "TEXEFF.MRG", "TEXETC.MRG", "TEXEVE.MRG", "TEXSYS.MRG",
        "MODEL.MRG", "MONSTER.MRG",
    };

    public const string IconMrgName = "ICON.MRG";
    public const string PictureMrg = "PICTURE.MRG";
    public const string PicMiniMrg = "PICMINI.MRG";

    public static IReadOnlyList<string> KnownRh2Names => Rh2MrgNames;

    public static bool IsoHasFile(string name)
    {
        return FileLoader.Instance.iso_data_files?.Any(f => f.EndsWith(name + ";1", StringComparison.OrdinalIgnoreCase)) == true;
    }

    public static string IsoPath(string name)
    {
        return FileLoader.Instance.iso_data_files.First(f => f.EndsWith(name + ";1", StringComparison.OrdinalIgnoreCase));
    }

    public static List<TextureSlot> LoadRh2Slots(byte[] mrgBytes)
    {
        var slots = new List<TextureSlot>();
        var offsets = AssetExtractor.FindAllRh2Offsets(mrgBytes);
        foreach (uint off in offsets)
        {
            try
            {
                var dec = Rh2Decoder.Decode(mrgBytes, (int)off, Rh2Decoder.AlphaTreat.ForceOpaque);
                if (dec.IsEmpty)
                    continue;
                var bmp = RgbaToBitmap(dec.Width, dec.Height, dec.Rgba);
                var thumb = BitmapToTexture(bmp);
                bmp.Dispose();
                int next = offsets.Count > slots.Count + 1 ? (int)offsets[slots.Count + 1] : mrgBytes.Length;
                var slotBytes = mrgBytes.AsSpan((int)off, next - (int)off).ToArray();
                slots.Add(new TextureSlot(slotBytes, thumb, dec.Width, dec.Height, TextureMrgKind.Rh2));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Slot @ 0x{off:X}: {ex.Message}");
            }
        }
        return slots;
    }

    public static List<TextureSlot> LoadIconSlots(byte[] mrgBytes)
    {
        var slots = new List<TextureSlot>();
        for (int i = 0; i < PS2Icon.TextureOffsets.Length; i++)
        {
            PS2Icon.CurrentTextureIndex = i;
            int off = PS2Icon.TextureOffsets[i];
            int len = i + 1 < PS2Icon.TextureOffsets.Length
                ? PS2Icon.TextureOffsets[i + 1] - off
                : mrgBytes.Length - off;
            if (off + len > mrgBytes.Length)
                break;
            try
            {
                var slotBytes = mrgBytes.AsSpan(off, len).ToArray();
                var bmp = PS2Icon.ExtractIconTexture(slotBytes);
                var thumb = BitmapToTexture(bmp);
                int w = bmp.Width, h = bmp.Height;
                bmp.Dispose();
                slots.Add(new TextureSlot(slotBytes, thumb, w, h, TextureMrgKind.Icon, i));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Icon slot {i}: {ex.Message}");
            }
        }
        return slots;
    }

    public static SKBitmap DecodeSlot(TextureSlot slot)
    {
        if (slot.Kind == TextureMrgKind.Icon)
        {
            PS2Icon.CurrentTextureIndex = slot.IconIndex;
            return PS2Icon.ExtractIconTexture(slot.Bytes);
        }
        var dec = Rh2Decoder.Decode(slot.Bytes, 0, Rh2Decoder.AlphaTreat.ForceOpaque);
        return RgbaToBitmap(dec.Width, dec.Height, dec.Rgba);
    }

    public static byte[] EncodeSlot(TextureSlot slot, SKBitmap bitmap)
    {
        if (slot.Kind == TextureMrgKind.Icon)
            return PS2Icon.SaveIconToByte(slot.Bytes, bitmap);
        byte[] rgba = BitmapToRgba(bitmap);
        return Rh2Encoder.Encode(slot.Bytes.AsSpan(), rgba, bitmap.Width, bitmap.Height);
    }

    public static byte[] EncodeSlotOctree(TextureSlot slot, SKBitmap bitmap)
    {
        if (slot.Kind == TextureMrgKind.Icon)
            return PS2Icon.SaveIconToByte(slot.Bytes, bitmap);
        byte[] rgba = BitmapToRgba(bitmap);
        rgba = OctreePreQuantise(rgba, bitmap.Width * bitmap.Height);
        return Rh2Encoder.Encode(slot.Bytes.AsSpan(), rgba, bitmap.Width, bitmap.Height);
    }

    public static void PatchMrg(byte[] mrgBytes, List<TextureSlot> slots, int index, byte[] newSlotBytes)
    {
        var slot = slots[index];
        if (slot.Kind == TextureMrgKind.Rh2)
        {
            var offsets = AssetExtractor.FindAllRh2Offsets(mrgBytes);
            if (index < offsets.Count)
            {
                int off = (int)offsets[index];
                int copyLen = Math.Min(newSlotBytes.Length, mrgBytes.Length - off);
                Buffer.BlockCopy(newSlotBytes, 0, mrgBytes, off, copyLen);
            }
        }
        else if (slot.Kind == TextureMrgKind.Icon)
        {
            int off = PS2Icon.TextureOffsets[slot.IconIndex];
            int copyLen = Math.Min(newSlotBytes.Length, mrgBytes.Length - off);
            Buffer.BlockCopy(newSlotBytes, 0, mrgBytes, off, copyLen);
        }
    }

    public static Texture2D RefreshThumb(TextureSlot slot, SKBitmap fromBitmap)
    {
        Raylib.UnloadTexture(slot.Thumb);
        return BitmapToTexture(fromBitmap);
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

        List<SKColor> list;
        if (unique.Count <= 256)
        {
            list = unique.Select(k => new SKColor((byte)k, (byte)(k >> 8), (byte)(k >> 16), (byte)(k >> 24))).ToList();
        }
        else
        {
            var q = new Quantizer(256);
            for (int i = 0; i < pixelCount; i++)
                q.AddColor(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]);
            list = q.GetPalette().Select(t => new SKColor(t.R, t.G, t.B, 255)).ToList();
        }

        while (list.Count < 256)
            list.Add(SKColors.Black);
        return list;
    }

    public static List<SKColor> BuildPaletteFromGameTexture()
    {
        var pal = GameImageManager.CurrentTexture.Palette;
        var list = new List<SKColor>(256);
        for (int i = 0; i < 256; i++)
        {
            uint p = i < pal.Length ? pal[i] : 0xFF000000u;
            list.Add(new SKColor((byte)(p >> 16), (byte)(p >> 8), (byte)p, (byte)(p >> 24)));
        }
        return list;
    }

    public static byte[] OctreePreQuantise(byte[] rgba, int pixelCount)
    {
        var q = new Quantizer(256);
        for (int i = 0; i < pixelCount; i++)
            q.AddColor(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]);
        var pal = q.GetPalette();
        var cache = new Dictionary<int, (byte R, byte G, byte B)>();
        var output = new byte[rgba.Length];
        for (int i = 0; i < pixelCount; i++)
        {
            byte r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2], a = rgba[i * 4 + 3];
            int key = r | (g << 8) | (b << 16);
            if (!cache.TryGetValue(key, out var nc))
            {
                nc = FindNearest(pal, r, g, b);
                cache[key] = nc;
            }
            output[i * 4] = nc.R;
            output[i * 4 + 1] = nc.G;
            output[i * 4 + 2] = nc.B;
            output[i * 4 + 3] = a;
        }
        return output;
    }

    static (byte R, byte G, byte B) FindNearest(List<(byte R, byte G, byte B)> pal, byte r, byte g, byte b)
    {
        var best = pal[0];
        long bestD = long.MaxValue;
        foreach (var (pr, pg, pb) in pal)
        {
            long dr = r - pr, dg = g - pg, db = b - pb;
            long d = dr * dr + dg * dg + db * db;
            if (d < bestD)
            {
                bestD = d;
                best = (pr, pg, pb);
            }
        }
        return best;
    }

    public static SKBitmap RgbaToBitmap(int w, int h, byte[] rgba)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        unsafe
        {
            fixed (byte* src = rgba)
                Buffer.MemoryCopy(src, (void*)bmp.GetPixels(), bmp.ByteCount, Math.Min(rgba.Length, bmp.ByteCount));
        }
        return bmp;
    }

    public static byte[] BitmapToRgba(SKBitmap bmp)
    {
        var src = bmp.ColorType == SKColorType.Rgba8888 ? bmp : bmp.Copy(SKColorType.Rgba8888);
        var bytes = src.Bytes.ToArray();
        if (!ReferenceEquals(src, bmp))
            src.Dispose();
        return bytes;
    }

    public static unsafe Texture2D BitmapToTexture(SKBitmap bmp)
    {
        var src = bmp.ColorType == SKColorType.Rgba8888 ? bmp : bmp.Copy(SKColorType.Rgba8888);
        var img = Raylib.GenImageColor(src.Width, src.Height, Color.Magenta);
        var tex = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);
        Raylib.UpdateTexture(tex, (void*)src.GetPixels());
        Raylib.SetTextureFilter(tex, TextureFilter.Point);
        if (!ReferenceEquals(src, bmp))
            src.Dispose();
        return tex;
    }

    public static Texture2D MakePlaceholderThumb()
    {
        var img = Raylib.GenImageColor(16, 16, Color.Purple);
        var tex = Raylib.LoadTextureFromImage(img);
        Raylib.UnloadImage(img);
        return tex;
    }

    public static void FreeSlots(List<TextureSlot> slots)
    {
        foreach (var s in slots)
            Raylib.UnloadTexture(s.Thumb);
        slots.Clear();
    }
}