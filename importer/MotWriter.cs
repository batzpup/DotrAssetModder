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
//   +0x12 u16 0                 (byte 0x13 bit7 = 0 -> no extra relocation)
//   +0x14 u32 motionOff  -> MotionDesc   (relocated)
//   +0x18 u32 = motionOff       (secondary ptr; relocated but unused in playback)
//   +0x1C u32 0
//   [MotionDesc @ motionOff]
//     +0x00 u32 trackTableOff   (relocated)   +0x04 u32 frameCount   +0x08 u16 type
//        type & 0x3f == 3 -> 16-byte (no-scale) track entries (== decoder's !hasScale)
//   [track table: nodeCount x 16] transPtr, rotPtr, transCount, rotCount  (ptrs relocated)
//   [keyframes]  TransKey{ s32 frame; f32 x,y,z }   RotKey{ s32 frame; s32 rx,ry,rz (BAMS) }
//
// Translation keys hold RAW values; the game multiplies by animTransScale (node 0 uses 1.0)
// then by the parent's accumulated scale. The glb->raw conversion lives in RebuildPipeline.
//
// === BATTLE-SLOT GUARANTEE (Fix 1, verified in Ghidra) ===============================
// The 3D battle state machine drives animation transitions via SzModel_SetAnimIntr@0x1f1550:
//   * SzModel_BattleSeq@0x1f0cd0  -> SetAnimIntr(modelId, 2)  (attacker: ATTACK anim)
//   * SzModel_BattleSeq2@0x1f0e50 -> SetAnimIntr(modelId, 5)  (defender: TAKE-HIT/DEATH)
// SetAnimIntr early-outs with return 0 when M3DMOT_AnimationDataPointers[slot] == NULL
// (i.e. the slot bit is clear in RH0 sectionFlags bits 0-7, so lSzModel_BindModelByAddr
// @0x1f0500 never set a pointer for it). When that happens the battle never transitions
// out of idle; SzDuel_Act then waits forever for SzModel_GetAnim@0x1f13f0 == 0 and for
// SzEffect_IsUse2 == 0 -> black screen / infinite wait, and the eventual skip/timeout
// resolves combat from a stale SzDuel_GetPlayMotion result, so the imported monster loses
// with no damage. A GLB that only ships slot 0 (idle) hits exactly this.
//
// To keep the state machine alive we GUARANTEE slots 2 and 5 are emitted: if the imported
// motions don't include them, we clone a present slot (idle/slot 0 preferred, else the
// lowest-id slot) into the missing slot. A cloned idle looks wrong during an attack but is
// a fully valid, terminating animation, so the battle plays out and resolves correctly.
// =====================================================================================

public sealed class EncMotTrack
{
    public List<(int Frame, Vector3 Raw)>? Trans;          // raw translation (pre animTransScale/parentAccum)
    public List<(int Frame, int Rx, int Ry, int Rz)>? Rot; // BAMS Euler
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
    private const int EntSize = 16;           // no-scale entries (type & 0x3f == 3)

    /// Slots the 3D battle sequence hard-requires (attack, take-hit). Missing ones are
    /// synthesised by cloning an existing slot so SzModel_SetAnimIntr never null-faults.
    private static readonly int[] BattleRequiredSlots = { 2, 5 };

    /// Serialise the motions (any order; emitted in ascending SlotId) into back-to-back MOT
    /// blocks, and report the RH0 sectionFlags low byte (set bit per slot present).
    public static (byte[] Blocks, int SectionFlagsByte) Write(int nodeCount, IReadOnlyList<EncMotion> motions)
    {
        var ordered = EnsureBattleSlots(motions);
        var outAll = new List<byte>();
        int flags = 0;
        foreach (var m in ordered)
        {
            flags |= 1 << (m.SlotId & 7);
            outAll.AddRange(WriteBlock(nodeCount, m));
        }
        return (outAll.ToArray(), flags & 0xFF);
    }

    /// Returns the motions ordered by SlotId, with battle-required slots (2 and 5) added by
    /// cloning an existing slot if absent. Never mutates the caller's objects. If there are
    /// no motions at all, nothing is synthesised (a model with zero animation can't be a
    /// valid battle monster and there is nothing to clone from).
    private static List<EncMotion> EnsureBattleSlots(IReadOnlyList<EncMotion> motions)
    {
        var ordered = motions.OrderBy(m => m.SlotId).ToList();
        if (ordered.Count == 0) return ordered;

        var present = new HashSet<int>(ordered.Select(m => m.SlotId));

        // Source preference: slot 0 (idle) if present, else the lowest-id slot we have.
        EncMotion donor = ordered.FirstOrDefault(m => m.SlotId == 0) ?? ordered[0];

        foreach (int slot in BattleRequiredSlots)
        {
            if (present.Contains(slot)) continue;
            ordered.Add(CloneMotion(donor, slot));
            present.Add(slot);
        }

        ordered.Sort((a, b) => a.SlotId.CompareTo(b.SlotId));
        return ordered;
    }

    /// Deep-copies a motion's track data under a new slot id. Track lists are copied so the
    /// clone and its source never share mutable state.
    private static EncMotion CloneMotion(EncMotion src, int newSlotId)
    {
        var dst = new EncMotion { SlotId = newSlotId, FrameCount = src.FrameCount };
        foreach (var (node, tr) in src.Tracks)
        {
            var copy = new EncMotTrack();
            if (tr.Trans is { Count: > 0 })
                copy.Trans = new List<(int, Vector3)>(tr.Trans);
            if (tr.Rot is { Count: > 0 })
                copy.Rot = new List<(int, int, int, int)>(tr.Rot);
            dst.Tracks[node] = copy;
        }
        return dst;
    }

    private static byte[] WriteBlock(int nodeCount, EncMotion m)
    {
        int ttBase = TrackTableOff;
        int kfBase = ttBase + nodeCount * EntSize;

        // build keyframe payload + per-node entries (block-relative pointers)
        var kf = new List<byte>();
        var entries = new (int pT, int pR, int cT, int cR)[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            if (!m.Tracks.TryGetValue(i, out var tr)) continue;
            int pT = 0, pR = 0, cT = 0, cR = 0;
            if (tr.Trans is { Count: > 0 } T)
            {
                pT = kfBase + kf.Count; cT = T.Count;
                foreach (var (frame, v) in T)
                {
                    AddI32(kf, frame); AddF32(kf, v.X); AddF32(kf, v.Y); AddF32(kf, v.Z);
                }
            }
            if (tr.Rot is { Count: > 0 } R)
            {
                pR = kfBase + kf.Count; cR = R.Count;
                foreach (var (frame, rx, ry, rz) in R)
                {
                    AddI32(kf, frame); AddI32(kf, rx); AddI32(kf, ry); AddI32(kf, rz);
                }
            }
            entries[i] = (pT, pR, cT, cR);
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
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(MotionOff + 0x08), 3); // type & 0x3f == 3
        // track table
        for (int i = 0; i < nodeCount; i++)
        {
            int e = ttBase + i * EntSize;
            W32(b, e + 0x00, (uint)entries[i].pT);
            W32(b, e + 0x04, (uint)entries[i].pR);
            W32(b, e + 0x08, (uint)entries[i].cT);
            W32(b, e + 0x0C, (uint)entries[i].cR);
        }
        // keyframes
        kf.CopyTo(b, kfBase);
        return b;
    }

    private static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void AddI32(List<byte> l, int v) { Span<byte> t = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(t, v); l.AddRange(t.ToArray()); }
    private static void AddF32(List<byte> l, float v) { Span<byte> t = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(t, v); l.AddRange(t.ToArray()); }
}