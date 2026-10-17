using DiscUtils.Iso9660;
using static DotrAssetModder.Bin;
namespace DotrAssetModder;

/* The only disc class: it opens an ISO9660 image, lists its files and says where every file
   physically sits. The LBA and byte size come from DiscUtils' own reader (CDReader.PathToExtents
   gives the extent's byte offset in the image), so a file is located exactly even if its bytes
   repeat elsewhere or its first sector is all zeroes.

   The retail disc uses no interleaving, no multi-extent files and no Joliet supplement that
   matters here, so one extent per file is assumed; a file with more is reported as an anomaly. */
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
    public string[] IsoDataFilesAll;
    public string[] iso_data_files_supported;
    string[] unsupportedDataDefaults = ["BMODULE.MRG", "CMODULE.MRG", "MMODULE.MRG", "STAGECAM.MRG"];

    public Action OnIsoloaded;

    /* Raised before the open image's stream is closed (another ISO opened, or Dispose), outside the
       stream lock, so a reader on another thread (BGM playback, a sound extraction) can stop
       first instead of reading through a closed or replaced stream. */
    public Action? OnIsoClosing;

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
        var loader = new FileLoader();
        loader.OpenIso(isoPath);
        return loader;
    }

    public void OpenIso(string filePath)
    {
        if (fileStream is not null)
        {
            OnIsoClosing?.Invoke();
        }
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

            CDReader isoReader = new CDReader(fileStream, true);


            // An image built for a test (or a disc without these folders) has neither.
            IsoDataFilesAll = isoReader.DirectoryExists("DATA") ? isoReader.GetFiles("DATA") : [];
            List<string> supportedFiles = new List<string>();
            foreach (var isoFilePath in IsoDataFilesAll)
            {
                if (!unsupportedDataDefaults.Contains(IsoPaths.FileName(isoFilePath)))
                {
                    supportedFiles.Add(isoFilePath);
                }
            }
            iso_data_files_supported = supportedFiles.ToArray();
            supportedFiles.Clear();

            MovieNames = isoReader.DirectoryExists("MOVIE") ? isoReader.GetFiles("MOVIE") : [];

            BuildFileTable(isoReader);
        }
        IsIsoLoaded = true;
        OnIsoloaded?.Invoke();
    }

    // ---- where every file sits ----------------------------------------------------------------

    // Caller holds FileStreamLock.
    void BuildFileTable(CDReader isoReader)
    {
        _files.Clear();
        _byKey.Clear();
        _byName.Clear();
        _anomalies.Clear();

        // Volume descriptors start at sector 16; the primary one carries the volume identifier
        // (32 chars at offset 40) and the volume size in sectors (offset 80).
        var primaryVolumeDescriptor = new byte[SectorSize];
        ReadAt(16L * SectorSize, primaryVolumeDescriptor, 0, primaryVolumeDescriptor.Length);
        VolumeIdentifier = System.Text.Encoding.ASCII.GetString(primaryVolumeDescriptor, 40, 32).TrimEnd(' ', '\0');
        VolumeSpaceSize = ReadU32(primaryVolumeDescriptor, 80);

        Walk(isoReader.Root);

        /* Capacity = the bytes between this file's first sector and the first sector of the next
           extent in LBA order; for the last file, up to the end of the image. It is never less
           than AllocatedSize. This is the room a file could grow into without moving anything
           else; the writer reports it but does not use it (growing needs the directory record's
           size field rewritten too, which is the rebuilder's job). */
        var filesInLbaOrder = _files.OrderBy(entry => entry.Lba).ToList();
        long imageSectors = ImageLength / SectorSize;
        for (int i = 0; i < filesInLbaOrder.Count; i++)
        {
            long nextLba = i + 1 < filesInLbaOrder.Count ? filesInLbaOrder[i + 1].Lba : imageSectors;
            long capacityBytes = (nextLba - filesInLbaOrder[i].Lba) * SectorSize;
            filesInLbaOrder[i].Capacity = Math.Max(capacityBytes, filesInLbaOrder[i].AllocatedSize);
        }

        void Walk(DiscUtils.DiscDirectoryInfo directory)
        {
            foreach (var discFileInfo in directory.GetFiles())
            {
                var extents = isoReader.PathToExtents(discFileInfo.FullName);
                if (extents.Length != 1)
                    _anomalies.Add($"{discFileInfo.FullName}: {extents.Length} extents, only the first is seen");

                var fileEntry = new FileEntry
                {
                    IsoPath = discFileInfo.FullName,
                    Key = NormaliseKey(discFileInfo.FullName),
                    Name = IsoPaths.FileName(discFileInfo.FullName),
                    Lba = (uint)(extents[0].Start / SectorSize),
                    Size = discFileInfo.Length,
                };
                fileEntry.AllocatedSize = (fileEntry.Size + SectorSize - 1) / SectorSize * SectorSize;
                _files.Add(fileEntry);
                if (!_byKey.TryAdd(fileEntry.Key, fileEntry)) _anomalies.Add($"{fileEntry.IsoPath}: duplicate path");
                if (!_byName.TryAdd(fileEntry.Name, fileEntry)) _anomalies.Add($"{fileEntry.IsoPath}: duplicate file name");
            }
            foreach (var subDirectory in directory.GetDirectories()) Walk(subDirectory);
        }
    }

    // Accepts any spelling: "DATA/Icon.mrg", "\DATA\ICON.MRG;1", "Icon.mrg".
    public FileEntry? Find(string path)
    {
        string key = NormaliseKey(path);
        if (_byKey.TryGetValue(key, out var foundByKey)) return foundByKey;
        // bare name, the form the game itself resolves by
        int lastSlashIndex = key.LastIndexOf('/');
        string bareName = lastSlashIndex >= 0 ? key[(lastSlashIndex + 1)..] : key;
        return _byName.TryGetValue(bareName, out var foundByName) ? foundByName : null;
    }

    public FileEntry Get(string path) =>
        Find(path) ?? throw new FileNotFoundException($"not on the disc: {path}");

    public static string NormaliseKey(string path)
    {
        string normalisedPath = path.Replace('\\', '/').TrimStart('/');
        int versionSeparatorIndex = normalisedPath.LastIndexOf(';');
        if (versionSeparatorIndex > normalisedPath.LastIndexOf('/')) normalisedPath = normalisedPath[..versionSeparatorIndex];
        return normalisedPath.ToUpperInvariant();
    }

    // ---- reading ------------------------------------------------------------------------------

    public byte[] ReadFile(string path) => ReadFile(Get(path));

    public byte[] ReadFile(FileEntry fileEntry)
    {
        var fileBytes = new byte[fileEntry.Size];
        ReadAt(fileEntry.ImageOffset, fileBytes, 0, fileBytes.Length);
        return fileBytes;
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

            var isoReader = new CDReader(fileStream, true);
            using Stream discStream = isoReader.OpenFile(path, FileMode.Open);
            var buffer = new byte[discStream.Length];
            discStream.ReadExactly(buffer);
            return buffer;
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
        if (fileStream is not null)
        {
            OnIsoClosing?.Invoke();
        }
        lock (FileStreamLock)
        {
            fileStream?.Dispose();
            fileStream = null;
            IsoDataFilesAll = null;
            _files.Clear();
            _byKey.Clear();
            _byName.Clear();
            _anomalies.Clear();
            IsIsoLoaded = false;
            CurrentIsoPath = string.Empty;
        }
    }
}
