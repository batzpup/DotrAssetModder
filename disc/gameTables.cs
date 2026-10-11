namespace DotrAssetModder;

// Which disc this is. The tables below live at different addresses in every build, so nothing
// here is read without a version. US is the only one measured; guessing the EU/JP addresses
// would silently corrupt a disc.
public enum GameVersion
{
    Unknown,
    UsSlus20515,   // "Yu-Gi-Oh! The Duelists of the Roses" (USA), boot ELF SLUS_205.15
    EuUnsupported, // SLES_*: addresses not mapped
    JpUnsupported, // SLPS/SLPM_*: the JP build names the tables but at other addresses
}

// Where each table is in one version's boot ELF: EE virtual addresses (DotR-PC
// NOTES/model-mrg.md, NOTES/file-layer.md), turned into file offsets through the ELF's own
// program headers.
public class GameTableAddresses
{
    public string BootElf;
    public uint AnOfsModel;
    public int ModelEntryCount;     // 81 entries + the file size terminator
    public uint AnOfsStageCam;
    public int StageCamEntryCount;  // 10 + terminator
    public uint AnOfsIcon;
    public int IconEntryCount;      // 4 + terminator
    public uint AnOfsTexEtc;
    public int TexEtcRegionCount;   // 3 regions: bounds[n]..bounds[n+1]
    public uint PackFirst;          // first model id per pack
    public uint PackSubCount;       // sub-models bound per pack
    public uint PackOfsPointers;    // pointer to each pack's sub-offset table
    public int PackCount;
    public uint ShareData;          // load address + first pack index per model type
    public int ShareDataCount;
    // SzModel_Load @ 0x1E8780, model type 1: lSzModel_LoadModelPack(id, path,
    // 0x10AA800 + side * 0xA000, size, ofs, size). The two sides are 0xA000 apart, so a
    // window can never exceed 0xA000 - and every retail window is exactly 0xA000.
    public uint WindowBufferBase;
    public uint WindowBufferSize;
    // Same function, types 4-6: dest size and read size are both 0x100000.
    public uint MonsterSlotSize;
    // The message table (SzMsg_LoadFont_Pipe @ 0x1FD680): u32 offset[count], message i starts at
    // blob + 2 * offset[i]. Card names are messages 320 + card id.
    public uint MessageOffsets;
    public uint MessageBlob;
    public int MessageCount;
    public int CardNameMessage;
    // Target_Model_Y: one signed byte per card, how high the map (Home_Camp_3D_LeaderModel @
    // 0x22B060) stands the deck leader's node 0 above the arena point (every ArenaYPos is 0),
    // with the model at scale 0.5.
    public uint MapLeaderY;
    public int MapLeaderYCount;
    // DuelistArenaDetailsArray: one 0x34-byte record per map arena: X, Y, Z (floats), then at
    // +0x10 and +0x12 the two EVENT2 sub-models the map (Home_Camp_3D @ 0x22A710) draws there,
    // a pedestal (8-10) and the marker on it (11-26).
    public uint Arenas;
    public int ArenaCount;
    public int ArenaStride;

    public static readonly GameTableAddresses Us = new GameTableAddresses
    {
        BootElf = "SLUS_205.15",
        AnOfsModel = 0x292A50, ModelEntryCount = 81,
        AnOfsStageCam = 0x292170, StageCamEntryCount = 10,
        AnOfsIcon = 0x29B050, IconEntryCount = 4,
        AnOfsTexEtc = 0x293CC0, TexEtcRegionCount = 3,
        PackFirst = 0x292CA0,
        PackSubCount = 0x292DE0,
        PackOfsPointers = 0x292E80,
        PackCount = 39,
        ShareData = 0x292BA0, ShareDataCount = 7,
        WindowBufferBase = 0x10AA800, WindowBufferSize = 0xA000,
        MonsterSlotSize = 0x100000,
        MessageOffsets = 0x2D19D0, MessageBlob = 0x2D49D4, MessageCount = 3073,
        CardNameMessage = 320,
        MapLeaderY = 0x299A30, MapLeaderYCount = 0x356,
        Arenas = 0x298BA0, ArenaCount = 22, ArenaStride = 0x34,
    };

