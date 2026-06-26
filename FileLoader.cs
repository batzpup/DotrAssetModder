using DiscUtils.Iso9660;
namespace DotrAssetModder;

public class FileLoader : IDisposable
{
    public static FileLoader instance; 

    public string CurrentIsoPath = string.Empty;
    static readonly object FileStreamLock = new object();
    static FileStream fileStream;
    public string[] iso_data_files;
    public bool IsIsoLoaded = false;

    public void OpenIso(string filePath)
    {
        CurrentIsoPath = filePath;

        lock (FileStreamLock)
        {
            fileStream?.Dispose();
            fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite
            );

            CDReader iso = new CDReader(fileStream, true);
            iso_data_files = iso.GetFiles("DATA");
        }
        IsIsoLoaded = true;
    }

    public byte[] GetFileBytesFromDisk(string path)
    {
        lock (FileStreamLock)
        {
            if (fileStream is null)
                throw new InvalidOperationException("No ISO loaded");
            var iso = new CDReader(fileStream, true);
            using Stream file = iso.OpenFile(path, FileMode.Open);
            var buffer = new byte[file.Length];
            file.ReadExactly(buffer);
            return buffer;
        }
    }

    // ---- model import support ----------------------------------------------------------
    const uint Rh0Magic = 0x00304852;            // "RH0\0"
    const int  ModelBlock = 0x100000;            // 1 MiB per RH0 block
    static readonly Dictionary<string, long> _baseCache = new();

    // Number of 1 MiB RH0 model blocks in a DATA .MRG (for the import index slider).
    public int CountRH0(string path)
    {
        byte[] data = GetFileBytesFromDisk(path);
        if (data.Length <= ModelBlock)
            return (data.Length >= 4 && BitConverter.ToUInt32(data, 0) == Rh0Magic) ? 1 : 0;
        int count = 0;
        for (long o = 0; o + 4 <= data.Length; o += ModelBlock)
            if (BitConverter.ToUInt32(data, (int)o) == Rh0Magic) count++;
        return count;
    }

    // Absolute byte offset of model block `index` inside the ISO file, or -1 if not found.
    // ISO9660 stores each file as one contiguous, 2048-aligned extent, so we find where the
    // file's bytes (read through the normal reader) physically sit in the image and add the
    // block offset. This can't fail to find a file that is actually present. Base is cached.
    public long GetStartOfModel(string path, int index)
    {
        long fileBase = FindFileBaseOffset(path);
        if (fileBase < 0) return -1;
        return fileBase + (long)index * ModelBlock;
    }

    // Absolute byte offset of the start of `path` inside the ISO image, or -1 if not found.
    // Same mechanism as GetStartOfModel; exposed publicly for the texture editor's
    // "Write MRG back to ISO" path.
    public long GetStartOfFile(string path) => FindFileBaseOffset(path);

    long FindFileBaseOffset(string path)
    {
        lock (FileStreamLock)
            if (_baseCache.TryGetValue(path, out long hit)) return hit;

        byte[] content = GetFileBytesFromDisk(path);   // takes the lock itself

        lock (FileStreamLock)
        {
            long found = SearchRawImage(content);       // lock held
            if (found >= 0) _baseCache[path] = found;
            return found;
        }
    }

    // Caller must hold FileStreamLock. Scans 2048-aligned positions for the file's first
    // bytes, then verifies up to a 1 MiB prefix to rule out collisions.
    long SearchRawImage(byte[] content)
    {
        const int Sector = 2048;
        if (fileStream is null) throw new InvalidOperationException("No ISO loaded");
        if (content.Length < Sector) return -1;

        long isoLen = fileStream.Length;
        byte[] buf = new byte[16 * 1024 * 1024];
        int verifyLen = (int)Math.Min(content.Length, ModelBlock);
        byte[] vbuf = new byte[verifyLen];
        byte b0 = content[0], b1 = content[1], b2 = content[2], b3 = content[3];

        for (long basePos = 0; basePos < isoLen;)
        {
            fileStream.Seek(basePos, SeekOrigin.Begin);
            int read = fileStream.Read(buf, 0, buf.Length);
            if (read <= 0) break;
            int usable = (read / Sector) * Sector;
            if (usable == 0) break;

            for (int off = 0; off + Sector <= usable; off += Sector)
            {
                if (buf[off] == b0 && buf[off + 1] == b1 && buf[off + 2] == b2 && buf[off + 3] == b3)
                {
                    long cand = basePos + off;
                    fileStream.Seek(cand, SeekOrigin.Begin);
                    fileStream.ReadExactly(vbuf, 0, verifyLen);
                    bool ok = true;
                    for (int k = 0; k < verifyLen; k++) if (vbuf[k] != content[k]) { ok = false; break; }
                    if (ok) return cand;
                }
            }
            basePos += usable;
        }
        return -1;
    }

    // Overwrite bytes at an absolute ISO offset (same-size write keeps the filesystem valid).
    public void WriteBytes(long offset, byte[] data)
    {
        lock (FileStreamLock)
        {
            if (fileStream is null) throw new InvalidOperationException("No ISO loaded");
            fileStream.Seek(offset, SeekOrigin.Begin);
            fileStream.Write(data, 0, data.Length);
            fileStream.Flush();
        }
    }

    public static FileLoader Instance
    {
        get
        {
            if (instance == null)
            {
                instance = new FileLoader();
            }

            return instance;
        }
    }

    public void Dispose()
    {
        lock (FileStreamLock)
        {
            fileStream?.Dispose();
            fileStream = null;
            iso_data_files = null;
            IsIsoLoaded = false;
            CurrentIsoPath = string.Empty;
        }
    }
}