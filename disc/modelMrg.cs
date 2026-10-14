using System.Security.Cryptography;
using static DotrAssetModder.Bin;

namespace DotrAssetModder;

/* The DATA/MODEL.MRG archive: parse, import staging and save.

   MODEL.MRG is 81 entries of anOfs_MODEL: 39 share-data packs (297 RH0 sub-models between them)
   then 42 single-model stained-glass windows. A pack's sub-models are located by the ELF's own
   per-pack sub-offset table (OFS[p]), which is authoritative.

   A sub-model's SLOT is the gap to the next OFS entry (the last entry of OFS[p] is the pack size).
   A replacement is written inside that slot and nothing else moves, so no ELF table is ever
   touched; growing past a slot needs the pack repacked. */
public class ModelMrg : IDisposable
{
    public const string MrgPath = "DATA/MODEL.MRG";

    // One RH0 block: a pack sub-model, or a window (a whole anOfs_MODEL entry of its own).
    public class Block
    {
        public int Index;             // global index: pack sub-models in pack order, then windows
        public int Pack = -1;         // -1 for a window
        public int SubModelIndex = -1;            // sub-model index inside the pack, -1 for a window
        public int Entry;             // anOfs_MODEL entry this block lives in (pack index, or 39..80)
        public int ModelId = -1;      // FIRST[pack] + K; -1 for a window (windows have no FIRST/COUNT)
        public int WindowIndex = -1;  // 0..41 for a window
        public long Offset;           // byte offset inside MODEL.MRG
        public long Slot;             // bytes this block may occupy: gap to OFS[p][K+1], or the
                                      // window entry size capped by the window load buffer
        public long Size;             // true block size (Rh0Assembler.ComputeBlockEnd)
        public uint Flags;            // header+0x04
        public int NodeCount;
        public int TexCount;
        public bool HasEmbeddedMot;
        public int ModelType = -1;    // aShareData index covering this block's pack (window: 1)
        public string Hash = string.Empty;   // SHA256 of the block's true bytes
        public int IdentityGroup;     // blocks with the same Hash share a group
        public int Copies = 1;        // identical blocks sharing these bytes (22 for DUEL)
        public bool IsMirror;         // a later copy (DUEL01-21): edited through its first copy
        public string Name = string.Empty;         // "DUEL00/00", "window_07"
        public string FriendlyName = string.Empty; // "Duel table and board"; "" if unknown
        public string Group = string.Empty;        // "DUEL", "RING", "STAGE", "EVENT", "FIELD", "windows"

        public long Gap
        {
            get { return Slot - Size; }
        }

        public bool IsWindow
        {
            get { return Pack < 0; }
        }

        public override string ToString()
        {
            return Name;
        }
    }

    public class PackInfo
    {
        public int Index;
        public string Name = string.Empty;
        public int ModelType = -1;
        public uint LoadBuffer;        // aShareData[type].Buffer, the EE address the pack loads to
        public long Offset;            // in MODEL.MRG
        public long Size;
        public int FirstModelId;
        public List<Block> Blocks = new List<Block>();
    }

    // One staged replacement.
    public class StagedBlock
    {
        public Block Block;
        public byte[] Bytes;
        public string SourcePath;
    }

    /* What Stage() answers with. Fits == false means Staged == false: nothing is staged when it
       does not fit its slot. Error != null means the rebuild threw and Size/Fits are meaningless. */
    public class StageResult
    {
        public int Index;
        public string Name;
        public bool Fits;
        public bool Staged;
        public long Size;
        public long Slot;
        public int Mirrors;
        public List<string> Warnings = new List<string>();
        public string? Error;

        public long OverBy
        {
            get
            {
                if (Fits)
                {
                    return 0;
                }
                return Math.Max(0, Size - Slot);
            }
        }

        public string Summary
        {
            get
            {
                if (Error != null)
                {
                    return $"{Name}: {Error}";
                }
                if (Fits)
                {
                    return $"{Name}: fits, {Size:N0} / {Slot:N0} B ({Slot - Size:N0} B spare)";
                }
                return $"{Name}: over by {OverBy:N0} B ({Size:N0} / {Slot:N0} B)";
            }
        }
    }

