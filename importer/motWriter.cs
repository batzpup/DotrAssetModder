using System.Numerics;
using static DotrAssetModder.Bin;

namespace DotrAssetModder;
// Mostly AI

/* MOT (skeletal motion) writer. Serialises one MOT block per animation slot. Layout
   (block-relative pointers; the game relocates them via NjaReloc_Adjust_M3DMOT at load)
   verified by a decode->encode->decode exact round trip on all 5 slots of
   blue-eyes_white_dragon.mrg:

   +0x00 u32 magic 'MOT\x1a' (0x1A544F4D)
   +0x04 u32 size              (total block bytes; advances to next slot)
   +0x08 u32 0
   +0x0C u32 0
   +0x10 u16 nodeCount         (must equal the model's node count)
   +0x12 u16 0                 (byte 0x13 bit 7, set on every retail motion, makes
                                 lNjaReloc_AdjustMotion @0x214100 also rebase the word just
                                 before the descriptor; NjaReloc_Adjust_M3DMOT @0x214090
                                 rebases +0x14/+0x18 either way, so leaving it clear is harmless)
   +0x14 u32 motionOff  -> MotionDesc   (relocated)
   +0x18 u32 = motionOff       (secondary ptr; relocated but unused in playback)
   +0x1C u32 0
   [MotionDesc @ motionOff]
     +0x00 u32 trackTableOff   (relocated)   +0x04 u32 frameCount   +0x08 u16 type
        type 3: 16-byte entries (position, angle); type 7: 24-byte entries (+ scale)
   [track table: nodeCount x 16]  transPtr, rotPtr, transCount, rotCount  (ptrs relocated)
   [or nodeCount x 24, type 7]    transPtr, rotPtr, scalePtr, transCount, rotCount, scaleCount
   [keyframes]  TransKey{ s32 frame; f32 x,y,z }   RotKey{ s32 frame; s32 rx,ry,rz (BAMS) }
                ScaleKey{ s32 frame; f32 x,y,z }

   Translation keys hold RAW values; the game multiplies by animTransScale (node 0 uses 1.0)
   then by the parent's accumulated scale. Scale keys are the node's own scale; a type-7
   motion leaves the root's scale alone. A list with fewer than 3 keys plays key 0 only.
   The glb->raw conversion and key fitting live in RebuildPipeline.

   BATTLE-SLOT GUARANTEE. The 3D battle drives animation through SzModel_SetAnimIntr@0x1f1550,
   which early-outs with return 0 when M3DMOT_AnimationDataPointers[slot] == NULL (the slot
   bit is clear in RH0 sectionFlags bits 0-7, so lSzModel_BindModelByAddr @0x1f0500 never set
   a pointer):
   * 0 idle
   * 2 attack           SzModel_BattleSeq@0x1f0cd0
   * 5 the WINNER's pose, with the bit-20 camera    SzModel_BattleSeq2@0x1f0e50
   * 4 death / 6 damage  the target's reaction: SzEffect_Draw@0x1f40d0 starts whatever
     SzEffect_GetPlayMotion picks (3 is promoted to 4). During the attack the game holds
     the reaction within motion frames ~10-16 while the effect module returns 6
     (0x1F45EC: at frame >= 16 it steps back 2; between 11 and 16 it steps back with
     chance 2/3), then lets the motion play out.
   A missing slot means the monster never plays it: an imported model without 4 or 6 never
   reacts to being hit. So {0, 2, 4, 5, 6} - the set 650 of 683 retail models have exactly,
   and 682 of 683 have at least - is guaranteed: a missing one is cloned from a present slot
   (see CloneSource). A clone is a valid, terminating motion, but a cloned idle in slot 4
   never shows a fall; Write reports every clone so the caller can tell the user.

   The guarantee follows the TEMPLATE slot being overwritten (templateSlotsByte, the retail
   block's own sectionFlags low byte). Where retail itself omits a battle slot the game never
   plays it for that card, so a clone there is dead weight in a 1 MiB block: the one retail
   monster like that is card 671 (Exodia, who never dies), carrying {0, 2} and no 4/5/6. A
   battle slot the template lacks is therefore not synthesised; the ones it has still are. A
   template with no MOT slots at all is not retail-like (custom or empty), and keeps the full
   {0, 2, 4, 5, 6} guarantee.

   Clones are built at full fidelity by default (cloneKeyStride = 1). Rh0Assembler passes a
   stride > 1 only as a last-resort size fallback, when the assembled block would otherwise
   exceed its budget: a clone is disposable filler, so it is the first thing traded for
   headroom. */

