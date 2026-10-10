using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DotrAssetModder;

// The DATA/MODEL.MRG archive: parse, import staging and save. The harness (`mrgimp`, `mrgrt`,
// `mrgnames`, `boardbands`) and the importer window drive exactly this class.
//
// MODEL.MRG is 81 entries of anOfs_MODEL: 39 share-data packs (297 RH0 sub-models between them)
// then 42 single-model stained-glass windows. A pack's sub-models are located by the ELF's own
// per-pack sub-offset table (OFS[p]), which is authoritative - no layout has to be guessed.
//
// A sub-model's SLOT is the gap to the next OFS entry (the last entry of OFS[p] is the pack size).
// A replacement is written inside that slot and nothing else moves, so no ELF table is ever
// touched; growing past a slot needs the pack repacked.
public class ModelMrg : IDisposable
{
    public const string MrgPath = "DATA/MODEL.MRG";

    // One RH0 block: a pack sub-model, or a window (a whole anOfs_MODEL entry of its own).
    public class Block
    {
        public int Index;             // global index: pack sub-models in pack order, then windows
        public int Pack = -1;         // -1 for a window
        public int K = -1;            // sub-model index inside the pack, -1 for a window
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

    // What Stage() answers with. Fits == false means Staged == false: nothing is ever staged
    // silently when it does not fit its slot. Error != null means the rebuild threw and
    // Size/Fits are meaningless.
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

    // What Build() answers with: the changes, plus which blocks they came from. Unchanged means
    // the replacement was byte-identical to what is on the disc. Errors are over-slot blocks,
    // named with model id / size / slot; any error means no change at all is kept.
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

    // What each model is, where it is known (DotR-PC NOTES/model-mrg.md). The DUEL names come
    // from renders, the FIELD order from messages 71-80, the window index from
    // SzDuel_SetupDuel @ 0x1DDE4C: side * 100 + (leader kind & 0x1F), kinds in the game's order
    // (DotR-PC tools/rules.py KINDS). STAGE and EVENT scenes are not mapped yet.
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