    /* What Build() answers with: the changes, plus which blocks they came from. Unchanged means
       the replacement was byte-identical to what is on the disc. Errors are over-slot blocks;
       any error means no change at all is kept. */
    public class BuildResult
    {
        public ChangeSet Changes = new ChangeSet();
        public List<Block> Written = new List<Block>();
        public List<Block> Mirrored = new List<Block>();
        public List<Block> Unchanged = new List<Block>();
        public List<string> Errors = new List<string>();

        public bool Ok
        {
            get { return Errors.Count == 0; }
        }
    }

    // Every group name in list order, for the filter.
    public static readonly string[] Groups = { "DUEL", "RING", "STAGE", "EVENT", "FIELD", "windows" };

    /* What each model is, where known (DotR-PC NOTES/model-mrg.md). The DUEL names come from
       renders, the FIELD order from messages 71-80, the window index from
       SzDuel_SetupDuel @ 0x1DDE4C: side * 100 + (leader kind & 0x1F), kinds in the game's order
       (DotR-PC tools/rules.py KINDS). STAGE and EVENT scenes are not mapped yet. */
    static readonly string[] DuelNames =
        { "Duel table and board", "Gold emblem", "Dark crystal", "Cyan crystal", "Pink crystal" };
    static readonly string[] FieldNames =
        { "Forest", "Wasteland", "Mountain", "Meadow", "Sea", "Dark", "Toon", "Normal", "Labyrinth", "Crush" };
    static readonly string[] KindNames =
    {
        "Dragon", "Spellcaster", "Zombie", "Warrior", "Beast-Warrior", "Beast", "Winged Beast", "Fiend",
        "Fairy", "Insect", "Dinosaur", "Reptile", "Fish", "Sea Serpent", "Machine", "Thunder", "Aqua",
        "Pyro", "Rock", "Plant", "Immortal",
    };

    FileLoader? discLoader;
    bool ownsDisc;
    byte[] mrgFileBytes = Array.Empty<byte>();
    Dictionary<int, StagedBlock> stagedBlocksByIndex = new Dictionary<int, StagedBlock>();

    public GameTables Tables;
    public List<PackInfo> Packs = new List<PackInfo>();
    public List<Block> Blocks = new List<Block>();          // every block, packs then windows
    public List<Block> Windows = new List<Block>();
    public List<List<Block>> IdentityGroups = new List<List<Block>>();
    public List<string> Anomalies = new List<string>();

    public FileLoader Disc
    {
        get
        {
            if (discLoader == null)
            {
                throw new InvalidOperationException("this MODEL.MRG was parsed from bytes, not a disc");
            }
            return discLoader;
        }
    }

    public IEnumerable<StagedBlock> Staged
    {
        get { return stagedBlocksByIndex.Values; }
    }

    public int StagedCount
    {
        get { return stagedBlocksByIndex.Count; }
    }

    public long MrgSize
    {
        get { return mrgFileBytes.LongLength; }
    }

    // One representative per identity group, in first-seen order: the 192 distinct models.
    public IEnumerable<Block> DistinctBlocks
    {
        get { return IdentityGroups.Select(identityGroup => identityGroup[0]); }
    }

    ModelMrg()
    {
    }

    public ModelMrg(FileLoader disc, bool ownsDisc = false)
    {
        this.discLoader = disc;
        this.ownsDisc = ownsDisc;
        Reload();
    }

    public static ModelMrg Parse(byte[] mrgBytes, GameTables tables)
    {
        var parsedMrg = new ModelMrg();
        parsedMrg.ParseBytes(mrgBytes, tables);
        return parsedMrg;
    }

    public static ModelMrg Open(string isoPath)
    {
        return new ModelMrg(FileLoader.Open(isoPath), true);
    }

