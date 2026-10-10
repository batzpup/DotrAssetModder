using DiscUtils.Iso9660;
namespace DotrAssetModder;

// The only disc class: it opens an ISO9660 image, lists its files and says where every file
// physically sits. The LBA and byte size come from DiscUtils' own reader (CDReader.PathToExtents
// gives the extent's byte offset in the image), so a file whose bytes happen to repeat elsewhere,
// or whose first sector is all zeroes, is still located exactly.
//
// The retail disc uses no interleaving, no multi-extent files and no Joliet supplement that
// matters here, so one extent per file is assumed; a file with more is reported as an anomaly
// rather than silently truncated.
public class FileLoader : IDisposable
{
    public const int SectorSize = 2048;

    // One file on the disc. All three sizes are different things and the writer needs all of them.
    public sealed class FileEntry
    {
        // Path as the disc spells it, e.g. "DATA\Icon.mrg" (no leading separator).
        public string IsoPath = string.Empty;

        // Uppercased, version-stripped, '/'-separated key used for lookups: "DATA/ICON.MRG".
        public string Key = string.Empty;

        // Bare file name without the ";1" version, e.g. "Icon.mrg". Unique disc-wide on this
        // disc, which is what the game's own mapFile resolver relies on (NOTES/file-layer.md).
        public string Name = string.Empty;

        public uint Lba;             // first sector of the file's extent
        public long Size;            // byte size from the directory record
        public long AllocatedSize;   // Size rounded up to a whole sector: the bytes the extent owns
        public long Capacity;        // bytes from Lba to the next extent in LBA order (>= AllocatedSize)
        public long SectorCount => AllocatedSize / SectorSize;
        public long ImageOffset => (long)Lba * SectorSize;
        public override string ToString() => $"{IsoPath} lba {Lba} size {Size}";
    }

    public static FileLoader instance;

    public string CurrentIsoPath = string.Empty;
    static readonly object FileStreamLock = new object();
    public FileStream fileStream;
    public bool IsIsoLoaded = false;


    public string[] folderNames = ["DATA", "MOVIE", "SOUND"];
    public string[] iso_data_files_all;
    public string[] iso_data_files_supported;
    string[] unsupportedDataDefaults = ["BMODULE.MRG", "CMODULE.MRG", "MMODULE.MRG", "STAGECAM.MRG"];

    public Action OnIsoloaded;

    public string[] MovieNames;

    readonly List<FileEntry> _files = new();
    readonly Dictionary<string, FileEntry> _byKey = new(StringComparer.Ordinal);
    readonly Dictionary<string, FileEntry> _byName = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _anomalies = new();

    public string ImagePath => CurrentIsoPath;
    public long ImageLength => fileStream.Length;
    public uint VolumeSpaceSize { get; private set; }   // PVD: sectors in the volume
    public string VolumeIdentifier { get; private set; } = string.Empty;
    public IReadOnlyList<FileEntry> Files => _files;

    // Files whose shape this reader does not handle, and duplicate paths or names.
    public IReadOnlyList<string> Anomalies => _anomalies;

    // A second loader on one image, for code that works on an ISO other than the open one.
    public static FileLoader Open(string isoPath)
    {
        var fl = new FileLoader();
        fl.OpenIso(isoPath);
        return fl;
    }

