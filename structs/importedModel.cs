using System.Numerics;

namespace DotrAssetModder;
// Mostly AI

public sealed class ImportedNode
{
    public int Index;                       // glb logical index
    public string Name = "";
    public Vector3 Translation;             // local
    public Quaternion Rotation = Quaternion.Identity; // local
    public Vector3 Scale = Vector3.One;     // local
    public Matrix4x4 WorldMatrix = Matrix4x4.Identity; // SharpGLTF world (== read-side `world`)
    public int Parent = -1;
    public List<int> Children = new();
    public bool IsJoint;                    // referenced by the skin
    public bool HasMesh;                    // true when this node instantiates a glTF mesh

    /// Extracted glbs only: the `_bone` child carrying this node's accumulated RH0 scale
    /// (ModelExtractor.WriteGlb), or -1. The child is folded back into this node: it is
    /// removed from Children, and skin joints / rigid meshes on it refer to this node.
    public int ScaleBone = -1;
}

public struct ImportedInfluence
{
    public int JointNode;   // glb node index of the influencing joint
    public float Weight;
}

public sealed class ImportedVertex
{
    /// Position used by the geometry/skinning encoder.
    /// For skinned meshes this stays in the mesh/skin bind space so animation remains coherent.
    /// For unskinned meshes this is already world-baked.
    public Vector3 Position;

    /// Frame-generation position (bounds/pivot/radius diagnostics in RebuildPipeline).
    /// Always equal to Position; not for skinning.
    public Vector3 FramePos;

    public Vector3 Normal;                  // normal used by the geometry encoder
    public Vector2 Uv;
    public List<ImportedInfluence> Influences = new(); // 0 => rigid to the prim's mesh node

    /// Extracted glbs: the vertex's influences as RH0 stored them (ModelExtractor's
    /// VertexTexture1Game; Bone = the joint's glb node), in the game's order with zero weights
    /// kept, while they still match the vertex; null = derive them from Position as for any glb.
    internal List<StripWeight>? Game;
}

public sealed class ImportedPrimitive
{
    /// glb logical node that instanced this mesh (-1 if unknown). For unskinned prims the
    /// vertices are world-baked at import and rigidly rigged to this node.
    public int NodeIndex = -1;
    public int TextureIndex;
    public bool IsAlt;
    /// GS ALPHA_1 low byte: 0x44 normal (Cs-Cd)*As+Cd, 0x48 additive Cs*As+Cd, 0x42 subtract.
    public byte GsAlpha = 0x44;
    /// GS TEST_1 low 20 bits from an extracted name ("_t703FD": the AREF 63 cutout); 0 = the
    /// chain's default (0x7000C opaque, 0x7000D alpha).
    public uint GsTest;
    public Vector4 BaseColor = Vector4.One; // RGBA 0..1
    /// The material record's c0 and c2 rows (+0x10 / +0x30), which a glb has no field for;
    /// an extracted glb carries them in DOTR_blend, anything else gets the retail defaults.
    public (int R, int G, int B) Color0 = (127, 127, 127);
    public (int R, int G, int B) Color2 = (255, 255, 255);
    /// The raw c1 row (+0x20) when the base colour is still the one the extractor wrote.
    public (int R, int G, int B)? RawColor1;
    /// An extracted "mat_untextured" page: its strips are drawn with PRIM TME off.
    public bool Untextured;
    public List<ImportedVertex> Vertices = new();
    public List<(int First, int Second, int Third)> Triangles = new();
}

/// Every channel is resampled at every integer frame 0..FrameCount-1 (t = frame / 30) with
/// its own sampler's interpolation (STEP, LINEAR or CUBICSPLINE), so entry i is frame i.
public sealed class ImportedAnimChannel
{
    public List<(float Time, Vector3 Value)>? Translation;
    public List<(float Time, Quaternion Rotation)>? Rotation;
    public List<(float Time, Vector3 Value)>? Scale;
}

public sealed class ImportedAnimation
{
    public string Name = "";
    public int FrameCount;
    public Dictionary<int, ImportedAnimChannel> Channels = new(); // key = glb node index
}

public sealed class ImportedModel
{
    public List<ImportedNode> Nodes = new();        // indexed by glb logical index
    public List<int> RootNodes = new();             // top-level node indices
    public List<int> JointNodeIndices = new();      // skin joints, in skin order
    public List<ImportedPrimitive> Primitives = new();
    public List<ImportedAnimation> Animations = new();

    /// Joints bound somewhere other than at their rest world matrix: an extracted `_bone`
    /// joint (glb-space inverse of its inverse bind matrix, accumulated scale included), or
    /// an unskinned mesh node at scale 0 in the rest pose (its world matrix in the first
    /// animation frame where it is not). Keyed by the joint's glb node index.
    public Dictionary<int, Matrix4x4> JointBind = new();

    /// Embedded base-colour images per texture index (from material names "mat_texN"), as
    /// SharpGLTF MemoryImage (PNG/JPEG bytes). They are decoded and passed to the assembler
    /// as Rh0Assembler.TextureInput; textures absent here keep the template model's RH2.
    public Dictionary<int, SharpGLTF.Memory.MemoryImage> TextureImages = new();