    // The pack names are the JP debug build's own symbols (NOTES/model-mrg.md).
    public static string PackName(int packIndex)
    {
        if (packIndex >= 0 && packIndex <= 21)
        {
            return $"DUEL{packIndex:D2}";
        }
        if (packIndex == 22)
        {
            return "RING";
        }
        if (packIndex >= 23 && packIndex <= 32)
        {
            return $"STAGE{packIndex - 23}";
        }
        if (packIndex >= 33 && packIndex <= 37)
        {
            return $"EVENT{packIndex - 33}";
        }
        if (packIndex == 38)
        {
            return "FIELD";
        }
        return $"pack{packIndex}";
    }

    // Re-reads MODEL.MRG from the disc and re-parses it. Drops anything staged.
    public void Reload()
    {
        stagedBlocksByIndex.Clear();
        ParseBytes(Disc.ReadFile(MrgPath), GameTables.Load(Disc));
    }

    void ParseBytes(byte[] bytes, GameTables tables)
    {
        Tables = tables;
        mrgFileBytes = bytes;
        Packs.Clear();
        Blocks.Clear();
        Windows.Clear();
        IdentityGroups.Clear();
        Anomalies.Clear();

        if (mrgFileBytes.LongLength != tables.ModelMrgOffsets[tables.ModelEntryCount])
        {
            Anomalies.Add($"MODEL.MRG is {mrgFileBytes.LongLength:N0} B but anOfs_MODEL ends at " +
                          $"{tables.ModelMrgOffsets[tables.ModelEntryCount]:N0}");
        }

        int index = 0;
        for (int packIndex = 0; packIndex < tables.PackCount; packIndex++)
        {
            var (packOffset, packSize) = tables.ModelEntry(packIndex);
            int modelType = TypeOfPack(tables, packIndex);
            var pack = new PackInfo();
            pack.Index = packIndex;
            pack.Name = PackName(packIndex);
            pack.ModelType = modelType;
            if (modelType >= 0)
            {
                pack.LoadBuffer = tables.ShareData[modelType].Buffer;
            }
            pack.Offset = packOffset;
            pack.Size = packSize;
            pack.FirstModelId = (int)tables.FirstPack[packIndex];

            uint[] subModelOffsets = tables.PackSubOffsets[packIndex];
            int subModelCount = (int)tables.PackSubCount[packIndex];
            if (subModelOffsets[subModelCount] != packSize)
            {
                Anomalies.Add($"{pack.Name}: OFS[{packIndex}][{subModelCount}] = {subModelOffsets[subModelCount]:N0} but the pack is {packSize:N0} B");
            }

            for (int k = 0; k < subModelCount; k++)
            {
                long blockOffset = packOffset + subModelOffsets[k];
                long blockSlot = subModelOffsets[k + 1] - subModelOffsets[k];
                Block block = ReadBlock(blockOffset, blockSlot);
                block.Index = index++;
                block.Pack = packIndex;
                block.SubModelIndex = k;
                block.Entry = packIndex;
                block.ModelId = pack.FirstModelId + k;
                block.ModelType = modelType;
                block.Name = $"{pack.Name}/{k:D2}";
                pack.Blocks.Add(block);
                Blocks.Add(block);
            }

            Packs.Add(pack);
        }

        /* Windows: anOfs_MODEL entries past the packs, one RH0 block each, loaded by
           SzModel_Load for model type 1 into a 0xA000 buffer - that cap, not the entry size,
           is a window's real slot (they are equal on retail). */
        long windowCapacity = tables.WindowBufferSize;
        for (int entryIndex = tables.PackCount; entryIndex < tables.ModelEntryCount; entryIndex++)
        {
            var (blockOffset, size) = tables.ModelEntry(entryIndex);
            long blockSlot = Math.Min(size, windowCapacity);
            if (size > windowCapacity)
            {
                Anomalies.Add($"window entry {entryIndex} is {size:N0} B, past the {windowCapacity:N0} B load buffer");
            }
            Block block = ReadBlock(blockOffset, blockSlot);
            block.Index = index++;
            block.Entry = entryIndex;
            block.WindowIndex = entryIndex - tables.PackCount;
            block.ModelType = 1;
            block.Name = $"window_{block.WindowIndex:D2}";
            Windows.Add(block);
            Blocks.Add(block);
        }

        // Identity groups: the 22 DUEL packs hold the same bytes, so one edit covers all of them.
        var groupIndexByHash = new Dictionary<string, int>();
        foreach (Block block in Blocks)
        {
            if (!groupIndexByHash.TryGetValue(block.Hash, out int groupIndex))
            {
                groupIndex = IdentityGroups.Count;
                groupIndexByHash[block.Hash] = groupIndex;
                IdentityGroups.Add(new List<Block>());
            }
            block.IdentityGroup = groupIndex;
            IdentityGroups[groupIndex].Add(block);
        }
        foreach (List<Block> identityGroup in IdentityGroups)
        {
            foreach (Block block in identityGroup)
            {
                block.Copies = identityGroup.Count;
                block.IsMirror = block != identityGroup[0];
            }
        }

        foreach (Block block in Blocks)
        {
            block.FriendlyName = FriendlyNameOf(block);
            block.Group = GroupOf(block);
        }
    }