    public static GameTableAddresses For(GameVersion version)
    {
        if (version == GameVersion.UsSlus20515)
        {
            return Us;
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

// aShareData @ 0x292BA0 (US): 7 x { u32 EE load address, u16 first pack index, u16
// invalidation mask }, one per model type. SzModel_LoadMerge reads pack `Base +
// shareDataIndex` into `Buffer`; dest size = read size = the pack's own size, so there is NO
// separate capacity constant for a pack in the ELF.
public class ShareDataEntry
{
    public uint Buffer;
    public ushort Base;
}

// Every table in the boot ELF that locates data on the disc, read out of the ELF.
public class GameTables
{
    public GameVersion Version;
    public GameTableAddresses Addresses;
    public string BootElfIsoPath;       // the disc path the ELF was read from

    // Each anOfs_* table is `entries + 1` u32: the last value is the file's size, which is what
    // lets a region be read as [table[n], table[n+1]).
    public uint[] AnOfsModel;
    public uint[] AnOfsStageCam;
    public uint[] AnOfsIcon;
    public uint[] AnOfsTexEtc;
    public uint[] PackFirst;
    public uint[] PackSubCount;
    public uint[][] PackSubOffsets;     // one per pack: COUNT[p]+1 values, last = pack size
    public ShareDataEntry[] ShareData;

    byte[] elf;
    // Loadable pieces of the boot ELF, used to turn an EE virtual address into a file offset. The
    // retail SLUS_205.15 has 835 program headers (one per overlay landing zone); exactly one has a
    // non-zero file size, and that is the one that holds every table here. The mapping is still
    // done through the headers rather than a hard-coded delta, because the JP/EU builds differ.
    List<uint> segVa = new List<uint>();
    List<uint> segOffset = new List<uint>();
    List<uint> segSize = new List<uint>();

    public int ModelEntryCount => Addresses.ModelEntryCount;
    public int PackCount => Addresses.PackCount;
    public uint WindowBufferBase => Addresses.WindowBufferBase;
    public uint WindowBufferSize => Addresses.WindowBufferSize;
    public uint MonsterSlotSize => Addresses.MonsterSlotSize;

    // SYSTEM.CNF is 57 bytes of "BOOT2 = cdrom0:\SLUS_205.15;1" and friends.
    public static string BootElfNameFrom(string systemCnf)
    {
        foreach (string line in systemCnf.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq < 0 || !line[..eq].Trim().Equals("BOOT2", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string value = line[(eq + 1)..].Trim().TrimEnd('\r');
            int colon = value.IndexOf(':');
            if (colon >= 0)
            {
                value = value[(colon + 1)..];
            }
            return IsoPaths.FileName(value.Replace('/', '\\'));
        }
        return string.Empty;
    }

    public static GameVersion DetectVersion(FileLoader disc)
    {
        var cnf = disc.Find("SYSTEM.CNF");
        if (cnf is null)
        {
            return GameVersion.Unknown;
        }
        string boot = BootElfNameFrom(System.Text.Encoding.ASCII.GetString(disc.ReadFile(cnf))).ToUpperInvariant();
        if (boot == "SLUS_205.15")
        {
            return GameVersion.UsSlus20515;
        }
        if (boot.StartsWith("SLES"))
        {
            return GameVersion.EuUnsupported;
        }
        if (boot.StartsWith("SLPS") || boot.StartsWith("SLPM"))
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
    public static GameTables FromElf(byte[] elf, GameVersion version)
    {
        return new GameTables(version, elf, GameTableAddresses.For(version).BootElf);
    }

    GameTables(GameVersion version, byte[] elfBytes, string bootElfIsoPath)
    {
        Version = version;
        Addresses = GameTableAddresses.For(version);
        BootElfIsoPath = bootElfIsoPath;
        elf = elfBytes;

        if (elf.Length < 0x34 || elf[0] != 0x7F || elf[1] != 'E' || elf[2] != 'L' || elf[3] != 'F')
        {
            throw new InvalidDataException("not an ELF");
        }
        uint phOff = BitConverter.ToUInt32(elf, 0x1C);
        ushort phEntSize = BitConverter.ToUInt16(elf, 0x2A);
        ushort phNum = BitConverter.ToUInt16(elf, 0x2C);
        for (int i = 0; i < phNum; i++)
        {
            int p = (int)phOff + i * phEntSize;
            uint type = BitConverter.ToUInt32(elf, p);
            uint fileSize = BitConverter.ToUInt32(elf, p + 16);
            if (type == 1 && fileSize != 0)
            {
                segOffset.Add(BitConverter.ToUInt32(elf, p + 4));
                segVa.Add(BitConverter.ToUInt32(elf, p + 8));
                segSize.Add(fileSize);
            }
        }
        if (segVa.Count == 0)
        {
            throw new InvalidDataException("ELF has no loadable segment with file contents");
        }

        GameTableAddresses a = Addresses;
        AnOfsModel = U32s(a.AnOfsModel, a.ModelEntryCount + 1);
        AnOfsStageCam = U32s(a.AnOfsStageCam, a.StageCamEntryCount + 1);
        AnOfsIcon = U32s(a.AnOfsIcon, a.IconEntryCount + 1);
        AnOfsTexEtc = U32s(a.AnOfsTexEtc, a.TexEtcRegionCount + 1);

        PackFirst = U32s(a.PackFirst, a.PackCount);
        PackSubCount = U32s(a.PackSubCount, a.PackCount);
        uint[] packOfsPointers = U32s(a.PackOfsPointers, a.PackCount);
        PackSubOffsets = new uint[a.PackCount][];
        for (int p = 0; p < a.PackCount; p++)
        {
            PackSubOffsets[p] = U32s(packOfsPointers[p], (int)PackSubCount[p] + 1);
        }

        ShareData = new ShareDataEntry[a.ShareDataCount];
        int shareOffset = VaToOffset(a.ShareData);
        for (int i = 0; i < a.ShareDataCount; i++)
        {
            int p = shareOffset + i * 8;
            ShareData[i] = new ShareDataEntry
            {
                Buffer = BitConverter.ToUInt32(elf, p),
                Base = BitConverter.ToUInt16(elf, p + 4),
            };
        }
    }

    int VaToOffset(uint va)
    {
        for (int i = 0; i < segVa.Count; i++)
        {
            if (va >= segVa[i] && va < segVa[i] + segSize[i])
            {
                return (int)(segOffset[i] + (va - segVa[i]));
            }
        }
        throw new ArgumentOutOfRangeException(nameof(va), $"0x{va:X8} is not inside any loadable segment");
    }

    uint[] U32s(uint address, int count)
    {
        int p = VaToOffset(address);
        uint[] values = new uint[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = BitConverter.ToUInt32(elf, p + i * 4);
        }
        return values;
    }

    // Byte range of MODEL.MRG entry `i` (packs 0-38, then the 42 windows).
    public (uint Offset, uint Size) ModelEntry(int i)
    {
        return (AnOfsModel[i], AnOfsModel[i + 1] - AnOfsModel[i]);
    }

    public (uint Offset, uint Size) TexEtcRegion(int i)
    {
        return (AnOfsTexEtc[i], AnOfsTexEtc[i + 1] - AnOfsTexEtc[i]);
    }

    // ---- messages (DotR-PC tools/usmsg.py, NOTES/cards.md) -------------------------------------
    // Each u16 word: bit 15 last word, bit 14 a two-word back-reference (`w & 0x3F` words,
    // starting `(w & 0x1FFF) >> 6` words into message `next & 0x3FFF`), bits 0-12 the glyph.
    // There is no character table: a glyph index is the character.
    const int MsgEnd = 0x8000;
    const int MsgRef = 0x4000;
    const int NameBuffer = 2;   // message the game writes the player's name into at runtime

    static string[] glyphs;

    static string[] Glyphs()
    {
        if (glyphs != null)
        {
            return glyphs;
        }
        string[] g = new string[0x2000];
        g[0] = "\n";
        g[1] = "○";
        g[2] = ",";
        g[3] = "●";
        g[335] = "∞";
        g[336] = "?";
        g[337] = "?";
        g[338] = ":";
        g[339] = "…";
        g[348] = "・";
        g[361] = "α";
        g[362] = "<";
        g[363] = ">";
        g[364] = "3.";
        g[365] = "□";
        g[366] = "△";
        g[367] = "×";
        g[368] = ";";
        string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        string lower = "abcdefghijklmnopqrstuvwxyz";
        // Full-width set. 30 is an empty bitmap: a full-width space.
        string full = upper + "　[]" + lower + "()" + "123456789" + "0!\"#$%&'=^-¥,./_";
        // Half-width set: the same order, but it has no comma.
        string half = upper + " []" + lower + "()" + "123456789" + "0!\"#$%&'=^-¥./_";
        for (int i = 0; i < full.Length; i++)
        {
            g[4 + i] = full[i].ToString();
        }
        for (int i = 0; i < half.Length; i++)
        {
            g[86 + i] = half[i].ToString();
        }
        glyphs = g;
        return g;
    }

    int MessageWord(int fileOffset)
    {
        return BitConverter.ToUInt16(elf, fileOffset);
    }

    int MessageStart(int i)
    {
        uint offset = BitConverter.ToUInt32(elf, VaToOffset(Addresses.MessageOffsets) + 4 * i);
        return VaToOffset(Addresses.MessageBlob) + 2 * (int)offset;
    }

    // Appends one element at `at`; returns how many words it took.
    int MessageElement(int at, System.Text.StringBuilder sb)
    {
        int w = MessageWord(at);
        if ((w & MsgRef) != 0)
        {
            int target = MessageWord(at + 2) & 0x3FFF;
            int skip = (w & 0x1FFF) >> 6;
            int left = w & 0x3F;
            if (target == NameBuffer)
            {
                sb.Append("{NAME}");
                return 2;
            }
            int src = MessageStart(target) + 2 * skip;
            while (left > 0)
            {
                int n = MessageElement(src, sb);
                if ((MessageWord(src + 2 * (n - 1)) & MsgEnd) != 0)
                {
                    break;
                }
                left -= n;
                src += 2 * n;
            }
            return 2;
        }
        int code = w & 0x1FFF;
        string? c = Glyphs()[code];
        if (c == null)
        {
            sb.Append($"<{code}>");
        }
        else
        {
            sb.Append(c);
        }
        return 1;
    }

    public string Message(int i)
    {
        var sb = new System.Text.StringBuilder();
        int at = MessageStart(i);
        while (true)
        {
            int n = MessageElement(at, sb);
            if ((MessageWord(at + 2 * (n - 1)) & MsgEnd) != 0)
            {
                return sb.ToString();
            }
            at += 2 * n;
        }
    }

    // How high the map stands this card's model as the deck leader (Target_Model_Y).
    public int MapLeaderY(int cardId)
    {
        if (cardId < 0 || cardId >= Addresses.MapLeaderYCount)
        {
            return 0;
        }
        return (sbyte)elf[MapLeaderYOffset(cardId)];
    }

    // Where card `cardId`'s Target_Model_Y byte is in the boot ELF file.
    public int MapLeaderYOffset(int cardId)
    {
        return VaToOffset(Addresses.MapLeaderY) + cardId;
    }

    // Changes the map height in the ELF read here only; saving it to the disc is the caller's.
    public void SetMapLeaderY(int cardId, int y)
    {
        elf[MapLeaderYOffset(cardId)] = (byte)(sbyte)y;
    }

    // Where map arena `arena` is, and the two EVENT2 sub-models drawn there.
    public void Arena(int arena, out float x, out float y, out float z, out int pedestal, out int marker)
    {
        int p = VaToOffset(Addresses.Arenas) + arena * Addresses.ArenaStride;
        x = BitConverter.ToSingle(elf, p);
        y = BitConverter.ToSingle(elf, p + 4);
        z = BitConverter.ToSingle(elf, p + 8);
        pedestal = BitConverter.ToInt16(elf, p + 0x10);
        marker = BitConverter.ToInt16(elf, p + 0x12);
    }

    public string CardName(int cardId)
    {
        return Message(Addresses.CardNameMessage + cardId);
    }
}
