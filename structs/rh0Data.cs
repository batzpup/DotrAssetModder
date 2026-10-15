using System.Numerics;
using static DotrAssetModder.Bin;
namespace DotrAssetModder;

//Mostly AI
readonly struct Rh0Header
{
    public readonly uint Magic;
    public readonly uint SectionFlags;
    public readonly uint MotTable;
    public readonly uint NodeTree, NodeTreeSize;
    public readonly uint Section1, Section1Size;
    public readonly uint Section2, Section2Size;
    public readonly uint Section1Alt, Section1AltSize;
    public readonly uint Section2Alt, Section2AltSize;
    public readonly uint TexTable, TexCount;
    public readonly uint RelocWalk, RelocWalkSize;
    public readonly float AnimTransScale;

    public Rh0Header(ReadOnlySpan<byte> data)
    {
        Magic = ReadU32(data, 0);
        if (Magic != Rh0Assembler.Rh0Magic) throw new InvalidDataException($"Not RH0 magic=0x{Magic:x8}");
        SectionFlags = ReadU32(data, 0x04);
        MotTable = ReadU32(data, 0x0C);
        NodeTree = ReadU32(data, 0x10);
        NodeTreeSize = ReadU32(data, 0x14);
        Section1 = ReadU32(data, 0x18);
        Section1Size = ReadU32(data, 0x1C);
        Section2 = ReadU32(data, 0x20);
        Section2Size = ReadU32(data, 0x24);
        Section1Alt = ReadU32(data, 0x28);
        Section1AltSize = ReadU32(data, 0x2C);
        Section2Alt = ReadU32(data, 0x30);
        Section2AltSize = ReadU32(data, 0x34);
        TexTable = ReadU32(data, 0x38);
        TexCount = ReadU32(data, 0x3C);
        RelocWalk = ReadU32(data, 0x50);
        RelocWalkSize = ReadU32(data, 0x54);
        AnimTransScale = BitConverter.Int32BitsToSingle(unchecked((int)ReadU32(data, 0x64)));
    }
}

sealed class GameNode
{
    public int Index;
    public int ParentIndex;
    public ushort Flags;
    public int MeshSlot;
    // as stored in the node (+0x00 scale, +0x10 rotation, +0x20 position)
    public Vector3 RawScale;
    public Vector3 RawPos;
    public (int X, int Y, int Z) RawRot;
    public Matrix4x4 RestWorld;
    public List<int> Children = new();
}

readonly record struct DotrBlendState(
    string Mode,
    float AlphaCutoff,
    float MaterialAlpha,
    ulong GsAlpha,
    ulong GsTest,
    int MaterialColorR,
    int MaterialColorG,
    int MaterialColorB,
    (int R, int G, int B) Color0,
    (int R, int G, int B) Color2,
    float AlphaW,
    bool AlphaBlendEnable,
    bool Textured,
    Vector3 Display,
    bool DisplayClamped);

// One material's worth of draw state: the texture, the chain, the colour block (c0, c1 and
// c1.w as Alpha), the GS registers, and the strip's PRIM bits (ABE | TME).
readonly record struct PrimitiveKey(
    int TextureIndex,
    bool IsAlt,
    int Alpha,
    (int R, int G, int B) MaterialColor0,
    (int R, int G, int B) MaterialColor1,
    (int R, int G, int B) MaterialColor2,
    ulong GsAlpha,
    ulong GsTest,
    ulong GsClamp,
    int Prim);

sealed class PageInfo
{
    public int WalkOff;
    public Dictionary<int, int> BoneMap = new();
    public uint Sec2Off;
    public uint PktSize;
    public (float R, float G, float B) ColorQ0;
    public (float R, float G, float B) ColorQ1;
    public (float R, float G, float B) ColorQ2;
    public float AlphaW;
    public ulong GsAlpha;
    public ulong GsTest;
    public ulong GsClamp;
}

sealed class Packet
{
    public int WalkOff;
    public Dictionary<int, int> BoneMap = new();
    public List<Strip> Strips = new();
    public (float R, float G, float B) ColorQ0;
    public (float R, float G, float B) ColorQ1;
    public (float R, float G, float B) ColorQ2;
    public bool IsAlt;
    public float AlphaW;
    public ulong GsAlpha;
    public ulong GsTest;
    public ulong GsClamp;
}

sealed class Strip
{
    public string Type = "SHARED";
    public int VuAddr;
    public int Prim;   // GIF tag PRIM: 0x10 TME (textured), 0x40 ABE (alpha blended)
    public List<StripVertex> Verts = new();
}

sealed class StripVertex
{
    public (short U, short V) Uv;
    public (short X, short Y, short Z) Position;
    public (short X, short Y, short Z) Normal; // stored vertex normal (s16, 1.0 ~= 32767)
    public List<StripWeight>? Weights;
}

readonly struct StripWeight
{
    public readonly int Bone;
    public readonly short Weight;
    public readonly (short X, short Y, short Z) Position;
    public readonly (short X, short Y, short Z) Normal; // bind-space normal for this influence

    public StripWeight(int bone, short weight, (short X, short Y, short Z) position, (short X, short Y, short Z) normal)
    {
        Bone = bone;
        Weight = weight;
        Position = position;
        Normal = normal;
    }
}

sealed class BuiltMesh
{
    public List<Vector3> Vertices = new();
    public List<Vector3> Normals = new(); // output-space vertex normals (winding orientation only)
    public List<Vector2> Uvs = new();
    public List<List<(int Joint, float W)>> VertJoints = new();
    // each vertex's influences as RH0 stores them, in its order with zero weights kept; Bone
    // is the joint index here (empty: none to keep)
    public List<List<StripWeight>> VertGame = new();
    public List<Primitive> Primitives = new();
    public List<int> SortedMslots = new();
    public Dictionary<int, int> MslotToJoint = new();
}

sealed class Primitive
{
    public PrimitiveKey Key;
    public float AlphaW;
    public List<(int First, int Second, int Third)> Faces = new();
}

sealed class AnimSlot
{
    public int SlotId;
    public int FrameCount;
    public bool Scaled;                     // type 7: entries carry a scale key list
    public Dictionary<int, AnimTrack> Tracks = new();
}

sealed class AnimTrack
{
    public List<(int Frame, float X, float Y, float Z)>? Trans;
    public List<(int Frame, int RotationX, int RotationY, int RotationZ)>? Rotation;
    public List<(int Frame, float X, float Y, float Z)>? Scale;
}

readonly struct DecodedTextureInfo
{
    public readonly int Width;
    public readonly int Height;
    public readonly byte[] Rgba;

    public DecodedTextureInfo(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
    }
}