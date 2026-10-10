using System.Buffers.Binary;
using System.Numerics;

namespace DotrAssetModder;
// Mostly AI

// === Step 6.5 — MOT (skeletal motion) writer =========================================
// Serialises one MOT block per animation slot. Layout (block-relative pointers; the game
// relocates them via NjaReloc_Adjust_M3DMOT at load) verified by a decode->encode->decode
// EXACT round-trip on all 5 slots of blue-eyes_white_dragon.mrg:
//
//   +0x00 u32 magic 'MOT\x1a' (0x1A544F4D)
//   +0x04 u32 size              (total block bytes; advances to next slot)
//   +0x08 u32 0
//   +0x0C u32 0
//   +0x10 u16 nodeCount         (must equal the model's node count)
//   +0x12 u16 0                 (byte 0x13 bit 7, set on every retail motion, makes
//                                 lNjaReloc_AdjustMotion @0x214100 also rebase the word just
//                                 before the descriptor; NjaReloc_Adjust_M3DMOT @0x214090
//                                 rebases +0x14/+0x18 either way, so leaving it clear is harmless)
//   +0x14 u32 motionOff  -> MotionDesc   (relocated)
//   +0x18 u32 = motionOff       (secondary ptr; relocated but unused in playback)
//   +0x1C u32 0
//   [MotionDesc @ motionOff]
//     +0x00 u32 trackTableOff   (relocated)   +0x04 u32 frameCount   +0x08 u16 type
//        type 3: 16-byte entries (position, angle); type 7: 24-byte entries (+ scale)
//   [track table: nodeCount x 16]  transPtr, rotPtr, transCount, rotCount  (ptrs relocated)
//   [or nodeCount x 24, type 7]    transPtr, rotPtr, scalePtr, transCount, rotCount, scaleCount
//   [keyframes]  TransKey{ s32 frame; f32 x,y,z }   RotKey{ s32 frame; s32 rx,ry,rz (BAMS) }
//                ScaleKey{ s32 frame; f32 x,y,z }
//
// Translation keys hold RAW values; the game multiplies by animTransScale (node 0 uses 1.0)
// then by the parent's accumulated scale. Scale keys are the node's own scale; a type-7
// motion leaves the root's scale alone. A list with fewer than 3 keys plays key 0 only.
// The glb->raw conversion and key fitting live in RebuildPipeline.
//
// === BATTLE-SLOT GUARANTEE ===========================================================
// The 3D battle drives animation through SzModel_SetAnimIntr@0x1f1550, which early-outs
// with return 0 when M3DMOT_AnimationDataPointers[slot] == NULL (the slot bit is clear in
// RH0 sectionFlags bits 0-7, so lSzModel_BindModelByAddr @0x1f0500 never set a pointer):
//   * 0 idle
//   * 2 attack           SzModel_BattleSeq@0x1f0cd0
//   * 5 the WINNER's pose, with the bit-20 camera    SzModel_BattleSeq2@0x1f0e50
//   * 4 death / 6 damage  the target's reaction: SzEffect_Draw@0x1f40d0 starts whatever
//     SzEffect_GetPlayMotion picks (3 is promoted to 4). During the attack the game holds
//     the reaction within motion frames ~10-16 while the effect module returns 6
//     (0x1F45EC: at frame >= 16 it steps back 2; between 11 and 16 it steps back with
//     chance 2/3), then lets the motion play out.
// A missing slot means the monster never plays it: an imported model without 4 or 6 never
// reacts to being hit. So {0, 2, 4, 5, 6} - the set 650 of 683 retail models have exactly,
// and 682 of 683 have at least - is guaranteed: a missing one is cloned from a present slot
// (see CloneSource). A clone is a valid, terminating motion, but a cloned idle in slot 4
// never shows a fall; Write reports every clone so the caller can tell the user.
//
// The guarantee follows the TEMPLATE slot being overwritten (templateSlotsByte, the retail
// block's own sectionFlags low byte). Where retail itself omits a battle slot the game never
// plays it for that card, so a clone there is dead weight in a 1 MiB block: the one retail
// monster like that is card 671 (Exodia, who never dies), carrying {0, 2} and no 4/5/6. A
// battle slot the template lacks is therefore not synthesised; the ones it has still are. A
// template with no MOT slots at all is not retail-like (custom or empty), and keeps the full
// {0, 2, 4, 5, 6} guarantee.
//
// Clones are built at full fidelity by default (cloneKeyStride = 1). Rh0Assembler only
// passes a stride > 1 as a last-resort size fallback, when the assembled block would
// otherwise exceed its budget - a full-density clone of a densely-baked clip is disposable
// cosmetic filler by design (see above), so it's the first thing traded for headroom, but
// only when the budget actually demands it.
// =====================================================================================

