using SkiaSharp;

namespace DotrAssetModder;
// Mostly AI

/* Loads texture files once each, looking next to the model and up to two folders below; a
   port of toglb.py's Images. SkiaSharp decodes PNG, JPEG, BMP, GIF and WebP (Pillow also read
   TGA/DDS; no source seen so far uses them). */
public sealed class SourceImages
{
    // file name (lower case) -> path, per model folder: the folder and two levels below it, so
    // a folder of models is listed once and nothing above or far below it is read
    static readonly Dictionary<string, Dictionary<string, string>> FolderFiles = new Dictionary<string, Dictionary<string, string>>();

    readonly string baseDir;
    readonly Action<string> warn;
    readonly Dictionary<string, SKBitmap?> cache = new Dictionary<string, SKBitmap?>();

    public SourceImages(string baseDir, Action<string> warn)
    {
        this.baseDir = baseDir;
        this.warn = warn;
    }

    string? Find(string name)
    {
        Dictionary<string, string>? files;
        lock (FolderFiles)
        {
            if (!FolderFiles.TryGetValue(baseDir, out files))
            {
                files = new Dictionary<string, string>();
                List<string> level = new List<string> { baseDir };
                for (int depth = 0; depth < 3; depth++)
                {
                    List<string> below = new List<string>();
                    foreach (string directory in level)
                    {
                        string[] entries;
                        try
                        {
                            entries = Directory.GetFileSystemEntries(directory);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        Array.Sort(entries, StringComparer.Ordinal);
                        foreach (string entry in entries)
                        {
                            if (File.Exists(entry))
                            {
                                files.TryAdd(Path.GetFileName(entry).ToLowerInvariant(), entry);
                            }
                            else if (Directory.Exists(entry))
                            {
                                below.Add(entry);
                            }
                        }
                    }
                    level = below;
                }
                FolderFiles[baseDir] = files;
            }
        }
        if (files.TryGetValue(name.ToLowerInvariant(), out string? hit))
        {
            return hit;
        }
        return null;
    }

    // RGBA, unpremultiplied (Skia's default premultiplies, which changes the colour of
    // semi-transparent pixels against Pillow's)
    public static SKBitmap? Decode(byte[] data)
    {
        try
        {
            using SKCodec? codec = SKCodec.Create(new SKMemoryStream(data));
            if (codec == null)
            {
                return null;
            }
            SKImageInfo info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            SKBitmap bitmap = new SKBitmap(info);
            SKCodecResult decodeResult = codec.GetPixels(info, bitmap.GetPixels());
            if (decodeResult != SKCodecResult.Success && decodeResult != SKCodecResult.IncompleteInput)
            {
                bitmap.Dispose();
                return null;
            }
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Where texture `name` is: as written, beside the model, or by file name up to two folders
    // below it; null when it is nowhere.
    public string? Locate(string name)
    {
        name = name.Trim().Replace('\\', '/');
        string file = Path.GetFileName(name);
        List<string> tries = new List<string>();
        if (Path.IsPathRooted(name))
        {
            tries.Add(name);
        }
        tries.Add(Path.Combine(baseDir, name));
        tries.Add(Path.Combine(baseDir, file));
        foreach (string candidate in tries)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return Find(file);
    }

    // The first of names that loads (embedded data first), or null.
    public SKBitmap? Load(List<string> names, byte[]? data = null)
    {
        if (data != null && data.Length > 0)
        {
            SKBitmap? decoded = Decode(data);
            if (decoded != null)
            {
                return decoded;
            }
        }
        foreach (string raw in names)
        {
            string name = raw.Trim().Replace('\\', '/');
            if (name.Length == 0)
            {
                continue;
            }
            if (cache.TryGetValue(name, out SKBitmap? cached))
            {
                return cached;
            }
            string? hit = Locate(name);
            if (hit == null)
            {
                continue;
            }
            SKBitmap? loaded = Decode(File.ReadAllBytes(hit));
            if (loaded == null)
            {
                warn("texture " + hit + " did not load");
            }
            cache[name] = loaded;
            return loaded;
        }
        if (names.Count > 0)
        {
            warn("texture not found: " + names[0]);
        }
        return null;
    }
}