    FileLoader? disc;
    bool ownsDisc;
    byte[] mrg = Array.Empty<byte>();
    Dictionary<int, StagedBlock> staged = new Dictionary<int, StagedBlock>();

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
            if (disc == null)
            {
                throw new InvalidOperationException("this MODEL.MRG was parsed from bytes, not a disc");
            }
            return disc;
        }
    }

    public IEnumerable<StagedBlock> Staged
    {
        get { return staged.Values; }
    }

    public int StagedCount
    {
        get { return staged.Count; }
    }

    public long MrgSize
    {
        get { return mrg.LongLength; }
    }

    // One representative per identity group, in first-seen order: the 192 distinct models.
    public IEnumerable<Block> DistinctBlocks
    {
        get { return IdentityGroups.Select(g => g[0]); }
    }

    ModelMrg()
    {
    }

    public ModelMrg(FileLoader disc, bool ownsDisc = false)
    {
        this.disc = disc;
        this.ownsDisc = ownsDisc;
        Reload();
    }

    public static ModelMrg Parse(byte[] mrg, GameTables tables)
    {
        var m = new ModelMrg();
        m.ParseBytes(mrg, tables);
        return m;
    }

    public static ModelMrg Open(string isoPath)
    {
        return new ModelMrg(FileLoader.Open(isoPath), true);
    }

    // The pack names are the JP debug build's own symbols (NOTES/model-mrg.md).
    public static string PackName(int p)
    {
        if (p >= 0 && p <= 21)
        {
            return $"DUEL{p:D2}";
        }
        if (p == 22)
        {
            return "RING";
        }
        if (p >= 23 && p <= 32)
        {
            return $"STAGE{p - 23}";
        }
        if (p >= 33 && p <= 37)
        {
            return $"EVENT{p - 33}";
        }
        if (p == 38)
        {
            return "FIELD";
        }
        return $"pack{p}";
    }

    static uint U32(byte[] d, long o)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)o, 4));
    }

    // Re-reads MODEL.MRG from the disc and re-parses it. Drops anything staged.
    public void Reload()
    {
        staged.Clear();
        ParseBytes(Disc.ReadFile(MrgPath), GameTables.Load(Disc));
    }

    void ParseBytes(byte[] bytes, GameTables tables)
    {
        Tables = tables;
        mrg = bytes;
        Packs.Clear();
        Blocks.Clear();
        Windows.Clear();
        IdentityGroups.Clear();
        Anomalies.Clear();

        if (mrg.LongLength != tables.AnOfsModel[tables.ModelEntryCount])
        {
            Anomalies.Add($"MODEL.MRG is {mrg.LongLength:N0} B but anOfs_MODEL ends at " +
                          $"{tables.AnOfsModel[tables.ModelEntryCount]:N0}");
        }

        int index = 0;
        for (int p = 0; p < tables.PackCount; p++)
        {
            var (packOff, packSize) = tables.ModelEntry(p);
            int type = TypeOfPack(tables, p);
            var pack = new PackInfo();
            pack.Index = p;
            pack.Name = PackName(p);
            pack.ModelType = type;
            if (type >= 0)
            {
                pack.LoadBuffer = tables.ShareData[type].Buffer;
            }
            pack.Offset = packOff;
            pack.Size = packSize;
            pack.FirstModelId = (int)tables.PackFirst[p];

            uint[] sub = tables.PackSubOffsets[p];
            int count = (int)tables.PackSubCount[p];
            if (sub[count] != packSize)
            {
                Anomalies.Add($"{pack.Name}: OFS[{p}][{count}] = {sub[count]:N0} but the pack is {packSize:N0} B");
            }

            for (int k = 0; k < count; k++)
            {
                long off = packOff + sub[k];
                long slot = sub[k + 1] - sub[k];
                Block b = ReadBlock(off, slot);
                b.Index = index++;
                b.Pack = p;
                b.K = k;
                b.Entry = p;
                b.ModelId = pack.FirstModelId + k;
                b.ModelType = type;
                b.Name = $"{pack.Name}/{k:D2}";
                pack.Blocks.Add(b);
                Blocks.Add(b);
            }

            Packs.Add(pack);
        }

        // Windows: anOfs_MODEL entries past the packs, one RH0 block each, loaded by
        // SzModel_Load for model type 1 into a 0xA000 buffer - that cap, not the entry size,
        // is a window's real slot (they are equal on retail).
        long winCap = tables.WindowBufferSize;
        for (int e = tables.PackCount; e < tables.ModelEntryCount; e++)
        {
            var (off, size) = tables.ModelEntry(e);
            long slot = Math.Min(size, winCap);
            if (size > winCap)
            {
                Anomalies.Add($"window entry {e} is {size:N0} B, past the {winCap:N0} B load buffer");
            }
            Block b = ReadBlock(off, slot);
            b.Index = index++;
            b.Entry = e;
            b.WindowIndex = e - tables.PackCount;
            b.ModelType = 1;
            b.Name = $"window_{b.WindowIndex:D2}";
            Windows.Add(b);
            Blocks.Add(b);
        }

        // Identity groups: the 22 DUEL packs hold the same bytes, so one edit covers all of them.
        var byHash = new Dictionary<string, int>();
        foreach (Block b in Blocks)
        {
            if (!byHash.TryGetValue(b.Hash, out int g))
            {
                g = IdentityGroups.Count;
                byHash[b.Hash] = g;
                IdentityGroups.Add(new List<Block>());
            }
            b.IdentityGroup = g;
            IdentityGroups[g].Add(b);
        }
        foreach (List<Block> g in IdentityGroups)
        {
            foreach (Block b in g)
            {
                b.Copies = g.Count;
                b.IsMirror = b != g[0];
            }
        }

        foreach (Block b in Blocks)
        {
            b.FriendlyName = FriendlyNameOf(b);
            b.Group = GroupOf(b);
        }
    }

    // Model type of a pack: the aShareData entry whose Base is the greatest one <= pack index
    // (anOfs_MODEL[aShareData[type].Base + i], NOTES/model-mrg.md). Bases at or past the pack
    // count address the windows, not a pack.
    static int TypeOfPack(GameTables tables, int p)
    {
        int best = -1;
        for (int t = 0; t < tables.ShareData.Length; t++)
        {
            ShareDataEntry e = tables.ShareData[t];
            if (e.Base > p || e.Base >= tables.PackCount)
            {
                continue;
            }
            if (best < 0 || e.Base > tables.ShareData[best].Base)
            {
                best = t;
            }
        }
        return best;
    }

    Block ReadBlock(long off, long slot)
    {
        var b = new Block();
        b.Offset = off;
        b.Slot = slot;
        b.Size = slot;
        if (off + 0x80 > mrg.LongLength)
        {
            Anomalies.Add($"block at 0x{off:X} runs past the file");
            return b;
        }
        if (U32(mrg, off) != Rh0Assembler.Rh0Magic)
        {
            Anomalies.Add($"block at 0x{off:X} has no RH0 magic (0x{U32(mrg, off):X8})");
            return b;
        }

        long end = Rh0Assembler.ComputeBlockEnd(mrg, off, out _);
        if (end < 0 || end == long.MaxValue || end > off + slot)
        {
            Anomalies.Add($"block at 0x{off:X}: ComputeBlockEnd gave 0x{end:X} for a slot of {slot:N0} B");
            end = off + slot;
        }
        b.Size = end - off;
        b.Flags = U32(mrg, off + 0x04);
        b.HasEmbeddedMot = (b.Flags & 0xFFu) != 0;
        long texTable = off + U32(mrg, off + 0x38);
        if (texTable >= off && texTable + 4 <= end)
        {
            b.TexCount = (int)U32(mrg, texTable);
        }
        b.NodeCount = CountNodes(mrg, off + U32(mrg, off + 0x10), (int)U32(mrg, off + 0x14));
        b.Hash = Convert.ToHexString(SHA256.HashData(mrg.AsSpan((int)off, (int)b.Size)));
        return b;
    }

    // The RH0 node tree's child (+0x1C) / sibling (+0x2C) walk.
    static int CountNodes(byte[] mrg, long treeAbs, int treeSize)
    {
        int count = 0;
        var stack = new Stack<long>();
        var seen = new HashSet<long>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            long off = stack.Pop();
            if (off < 0 || off + 0x30 > treeSize || treeAbs + off + 0x30 > mrg.LongLength || !seen.Add(off))
            {
                continue;
            }
            count++;
            long child = U32(mrg, treeAbs + off + 0x1C) & 0x0FFFFFFF;
            long sib = U32(mrg, treeAbs + off + 0x2C) & 0x0FFFFFFF;
            if (sib != 0)
            {
                stack.Push(sib);
            }
            if (child != 0)
            {
                stack.Push(child);
            }
        }
        return count;
    }

    static string FriendlyNameOf(Block b)
    {
        if (b.IsWindow)
        {
            int side = b.WindowIndex / KindNames.Length;
            int kind = b.WindowIndex % KindNames.Length;
            string frame = "white";
            if (side == 0)
            {
                frame = "red";
            }
            return $"Window, side {side} ({frame} frame): {KindNames[kind]} leader";
        }
        string pack = PackName(b.Pack);
        if (pack.StartsWith("DUEL", StringComparison.Ordinal) && b.K < DuelNames.Length)
        {
            return DuelNames[b.K];
        }
        if (pack == "RING")
        {
            return "Gold ring";
        }
        if (pack == "FIELD" && b.K < FieldNames.Length)
        {
            return $"Terrain tile: {FieldNames[b.K]}";
        }
        return string.Empty;
    }

    static string GroupOf(Block b)
    {
        if (b.IsWindow)
        {
            return "windows";
        }
        string name = PackName(b.Pack);
        foreach (string g in Groups)
        {
            if (g != "windows" && name.StartsWith(g, StringComparison.Ordinal))
            {
                return g;
            }
        }
        return name;
    }

    public Block Sub(int pack, int k)
    {
        return Packs[pack].Blocks[k];
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
        foreach (Block b in Blocks)
        {
            if (string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return b;
            }
        }
        return null;
    }

    // The block's current bytes: what the extractor exported, and the donor a rebuild uses.
    public byte[] BlockBytes(int index)
    {
        Block? b = Find(index);
        if (b == null)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"no MODEL.MRG block {index}");
        }
        return mrg.AsSpan((int)b.Offset, (int)b.Size).ToArray();
    }

    public byte[]? StagedBytes(int index)
    {
        if (staged.TryGetValue(index, out StagedBlock? s))
        {
            return s.Bytes;
        }
        return null;
    }

    // Rebuilds `glbPath` over the block's own retail bytes (the donor), with the block's slot as
    // the hard output budget, and stages the result if it fits. A block that does not fit is
    // never staged: the result says by how much it is over and the staging list is unchanged.
    // allowReduction lets the assembler shrink textures / animation precision to fit; off by
    // default, so an over-slot model is reported, not quietly degraded.
    public StageResult Stage(int entryIndex, string glbPath, bool allowReduction = false,
        int maxTextureDim = 512)
    {
        return StageModel(entryIndex, null, glbPath, allowReduction, maxTextureDim);
    }

    // The same, from glb bytes already in memory (the model converter's output). glbLabel is only
    // the name the staging list shows for it, usually the source file's path.
    public StageResult Stage(int entryIndex, byte[] glb, string glbLabel, bool allowReduction = false,
        int maxTextureDim = 512)
    {
        return StageModel(entryIndex, glb, glbLabel, allowReduction, maxTextureDim);
    }

    // glb null: read it from glbPath instead, which is then also the staged block's source name.
    StageResult StageModel(int entryIndex, byte[]? glb, string glbPath, bool allowReduction,
        int maxTextureDim)
    {
        Block? b = Find(entryIndex);
        if (b == null)
        {
            throw new ArgumentOutOfRangeException(nameof(entryIndex));
        }
        var res = new StageResult();
        res.Index = b.Index;
        res.Name = b.Name;
        res.Slot = b.Slot;
        res.Mirrors = b.Copies - 1;

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
            built = Rebuilder.Import(imported, BlockBytes(entryIndex), (int)b.Slot, true,
                (category, categoryBytes, totalBytes, budget) => allowReduction, res.Warnings, maxTextureDim);
        }
        catch (Exception ex)
        {
            res.Error = ex.Message;
            return res;
        }

        res.Size = built.Length;
        res.Fits = built.Length <= b.Slot;
        if (res.Fits)
        {
            Add(b, built, glbPath);
            res.Staged = true;
        }
        return res;
    }

    // Stages an already-built RH0 block (the harness' board-band path, or a .rh0 on disk). The
    // same slot rule applies.
    public StageResult StageBytes(int entryIndex, byte[] rh0, string sourceLabel = "")
    {
        Block? b = Find(entryIndex);
        if (b == null)
        {
            throw new ArgumentOutOfRangeException(nameof(entryIndex));
        }
        var res = new StageResult();
        res.Index = b.Index;
        res.Name = b.Name;
        res.Slot = b.Slot;
        res.Mirrors = b.Copies - 1;
        res.Size = rh0.Length;
        res.Fits = rh0.Length <= b.Slot;
        if (res.Fits)
        {
            Add(b, rh0, sourceLabel);
            res.Staged = true;
        }
        return res;
    }

    void Add(Block b, byte[] bytes, string source)
    {
        var s = new StagedBlock();
        s.Block = b;
        s.Bytes = bytes;
        s.SourcePath = source;
        staged[b.Index] = s;
    }

    public bool Unstage(int entryIndex)
    {
        return staged.Remove(entryIndex);
    }

    public void ClearStaged()
    {
        staged.Clear();
    }

    public bool IsStaged(int entryIndex)
    {
        return staged.ContainsKey(entryIndex);
    }

    // Lays replacements back into MODEL.MRG. Every block keeps its offset, so the ELF tables are
    // untouched; a block that does not fit its slot is an error and nothing is written at all. A
    // replacement equal to the retail block produces no change. Each replacement is written to
    // every block that shared the edited block's retail content (the 22 DUEL packs), which is
    // what the game needs to look edited everywhere.
    public BuildResult Build(List<StagedBlock> replacements)
    {
        var result = new BuildResult();
        var wantedBytes = new Dictionary<int, byte[]>();
        var wantedSource = new Dictionary<int, Block>();

        foreach (StagedBlock r in replacements)
        {
            foreach (Block b in IdentityGroups[r.Block.IdentityGroup])
            {
                if (wantedBytes.TryGetValue(b.Index, out byte[]? prev) && !prev.AsSpan().SequenceEqual(r.Bytes))
                {
                    throw new ArgumentException($"two different replacements both apply to {b.Name} " +
                        $"(model id {b.ModelId}); {wantedSource[b.Index].Name} and {r.Block.Name} hold " +
                        "identical retail bytes, so each edit mirrors onto the other");
                }
                wantedBytes[b.Index] = r.Bytes;
                wantedSource[b.Index] = r.Block;
            }
        }

        foreach (int idx in wantedBytes.Keys.OrderBy(k => k))
        {
            Block b = Blocks[idx];
            byte[] bytes = wantedBytes[idx];

            if (bytes.LongLength > b.Slot)
            {
                string window = "";
                if (b.IsWindow)
                {
                    window = ", window";
                }
                result.Errors.Add(
                    $"{b.Name} (model id {b.ModelId}{window}): the new block is " +
                    $"{bytes.LongLength:N0} B but its slot is only {b.Slot:N0} B " +
                    $"({bytes.LongLength - b.Slot:N0} B over; retail block {b.Size:N0} B). " +
                    "Shrink it (fewer/smaller textures, less geometry) - growing a slot needs the " +
                    "pack repacked.");
                continue;
            }

            // Unchanged: the same content, same length, as retail. Compared over the block's own
            // bytes rather than the whole slot, so retail's inter-block gap bytes (whatever they
            // are) are left exactly as they were.
            if (bytes.LongLength == b.Size &&
                bytes.AsSpan().SequenceEqual(mrg.AsSpan((int)b.Offset, (int)b.Size)))
            {
                result.Unchanged.Add(b);
                continue;
            }

            // Edited: write the full slot, so no stale bytes of the old block survive in the gap.
            byte[] buf = new byte[b.Slot];
            bytes.CopyTo(buf, 0);
            if (buf.AsSpan().SequenceEqual(mrg.AsSpan((int)b.Offset, (int)b.Slot)))
            {
                result.Unchanged.Add(b);
                continue;
            }

            result.Changes.Add(MrgPath, b.Offset, buf,
                $"{b.Name} model id {b.ModelId} ({bytes.Length:N0} B in a {b.Slot:N0} B slot)");
            if (b.Index == wantedSource[idx].Index)
            {
                result.Written.Add(b);
            }
            else
            {
                result.Mirrored.Add(b);
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
        return Build(staged.Values.OrderBy(s => s.Block.Index).ToList());
    }

    // The plan the Save button shows: writes, no-ops and errors. Reads the disc, writes nothing.
    public ChangeSet.Plan Validate(out BuildResult report)
    {
        report = BuildChangeSet();
        ChangeSet.Plan plan = report.Changes.Validate(Disc);
        plan.Errors.AddRange(report.Errors);
        return plan;
    }

    // Validates, then writes only the differing bytes. Writes nothing at all if the build or the
    // writer reported any error. On a successful write with at least one byte changed, MODEL.MRG
    // is re-read so the list and the hashes reflect the disc again.
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
        if (ownsDisc && disc != null)
        {
            disc.Dispose();
        }
    }
}
