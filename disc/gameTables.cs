using static DotrAssetModder.Bin;
namespace DotrAssetModder;

/* Which disc this is, from the boot ELF named in SYSTEM.CNF. The tables below live at different
   addresses in every build, so nothing here is read without a version. Only builds whose
   addresses were measured are listed; a guessed address would corrupt a disc. */
public enum GameVersion
{
    Unknown,
    UsSlus20515,   // "Yu-Gi-Oh! The Duelists of the Roses" (USA), boot ELF SLUS_205.15
    JpSlpm65050,   // "Yu-Gi-Oh! Shin Duel Monsters II" (Japan), boot ELF SLPM_650.50
    EuSles52480,   // "Yu-Gi-Oh! The Duelists of the Roses" (Europe), boot ELF SLES_524.80
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
       blob + MessageOffsetUnit * offset[i] (US counts u16 words, JP counts bytes). Card names
       are messages CardNameMessage + card id (SzDuel_GetMsgName). */
    public uint MessageOffsets;
    public uint MessageBlob;
    public int MessageCount;
    public int MessageOffsetUnit;
    public int CardNameMessage;
    // Null when the text is in the boot ELF; else one disc file per language, which the game
    // loads whole at MessageFileLoadAddress (the addresses above are inside it).
    public MessageLanguage[] MessageLanguages;
    public uint MessageFileLoadAddress;
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
        MessageOffsets = 0x2D19D0, MessageBlob = 0x2D49D4, MessageCount = 3073, MessageOffsetUnit = 2,
        CardNameMessage = 320,
        MapLeaderY = 0x299A30, MapLeaderYCount = 0x356,
        Arenas = 0x298BA0, ArenaCount = 22, ArenaStride = 0x34,
    };

    /* The JP ELF keeps its symbols (anOfs_*, aShareData, gSzMsg_Data/Root). FIRST, COUNT and OFS
       have no JP names; they sit at the same distances from aShareData as in the US build and hold
       the same values, and every pack's sub-offsets end at its MODEL.MRG size. Window buffer from
       SzModel_Load @ 0x1E6410 (lui 0x10B / ori 0xB000, side * 0xA000); card names from
       SzDuel_GetMsgName @ 0x1DBEA0 (317 + id for id < 0x356). */
    public static readonly GameTableAddresses JpSlpm65050 = new GameTableAddresses
    {
        BootElf = "SLPM_650.50",
        ModelMrgOffsets = 0x28E470, ModelEntryCount = 81,
        StageCamMrgOffsets = 0x28DB90, StageCamEntryCount = 10,
        IconMrgOffsets = 0x296A90, IconEntryCount = 4,
        TexEtcMrgOffsets = 0x28F700, TexEtcRegionCount = 3,
        FirstPack = 0x28E6C0,
        PackSubCount = 0x28E800,
        PackOfsPointers = 0x28E8A0,
        PackCount = 39,
        ShareData = 0x28E5C0, ShareDataCount = 7,
        WindowBufferBase = 0x10BB000, WindowBufferSize = 0xA000,
        MessageOffsets = 0x2CD4E0, MessageBlob = 0x2D70D4, MessageCount = 9981, MessageOffsetUnit = 1,
        CardNameMessage = 317,
        MapLeaderY = 0x295470, MapLeaderYCount = 0x356,
        Arenas = 0x2945E0, ArenaCount = 22, ArenaStride = 0x34,
    };

    /* The EU tables hold the same values as the US ones (same MODEL.MRG), found by those values;
       SzModel_Load builds the window buffer with the same code as US. The EU text is not in the
       ELF: SzMsg_Init (0x246CA0) loads one of MSG/MSG_E/F/G/I/S.DAT (an "MWo3" overlay, loaded
       whole at 0x3B2000) and points the message table, blob and font into it; the table and blob
       are at the same addresses in all five, and card names are 323 + id in each. */
    public static readonly GameTableAddresses EuSles52480 = new GameTableAddresses
    {
        BootElf = "SLES_524.80",
        ModelMrgOffsets = 0x293790, ModelEntryCount = 81,
        StageCamMrgOffsets = 0x292EB0, StageCamEntryCount = 10,
        IconMrgOffsets = 0x29BF40, IconEntryCount = 4,
        TexEtcMrgOffsets = 0x294B10, TexEtcRegionCount = 3,
        FirstPack = 0x2939E0,
        PackSubCount = 0x293B20,
        PackOfsPointers = 0x293BC0,
        PackCount = 39,
        ShareData = 0x2938E0, ShareDataCount = 7,
        WindowBufferBase = 0x10AA800, WindowBufferSize = 0xA000,
        MessageOffsets = 0x3B2100, MessageBlob = 0x3B613C, MessageCount = 4111, MessageOffsetUnit = 2,
        CardNameMessage = 323,
        MessageLanguages = GameTables.EuMessageLanguages, MessageFileLoadAddress = 0x3B2000,
        MapLeaderY = 0x29A960, MapLeaderYCount = 0x356,
        Arenas = 0x299AD0, ArenaCount = 22, ArenaStride = 0x34,
    };

    public static GameTableAddresses For(GameVersion version)
    {
        if (version == GameVersion.UsSlus20515)
        {
            return UsSlus20515;
        }
        if (version == GameVersion.JpSlpm65050)
        {
            return JpSlpm65050;
        }
        if (version == GameVersion.EuSles52480)
        {
            return EuSles52480;
        }
        throw new NotSupportedException("unknown game version: no table addresses");
    }
}