    /* Model type of a pack: the aShareData entry whose Base is the greatest one <= pack index
       (anOfs_MODEL[aShareData[type].Base + i], NOTES/model-mrg.md). Bases at or past the pack
       count address the windows, not a pack. */
    static int TypeOfPack(GameTables tables, int packIndex)
    {
        int bestShareType = -1;
        for (int shareTypeIndex = 0; shareTypeIndex < tables.ShareData.Length; shareTypeIndex++)
        {
            ShareDataEntry shareEntry = tables.ShareData[shareTypeIndex];
            if (shareEntry.Base > packIndex || shareEntry.Base >= tables.PackCount)
            {
                continue;
            }
            if (bestShareType < 0 || shareEntry.Base > tables.ShareData[bestShareType].Base)
            {
                bestShareType = shareTypeIndex;
            }
        }
        return bestShareType;
    }

    Block ReadBlock(long blockOffset, long blockSlot)
    {
        var block = new Block();
        block.Offset = blockOffset;
        block.Slot = blockSlot;
        block.Size = blockSlot;
        if (blockOffset + 0x80 > mrgFileBytes.LongLength)
        {
            Anomalies.Add($"block at 0x{blockOffset:X} runs past the file");
            return block;
        }
        if (ReadU32(mrgFileBytes, (int)blockOffset) != Rh0Assembler.Rh0Magic)
        {
            Anomalies.Add($"block at 0x{blockOffset:X} has no RH0 magic (0x{ReadU32(mrgFileBytes, (int)blockOffset):X8})");
            return block;
        }

        long blockEnd = Rh0Assembler.ComputeBlockEnd(mrgFileBytes, blockOffset, out _);
        if (blockEnd < 0 || blockEnd == long.MaxValue || blockEnd > blockOffset + blockSlot)
        {
            Anomalies.Add($"block at 0x{blockOffset:X}: ComputeBlockEnd gave 0x{blockEnd:X} for a slot of {blockSlot:N0} B");
            blockEnd = blockOffset + blockSlot;
        }
        block.Size = blockEnd - blockOffset;
        block.Flags = ReadU32(mrgFileBytes, (int)(blockOffset + 0x04));
        block.HasEmbeddedMot = (block.Flags & 0xFFu) != 0;
        long texTable = blockOffset + ReadU32(mrgFileBytes, (int)(blockOffset + 0x38));
        if (texTable >= blockOffset && texTable + 4 <= blockEnd)
        {
            block.TexCount = (int)ReadU32(mrgFileBytes, (int)texTable);
        }
        block.NodeCount = CountNodes(mrgFileBytes, blockOffset + ReadU32(mrgFileBytes, (int)(blockOffset + 0x10)), (int)ReadU32(mrgFileBytes, (int)(blockOffset + 0x14)));
        block.Hash = Convert.ToHexString(SHA256.HashData(mrgFileBytes.AsSpan((int)blockOffset, (int)block.Size)));
        return block;
    }

