using Raylib_cs;
using System;
using System.Collections.Generic;

namespace DotrAssetModder;

public class TextureCache : IDisposable
{
    public class Entry
    {
        public Texture2D Texture;
        public string Path;
    }

    private Dictionary<string, Entry> cache = new();

    public Texture2D Load(string path)
    {
        if (!cache.TryGetValue(path, out var entry))
        {
            Image img = Raylib.LoadImage(path);
            Texture2D tex = Raylib.LoadTextureFromImage(img);
            Raylib.UnloadImage(img);

            entry = new Entry
            {
                Texture = tex,
                Path = path
            };

            cache[path] = entry;
        }

        return entry.Texture;
    }

    public bool TryGet(string path, out Texture2D tex)
    {
        if (cache.TryGetValue(path, out var entry))
        {
            tex = entry.Texture;
            return true;
        }

        tex = default;
        return false;
    }

    public IEnumerable<Entry> All() => cache.Values;

    public void Clear()
    {
        foreach (var e in cache.Values)
            Raylib.UnloadTexture(e.Texture);

        cache.Clear();
    }

    public void Dispose()
    {
        Clear();
    }
}