public sealed class EncMotTrack
{
    public List<(int Frame, Vector3 Raw)>? Trans;          // raw translation (pre animTransScale/parentAccum)
    public List<(int Frame, int RotationX, int RotationY, int RotationZ)>? Rot; // BAMS Euler
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

    public sealed class Result
    {
        public byte[] Blocks = Array.Empty<byte>();
        public int SectionFlagsByte;                       // RH0 sectionFlags low byte, bit per slot present
        public List<(int Slot, int From)> Clones = new();  // synthesised battle slots: (missing slot, cloned from)
    }

    /// Serialise the motions (any order; emitted in ascending SlotId) into back-to-back MOT
    /// blocks, and report the RH0 sectionFlags low byte (set bit per slot present).
    /// cloneKeyStride: keyframe decimation stride applied only to synthesised battle-slot
    /// clones; 1 keeps them full-fidelity, higher strides shrink them (Rh0Assembler, under
    /// size pressure).
    /// rotTolBams > 0: also drop rotation keys that the game's per-axis lerp between the
    /// kept neighbours reproduces within that many BAMS (RebuildPipeline fits at 0.75).
    /// Rotation keys are ~95% of retail key data.
    /// templateSlotsByte: the template block's sectionFlags low byte (bit per MOT slot it
    /// carries). A battle slot the template lacks is not cloned; 0 means no template is
    /// known and every battle slot is guaranteed.
    public static Result Write(int nodeCount, IReadOnlyList<EncMotion> motions,
        int cloneKeyStride, float rotTolBams, int templateSlotsByte)
    {
        var result = new Result();
        if (rotTolBams > 0f) motions = motions.Select(motion => SimplifyRotations(motion, rotTolBams)).ToList();
        var ordered = EnsureBattleSlots(motions, cloneKeyStride, result.Clones, templateSlotsByte);
        var allBlockBytes = new List<byte>();
        int sectionFlags = 0;
        foreach (var motion in ordered)
        {
            sectionFlags |= 1 << (motion.SlotId & 7);
            allBlockBytes.AddRange(WriteBlock(nodeCount, motion));
        }
        result.Blocks = allBlockBytes.ToArray();
        result.SectionFlagsByte = sectionFlags & 0xFF;
        return result;
    }

    /// Returns the motions ordered by SlotId, with the battle slots {0, 2, 4, 5, 6} added by
    /// cloning an existing slot if absent. Never mutates the caller's objects. With no
    /// motions at all nothing is synthesised (there is nothing to clone from). A nonzero
    /// templateSlotsByte limits the guaranteed slots to the ones the template carries:
    /// retail omits a battle slot only for a card the game never plays it for (671), so
    /// cloning it only spends the block's budget.
    private static List<EncMotion> EnsureBattleSlots(IReadOnlyList<EncMotion> motions, int cloneKeyStride,
        List<(int Slot, int From)> clones, int templateSlotsByte = 0)
    {
        var ordered = motions.OrderBy(motion => motion.SlotId).ToList();
        if (ordered.Count == 0) return ordered;

        var source = ordered.ToDictionary(motion => motion.SlotId);
        foreach (int slot in BattleRequiredSlots)
        {
            if (source.ContainsKey(slot)) continue;
            if (templateSlotsByte != 0 && (templateSlotsByte & (1 << slot)) == 0) continue;
            var donor = CloneSource(source) ?? ordered[0];
            ordered.Add(CloneMotion(donor, slot, cloneKeyStride));
            clones.Add((slot, donor.SlotId));
        }

        ordered.Sort((left, right) => left.SlotId.CompareTo(right.SlotId));
        return ordered;
    }

    /// Which supplied motion stands in for a missing one: idle, for every slot.
    private static EncMotion? CloneSource(Dictionary<int, EncMotion> present)
    {
        if (present.TryGetValue(0, out var idleMotion)) return idleMotion;
        return null;
    }