    public void OpenIso(string filePath)
    {
        CurrentIsoPath = filePath;

        lock (FileStreamLock)
        {
            fileStream?.Dispose();
            // Read-only: the only writer is ChangeSet.Write, which opens its own handle.
            fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );

            CDReader iso = new CDReader(fileStream, true);


            // An image built for a test (or a disc without these folders) has neither.
            iso_data_files_all = iso.DirectoryExists("DATA") ? iso.GetFiles("DATA") : [];
            List<string> supportedFiles = new List<string>();
            foreach (var file in iso_data_files_all)
            {
                if (!unsupportedDataDefaults.Contains(IsoPaths.FileName(file)))
                {
                    supportedFiles.Add(file);
                }
            }
            iso_data_files_supported = supportedFiles.ToArray();
            supportedFiles.Clear();

            MovieNames = iso.DirectoryExists("MOVIE") ? iso.GetFiles("MOVIE") : [];

            BuildFileTable(iso);
        }
        IsIsoLoaded = true;
        OnIsoloaded?.Invoke();
    }

    // ---- where every file sits ----------------------------------------------------------------

    // Caller holds FileStreamLock.
    void BuildFileTable(CDReader iso)
    {
        _files.Clear();
        _byKey.Clear();
        _byName.Clear();
        _anomalies.Clear();

        // Volume descriptors start at sector 16; the primary one carries the volume identifier
        // (32 chars at offset 40) and the volume size in sectors (offset 80).
        var pvd = new byte[SectorSize];
        ReadAt(16L * SectorSize, pvd, 0, pvd.Length);
        VolumeIdentifier = System.Text.Encoding.ASCII.GetString(pvd, 40, 32).TrimEnd(' ', '\0');
        VolumeSpaceSize = BitConverter.ToUInt32(pvd, 80);

        Walk(iso.Root);

        // Capacity = the bytes between this file's first sector and the first sector of the next
        // extent in LBA order; for the last file, up to the end of the image. It is never less
        // than AllocatedSize. This is the room a file could grow into without moving anything
        // else, which the writer reports but does not use (growing needs the directory record's
        // size field rewritten too, which is the rebuilder's job).
        var ordered = _files.OrderBy(f => f.Lba).ToList();
        long imageSectors = ImageLength / SectorSize;
        for (int i = 0; i < ordered.Count; i++)
        {
            long nextLba = i + 1 < ordered.Count ? ordered[i + 1].Lba : imageSectors;
            long cap = (nextLba - ordered[i].Lba) * SectorSize;
            ordered[i].Capacity = Math.Max(cap, ordered[i].AllocatedSize);
        }

        void Walk(DiscUtils.DiscDirectoryInfo dir)
        {
            foreach (var fi in dir.GetFiles())
            {
                var extents = iso.PathToExtents(fi.FullName);
                if (extents.Length != 1)
                    _anomalies.Add($"{fi.FullName}: {extents.Length} extents, only the first is seen");

                var f = new FileEntry
                {
                    IsoPath = fi.FullName,
                    Key = NormaliseKey(fi.FullName),
                    Name = IsoPaths.FileName(fi.FullName),
                    Lba = (uint)(extents[0].Start / SectorSize),
                    Size = fi.Length,
                };
                f.AllocatedSize = (f.Size + SectorSize - 1) / SectorSize * SectorSize;
                _files.Add(f);
                if (!_byKey.TryAdd(f.Key, f)) _anomalies.Add($"{f.IsoPath}: duplicate path");
                if (!_byName.TryAdd(f.Name, f)) _anomalies.Add($"{f.IsoPath}: duplicate file name");
            }
            foreach (var sub in dir.GetDirectories()) Walk(sub);
        }
    }

    // Accepts any spelling: "DATA/Icon.mrg", "\DATA\ICON.MRG;1", "Icon.mrg".
    public FileEntry? Find(string path)
    {
        string key = NormaliseKey(path);
        if (_byKey.TryGetValue(key, out var hit)) return hit;
        // bare name, the form the game itself resolves by
        int slash = key.LastIndexOf('/');
        string name = slash >= 0 ? key[(slash + 1)..] : key;
        return _byName.TryGetValue(name, out var byName) ? byName : null;
    }

    public FileEntry Get(string path) =>
        Find(path) ?? throw new FileNotFoundException($"not on the disc: {path}");

    public static string NormaliseKey(string path)
    {
        string s = path.Replace('\\', '/').TrimStart('/');
        int semi = s.LastIndexOf(';');
        if (semi > s.LastIndexOf('/')) s = s[..semi];
        return s.ToUpperInvariant();
    }

    // ---- reading ------------------------------------------------------------------------------

    public byte[] ReadFile(string path) => ReadFile(Get(path));

    public byte[] ReadFile(FileEntry f)
    {
        var buf = new byte[f.Size];
        ReadAt(f.ImageOffset, buf, 0, buf.Length);
        return buf;
    }

    // A slice of a file, for the big archives nobody wants in memory whole.
    public byte[] ReadFileRange(FileEntry f, long offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > f.Size)
            throw new ArgumentOutOfRangeException(nameof(offset), $"{f.IsoPath}: [{offset},{offset + length}) outside the file's {f.Size} bytes");
        var buf = new byte[length];
        ReadAt(f.ImageOffset + offset, buf, 0, length);
        return buf;
    }

    public void ReadAt(long imageOffset, byte[] buffer, int bufferOffset, int count)
    {
        lock (FileStreamLock)
        {
            if (fileStream is null) throw new InvalidOperationException("No ISO loaded");
            fileStream.Seek(imageOffset, SeekOrigin.Begin);
            fileStream.ReadExactly(buffer, bufferOffset, count);
        }
    }

    public byte[] GetFileBytesFromDisk(string path)
    {
        lock (FileStreamLock)
        {
            if (fileStream is null)
            {
                throw new InvalidOperationException("No ISO loaded");
            }

            var iso = new CDReader(fileStream, true);
            using Stream file = iso.OpenFile(path, FileMode.Open);
            var buffer = new byte[file.Length];
            file.ReadExactly(buffer);
            return buffer;
        }
    }

    const uint Rh0Magic = 0x00304852; // "RH0\0"
    public const int ModelBlock = 0x100000; // 1 MiB per RH0 block

    // Number of 1 MiB RH0 model blocks in a DATA .MRG (for the import index slider).
    public int CountRH0(string path)
    {
        byte[] data = GetFileBytesFromDisk(path);
        if (data.Length <= ModelBlock)
            return (data.Length >= 4 && BitConverter.ToUInt32(data, 0) == Rh0Magic) ? 1 : 0;
        int count = 0;
        for (long o = 0; o + 4 <= data.Length; o += ModelBlock)
            if (BitConverter.ToUInt32(data, (int)o) == Rh0Magic)
                count++;
        return count;
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
            iso_data_files_all = null;
            _files.Clear();
            _byKey.Clear();
            _byName.Clear();
            _anomalies.Clear();
            IsIsoLoaded = false;
            CurrentIsoPath = string.Empty;
        }
    }
}