public sealed class EncMotTrack
{
    public List<(int Frame, Vector3 Raw)>? Trans;          // raw translation (pre animTransScale/parentAccum)
    public List<(int Frame, int Rx, int Ry, int Rz)>? Rot; // BAMS Euler
    public List<(int Frame, Vector3 Scale)>? Scale;        // node's own scale (type-7 motion)
}

public sealed class EncMotion
{
    public int SlotId;                                     // 0..7 (animation slot bit)
    public int FrameCount;
    public Dictionary<int, EncMotTrack> Tracks = new();    // key = node index (matches NodeTreeWriter order)
}

public static class MotWriter
{
    public const uint Magic = 0x1A544F4D;
    private const int MotionOff = 0x20;
    private const int TrackTableOff = 0x2C;   // relative to block; track table follows the desc
    /// Slots the 3D battle plays (idle, attack, death, win, damage; see the header). Missing
    /// ones are synthesised by cloning an existing slot so SzModel_SetAnimIntr never no-ops -
    /// except the ones the template itself lacks (templateSlotsByte).
    private static readonly int[] BattleRequiredSlots = { 0, 2, 4, 5, 6 };

    /// Slots synthesised by the last Write, as (missing slot, cloned from).
    [ThreadStatic] public static List<(int Slot, int From)>? LastClones;

    /// Serialise the motions (any order; emitted in ascending SlotId) into back-to-back MOT
    /// blocks, and report the RH0 sectionFlags low byte (set bit per slot present).
    /// cloneKeyStride: keyframe decimation stride applied ONLY to synthesised battle-slot
    /// clones (see EnsureBattleSlots) - 1 (default) keeps them full-fidelity; a caller under
    /// size pressure (Rh0Assembler) passes progressively higher strides to shrink them.
    /// rotTolBams > 0: also drop rotation keys that the game's per-axis lerp between the
    /// kept neighbours reproduces within that many BAMS (a size lever; RebuildPipeline fits
    /// at 0.75). Rotation keys are ~95% of retail key data.
    /// templateSlotsByte: the template block's sectionFlags low byte (bit per MOT slot it
    /// carries). A battle slot the template lacks is not cloned (see EnsureBattleSlots); 0
    /// (the default) means no template is known and every battle slot is guaranteed.
    public static (byte[] Blocks, int SectionFlagsByte) Write(int nodeCount, IReadOnlyList<EncMotion> motions,
        int cloneKeyStride = 1, float rotTolBams = 0f, int templateSlotsByte = 0)
    {
        var clones = new List<(int Slot, int From)>();
        if (rotTolBams > 0f) motions = motions.Select(m => SimplifyRotations(m, rotTolBams)).ToList();
        var ordered = EnsureBattleSlots(motions, cloneKeyStride, clones, templateSlotsByte);
        LastClones = clones;
        var outAll = new List<byte>();
        int flags = 0;
        foreach (var m in ordered)
        {
            flags |= 1 << (m.SlotId & 7);
            outAll.AddRange(WriteBlock(nodeCount, m));
        }
        return (outAll.ToArray(), flags & 0xFF);
    }

