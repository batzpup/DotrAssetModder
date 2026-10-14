using static DotrAssetModder.Bin;
namespace DotrAssetModder;

/* Which disc this is. The tables below live at different addresses in every build, so nothing
   here is read without a version. US is the only one measured; guessed EU/JP addresses would
   corrupt a disc. */
public enum GameVersion
{
    Unknown,
    UsSlus20515,   // "Yu-Gi-Oh! The Duelists of the Roses" (USA), boot ELF SLUS_205.15
    EuUnsupported, // SLES_*: addresses not mapped
    JpUnsupported, // SLPS/SLPM_*: the JP build names the tables but at other addresses
}

/* Where each table is in one version's boot ELF: EE virtual addresses (DotR-PC
   NOTES/model-mrg.md, NOTES/file-layer.md), turned into file offsets through the ELF's own
   program headers. */
public class GameTableAddresses
{
    public string BootElf;
    public uint ModelMrgOffsets;
    public int ModelEntryCount;     // 81 entries + the file size terminator
    public uint StageCamMrgOffsets;
    public int StageCamEntryCount;  // 10 + terminator
    public uint IconMrgOffsets;
    public int IconEntryCount;      // 4 + terminator
    public uint TexEtcMrgOffsets;
    public int TexEtcRegionCount;   // 3 regions: bounds[n]..bounds[n+1]
    public uint FirstPack;          // first model id per pack
    public uint PackSubCount;       // sub-models bound per pack
    public uint PackOfsPointers;    // pointer to each pack's sub-offset table
    public int PackCount;
    public uint ShareData;          // load address + first pack index per model type
    public int ShareDataCount;
    /* SzModel_Load @ 0x1E8780, model type 1: lSzModel_LoadModelPack(id, path,
       0x10AA800 + side * 0xA000, size, ofs, size). The two sides are 0xA000 apart, so a
       window can never exceed 0xA000 - and every retail window is exactly 0xA000. */
    public uint WindowBufferBase;
    public uint WindowBufferSize;
    /* The message table (SzMsg_LoadFont_Pipe @ 0x1FD680): u32 offset[count], message i starts at
       blob + 2 * offset[i]. Card names are messages 320 + card id. */
    public uint MessageOffsets;
    public uint MessageBlob;
    public int MessageCount;
    public int CardNameMessage;
    /* Target_Model_Y: one signed byte per card, how high the map (Home_Camp_3D_LeaderModel @
       0x22B060) stands the deck leader's node 0 above the arena point (every ArenaYPos is 0),
       with the model at scale 0.5. */
    public uint MapLeaderY;
    public int MapLeaderYCount;
    /* DuelistArenaDetailsArray: one 0x34-byte record per map arena: X, Y, Z (floats), then at
       +0x10 and +0x12 the two EVENT2 sub-models the map (Home_Camp_3D @ 0x22A710) draws there,
       a pedestal (8-10) and the marker on it (11-26). */
    public uint Arenas;
    public int ArenaCount;
    public int ArenaStride;

    public static readonly GameTableAddresses UsSlus20515 = new GameTableAddresses
    {
        BootElf = "SLUS_205.15",
        ModelMrgOffsets = 0x292A50, ModelEntryCount = 81,
        StageCamMrgOffsets = 0x292170, StageCamEntryCount = 10,
        IconMrgOffsets = 0x29B050, IconEntryCount = 4,
        TexEtcMrgOffsets = 0x293CC0, TexEtcRegionCount = 3,
        FirstPack = 0x292CA0,
        PackSubCount = 0x292DE0,
        PackOfsPointers = 0x292E80,
        PackCount = 39,
        ShareData = 0x292BA0, ShareDataCount = 7,
        WindowBufferBase = 0x10AA800, WindowBufferSize = 0xA000,
        MessageOffsets = 0x2D19D0, MessageBlob = 0x2D49D4, MessageCount = 3073,
        CardNameMessage = 320,
        MapLeaderY = 0x299A30, MapLeaderYCount = 0x356,
        Arenas = 0x298BA0, ArenaCount = 22, ArenaStride = 0x34,
    };