// One of a disc's text files (EU: MSG/MSG_E.DAT ...) and the font glyphs it holds.
public class MessageLanguage
{
    public string Name;
    public string File;
    public string GlyphText;    // glyph k is character k
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
    // The bytes the message table is read from: the boot ELF, or MessageLanguage's file (null
    // until read from a disc, so a loose EU ELF has no card names).
    byte[] messageBytes;
    // The language read, for a build with MessageLanguages; else null.
    public MessageLanguage MessageLanguage;
    string[] glyphTable;

    /* Which language a disc with MessageLanguages is read in (Program.config.messageLanguage, by
       name; the first one when unset). Windows that show card names compare the count with the
       one they read with and read again when it changed. */
    public static int MessageLanguageChangeCount;

    public static void ChooseMessageLanguage(string languageName)
    {
        Program.config.messageLanguage = languageName;
        MessageLanguageChangeCount++;
    }

    // The languages a disc's text comes in; null when it has one, in its ELF (US, JP, unknown).
    public static MessageLanguage[] MessageLanguagesOf(FileLoader disc)
    {
        GameVersion version = DetectVersion(disc);
        if (version == GameVersion.Unknown)
        {
            return null;
        }
        return GameTableAddresses.For(version).MessageLanguages;
    }

    public static MessageLanguage ChosenLanguage(MessageLanguage[] languages)
    {
        string? chosenName = Program.config?.messageLanguage;
        foreach (MessageLanguage language in languages)
        {
            if (language.Name == chosenName)
            {
                return language;
            }
        }
        return languages[0];
    }