    // The RH0 node tree's child (+0x1C) / sibling (+0x2C) walk.
    static int CountNodes(byte[] mrgBytes, long treeAbsoluteOffset, int treeSize)
    {
        int nodeCount = 0;
        var pendingOffsets = new Stack<long>();
        var visitedOffsets = new HashSet<long>();
        pendingOffsets.Push(0);
        while (pendingOffsets.Count > 0)
        {
            long nodeOffset = pendingOffsets.Pop();
            if (nodeOffset < 0 || nodeOffset + 0x30 > treeSize || treeAbsoluteOffset + nodeOffset + 0x30 > mrgBytes.LongLength || !visitedOffsets.Add(nodeOffset))
            {
                continue;
            }
            nodeCount++;
            long childOffset = ReadU32(mrgBytes, (int)(treeAbsoluteOffset + nodeOffset + 0x1C)) & 0x0FFFFFFF;
            long siblingOffset = ReadU32(mrgBytes, (int)(treeAbsoluteOffset + nodeOffset + 0x2C)) & 0x0FFFFFFF;
            if (siblingOffset != 0)
            {
                pendingOffsets.Push(siblingOffset);
            }
            if (childOffset != 0)
            {
                pendingOffsets.Push(childOffset);
            }
        }
        return nodeCount;
    }

    static string FriendlyNameOf(Block block)
    {
        if (block.IsWindow)
        {
            int side = block.WindowIndex / KindNames.Length;
            int kind = block.WindowIndex % KindNames.Length;
            string frame = "white";
            if (side == 0)
            {
                frame = "red";
            }
            return $"Window, side {side} ({frame} frame): {KindNames[kind]} leader";
        }
        string pack = PackName(block.Pack);
        if (pack.StartsWith("DUEL", StringComparison.Ordinal) && block.SubModelIndex < DuelNames.Length)
        {
            return DuelNames[block.SubModelIndex];
        }
        if (pack == "RING")
        {
            return "Gold ring";
        }
        if (pack == "FIELD" && block.SubModelIndex < FieldNames.Length)
        {
            return $"Terrain tile: {FieldNames[block.SubModelIndex]}";
        }
        return string.Empty;
    }

    static string GroupOf(Block block)
    {
        if (block.IsWindow)
        {
            return "windows";
        }
        string name = PackName(block.Pack);
        foreach (string groupName in Groups)
        {
            if (groupName != "windows" && name.StartsWith(groupName, StringComparison.Ordinal))
            {
                return groupName;
            }
        }
        return name;
    }

    public Block SubModel(int pack, int subModelIndex)
    {
        return Packs[pack].Blocks[subModelIndex];
    }

    public Block? Find(int index)
    {
        if (index < 0 || index >= Blocks.Count)
        {
            return null;
        }
        return Blocks[index];
    }