    public static GameTableAddresses For(GameVersion version)
    {
        if (version == GameVersion.UsSlus20515)
        {
            return UsSlus20515;
        }
        if (version == GameVersion.EuUnsupported)
        {
            throw new NotImplementedException("EU disc: table addresses are not mapped yet");
        }
        if (version == GameVersion.JpUnsupported)
        {
            throw new NotImplementedException("JP disc: table addresses are not mapped yet");
        }
        throw new NotSupportedException("unknown game version: no table addresses");
    }
}

/* aShareData @ 0x292BA0 (US): 7 x { u32 EE load address, u16 first pack index, u16
   invalidation mask }, one per model type. SzModel_LoadMerge reads pack `Base +
   shareDataIndex` into `Buffer`; dest size = read size = the pack's own size, so the ELF has
   no separate capacity constant for a pack. */
public class ShareDataEntry
{
    public uint Buffer;
    public ushort Base;
}

// Every table in the boot ELF that locates data on the disc, read out of the ELF.
public class GameTables
{
    // One MONSTER.MRG slot (one card's RH0 block). SzModel_Load @ 0x1E8780, model types 4-6: dest
    // size and read size are both 0x100000.
    public const int MonsterSlotSize = 0x100000;

    public GameVersion Version;
    public GameTableAddresses Addresses;
    public string BootElfIsoPath;       // the disc path the ELF was read from

    // Each anOfs_* table is `entries + 1` u32: the last value is the file's size, which is what
    // lets a region be read as [table[n], table[n+1]).
    public uint[] ModelMrgOffsets;
    public uint[] StageCamMrgOffsets;
    public uint[] IconMrgOffsets;
    public uint[] TexEtcMrgOffsets;
    public uint[] FirstPack;
    public uint[] PackSubCount;
    public uint[][] PackSubOffsets;     // one per pack: COUNT[p]+1 values, last = pack size
    public ShareDataEntry[] ShareData;

    byte[] bootElfBytes;
    /* Loadable pieces of the boot ELF, used to turn an EE virtual address into a file offset. The
       retail SLUS_205.15 has 835 program headers (one per overlay landing zone); exactly one has a
       non-zero file size, and it holds every table here. The mapping goes through the headers,
       not a hard-coded delta, because the JP/EU builds differ. */
    List<uint> segmentVirtualAddresses = new List<uint>();
    List<uint> segmentFileOffsets = new List<uint>();
    List<uint> segmentFileSizes = new List<uint>();

    public int ModelEntryCount => Addresses.ModelEntryCount;
    public int PackCount => Addresses.PackCount;
    public uint WindowBufferBase => Addresses.WindowBufferBase;
    public uint WindowBufferSize => Addresses.WindowBufferSize;