    // Reads the chosen language's text file from `disc` (nothing for a build whose text is in its ELF).
    public void ReadMessages(FileLoader disc)
    {
        if (Addresses.MessageLanguages == null)
        {
            return;
        }
        MessageLanguage language = ChosenLanguage(Addresses.MessageLanguages);
        messageBytes = disc.ReadFile(language.File);
        MessageLanguage = language;
        glyphTable = null;
    }
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
        if (bootElfName == GameTableAddresses.UsSlus20515.BootElf)
        {
            return GameVersion.UsSlus20515;
        }
        if (bootElfName == GameTableAddresses.JpSlpm65050.BootElf)
        {
            return GameVersion.JpSlpm65050;
        }
        if (bootElfName == GameTableAddresses.EuSles52480.BootElf)
        {
            return GameVersion.EuSles52480;
        }
        return GameVersion.Unknown;
    }

    public static GameTables Load(FileLoader disc)
    {
        GameVersion version = DetectVersion(disc);
        var elfFile = disc.Get(GameTableAddresses.For(version).BootElf);
        var tables = new GameTables(version, disc.ReadFile(elfFile), elfFile.IsoPath);
        tables.ReadMessages(disc);
        return tables;
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
        if (Addresses.MessageLanguages == null)
        {
            messageBytes = bootElfBytes;
        }

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

    static string[] cachedUsGlyphs;

    /* The JP font (gSzMsg_Image, 1947 glyphs of 22x22 at 4 bpp): glyph k is character k of this
       text, read off the font's bitmaps for glyphs 0-929, the range card names use. Checked by
       decoding all 854 JP card names against their US names. Full-width Latin and digits are
       written as ASCII, as in the US table; 30 is the full-width space, 270 the half-width one. */
    const string JpGlyphText =
        "\n○、●ABCDEFGHIJKLMNOPQRSTUVWXYZ　[]abcdefg" + // 0
        "hijklmnopqrstuvwxyz()1234567890・…?“”☆∞♂♀" + // 40
        "※%=~!♪↑↓←→世あいうえおかきくけこさしすせそたちつてとなにぬねのはひふへ" + // 80
        "ほまみむめもやゆよらりるれろわゐゑをんっーぁぃぅぇぉがぎぐげござじずぜぞだぢづで" + // 120
        "どばびぶべぼぱぴぷぺぽゃゅょアイウエオカキクケコサシスセソタチツテトナニヌネノハ" + // 160
        "ヒフヘホマミムメモヤユヨラリルレロワヰヴヱヲンッーァィゥェォガギグゲゴザジズゼゾ" + // 200
        "ダヂヅデドバビブベボパピプペポャュョ英数0123456789 -ABCDEFGH" + // 240
        "IJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuv" + // 280
        "wxyz記号#$&魔法強化罠範囲全体儀式使戦士獣鳥悪天昆虫恐竜爬類魚海機械雷水炎" + // 320
        "岩石植物旧神光闇地風森荒野山草原迷宮闘員少尉中大佐准将上級元帥昇進降格埋財宝伏札" + // 360
        "予想自己移動力増回復直撃半減形変更同族召喚軽支援拡味方抗敵特定種弱呪縛抹殺真表示" + // 400
        "省略編成行設値破棄画面戻本当実良以下条件該枚限越一持既送先選択補充能転生完了入功" + // 440
        "獲得失敗個発見別属性攻守備現在階基情報殊決終墓場詳細手裏取消書位置複合順序蘇参照" + // 480
        "確島峡約束日時急練習初期家側薔薇効果態寄準感染界解除去験版試月売製品待頂赤白／名" + // 520
        "称青眼龍究極暗黒王紅火這密林砦翼騎千年絵巻盾頭邪空術師泥棒妖精贈聖音楽帝明乙女厳" + // 560
        "老剣薄幸美封印者右足左腕道幻眠子人魅惑怪盗仮古代導配誘紳操陰陽霊注射番亡鎧武招目" + // 600
        "葬死銀髑髏寺院屍脚骨金色象食太無讐狩斬鬼東雄協魂像謎傀儡紫影首住冥異次座護伝説豪" + // 640
        "獄国忍裁装侵響氷小牛漆豹獅深長虎有三姉妹鷹巨狗冠戴蒼晦城覇職鏡命判司知恵淵玉銃運" + // 680
        "未熟壊芸調理屋根偵察喰箱男爵壁逆心鎖砂計北勇気誕沈黙間吸血蜘蛛蠍幼兵二貪口蜥蜴陸" + // 720
        "恍惚潜舌要塞鮫室前車振刃拷問隊造砲球爆弾軍曹型鉄鋼仙双電娘々怒轟蛇踊沼万亀狸満潮" + // 760
        "泉両用弓引巫壺朱雀溶窟壷棲木悟樹緑笑花咲瓶葉磁従星刻燃嵐羽帚秘薬治療祝福粉刑昼夜" + // 800
        "事業訪仏酸剤息吹永遠渇割払塵寒波洗脳結雨暴唱『』収呼声律殻制斧甲器付猛歯羅菌矢角" + // 840
        "鞭晶覚醒改工温突扇灼熱槍爪殖芳香華麗分身繭反孵六芒催涙蟻超重網粘着線硫落穴零砕副" + // 880
        "作偽避針触幕活祈誓契"; // 920

    /* The EU English font (MSG_E.DAT, 318 glyphs at 0x3E4340): a narrow set 0-162, then a wide
       copy 163-317, read off its bitmaps and checked by decoding all 854 card names (853 equal the
       US names; card 414 is "Kuwagata Alpha"). Glyph 0 (drawn as a TM) is the line break, as in
       US: message 0 is that one glyph. 1 and 228 are spaces. MSG_F/I/S have the same font
       (identical bitmaps); MSG_G's lacks glyph 160, so its glyphs from 160 on are one lower.
       All five languages' card names were decoded with these. */
    const string EuGlyphText =
        "\n ©,®ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi" + // 0
        "jklmnopqrstuvwxyz1234567890-•°\"#$%&'()=ƒ" + // 40
        "ßÀÁÂÃÄÅÆŒÇÈÉÊËÌÍÎÏÐÒÓÔÕÖØÑÙÚÛÜ“”«».[]!?à" + // 80
        "áâãäåæœçèéêëìíîïðòóôõöøñùúûü‘’<>{}¡¿:/□○" + // 120
        ";∞△ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijk" + // 160
        "lmnopqrstuvwxyz1234567890-•° \"#$%&'()=ƒß" + // 200
        "ÀÁÂÃÄÅÆŒÇÈÉÊËÌÍÎÏÐÒÓÔÕÖØÑÙÚÛÜ“”«».[]!?àá" + // 240
        "âãäåæœçèéêëìíîïðòóôõöøñùúûü‘’<>,{}¡¿∞_"; // 280

    public static readonly MessageLanguage[] EuMessageLanguages =
    {
        new MessageLanguage { Name = "English", File = "MSG/MSG_E.DAT", GlyphText = EuGlyphText },
        new MessageLanguage { Name = "French", File = "MSG/MSG_F.DAT", GlyphText = EuGlyphText },
        new MessageLanguage { Name = "German", File = "MSG/MSG_G.DAT", GlyphText = EuGlyphText.Remove(160, 1) },
        new MessageLanguage { Name = "Italian", File = "MSG/MSG_I.DAT", GlyphText = EuGlyphText },
        new MessageLanguage { Name = "Spanish", File = "MSG/MSG_S.DAT", GlyphText = EuGlyphText },
    };

    // Glyph index -> text for this build's font (null = not mapped, shown as <index>).
    string[] Glyphs()
    {
        if (glyphTable != null)
        {
            return glyphTable;
        }
        if (MessageLanguage != null)
        {
            glyphTable = GlyphTableFrom(MessageLanguage.GlyphText);
        }
        else if (Version == GameVersion.JpSlpm65050)
        {
            glyphTable = GlyphTableFrom(JpGlyphText);
        }
        else
        {
            glyphTable = UsGlyphs();
        }
        return glyphTable;
    }

    // Every character any build's glyph table can produce (for the UI font).
    public static string GlyphCharacters()
    {
        var characters = new System.Text.StringBuilder(JpGlyphText + EuGlyphText);
        foreach (string? glyphText in UsGlyphs())
        {
            if (glyphText != null)
            {
                characters.Append(glyphText);
            }
        }
        return characters.ToString();
    }

    static string[] GlyphTableFrom(string glyphText)
    {
        string[] table = new string[0x2000];
        for (int i = 0; i < glyphText.Length; i++)
        {
            table[i] = glyphText[i].ToString();
        }
        return table;
    }

    static string[] UsGlyphs()
    {
        if (cachedUsGlyphs != null)
        {
            return cachedUsGlyphs;
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
        cachedUsGlyphs = glyphTable;
        return glyphTable;
    }

    int MessageWord(int fileOffset)
    {
        return ReadU16(messageBytes, fileOffset);
    }

    // Where EE address `address` (in the message table or blob) is in messageBytes.
    int MessageFileOffset(uint address)
    {
        if (Addresses.MessageLanguages == null)
        {
            return VirtualAddressToFileOffset(address);
        }
        return (int)(address - Addresses.MessageFileLoadAddress);
    }

    int MessageStart(int messageIndex)
    {
        uint messageOffset = ReadU32(messageBytes, MessageFileOffset(Addresses.MessageOffsets) + 4 * messageIndex);
        return MessageFileOffset(Addresses.MessageBlob) + Addresses.MessageOffsetUnit * (int)messageOffset;
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

    // Empty when the text was not read (an EU ELF opened without its disc).
    public string CardName(int cardId)
    {
        if (messageBytes == null)
        {
            return string.Empty;
        }
        return Message(Addresses.CardNameMessage + cardId);
    }
}