    public Block? FindByName(string name)
    {
        foreach (Block block in Blocks)
        {
            if (string.Equals(block.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return block;
            }
        }
        return null;
    }

    // The block's current bytes: what the extractor exported, and the donor a rebuild uses.
    public byte[] BlockBytes(int index)
    {
        Block? block = Find(index);
        if (block == null)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"no MODEL.MRG block {index}");
        }
        return mrgFileBytes.AsSpan((int)block.Offset, (int)block.Size).ToArray();
    }

    public byte[]? StagedBytes(int index)
    {
        if (stagedBlocksByIndex.TryGetValue(index, out StagedBlock? stagedBlock))
        {
            return stagedBlock.Bytes;
        }
        return null;
    }

    /* Rebuilds `glbPath` over the block's own retail bytes (the donor), with the block's slot as
       the hard output budget, and stages the result if it fits. A block that does not fit is
       never staged: the result says by how much it is over. allowReduction lets the assembler
       shrink textures / animation precision to fit; off by default, so an over-slot model is
       reported, not degraded. */
    public StageResult Stage(int entryIndex, string glbPath, bool allowReduction = false)
    {
        return StageModel(entryIndex, null, glbPath, allowReduction);
    }

    // The same, from glb bytes already in memory (the model converter's output). glbLabel is only
    // the name the staging list shows for it, usually the source file's path.
    public StageResult Stage(int entryIndex, byte[] glb, string glbLabel, bool allowReduction = false)
    {
        return StageModel(entryIndex, glb, glbLabel, allowReduction);
    }

    // glb null: read it from glbPath instead, which is then also the staged block's source name.
    StageResult StageModel(int entryIndex, byte[]? glb, string glbPath, bool allowReduction)
    {
        Block? block = Find(entryIndex);
        if (block == null)
        {
            throw new ArgumentOutOfRangeException(nameof(entryIndex));
        }
        var stageResult = new StageResult();
        stageResult.Index = block.Index;
        stageResult.Name = block.Name;
        stageResult.Slot = block.Slot;
        stageResult.Mirrors = block.Copies - 1;

        byte[] built;
        try
        {
            ImportedModel imported;
            if (glb == null)
            {
                imported = GlbImporter.Load(glbPath);
            }
            else
            {
                imported = GlbImporter.LoadFromBytes(glb);
            }
            Rebuilder.ImportResult importResult = Rebuilder.Import(imported, BlockBytes(entryIndex), (int)block.Slot, true,
                (category, categoryBytes, totalBytes, budget) => allowReduction);
            built = importResult.Block;
            stageResult.Warnings.AddRange(importResult.Warnings);
        }
        catch (Exception exception)
        {
            stageResult.Error = exception.Message;
            return stageResult;
        }

        stageResult.Size = built.Length;
        stageResult.Fits = built.Length <= block.Slot;
        if (stageResult.Fits)
        {
            Add(block, built, glbPath);
            stageResult.Staged = true;
        }
        return stageResult;
    }

    // Stages an already-built RH0 block (the harness' board-band path, or a .rh0 on disk). The
    // same slot rule applies.
    public StageResult StageBytes(int entryIndex, byte[] rh0, string sourceLabel = "")
    {
        Block? block = Find(entryIndex);
        if (block == null)
        {
            throw new ArgumentOutOfRangeException(nameof(entryIndex));
        }
        var stageResult = new StageResult();
        stageResult.Index = block.Index;
        stageResult.Name = block.Name;
        stageResult.Slot = block.Slot;
        stageResult.Mirrors = block.Copies - 1;
        stageResult.Size = rh0.Length;
        stageResult.Fits = rh0.Length <= block.Slot;
        if (stageResult.Fits)
        {
            Add(block, rh0, sourceLabel);
            stageResult.Staged = true;
        }
        return stageResult;
    }

    void Add(Block block, byte[] bytes, string source)
    {
        var stagedBlock = new StagedBlock();
        stagedBlock.Block = block;
        stagedBlock.Bytes = bytes;
        stagedBlock.SourcePath = source;
        stagedBlocksByIndex[block.Index] = stagedBlock;
    }

    public bool Unstage(int entryIndex)
    {
        return stagedBlocksByIndex.Remove(entryIndex);
    }

    public void ClearStaged()
    {
        stagedBlocksByIndex.Clear();
    }

    public bool IsStaged(int entryIndex)
    {
        return stagedBlocksByIndex.ContainsKey(entryIndex);
    }

    /* Lays replacements back into MODEL.MRG. Every block keeps its offset, so the ELF tables are
       untouched; a block that does not fit its slot is an error and nothing is written at all. A
       replacement equal to the retail block produces no change. Each replacement is written to
       every block that shared the edited block's retail content (the 22 DUEL packs), so the
       edit shows everywhere in the game. */
    public BuildResult Build(List<StagedBlock> replacements)
    {
        var result = new BuildResult();
        var wantedBytes = new Dictionary<int, byte[]>();
        var wantedSource = new Dictionary<int, Block>();

        foreach (StagedBlock stagedReplacement in replacements)
        {
            foreach (Block block in IdentityGroups[stagedReplacement.Block.IdentityGroup])
            {
                if (wantedBytes.TryGetValue(block.Index, out byte[]? previousBytes) && !previousBytes.AsSpan().SequenceEqual(stagedReplacement.Bytes))
                {
                    throw new ArgumentException($"two different replacements both apply to {block.Name} " +
                        $"(model id {block.ModelId}); {wantedSource[block.Index].Name} and {stagedReplacement.Block.Name} hold " +
                        "identical retail bytes, so each edit mirrors onto the other");
                }
                wantedBytes[block.Index] = stagedReplacement.Bytes;
                wantedSource[block.Index] = stagedReplacement.Block;
            }
        }

        foreach (int blockIndex in wantedBytes.Keys.OrderBy(indexValue => indexValue))
        {
            Block block = Blocks[blockIndex];
            byte[] bytes = wantedBytes[blockIndex];

            if (bytes.LongLength > block.Slot)
            {
                string window = "";
                if (block.IsWindow)
                {
                    window = ", window";
                }
                result.Errors.Add(
                    $"{block.Name} (model id {block.ModelId}{window}): the new block is " +
                    $"{bytes.LongLength:N0} B but its slot is only {block.Slot:N0} B " +
                    $"({bytes.LongLength - block.Slot:N0} B over; retail block {block.Size:N0} B). " +
                    "Shrink it (fewer/smaller textures, less geometry) - growing a slot needs the " +
                    "pack repacked.");
                continue;
            }

            /* Unchanged: same content and length as retail. Compared over the block's own bytes,
               not the whole slot, so retail's inter-block gap bytes are left as they were. */
            if (bytes.LongLength == block.Size &&
                bytes.AsSpan().SequenceEqual(mrgFileBytes.AsSpan((int)block.Offset, (int)block.Size)))
            {
                result.Unchanged.Add(block);
                continue;
            }

            // Edited: write the full slot, so no stale bytes of the old block survive in the gap.
            byte[] slotBytes = new byte[block.Slot];
            bytes.CopyTo(slotBytes, 0);
            if (slotBytes.AsSpan().SequenceEqual(mrgFileBytes.AsSpan((int)block.Offset, (int)block.Slot)))
            {
                result.Unchanged.Add(block);
                continue;
            }

            result.Changes.Add(MrgPath, block.Offset, slotBytes,
                $"{block.Name} model id {block.ModelId} ({bytes.Length:N0} B in a {block.Slot:N0} B slot)");
            if (block.Index == wantedSource[blockIndex].Index)
            {
                result.Written.Add(block);
            }
            else
            {
                result.Mirrored.Add(block);
            }
        }

        if (!result.Ok)
        {
            result.Changes.Clear();   // validation before any write
        }
        return result;
    }

    // Turns everything staged into a ChangeSet (mirroring the identical DUEL copies).
    public BuildResult BuildChangeSet()
    {
        return Build(stagedBlocksByIndex.Values.OrderBy(stagedBlock => stagedBlock.Block.Index).ToList());
    }

    // The plan the Save button shows: writes, no-ops and errors. Reads the disc, writes nothing.
    public ChangeSet.Plan Validate(out BuildResult report)
    {
        report = BuildChangeSet();
        ChangeSet.Plan plan = report.Changes.Validate(Disc);
        plan.Errors.AddRange(report.Errors);
        return plan;
    }

    /* Validates, then writes only the differing bytes. Writes nothing if the build or the writer
       reported any error. After a write that changed a byte, MODEL.MRG is re-read so the list
       and the hashes match the disc. */
    public ChangeSet.Plan Save(out BuildResult report)
    {
        report = BuildChangeSet();
        if (!report.Ok)
        {
            ChangeSet.Plan refused = report.Changes.Validate(Disc);
            refused.Errors.AddRange(report.Errors);
            return refused;
        }
        ChangeSet.Plan plan = report.Changes.Write(Disc);
        if (plan.Ok && plan.Writes.Count > 0)
        {
            Reload();
        }
        return plan;
    }

    public void Dispose()
    {
        if (ownsDisc && discLoader != null)
        {
            discLoader.Dispose();
        }
    }
}
