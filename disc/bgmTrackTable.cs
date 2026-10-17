using static DotrAssetModder.Bin;
namespace DotrAssetModder;

/* SOUND/BGM/PCMLIST.BIN, the game's BGM track table: 256 entries of { u32 size, u32 id, u32
   loopStart, u32 loopEnd } (unused entries zero). SD_vInitializeFileHandler @ 0x10A8B0 loads it
   once at boot; every lookup is a linear search for the id. The game reads only this table, never
   a track's own header, and streams the audio from +0x800 of SOUND/BGM/0x/NNNN.PCM (found by bare
   file name). Audio: s16 stereo interleaved at 24 kHz. loopEnd == frames when the track loops (0 =
   plays once); the loader rounds loop points down to 4 frames and the size down to 16 bytes.
   Each .PCM starts with a 0x800 header that copies its entry, frames in place of the id.
   DotR-PC NOTES/sound-bgm.md has the traced player. */
public sealed class BgmTrackTable
{
    public const string TablePath = "SOUND/BGM/PCMLIST.BIN";
    public const int EntryCount = 256;
    public const int EntrySize = 16;
    public const int TableSize = EntryCount * EntrySize;
    public const int TrackHeaderSize = 0x800;
    public const int SampleRate = 24000;
    public const int ChannelCount = 2;
    public const int BytesPerFrame = 4;
    // Loop points and frame counts written by the tool are multiples of this (the loader's rounding).
    public const int FrameStep = 4;

    public readonly List<BgmTrackEntry> Entries = new List<BgmTrackEntry>();

    public static BgmTrackTable Parse(byte[] tableBytes)
    {
        if (tableBytes.Length < TableSize)
        {
            throw new InvalidDataException($"{TablePath} is {tableBytes.Length} bytes, expected {TableSize}");
        }
        var table = new BgmTrackTable();
        for (int tableIndex = 0; tableIndex < EntryCount; tableIndex++)
        {
            int entryOffset = tableIndex * EntrySize;
            var entry = new BgmTrackEntry();
            entry.TableIndex = tableIndex;
            entry.Size = ReadU32(tableBytes, entryOffset);
            entry.TrackId = ReadI32(tableBytes, entryOffset + 4);
            entry.LoopStart = ReadU32(tableBytes, entryOffset + 8);
            entry.LoopEnd = ReadU32(tableBytes, entryOffset + 12);
            if (entry.TrackId == 0 && entry.Size == 0)
            {
                continue;
            }
            table.Entries.Add(entry);
        }
        return table;
    }

    public BgmTrackEntry? Find(int trackId)
    {
        foreach (BgmTrackEntry entry in Entries)
        {
            if (entry.TrackId == trackId)
            {
                return entry;
            }
        }
        return null;
    }

    public static string TrackFileName(int trackId)
    {
        return trackId.ToString("0000") + ".PCM";
    }

    // A track with no table entry (0001.PCM): its values from its own header, TableIndex -1.
    public static BgmTrackEntry ReadHeaderEntry(FileLoader disc, FileLoader.FileEntry trackFile, int trackId)
    {
        if (trackFile.Size < TrackHeaderSize)
        {
            throw new InvalidDataException($"{trackFile.Size} bytes, shorter than the {TrackHeaderSize}-byte header");
        }
        var headerBytes = new byte[16];
        disc.ReadAt(trackFile.ImageOffset, headerBytes, 0, headerBytes.Length);
        var entry = new BgmTrackEntry();
        entry.TableIndex = -1;
        entry.TrackId = trackId;
        entry.Size = ReadU32(headerBytes, 0);
        entry.LoopStart = ReadU32(headerBytes, 8);
        entry.LoopEnd = ReadU32(headerBytes, 12);
        return entry;
    }

    // The 16 bytes of `entry` in the table.
    public static byte[] EntryBytes(BgmTrackEntry entry)
    {
        var entryBytes = new byte[EntrySize];
        WriteU32(entryBytes, 0, entry.Size);
        WriteI32(entryBytes, 4, entry.TrackId);
        WriteU32(entryBytes, 8, entry.LoopStart);
        WriteU32(entryBytes, 12, entry.LoopEnd);
        return entryBytes;
    }

    // The first 16 bytes of a track's header (the rest of its 0x800 is zero).
    public static byte[] TrackHeaderStart(BgmTrackEntry entry)
    {
        var headerBytes = new byte[16];
        WriteU32(headerBytes, 0, entry.Size);
        WriteU32(headerBytes, 4, entry.Frames);
        WriteU32(headerBytes, 8, entry.LoopStart);
        WriteU32(headerBytes, 12, entry.LoopEnd);
        return headerBytes;
    }
}

public sealed class BgmTrackEntry
{
    // Index in PCMLIST.BIN, -1 for a track the table does not list.
    public int TableIndex;
    // Audio bytes (4 per stereo frame).
    public uint Size;
    public int TrackId;
    public uint LoopStart;
    // 0 = plays once; else the frame where it jumps back to LoopStart (the track's end on retail).
    public uint LoopEnd;

    public uint Frames
    {
        get { return Size / BgmTrackTable.BytesPerFrame; }
    }

    public bool Loops
    {
        get { return LoopEnd != 0; }
    }

    public BgmTrackEntry Clone()
    {
        return (BgmTrackEntry)MemberwiseClone();
    }
}
