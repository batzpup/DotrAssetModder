using System.Buffers.Binary;

namespace DotrAssetModder;

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

    /// Serialise the node list (DFS preorder, node[0] = root) into the RH0 node-tree blob.
    public static byte[] Write(IReadOnlyList<EncNode> nodes)
    {
        int n = nodes.Count;
        var buf = new byte[n * NodeSize];
        if (n == 0) return buf;

        int Slot(int i) => i * NodeSize;
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
        return buf;
    }

    // ---- LE helpers ------------------------------------------------------------------
    private static void WriteF32(byte[] b, int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o), v);
    private static void WriteI32(byte[] b, int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
    private static void WriteU16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);
}