    /// Deep-copies a motion's track data under a new slot id. cloneKeyStride == 1 copies
    /// every key (full fidelity, the default); a higher stride keeps every Nth key (plus
    /// always the first and last key of each track, so the clone still starts/ends at the
    /// source's actual pose) - used only as a size fallback. Track lists are copied so the
    /// clone and its source never share mutable state.
    private static EncMotion CloneMotion(EncMotion sourceMotion, int newSlotId, int cloneKeyStride = 1)
    {
        var clonedMotion = new EncMotion { SlotId = newSlotId, FrameCount = sourceMotion.FrameCount };
        foreach (var (nodeIndex, sourceTrack) in sourceMotion.Tracks)
        {
            var clonedTrack = new EncMotTrack();
            if (sourceTrack.Trans is { Count: > 0 } translationKeys)
                clonedTrack.Trans = cloneKeyStride <= 1 ? new List<(int, Vector3)>(translationKeys) : DecimateKeys(translationKeys, cloneKeyStride);
            if (sourceTrack.Rot is { Count: > 0 } rotationKeys)
                clonedTrack.Rot = cloneKeyStride <= 1 ? new List<(int, int, int, int)>(rotationKeys) : DecimateKeys(rotationKeys, cloneKeyStride);
            if (sourceTrack.Scale is { Count: > 0 } scaleKeys)
                clonedTrack.Scale = cloneKeyStride <= 1 ? new List<(int, Vector3)>(scaleKeys) : DecimateKeys(scaleKeys, cloneKeyStride);
            clonedMotion.Tracks[nodeIndex] = clonedTrack;
        }
        return clonedMotion;
    }

    /// Clone stride that keeps only each track's first key: the clone holds the source's
    /// first pose (a list under 3 keys plays key 0 only). The smallest clone there is.
    public const int StaticPose = int.MaxValue;

    private static List<T> DecimateKeys<T>(List<T> sourceKeys, int stride) where T : struct
    {
        if (stride == StaticPose) return new List<T> { sourceKeys[0] };
        if (sourceKeys.Count <= 3) return new List<T>(sourceKeys);
        var keptKeys = new List<T>();
        for (int i = 0; i < sourceKeys.Count; i += stride)
            keptKeys.Add(sourceKeys[i]);
        if (!keptKeys[^1].Equals(sourceKeys[^1]))
            keptKeys.Add(sourceKeys[^1]);
        // a list under 3 keys plays key 0 only, so keep the original
        if (keptKeys.Count < 3) return new List<T>(sourceKeys);
        return keptKeys;
    }

    private static EncMotion SimplifyRotations(EncMotion sourceMotion, float toleranceBams)
    {
        var simplifiedMotion = new EncMotion { SlotId = sourceMotion.SlotId, FrameCount = sourceMotion.FrameCount };
        foreach (var (nodeIndex, sourceTrack) in sourceMotion.Tracks)
            simplifiedMotion.Tracks[nodeIndex] = new EncMotTrack {
                Trans = sourceTrack.Trans, Scale = sourceTrack.Scale,
                Rot = sourceTrack.Rot is { Count: > 3 } rotationKeys ? SimplifyKeys(rotationKeys, toleranceBams) : sourceTrack.Rot,
            };
        return simplifiedMotion;
    }

    /// Greedy refit of a BAMS key list: extend each segment while every dropped key lies
    /// within tol of the line between the kept ends. The source curve is piecewise linear
    /// between its keys, so checking the keys bounds the error on every frame. A step stays
    /// under 0x7F00 (the game lerps the short way round 0x10000), and a result under 3 keys
    /// (which would play key 0 only) keeps the original list.
    private static List<(int Frame, int RotationX, int RotationY, int RotationZ)> SimplifyKeys(
        List<(int Frame, int RotationX, int RotationY, int RotationZ)> keys, float toleranceBams)
    {
        bool Fits(int firstIndex, int lastIndex)
        {
            var (firstFrame, firstRx, firstRy, firstRz) = keys[firstIndex];
            var (lastFrame, lastRx, lastRy, lastRz) = keys[lastIndex];
            if (Math.Abs(lastRx - firstRx) > 0x7F00 || Math.Abs(lastRy - firstRy) > 0x7F00 || Math.Abs(lastRz - firstRz) > 0x7F00) return false;
            for (int j = firstIndex + 1; j < lastIndex; j++)
            {
                var (keyFrame, keyRx, keyRy, keyRz) = keys[j];
                float interpolation = (keyFrame - firstFrame) / (float)(lastFrame - firstFrame);
                if (Math.Abs(firstRx + (lastRx - firstRx) * interpolation - keyRx) > toleranceBams || Math.Abs(firstRy + (lastRy - firstRy) * interpolation - keyRy) > toleranceBams ||
                    Math.Abs(firstRz + (lastRz - firstRz) * interpolation - keyRz) > toleranceBams) return false;
            }
            return true;
        }
        var keptKeys = new List<(int, int, int, int)> { keys[0] };
        int startIndex = 0;
        while (startIndex < keys.Count - 1)
        {
            int endIndex = startIndex + 1;
            while (endIndex + 1 < keys.Count && Fits(startIndex, endIndex + 1)) endIndex++;
            keptKeys.Add(keys[endIndex]);
            startIndex = endIndex;
        }
        return keptKeys.Count < 3 ? keys : keptKeys;
    }