    /// Animated-texture frame sequences, keyed by the same texIdx as TextureImages. Frames
    /// come from extra embedded glb images named "tex{N}_anim{frame}" (any width, any count);
    /// glTF has no image-sequence texture, so a Blender "Image Sequence" node must be flattened
    /// into individually named stills (name each frame's Image datablock that way before export).
    /// Frames are sorted by trailing frame number and packed into one sprite sheet by
    /// GlbImporter.DecodeGlbTextures, which Rh0Assembler uses to synthesize the TexAnm
    /// flipbook entry. A texIdx with no such images uses the static TextureImages path.
    /// Fewer than 2 frames is not treated as animated; frame 0's image is also harvested into
    /// TextureImages either way, so the slot imports as a static texture.
    public Dictionary<int, List<SharpGLTF.Memory.MemoryImage>> TextureAnimFrames = new();

    /// Per-texture flipbook tick rate (hold time per frame, written to the ANM entry's
    /// aux0/aux1, see Rh0Assembler.BuildCustomTexAnimEntry), parsed from an optional "_tN"
    /// suffix on the slot's "tex{N}_anim{frame}" image names ("tex0_anim0_t10" = 10 ticks per
    /// frame). Default 5, matching the one retail sample.
    public Dictionary<int, int> TextureAnimTicks = new();

    /// UV scale of each packed flipbook slot (DecodeGlbTextures): its faces sample only cell 0 of
    /// the sprite sheet, so RebuildPipeline.ApplyTexAnimUv squeezes their [0,1] UVs into it.
    public Dictionary<int, Vector2> TextureAnimUvScale = new();

    /// Set once RebuildPipeline.ApplyTexAnimUv has rewritten the UVs (Build may run twice).
    public bool AppliedTexAnimUv;

    /// Auto-assigned texture index per glb material (LogicalIndex), for materials not named
    /// "mat_texN". Assigned in LogicalIndex order, skipping slots taken by explicit mat_texN
    /// names, so the glb's materials array order is the slot order.
    public Dictionary<int, int> MaterialTexAuto = new();

    /// Dense renumbering of explicitly named "mat_texN" materials, for CUSTOM (non-extracted)
    /// glbs only: material LogicalIndex -> final texture slot, holding only the materials whose
    /// number actually changed. Empty for extracted glbs, whose mat_texN numbers ARE the retail
    /// slot numbers and must survive a round trip untouched.
    public Dictionary<int, int> MatTexRemap = new();

    /// The retail texture-animation records of an extracted glb, per texture slot, already
    /// packed as the 24-byte entries the game reads (ModelExtractor's "DOTR_texAnimRecords"
    /// material extras). They hold no pointers, so they are written back verbatim.
    public Dictionary<int, byte[]> TextureAnimRecords = new();

    /// Texture slots whose image alpha is not coverage (a custom glb's additive material keeps
    /// its glow strength there for glTF viewers): encoded with alpha forced to 255, since the
    /// 1-bit-alpha CLUT would otherwise turn every texel under half alpha transparent black.
    public HashSet<int> OpaqueAlphaSlots = new();

    /// Texture slots of a custom glb's translucent (BLEND/MASK) materials: encoded with alpha cut
    /// to coverage (texel alpha >= 8 -> opaque, else a hole) instead of at half alpha, because the
    /// strength now comes from the page's c1.w (see ParsePlainAlpha).
    public HashSet<int> CoverageAlphaSlots = new();

    /// Decoded alpha planes of base-colour images, by glb image index (ParsePlainAlpha).
    internal Dictionary<int, (int W, int H, byte[] A)?> AlphaPlanes = new();

    /// The named frame images of an EXTRACTED glb's animated slot ("tex{N}_anim{frame}",
    /// written by ModelExtractor.InjectTextureAnimations), kept apart from
    /// TextureAnimFrames: this slot's animation is already carried by TextureAnimRecords,
    /// so the frames are not repacked into a flipbook sheet. An edited frame is written
    /// back into the cell the record copies it from instead, so the records stay retail's
    /// (GlbImporter.ApplyEditedAnimFrames). Keyed by texIdx, then frame index.
    public Dictionary<int, SortedDictionary<int, SharpGLTF.Memory.MemoryImage>> TextureAnimEditFrames = new();

    /// The GS copies each of those frames is made of, from the material's "DOTR_texAnim"
    /// extras: texIdx -> frame -> (record index, cell of that record's sheet, hash of the
    /// frame's destination rectangle as extracted).
    public Dictionary<int, Dictionary<int, List<(int Rec, int Cell, uint Hash)>>> TextureAnimStamps = new();

    /// Duplicate or out-of-range "mat_texN" slots seen by the texture-slot pre-pass, one line
    /// each (also echoed to stderr). Reported rather than silently mishandled.
    public List<string> TexSlotWarnings = new();

    /// Skinned vertices of an extracted glb that kept their game influences, and those that
    /// carried them but no longer matched (edited elsewhere) and were derived from Position.
    public int GameVertices;
    public int GameVerticesDropped;

    /// Set by RebuildPipeline.ApplyTexMerges after it rewrites primitive UVs/TextureIndex for
    /// UV-atlas slot merging (guards against applying twice when Build runs twice). Holds the
    /// merge plan: base slot -> member slots in band order.
    public List<(int BaseSlot, List<int> MemberSlots)>? AppliedTexMerges;

    /// True for GLBs produced by this project's extractor: they contain an `MrgRoot` wrapper
    /// and have skinned positions already baked into the extractor's round-trip coordinate
    /// space, so they are not handled like generic Blender/Sketchfab GLBs.
    public bool IsDotrExtractedGlb;

    /// Inverse of the extractor's MrgRoot world, when RebuildPipeline.PrepareRoot dropped it
    /// without baking it into node 0: maps glb-space positions back to model space.
    public Matrix4x4? DroppedRootUndo;
}