    // SYSTEM.CNF is 57 bytes of "BOOT2 = cdrom0:\SLUS_205.15;1" and friends.
    public static string BootElfNameFrom(string systemCnf)
    {
        foreach (string line in systemCnf.Split('\n'))
        {
            int equalsIndex = line.IndexOf('=');
            if (equalsIndex < 0 || !line[..equalsIndex].Trim().Equals("BOOT2", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string value = line[(equalsIndex + 1)..].Trim().TrimEnd('\r');
            int colonIndex = value.IndexOf(':');
            if (colonIndex >= 0)
            {
                value = value[(colonIndex + 1)..];
            }
            return IsoPaths.FileName(value.Replace('/', '\\'));
        }
        return string.Empty;
    }

    public static GameVersion DetectVersion(FileLoader disc)
    {
        var systemCnfFile = disc.Find("SYSTEM.CNF");
        if (systemCnfFile is null)
        {
            return GameVersion.Unknown;
        }
        string bootElfName = BootElfNameFrom(System.Text.Encoding.ASCII.GetString(disc.ReadFile(systemCnfFile))).ToUpperInvariant();
        if (bootElfName == "SLUS_205.15")
        {
            return GameVersion.UsSlus20515;
        }
        if (bootElfName.StartsWith("SLES"))
        {
            return GameVersion.EuUnsupported;
        }
        if (bootElfName.StartsWith("SLPS") || bootElfName.StartsWith("SLPM"))
        {
            return GameVersion.JpUnsupported;
        }
        return GameVersion.Unknown;
    }

    public static GameTables Load(FileLoader disc)
    {
        GameVersion version = DetectVersion(disc);
        var elfFile = disc.Get(GameTableAddresses.For(version).BootElf);
        return new GameTables(version, disc.ReadFile(elfFile), elfFile.IsoPath);
    }

    // For working on a loose ELF (OriginalGameData) with no disc.
    public static GameTables FromElf(byte[] elfBytes, GameVersion version)
    {
        return new GameTables(version, elfBytes, GameTableAddresses.For(version).BootElf);
    }

    GameTables(GameVersion version, byte[] elfBytes, string bootElfIsoPath)
    {
        Version = version;
        Addresses = GameTableAddresses.For(version);
        BootElfIsoPath = bootElfIsoPath;
        bootElfBytes = elfBytes;

        if (bootElfBytes.Length < 0x34 || bootElfBytes[0] != 0x7F || bootElfBytes[1] != 'E' || bootElfBytes[2] != 'L' || bootElfBytes[3] != 'F')
        {
            throw new InvalidDataException("not an ELF");
        }
        uint programHeaderOffset = ReadU32(bootElfBytes, 0x1C);
        ushort programHeaderEntrySize = ReadU16(bootElfBytes, 0x2A);
        ushort programHeaderCount = ReadU16(bootElfBytes, 0x2C);
        for (int i = 0; i < programHeaderCount; i++)
        {
            int programHeaderFileOffset = (int)programHeaderOffset + i * programHeaderEntrySize;
            uint segmentType = ReadU32(bootElfBytes, programHeaderFileOffset);
            uint segmentFileSize = ReadU32(bootElfBytes, programHeaderFileOffset + 16);
            if (segmentType == 1 && segmentFileSize != 0)
            {
                segmentFileOffsets.Add(ReadU32(bootElfBytes, programHeaderFileOffset + 4));
                segmentVirtualAddresses.Add(ReadU32(bootElfBytes, programHeaderFileOffset + 8));
                segmentFileSizes.Add(segmentFileSize);
            }
        }
        if (segmentVirtualAddresses.Count == 0)
        {
            throw new InvalidDataException("ELF has no loadable segment with file contents");
        }

        GameTableAddresses tableAddresses = Addresses;
        ModelMrgOffsets = ReadU32Table(tableAddresses.ModelMrgOffsets, tableAddresses.ModelEntryCount + 1);
        StageCamMrgOffsets = ReadU32Table(tableAddresses.StageCamMrgOffsets, tableAddresses.StageCamEntryCount + 1);
        IconMrgOffsets = ReadU32Table(tableAddresses.IconMrgOffsets, tableAddresses.IconEntryCount + 1);
        TexEtcMrgOffsets = ReadU32Table(tableAddresses.TexEtcMrgOffsets, tableAddresses.TexEtcRegionCount + 1);

        FirstPack = ReadU32Table(tableAddresses.FirstPack, tableAddresses.PackCount);
        PackSubCount = ReadU32Table(tableAddresses.PackSubCount, tableAddresses.PackCount);
        uint[] packOfsPointers = ReadU32Table(tableAddresses.PackOfsPointers, tableAddresses.PackCount);
        PackSubOffsets = new uint[tableAddresses.PackCount][];
        for (int packIndex = 0; packIndex < tableAddresses.PackCount; packIndex++)
        {
            PackSubOffsets[packIndex] = ReadU32Table(packOfsPointers[packIndex], (int)PackSubCount[packIndex] + 1);
        }

        ShareData = new ShareDataEntry[tableAddresses.ShareDataCount];
        int shareOffset = VirtualAddressToFileOffset(tableAddresses.ShareData);
        for (int i = 0; i < tableAddresses.ShareDataCount; i++)
        {
            int shareEntryFileOffset = shareOffset + i * 8;
            ShareData[i] = new ShareDataEntry
            {
                Buffer = ReadU32(bootElfBytes, shareEntryFileOffset),
                Base = ReadU16(bootElfBytes, shareEntryFileOffset + 4),
            };
        }
    }

    int VirtualAddressToFileOffset(uint virtualAddress)
    {
        for (int i = 0; i < segmentVirtualAddresses.Count; i++)
        {
            if (virtualAddress >= segmentVirtualAddresses[i] && virtualAddress < segmentVirtualAddresses[i] + segmentFileSizes[i])
            {
                return (int)(segmentFileOffsets[i] + (virtualAddress - segmentVirtualAddresses[i]));
            }
        }
        throw new ArgumentOutOfRangeException(nameof(virtualAddress), $"0x{virtualAddress:X8} is not inside any loadable segment");
    }

    uint[] ReadU32Table(uint address, int count)
    {
        int tableFileOffset = VirtualAddressToFileOffset(address);
        uint[] values = new uint[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = ReadU32(bootElfBytes, tableFileOffset + i * 4);
        }
        return values;
    }

    // Byte range of MODEL.MRG entry `entryIndex` (packs 0-38, then the 42 windows).
    public (uint Offset, uint Size) ModelEntry(int entryIndex)
    {
        return (ModelMrgOffsets[entryIndex], ModelMrgOffsets[entryIndex + 1] - ModelMrgOffsets[entryIndex]);
    }

    public (uint Offset, uint Size) TexEtcRegion(int regionIndex)
    {
        return (TexEtcMrgOffsets[regionIndex], TexEtcMrgOffsets[regionIndex + 1] - TexEtcMrgOffsets[regionIndex]);
    }

    /* ---- messages (DotR-PC tools/usmsg.py, NOTES/cards.md) ----
       Each u16 word: bit 15 last word, bit 14 a two-word back-reference (`w & 0x3F` words,
       starting `(w & 0x1FFF) >> 6` words into message `next & 0x3FFF`), bits 0-12 the glyph.
       There is no character table: a glyph index is the character. */
    const int MessageEndFlag = 0x8000;
    const int MessageReferenceFlag = 0x4000;
    const int NameBuffer = 2;   // message the game writes the player's name into at runtime

    static string[] cachedGlyphs;

    static string[] Glyphs()
    {
        if (cachedGlyphs != null)
        {
            return cachedGlyphs;
        }
        string[] glyphTable = new string[0x2000];
        glyphTable[0] = "\n";
        glyphTable[1] = "○";
        glyphTable[2] = ",";
        glyphTable[3] = "●";
        glyphTable[335] = "∞";
        glyphTable[336] = "?";
        glyphTable[337] = "?";
        glyphTable[338] = ":";
        glyphTable[339] = "…";
        glyphTable[348] = "・";
        glyphTable[361] = "α";
        glyphTable[362] = "<";
        glyphTable[363] = ">";
        glyphTable[364] = "3.";
        glyphTable[365] = "□";
        glyphTable[366] = "△";
        glyphTable[367] = "×";
        glyphTable[368] = ";";
        string uppercaseLetters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        string lowercaseLetters = "abcdefghijklmnopqrstuvwxyz";
        // Full-width set. 30 is an empty bitmap: a full-width space.
        string fullWidthGlyphs = uppercaseLetters + "　[]" + lowercaseLetters + "()" + "123456789" + "0!\"#$%&'=^-¥,./_";
        // Half-width set: the same order, but it has no comma.
        string halfWidthGlyphs = uppercaseLetters + " []" + lowercaseLetters + "()" + "123456789" + "0!\"#$%&'=^-¥./_";
        for (int i = 0; i < fullWidthGlyphs.Length; i++)
        {
            glyphTable[4 + i] = fullWidthGlyphs[i].ToString();
        }
        for (int i = 0; i < halfWidthGlyphs.Length; i++)
        {
            glyphTable[86 + i] = halfWidthGlyphs[i].ToString();
        }
        cachedGlyphs = glyphTable;
        return glyphTable;
    }

    int MessageWord(int fileOffset)
    {
        return ReadU16(bootElfBytes, fileOffset);
    }

    int MessageStart(int messageIndex)
    {
        uint messageWordOffset = ReadU32(bootElfBytes, VirtualAddressToFileOffset(Addresses.MessageOffsets) + 4 * messageIndex);
        return VirtualAddressToFileOffset(Addresses.MessageBlob) + 2 * (int)messageWordOffset;
    }

    // Appends one element at `elementFileOffset`; returns how many words it took.
    int MessageElement(int elementFileOffset, System.Text.StringBuilder stringBuilder)
    {
        int messageWord = MessageWord(elementFileOffset);
        if ((messageWord & MessageReferenceFlag) != 0)
        {
            int referencedMessage = MessageWord(elementFileOffset + 2) & 0x3FFF;
            int wordsToSkip = (messageWord & 0x1FFF) >> 6;
            int wordsLeft = messageWord & 0x3F;
            if (referencedMessage == NameBuffer)
            {
                stringBuilder.Append("{NAME}");
                return 2;
            }
            int sourceFileOffset = MessageStart(referencedMessage) + 2 * wordsToSkip;
            while (wordsLeft > 0)
            {
                int wordsTaken = MessageElement(sourceFileOffset, stringBuilder);
                if ((MessageWord(sourceFileOffset + 2 * (wordsTaken - 1)) & MessageEndFlag) != 0)
                {
                    break;
                }
                wordsLeft -= wordsTaken;
                sourceFileOffset += 2 * wordsTaken;
            }
            return 2;
        }
        int code = messageWord & 0x1FFF;
        string? glyphText = Glyphs()[code];
        if (glyphText == null)
        {
            stringBuilder.Append($"<{code}>");
        }
        else
        {
            stringBuilder.Append(glyphText);
        }
        return 1;
    }

    public string Message(int messageIndex)
    {
        var stringBuilder = new System.Text.StringBuilder();
        int elementFileOffset = MessageStart(messageIndex);
        while (true)
        {
            int wordsTaken = MessageElement(elementFileOffset, stringBuilder);
            if ((MessageWord(elementFileOffset + 2 * (wordsTaken - 1)) & MessageEndFlag) != 0)
            {
                return stringBuilder.ToString();
            }
            elementFileOffset += 2 * wordsTaken;
        }
    }

    // How high the map stands this card's model as the deck leader (Target_Model_Y).
    public int MapLeaderY(int cardId)
    {
        if (cardId < 0 || cardId >= Addresses.MapLeaderYCount)
        {
            return 0;
        }
        return (sbyte)bootElfBytes[MapLeaderYOffset(cardId)];
    }

    // Where card `cardId`'s Target_Model_Y byte is in the boot ELF file.
    public int MapLeaderYOffset(int cardId)
    {
        return VirtualAddressToFileOffset(Addresses.MapLeaderY) + cardId;
    }

    // Changes the map height in the ELF read here only; saving it to the disc is the caller's.
    public void SetMapLeaderY(int cardId, int y)
    {
        bootElfBytes[MapLeaderYOffset(cardId)] = (byte)(sbyte)y;
    }

    // Where map arena `arena` is, and the two EVENT2 sub-models drawn there.
    public void Arena(int arena, out float x, out float y, out float z, out int pedestal, out int marker)
    {
        int arenaFileOffset = VirtualAddressToFileOffset(Addresses.Arenas) + arena * Addresses.ArenaStride;
        x = ReadF32(bootElfBytes, arenaFileOffset);
        y = ReadF32(bootElfBytes, arenaFileOffset + 4);
        z = ReadF32(bootElfBytes, arenaFileOffset + 8);
        pedestal = ReadI16(bootElfBytes, arenaFileOffset + 0x10);
        marker = ReadI16(bootElfBytes, arenaFileOffset + 0x12);
    }

    public string CardName(int cardId)
    {
        return Message(Addresses.CardNameMessage + cardId);
    }
}
