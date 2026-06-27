using System.Buffers.Binary;

namespace DotrAssetModder;
// AI

// === Step 6.3 — Node-tree (skeleton) byte writer =====================================
// Emits the RH0 node tree: 48-byte (0x30) RH0Node records linked first-child/next-sibling.
// Validated against blue-eyes_white_dragon.mrg: re-emitting parsed nodes in DFS order with
// recomputed child/sib offsets reproduces every node's RestWorldSkin matrix exactly
// (max element error 0). Layout per node (offsets rel. NodeTree base, little-endian):
//   +0x00 f32 scaleX  +0x04 f32 scaleY  +0x08 f32 scaleZ
//   +0x0C u16 flags   (bit0 skipT, bit1 skipR, bit2 noScaleProp; higher bits engine-internal)
//   +0x0E u16 meshSlot (0 = none; links geometry bone slots -> this node)
//   +0x10 s32 rotX    +0x14 s32 rotY    +0x18 s32 rotZ   (BAMS; full circle = 0x10000)
//   +0x1C s32 childOff (-> first child, rel. NodeTree base, 0 = none)
//   +0x20 f32 posX    +0x24 f32 posY    +0x28 f32 posZ   (local translation, pre-parent-scale)
//   +0x2C s32 sibOff  (-> next sibling, rel. NodeTree base, 0 = none)
//
// BOUNDS RECORD (0x10 bytes) at NodeTree base +0x30, i.e. immediately AFTER the root node:
//   +0x30 f32 centerX +0x34 f32 centerY +0x38 f32 centerZ  (model bbox/pivot centre, WORLD units)
//   +0x3C f32 radius                                       (bounding-sphere radius, WORLD units)
// The engine (lSzModel_LoadCB@0x...) seeds handle+0x90 (centre, w=1.0) from +0x30..+0x38 and
// handle+0x1E4 (radius) from +0x3C at load; SzModel_DrawWithFunc_Pipe REWRITES +0x3C with
// radius*overallScale every frame as draw scratch. handle+0x90 -> handle+0x80 (SzModel_GetCenter)
// is the booked world point the DUEL camera Y-follows each frame and the OVERWORLD enemy preview
// (Taku_Camp_Teki_Model_Disp) frames the camera on — NEITHER touches GNode(1). Without this record
// those camera-framed views (duel / 3D battle / enemy preview) frame garbage, so rebuilt monsters
// render off-centre regardless of the GNode(1) 'origin' pivot (which only drives the overworld
// rotation pivot + the one-time duel SetPos pin). The retail tool leaves a 0x10 gap here and the
// root's childOff points PAST it (>= 0x40); the extractor walks via childOff/sibOff and never sees
// these bytes, so they must be regenerated on rebuild. node[i>=1] therefore start at 0x40, not 0x30.
// Read math (the game's, replicated by the extractor):
//   rp = (skipT?0:pos) * parentAccum ; rr = skipR?0:rot
//   accum = noScaleProp ? parentAccum : parentAccum*scale
//   local = RotX(rr)*RotY(rr)*RotZ(rr) * Translation(rp)
//   world = local * parentWorld ; RestWorldSkin = Scale(accum) * world
// The model's global scale lives in the root node's `scale` (propagated). See DotR_WriteSpec.
// =====================================================================================

/// One node for the writer. Nodes are supplied in DFS preorder (parent before its children,
/// node[0] = root). `Parent` is the index of the parent in the list (-1 for the root); a
/// parent's `Children` list defines first-child / sibling ordering.
public sealed class EncNode
{
    public (float X, float Y, float Z) Scale = (1f, 1f, 1f);
    public ushort Flags;
    public ushort MeshSlot;                 // bone/mesh slot id (matches Section1 bone-binds)
    public (int X, int Y, int Z) RotBams;   // BAMS Euler, X*Y*Z order
    public (float X, float Y, float Z) Pos; // raw local translation (read multiplies by parent accum)
    public int Parent = -1;
    public List<int> Children = new();
}

public static class NodeTreeWriter
{
    public const int NodeSize = 0x30;
    public const int BoundsSize = 0x10;   // centre.xyz + radius, sits at tree base +0x30

    /// Serialise the node list (DFS preorder, node[0] = root) into the RH0 node-tree blob.
    /// A 0x10-byte bounds record (boundsCenter.xyz + boundsRadius, all WORLD units) is written
    /// at tree offset 0x30 — immediately after the root node — and node[i>=1] are shifted to
    /// start at 0x40. This matches the retail layout the camera-framing paths (duel per-frame
    /// Y-follow, 3D battle, overworld enemy preview) read from; see the header comment.
    public static byte[] Write(IReadOnlyList<EncNode> nodes,
        (float X, float Y, float Z) boundsCenter = default, float boundsRadius = 0f)
    {
        int n = nodes.Count;
        if (n == 0) return new byte[0];
        // node[0] @ 0x00 | bounds @ 0x30 | node[1] @ 0x40 | node[2] @ 0x70 | ...
        var buf = new byte[n * NodeSize + BoundsSize];

        // Offset of node i: the root stays at 0; every other node is pushed past the bounds
        // record. childOff/sibOff are emitted via Slot(), so the root's childOff lands at 0x40
        // automatically and no caller needs to know about the gap.
        int Slot(int i) => i == 0 ? 0 : i * NodeSize + BoundsSize;
        int FirstChild(int i) => nodes[i].Children.Count > 0 ? nodes[i].Children[0] : -1;
        int NextSib(int i)
        {
            int p = nodes[i].Parent;
            if (p < 0) return -1;
            var sibs = nodes[p].Children;
            int k = sibs.IndexOf(i);
            return (k >= 0 && k + 1 < sibs.Count) ? sibs[k + 1] : -1;
        }

        for (int i = 0; i < n; i++)
        {
            int b = Slot(i);
            var nd = nodes[i];
            WriteF32(buf, b + 0x00, nd.Scale.X);
            WriteF32(buf, b + 0x04, nd.Scale.Y);
            WriteF32(buf, b + 0x08, nd.Scale.Z);
            WriteU16(buf, b + 0x0C, nd.Flags);
            WriteU16(buf, b + 0x0E, nd.MeshSlot);
            WriteI32(buf, b + 0x10, nd.RotBams.X);
            WriteI32(buf, b + 0x14, nd.RotBams.Y);
            WriteI32(buf, b + 0x18, nd.RotBams.Z);
            int fc = FirstChild(i);
            WriteI32(buf, b + 0x1C, fc >= 0 ? Slot(fc) : 0);
            WriteF32(buf, b + 0x20, nd.Pos.X);
            WriteF32(buf, b + 0x24, nd.Pos.Y);
            WriteF32(buf, b + 0x28, nd.Pos.Z);
            int sb = NextSib(i);
            WriteI32(buf, b + 0x2C, sb >= 0 ? Slot(sb) : 0);
        }

        // Bounds record at tree base +0x30 (right after the root node). centre.w is implicit
        // (the engine forces handle+0x90+0xC = 1.0). radius is read once at load then reused as
        // per-frame draw scratch, so the on-disk value is just the initial bounding radius.
        WriteF32(buf, NodeSize + 0x00, boundsCenter.X);
        WriteF32(buf, NodeSize + 0x04, boundsCenter.Y);
        WriteF32(buf, NodeSize + 0x08, boundsCenter.Z);
        WriteF32(buf, NodeSize + 0x0C, boundsRadius);
        return buf;
    }

    // ---- LE helpers ------------------------------------------------------------------
    private static void WriteF32(byte[] b, int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o), v);
    private static void WriteI32(byte[] b, int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
    private static void WriteU16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);
}