    /// Returns the motions ordered by SlotId, with the battle slots {0, 2, 4, 5, 6} added by
    /// cloning an existing slot if absent. Never mutates the caller's objects. If there are
    /// no motions at all, nothing is synthesised (a model with zero animation can't be a
    /// valid battle monster and there is nothing to clone from). templateSlotsByte != 0
    /// limits the slots guaranteed to the ones the template itself carries: retail omits a
    /// battle slot only for a card the game never plays it for (671), so cloning it only
    /// spends the block's budget (see the header).
    private static List<EncMotion> EnsureBattleSlots(IReadOnlyList<EncMotion> motions, int cloneKeyStride,
        List<(int Slot, int From)> clones, int templateSlotsByte = 0)
    {
        var ordered = motions.OrderBy(m => m.SlotId).ToList();
        if (ordered.Count == 0) return ordered;

        var source = ordered.ToDictionary(m => m.SlotId);
        foreach (int slot in BattleRequiredSlots)
        {
            if (source.ContainsKey(slot)) continue;
            if (templateSlotsByte != 0 && (templateSlotsByte & (1 << slot)) == 0) continue;
            var donor = CloneSource(source) ?? ordered[0];
            ordered.Add(CloneMotion(donor, slot, cloneKeyStride));
            clones.Add((slot, donor.SlotId));
        }

        ordered.Sort((a, b) => a.SlotId.CompareTo(b.SlotId));
        return ordered;
    }

    /// Which supplied motion stands in for a missing one: idle, for every slot.
    private static EncMotion? CloneSource(Dictionary<int, EncMotion> present)
    {
        if (present.TryGetValue(0, out var m)) return m;
        return null;
    }

    /// Deep-copies a motion's track data under a new slot id. cloneKeyStride == 1 copies
    /// every key (full fidelity, the default); a higher stride keeps every Nth key (plus
    /// always the first and last key of each track, so the clone still starts/ends at the
    /// source's actual pose) - used only as a size fallback, see Write's doc comment.
    /// Track lists are copied so the clone and its source never share mutable state.
    private static EncMotion CloneMotion(EncMotion src, int newSlotId, int cloneKeyStride = 1)
    {
        var dst = new EncMotion { SlotId = newSlotId, FrameCount = src.FrameCount };
        foreach (var (node, tr) in src.Tracks)
        {
            var copy = new EncMotTrack();
            if (tr.Trans is { Count: > 0 } trans)
                copy.Trans = cloneKeyStride <= 1 ? new List<(int, Vector3)>(trans) : DecimateKeys(trans, cloneKeyStride);
            if (tr.Rot is { Count: > 0 } rot)
                copy.Rot = cloneKeyStride <= 1 ? new List<(int, int, int, int)>(rot) : DecimateKeys(rot, cloneKeyStride);
            if (tr.Scale is { Count: > 0 } scale)
                copy.Scale = cloneKeyStride <= 1 ? new List<(int, Vector3)>(scale) : DecimateKeys(scale, cloneKeyStride);
            dst.Tracks[node] = copy;
        }
        return dst;
    }

    /// Clone stride that keeps only each track's first key: the clone holds the source's
    /// first pose (a list under 3 keys plays key 0 only). The smallest clone there is.
    public const int StaticPose = int.MaxValue;

    private static List<T> DecimateKeys<T>(List<T> src, int stride) where T : struct
    {
        if (stride == StaticPose) return new List<T> { src[0] };
        if (src.Count <= 3) return new List<T>(src);
        var outp = new List<T>();
        for (int i = 0; i < src.Count; i += stride)
            outp.Add(src[i]);
        if (!outp[^1].Equals(src[^1]))
            outp.Add(src[^1]);
        // a list under 3 keys plays key 0 only
        if (outp.Count < 3) return new List<T>(src);
        return outp;
    }

    private static EncMotion SimplifyRotations(EncMotion src, float tol)
    {
        var dst = new EncMotion { SlotId = src.SlotId, FrameCount = src.FrameCount };
        foreach (var (node, tr) in src.Tracks)
            dst.Tracks[node] = new EncMotTrack {
                Trans = tr.Trans, Scale = tr.Scale,
                Rot = tr.Rot is { Count: > 3 } rot ? SimplifyKeys(rot, tol) : tr.Rot,
            };
        return dst;
    }