    private static byte[] WriteBlock(int nodeCount, EncMotion motion)
    {
        bool hasScaleKeys = motion.Tracks.Values.Any(track => track.Scale is { Count: > 0 });
        int listCount = hasScaleKeys ? 3 : 2;
        int entrySize = listCount * 8;
        int trackTableBase = TrackTableOff;
        int keyframeBase = trackTableBase + nodeCount * entrySize;

        var keyframeStream = new MemoryStream();
        var listPointers = new int[nodeCount, 3];
        var listCounts = new int[nodeCount, 3];
        void WriteKeyList<T>(int nodeIndex, int listIndex, List<T>? keys, Action<T> writeKey)
        {
            if (keys is not { Count: > 0 }) return;
            listPointers[nodeIndex, listIndex] = keyframeBase + (int)keyframeStream.Length;
            listCounts[nodeIndex, listIndex] = keys.Count;
            foreach (var key in keys) writeKey(key);
        }
        for (int i = 0; i < nodeCount; i++)
        {
            if (!motion.Tracks.TryGetValue(i, out var track)) continue;
            WriteKeyList(i, 0, track.Trans, key => { AppendI32(keyframeStream, key.Frame); AppendF32(keyframeStream, key.Raw.X); AppendF32(keyframeStream, key.Raw.Y); AppendF32(keyframeStream, key.Raw.Z); });
            WriteKeyList(i, 1, track.Rot, key => { AppendI32(keyframeStream, key.Frame); AppendI32(keyframeStream, key.RotationX); AppendI32(keyframeStream, key.RotationY); AppendI32(keyframeStream, key.RotationZ); });
            if (hasScaleKeys)
                WriteKeyList(i, 2, track.Scale, key => { AppendI32(keyframeStream, key.Frame); AppendF32(keyframeStream, key.Scale.X); AppendF32(keyframeStream, key.Scale.Y); AppendF32(keyframeStream, key.Scale.Z); });
        }

        int blockSize = keyframeBase + (int)keyframeStream.Length;
        blockSize = (blockSize + 15) & ~15; // 16-byte align
        var blockBytes = new byte[blockSize];

        WriteU32(blockBytes, 0x00, Magic);
        WriteU32(blockBytes, 0x04, (uint)blockSize);
        WriteU16(blockBytes, 0x10, (ushort)nodeCount);
        WriteU32(blockBytes, 0x14, MotionOff);
        WriteU32(blockBytes, 0x18, MotionOff);              // secondary ptr -> MotionDesc (relocated, unused in playback)
        WriteU32(blockBytes, MotionOff + 0x00, (uint)TrackTableOff);
        WriteU32(blockBytes, MotionOff + 0x04, (uint)motion.FrameCount);
        WriteU16(blockBytes, MotionOff + 0x08, (ushort)(hasScaleKeys ? 7 : 3));
        // track table: the list pointers, then their counts
        for (int i = 0; i < nodeCount; i++)
        {
            int entryOffset = trackTableBase + i * entrySize;
            for (int listSlot = 0; listSlot < listCount; listSlot++)
            {
                WriteU32(blockBytes, entryOffset + listSlot * 4, (uint)listPointers[i, listSlot]);
                WriteU32(blockBytes, entryOffset + (listCount + listSlot) * 4, (uint)listCounts[i, listSlot]);
            }
        }
        Array.Copy(keyframeStream.GetBuffer(), 0, blockBytes, keyframeBase, (int)keyframeStream.Length);
        return blockBytes;
    }
}