    /// Greedy refit of a BAMS key list: extend each segment while every dropped key lies
    /// within tol of the line between the kept ends. The old curve is piecewise linear
    /// between its keys, so checking the keys bounds the error on every frame. A step stays
    /// under 0x7F00 (the game lerps the short way round 0x10000), and a result under 3 keys
    /// (which would play key 0 only) keeps the original list.
    private static List<(int Frame, int Rx, int Ry, int Rz)> SimplifyKeys(
        List<(int Frame, int Rx, int Ry, int Rz)> k, float tol)
    {
        bool Fits(int a, int b)
        {
            var (fa, xa, ya, za) = k[a];
            var (fb, xb, yb, zb) = k[b];
            if (Math.Abs(xb - xa) > 0x7F00 || Math.Abs(yb - ya) > 0x7F00 || Math.Abs(zb - za) > 0x7F00) return false;
            for (int j = a + 1; j < b; j++)
            {
                var (f, x, y, z) = k[j];
                float u = (f - fa) / (float)(fb - fa);
                if (Math.Abs(xa + (xb - xa) * u - x) > tol || Math.Abs(ya + (yb - ya) * u - y) > tol ||
                    Math.Abs(za + (zb - za) * u - z) > tol) return false;
            }
            return true;
        }
        var outp = new List<(int, int, int, int)> { k[0] };
        int a = 0;
        while (a < k.Count - 1)
        {
            int b = a + 1;
            while (b + 1 < k.Count && Fits(a, b + 1)) b++;
            outp.Add(k[b]);
            a = b;
        }
        return outp.Count < 3 ? k : outp;
    }

    private static byte[] WriteBlock(int nodeCount, EncMotion m)
    {
        bool scaled = m.Tracks.Values.Any(t => t.Scale is { Count: > 0 });
        int lists = scaled ? 3 : 2;
        int entSize = lists * 8;
        int ttBase = TrackTableOff;
        int kfBase = ttBase + nodeCount * entSize;

        // build keyframe payload + per-node entries (block-relative pointers)
        var kf = new List<byte>();
        var ptr = new int[nodeCount, 3];
        var cnt = new int[nodeCount, 3];
        void Keys<T>(int node, int list, List<T>? keys, Action<T> write)
        {
            if (keys is not { Count: > 0 }) return;
            ptr[node, list] = kfBase + kf.Count;
            cnt[node, list] = keys.Count;
            foreach (var k in keys) write(k);
        }
        for (int i = 0; i < nodeCount; i++)
        {
            if (!m.Tracks.TryGetValue(i, out var tr)) continue;
            Keys(i, 0, tr.Trans, k => { AddI32(kf, k.Frame); AddF32(kf, k.Raw.X); AddF32(kf, k.Raw.Y); AddF32(kf, k.Raw.Z); });
            Keys(i, 1, tr.Rot, k => { AddI32(kf, k.Frame); AddI32(kf, k.Rx); AddI32(kf, k.Ry); AddI32(kf, k.Rz); });
            if (scaled)
                Keys(i, 2, tr.Scale, k => { AddI32(kf, k.Frame); AddF32(kf, k.Scale.X); AddF32(kf, k.Scale.Y); AddF32(kf, k.Scale.Z); });
        }

        int total = kfBase + kf.Count;
        total = (total + 15) & ~15; // 16-byte align
        var b = new byte[total];

        // header
        W32(b, 0x00, Magic);
        W32(b, 0x04, (uint)total);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x10), (ushort)nodeCount);
        W32(b, 0x14, MotionOff);
        W32(b, 0x18, MotionOff);              // secondary ptr -> MotionDesc (relocated, unused in playback)
        // MotionDesc
        W32(b, MotionOff + 0x00, (uint)TrackTableOff);
        W32(b, MotionOff + 0x04, (uint)m.FrameCount);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(MotionOff + 0x08), (ushort)(scaled ? 7 : 3));
        // track table: the list pointers, then their counts
        for (int i = 0; i < nodeCount; i++)
        {
            int e = ttBase + i * entSize;
            for (int l = 0; l < lists; l++)
            {
                W32(b, e + l * 4, (uint)ptr[i, l]);
                W32(b, e + (lists + l) * 4, (uint)cnt[i, l]);
            }
        }
        // keyframes
        kf.CopyTo(b, kfBase);
        return b;
    }

    private static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void AddI32(List<byte> l, int v) { Span<byte> t = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(t, v); l.AddRange(t.ToArray()); }
    private static void AddF32(List<byte> l, float v) { Span<byte> t = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(t, v); l.AddRange(t.ToArray()); }
}