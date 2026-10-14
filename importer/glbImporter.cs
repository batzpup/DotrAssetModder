using System.IO;
using System.Numerics;
using Raylib_cs;
using SharpGLTF.Schema2;
using Buffer = System.Buffer;
using Image = Raylib_cs.Image;
using Material = SharpGLTF.Schema2.Material;
using static DotrAssetModder.Bin;

namespace DotrAssetModder;
// Mostly AI


/* GLB parser. Loads a .glb (SharpGLTF 1.0.6) and flattens it into the intermediate the
   rebuilder pipeline consumes. The extractor wrote skinned meshes with an identity mesh
   transform, so the POSITION accessor already holds world-space bind positions; joints
   carry their world matrix directly (node scales are 1; the global scale S is
   re-introduced later in the root node). Material names are `mat_tex{N}` (+`_alt`),
   encoding the texture index and the alt-mesh flag.

   Validation targets (Blue-Eyes_White_Dragon.glb): 133 nodes, 1 skin / 71 joints,
   4 primitives (mat_tex0..3), 4818 triangles, 3951 verts. */

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

public static class GlbImporter
{
    public static ImportedModel Load(string path) => FromModel(ModelRoot.Load(path));

    public static ImportedModel LoadFromBytes(byte[] glb) => FromModel(ModelRoot.ParseGLB(glb));

    private static ImportedModel FromModel(ModelRoot model)
    {
        var importedModel = new ImportedModel
        {
            IsDotrExtractedGlb = model.LogicalNodes.Any(node =>
                string.Equals(node.Name, "MrgRoot", StringComparison.Ordinal))
        };

        var byLogical = new Dictionary<int, int>(); // logical index -> position in importedModel.Nodes
        foreach (var gltfNode in model.LogicalNodes)
        {
            var localTransform = gltfNode.LocalTransform;
            var node = new ImportedNode
            {
                Index = gltfNode.LogicalIndex,
                Name = gltfNode.Name ?? "",
                Translation = localTransform.Translation,
                Rotation = localTransform.Rotation,
                Scale = localTransform.Scale,
                WorldMatrix = WorldOf(gltfNode),
            };
            byLogical[gltfNode.LogicalIndex] = importedModel.Nodes.Count;
            importedModel.Nodes.Add(node);
        }
        foreach (var gltfNode in model.LogicalNodes)
        {
            var node = importedModel.Nodes[byLogical[gltfNode.LogicalIndex]];
            node.Parent = gltfNode.VisualParent != null ? gltfNode.VisualParent.LogicalIndex : -1;
            if (node.Parent < 0) importedModel.RootNodes.Add(node.Index);
            foreach (var child in gltfNode.VisualChildren) node.Children.Add(child.LogicalIndex);
        }

        /* Extracted `_bone` scale nodes (ModelExtractor.WriteGlb): a slot node's accumulated
           scale rides a `_bone` child so that, as in RH0, it reaches only that node's own
           matrix. Each is folded back into its node (the RH0 tree has no such node); the skin
           joint and any rigid mesh on it belong to the node. RebuildPipeline reads the child's
           scale (rest and animated) as the node's accumulated scale. */
        var boneOwner = new Dictionary<int, int>(); // `_bone` glb index -> owning node
        if (importedModel.IsDotrExtractedGlb)
        {
            foreach (var importedNode in importedModel.Nodes)
            {
                if (importedNode.Parent < 0 || importedNode.Children.Count != 0) continue;
                if (!System.Text.RegularExpressions.Regex.IsMatch(importedNode.Name, @"_bone(?:\.\d+)?$")) continue;
                var owner = importedModel.Nodes[byLogical[importedNode.Parent]];
                if (owner.ScaleBone >= 0) continue;
                owner.ScaleBone = importedNode.Index;
                owner.Children.Remove(importedNode.Index);
                boneOwner[importedNode.Index] = owner.Index;
            }
        }
        int OwnerOf(int glbIndex) => boneOwner.TryGetValue(glbIndex, out int ownerIndex) ? ownerIndex : glbIndex;

        if (model.LogicalSkins.Count > 0)
        {
            var skin = model.LogicalSkins[0];
            for (int i = 0; i < skin.JointsCount; i++)
            {
                var (joint, inverseBind) = skin.GetJoint(i);
                int jointOwner = OwnerOf(joint.LogicalIndex);
                importedModel.JointNodeIndices.Add(jointOwner);
                importedModel.Nodes[byLogical[jointOwner]].IsJoint = true;
                // A `_bone` joint is bound where its part is visible, not at the scale-0 rest pose.
                if (jointOwner != joint.LogicalIndex && Matrix4x4.Invert(inverseBind, out var bindWorld))
                    importedModel.JointBind[jointOwner] = bindWorld;
                /* A joint whose bind pose is not its rest pose (DoD exports bind every joint away
                   from the node rest values) must encode its vertices against the bind, or limbs
                   stretch once the motion moves them. */
                else if (jointOwner == joint.LogicalIndex && Matrix4x4.Invert(inverseBind, out bindWorld) &&
                         !NearlyEqual(bindWorld, joint.WorldMatrix))
                    importedModel.JointBind[jointOwner] = bindWorld;
            }
        }

        /* Every channel is resampled at every integer frame with its sampler's own interpolation.
           This turns STEP and CUBICSPLINE into plain per-frame values (CUBICSPLINE outputs are
           tangent/value/tangent triplets, never keys) and lets RebuildPipeline fit the game's
           key lists (per-axis Euler lerp, a list under 3 keys plays key 0 only) to the motion.
           CreateCurveSampler(true) copies the keys out once; without it every sample re-reads
           the accessors (card 60: 1 s instead of 17 ms). */
        foreach (var anim in model.LogicalAnimations)
        {
            int frames = (int)MathF.Round(anim.Duration * AnimFps) + 1;
            var importedAnimation = new ImportedAnimation { Name = anim.Name ?? "", FrameCount = frames };
            foreach (var channel in anim.Channels)
            {
                if (channel.TargetNode == null) continue;
                int node = channel.TargetNode.LogicalIndex;
                if (!importedAnimation.Channels.TryGetValue(node, out var channels)) { channels = new ImportedAnimChannel(); importedAnimation.Channels[node] = channels; }
                switch (channel.TargetNodePath)
                {
                    case PropertyPath.translation:
                        channels.Translation = Resample(channel.GetTranslationSampler()?.CreateCurveSampler(true), frames);
                        break;
                    case PropertyPath.rotation:
                        channels.Rotation = Resample(channel.GetRotationSampler()?.CreateCurveSampler(true), frames);
                        break;
                    case PropertyPath.scale:
                        channels.Scale = Resample(channel.GetScaleSampler()?.CreateCurveSampler(true), frames);
                        break;
                }
            }
            // no node channel (e.g. texture-scroll animations through KHR_animation_pointer): not a motion
            if (importedAnimation.Channels.Count == 0) continue;
            importedModel.Animations.Add(importedAnimation);
        }

        /* Rigid extractor output (ModelExtractor.WriteGlb's rigid mode) has no skin; each bone's
           mesh sits on the bone's own node, named with a `_m<slot>` suffix. The joint set the
           skinned export would declare (every slot node, in slot order) is rebuilt, so a round
           trip keeps the original matrix slots, including slots with no geometry. */
        bool rigidDotr = importedModel.IsDotrExtractedGlb && model.LogicalSkins.Count == 0;
        if (rigidDotr)
        {
            var slotted = new List<(int Slot, int Node)>();
            foreach (var gltfNode in model.LogicalNodes)
            {
                var slotMatch = System.Text.RegularExpressions.Regex.Match(gltfNode.Name ?? "", @"_m(\d+)(?:\.\d+)?$");
                if (slotMatch.Success) slotted.Add((int.Parse(slotMatch.Groups[1].Value), gltfNode.LogicalIndex));
            }
            foreach (var (_, nodeIndex) in slotted.OrderBy(entry => entry.Slot))
            {
                importedModel.JointNodeIndices.Add(nodeIndex);
                importedModel.Nodes[byLogical[nodeIndex]].IsJoint = true;
            }
        }

        /* Auto texture-slot assignment, a pre-pass in material LogicalIndex order so the
           material -> slot mapping depends on the glb alone. Explicitly named mat_texN
           materials own their slots; every other material gets the next free slot.

           A slot number indexes the template model's RH2 blocks, and a slot nothing supplies
           costs a 9 KB white placeholder (Rh0Assembler), so a gap is pure waste. An extracted
           glb's mat_texN numbers are the retail slot numbers and are kept exactly. A custom
           glb's numbers are only the author's labels, so they are compacted to a dense 0..k-1
           in ascending mat_texN order; unnamed materials then take k, k+1, ... (sharing a slot
           when they share a base-colour image or flat colour). A duplicate or out-of-range
           mat_texN is reported in TexSlotWarnings.
           texLabelRemap maps mat_texN label -> dense slot for a custom glb (empty for an
           extracted one); the "tex{N}_anim{frame}" pass below uses the same author labels and
           follows the same remap. */
        var texLabelRemap = new Dictionary<int, int>();
        {
            var explicitSlot = new Dictionary<int, int>();  // material LogicalIndex -> mat_texN
            /* mat_texN -> first material naming it and its image/colour. Several materials on one
               slot is normal (retail splits a slot by alpha/GS state; the extractor writes one
               material per page), but two different images on one slot clash: only the first is
               harvested into TextureImages. */
            var firstOwner = new Dictionary<int, (int Mat, string Key)>();
            foreach (var material in model.LogicalMaterials)
            {
                if (!TryParseMatTexIndex(material.Name, out int slot)) continue;
                if (slot < 0 || slot > MaxTexSlot)
                    importedModel.TexSlotWarnings.Add($"material {material.LogicalIndex} \"{material.Name}\": texture slot {slot} is out of range 0..{MaxTexSlot}");
                string key = MaterialShareKey(material);
                if (firstOwner.TryGetValue(slot, out var first))
                {
                    if (first.Key != key)
                        importedModel.TexSlotWarnings.Add($"material {material.LogicalIndex} \"{material.Name}\": texture slot {slot} already holds a different image from material {first.Mat}; only material {first.Mat}'s image is imported");
                }
                else firstOwner[slot] = (material.LogicalIndex, key);
                explicitSlot[material.LogicalIndex] = slot;
            }
            if (!importedModel.IsDotrExtractedGlb && explicitSlot.Count != 0)
            {
                // custom glb: compact the named slots (an out-of-range one lands in range here)
                var compact = new Dictionary<int, int>();
                int dense = 0;
                foreach (int slot in new SortedSet<int>(explicitSlot.Values)) compact[slot] = dense++;
                foreach (var entry in explicitSlot)
                    if (compact[entry.Value] != entry.Value) importedModel.MatTexRemap[entry.Key] = compact[entry.Value];
                foreach (var entry in compact)
                    if (entry.Key != entry.Value) texLabelRemap[entry.Key] = entry.Value;
            }
            foreach (var warning in importedModel.TexSlotWarnings) Console.Error.WriteLine("GlbImporter: " + warning);
            // slots held by named materials after the remap; auto assignment skips them
            var explicitSlots = new HashSet<int>();
            foreach (var entry in explicitSlot)
                explicitSlots.Add(importedModel.MatTexRemap.TryGetValue(entry.Key, out int remapped) ? remapped : entry.Value);
            int nextAuto = 0;
            var slotByKey = new Dictionary<string, int>();   // image/colour dedupe
            foreach (var material in model.LogicalMaterials)
            {
                if (explicitSlot.ContainsKey(material.LogicalIndex)) continue;
                if (IsUntexturedMaterial(material.Name)) continue;   // draws with TME off: no slot of its own
                // A flat-colour material (no base-colour texture) is drawn with TME off, its colour
                // carried as the page colour (ParseMaterial), so it takes no slot either.
                if (!HasBaseColorTexture(material)) continue;
                // materials with the same base-colour image or flat colour share one slot (slots are scarce)
                string key = MaterialShareKey(material);
                if (slotByKey.TryGetValue(key, out int shared))
                {
                    importedModel.MaterialTexAuto[material.LogicalIndex] = shared;
                    continue;
                }
                while (explicitSlots.Contains(nextAuto)) nextAuto++;
                slotByKey[key] = nextAuto;
                importedModel.MaterialTexAuto[material.LogicalIndex] = nextAuto++;
            }
        }

        /* Mesh primitives, iterated per node instance so the node transform is known.
           Skinned prims: vertices are already in skin space (glTF ignores the node transform
           for skinned meshes), so they are kept as-is; influences come from JOINTS_0/WEIGHTS_0.
           Unskinned prims (static props/vehicles): the node's world transform is baked into the
           vertices and they are rigged rigidly to that node (a one-bone "joint" downstream). */
        foreach (var meshNode in model.LogicalNodes)
        {
            if (meshNode.Mesh == null) continue;
            // a rigid mesh on an extracted `_bone` belongs to the bone's node
            int meshOwner = OwnerOf(meshNode.LogicalIndex);
            // A rigid-extract joint keeps joint-only status, as in the skinned export (RebuildPipeline root detection).
            if (byLogical.TryGetValue(meshOwner, out int meshNodePos)
                && !(rigidDotr && importedModel.Nodes[meshNodePos].IsJoint))
                importedModel.Nodes[meshNodePos].HasMesh = true;
            bool nodeSkinned = meshNode.Skin != null;
            Matrix4x4 nodeWorld = WorldOf(meshNode);
            if (!nodeSkinned)
            {
                /* A node at scale 0 is how DotR models hide a part until a motion scales it up
                   (Meteor Dragon's head and limbs). Its vertices are bound at the first animation
                   frame where it is visible, and the pipeline encodes them against that matrix.
                   Only a node invisible in every frame is skipped. */
                var bind = FirstVisibleWorld(importedModel, meshNode.LogicalIndex, nodeWorld);
                if (bind is not { } bindWorld) continue;
                if (meshOwner != meshNode.LogicalIndex || bindWorld != nodeWorld)
                    importedModel.JointBind[meshOwner] = bindWorld;
                nodeWorld = bindWorld;
            }
            foreach (var prim in meshNode.Mesh.Primitives)
            {
                var importedPrimitive = new ImportedPrimitive { NodeIndex = meshOwner };
                ParseMaterial(prim.Material, importedPrimitive, importedModel);

                var positions = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                if (positions == null) continue;
                var normals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var uvs = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                var joints0 = prim.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
                var weights0 = prim.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
                // a vertex may carry up to 8 influences (ModelExtractor writes a second set)
                var joints1 = prim.GetVertexAccessor("JOINTS_1")?.AsVector4Array();
                var weights1 = prim.GetVertexAccessor("WEIGHTS_1")?.AsVector4Array();

                bool skinnedPrim = nodeSkinned && joints0 != null && weights0 != null;
                int vertexCount = positions.Count;
                var game = skinnedPrim && importedModel.IsDotrExtractedGlb ? GameAccessors(prim) : null;
                for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
                {
                    var vertex = new ImportedVertex();
                    if (skinnedPrim)
                    {
                        /* Skinned positions stay in skin/bind space; baking the mesh node world matrix
                           here makes the skeleton/rest basis disagree with the vertices (90-degree
                           rotation or broken animation on external Armature-rooted GLBs). */
                        vertex.Position = positions[vertexIndex];
                        vertex.Normal = normals != null ? SafeNormalize(normals[vertexIndex]) : Vector3.UnitY;
                        vertex.FramePos = vertex.Position;
                    }
                    else
                    {
                        // unskinned: rigid on the mesh node, so bake its world transform
                        vertex.Position = Vector3.Transform(positions[vertexIndex], nodeWorld);
                        vertex.FramePos = vertex.Position;
                        vertex.Normal = normals != null ? SafeNormalize(Vector3.TransformNormal(normals[vertexIndex], nodeWorld)) : Vector3.UnitY;
                    }
                    vertex.Uv = uvs != null ? uvs[vertexIndex] : Vector2.Zero;

                    if (skinnedPrim)
                    {
                        var jointIndices = joints0[vertexIndex]; var weights = weights0[vertexIndex];
                        AddInfluence(vertex, importedModel, (int)jointIndices.X, weights.X);
                        AddInfluence(vertex, importedModel, (int)jointIndices.Y, weights.Y);
                        AddInfluence(vertex, importedModel, (int)jointIndices.Z, weights.Z);
                        AddInfluence(vertex, importedModel, (int)jointIndices.W, weights.W);
                        if (joints1 != null && weights1 != null)
                        {
                            var jointIndicesSet1 = joints1[vertexIndex]; var weightsSet1 = weights1[vertexIndex];
                            AddInfluence(vertex, importedModel, (int)jointIndicesSet1.X, weightsSet1.X);
                            AddInfluence(vertex, importedModel, (int)jointIndicesSet1.Y, weightsSet1.Y);
                            AddInfluence(vertex, importedModel, (int)jointIndicesSet1.Z, weightsSet1.Z);
                            AddInfluence(vertex, importedModel, (int)jointIndicesSet1.W, weightsSet1.W);
                        }
                    }
                    else
                    {
                        vertex.Influences.Add(new ImportedInfluence { JointNode = meshOwner, Weight = 1f });
                    }
                    if (game != null && game.JointWeights[0][vertexIndex].X >= 0)
                    {
                        vertex.Game = GameInfluences(importedModel, vertex, game, vertexIndex);
                        if (vertex.Game != null) importedModel.GameVertices++;
                        else importedModel.GameVerticesDropped++;
                    }
                    importedPrimitive.Vertices.Add(vertex);
                }

                foreach (var (corner0, corner1, corner2) in prim.GetTriangleIndices())
                    importedPrimitive.Triangles.Add((corner0, corner1, corner2));

                // KHR_texture_transform on the base colour (an atlas cell picked by scale/offset,
                // as DoD exports do): the GS samples the UVs as they are, so bake it into them.
                ApplyBaseTextureTransform(prim.Material, importedPrimitive);
                ApplyDotrImageUv(prim.Material, importedPrimitive);
                if (!importedModel.IsDotrExtractedGlb) ParsePlainAlpha(prim.Material, importedPrimitive, importedModel);

                /* Out-of-[0,1] UVs are folded into tiles only on axes the GS really clamps. The
                   extractor writes the page's GS CLAMP byte into the material name as "_cN"
                   (ModelExtractor's matName); a stage model's pages are usually REPEAT on both
                   axes, where tiling UVs reproduce exactly (4.12 UV reaches +-8). Folding them
                   would change the picture and duplicate every vertex straddling a tile, which
                   destroys the adjacency the stripifier needs (section 2/4 became one strip per
                   triangle on MODEL.MRG scenery). A glb without the tag folds both axes. */
                bool foldS = true, foldT = true;
                if (ParseGsClampTag(prim.Material?.Name, out int clampTag))
                {
                    foldS = (clampTag & 0x3) == 1;
                    foldT = ((clampTag >> 2) & 0x3) == 1;
                }
                WrapPrimitiveUvs(importedPrimitive, foldS, foldT);

                importedModel.Primitives.Add(importedPrimitive);
            }
        }

        /* Animated-texture frame sequences ("tex{N}_anim{frame}" image names). All embedded images
           are scanned, not only those a material references: Blender's glTF exporter can leave
           extra-frame images attached only via the Image datablock name. */
        {
            var framesByTexture = new Dictionary<int, List<(int frame, SharpGLTF.Memory.MemoryImage img)>>();
            var ticksByTexture = new Dictionary<int, int>();
            foreach (var image in model.LogicalImages)
            {
                if (TryParseAnimImageName(image.Name, out int textureIndex, out int frame, out int ticks) && image.Content.IsValid)
                {
                    // N is the author's mat_texN label: frames follow their slot's remap (empty for an extracted glb)
                    if (texLabelRemap.TryGetValue(textureIndex, out int denseTexture)) textureIndex = denseTexture;
                    /* An extracted glb's frames are views of the base image the retail records copy
                       from, not a flipbook to repack. They are kept aside so the records and the
                       texture size stay retail's; ApplyEditedAnimFrames pushes an edited frame
                       back into its source cell. */
                    if (importedModel.TextureAnimRecords.ContainsKey(textureIndex))
                    {
                        if (!importedModel.TextureAnimEditFrames.TryGetValue(textureIndex, out var editFrames))
                            importedModel.TextureAnimEditFrames[textureIndex] = editFrames = new();
                        editFrames[frame] = image.Content;
                        continue;
                    }
                    if (!framesByTexture.TryGetValue(textureIndex, out var frameList))
                        framesByTexture[textureIndex] = frameList = new();
                    frameList.Add((frame, image.Content));
                    if (ticks > 0) ticksByTexture[textureIndex] = ticks; // the last frame that gives one wins
                }
            }
            foreach (var (animatedTexture, frames) in framesByTexture)
            {
                if (frames.Count < 2) continue; // a single frame is a naming accident
                frames.Sort((first, second) => first.frame.CompareTo(second.frame));
                importedModel.TextureAnimFrames[animatedTexture] = frames.Select(frame => frame.img).ToList();
                if (ticksByTexture.TryGetValue(animatedTexture, out int ticks)) importedModel.TextureAnimTicks[animatedTexture] = ticks;
                // frame 0 is also the slot's static fallback image
                importedModel.TextureImages[animatedTexture] = frames[0].img;
            }
        }

        return importedModel;
    }

    public const float AnimFps = 30f;

    // Node.WorldMatrix rejects a node whose local matrix is not invertible; DotR models hide
    // parts with tiny or zero scales (card 83 nests 1e-5 per level down to ~1e-24).
    private static Matrix4x4 WorldOf(Node node) =>
        node.VisualParent == null ? node.LocalTransform.Matrix : node.LocalTransform.Matrix * WorldOf(node.VisualParent);

    private static List<(float Time, T Value)>? Resample<T>(SharpGLTF.Animations.ICurveSampler<T>? sampler, int frames)
    {
        if (sampler == null) return null;
        var samples = new List<(float, T)>(frames);
        for (int frame = 0; frame < frames; frame++)
        {
            float time = frame / AnimFps;
            samples.Add((time, sampler.GetPoint(time)));
        }
        return samples;
    }

    /// glb world matrix of the node at a frame of an animation (rest values where it has no
    /// channel); anim == null is the rest pose.
    public static Matrix4x4 WorldAt(ImportedModel importedModel, int glbIndex, ImportedAnimation? anim, int frame)
    {
        var node = importedModel.Nodes[glbIndex];
        var translation = node.Translation;
        var rotation = node.Rotation;
        var scale = node.Scale;
        if (anim != null && anim.Channels.TryGetValue(glbIndex, out var channels))
        {
            if (channels.Translation is { Count: > 0 } translationKeys) translation = translationKeys[Math.Min(frame, translationKeys.Count - 1)].Value;
            if (channels.Rotation is { Count: > 0 } rotationKeys) rotation = rotationKeys[Math.Min(frame, rotationKeys.Count - 1)].Rotation;
            if (channels.Scale is { Count: > 0 } scaleKeys) scale = scaleKeys[Math.Min(frame, scaleKeys.Count - 1)].Value;
        }
        var local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
        return node.Parent < 0 ? local : local * WorldAt(importedModel, node.Parent, anim, frame);
    }

    /// The rest world matrix if it is invertible, else the first animation frame's that is;
    /// null when the node is at scale 0 in every frame.
    private static Matrix4x4? FirstVisibleWorld(ImportedModel importedModel, int glbIndex, Matrix4x4 rest)
    {
        if (!IsSingular(rest)) return rest;
        foreach (var anim in importedModel.Animations)
            for (int frame = 0; frame < anim.FrameCount; frame++)
            {
                var world = WorldAt(importedModel, glbIndex, anim, frame);
                if (!IsSingular(world)) return world;
            }
        return null;
    }

    /// Scale 0 on some axis. Relative to the largest axis, since DotR worlds carry the model's
    /// root scale (often ~1e-3) and an absolute determinant test would trip on whole models.
    public static bool IsSingular(Matrix4x4 matrix)
    {
        float row1Length = new Vector3(matrix.M11, matrix.M12, matrix.M13).Length();
        float row2Length = new Vector3(matrix.M21, matrix.M22, matrix.M23).Length();
        float row3Length = new Vector3(matrix.M31, matrix.M32, matrix.M33).Length();
        float largest = Math.Max(row1Length, Math.Max(row2Length, row3Length));
        return !(largest > 0f) || Math.Min(row1Length, Math.Min(row2Length, row3Length)) < largest * 1e-6f;
    }

    /// Matches image names "tex{N}_anim{frame}" (case-insensitive) with an optional trailing
    /// "_tN" tick-rate suffix: "tex1_anim0", "tex1_anim0_t10", "TEX2_ANIM03_T3". N, frame and
    /// the tick count are plain non-negative integers. `ticks` is -1 when there is no suffix.
    private static bool TryParseAnimImageName(string? name, out int textureIndex, out int frame, out int ticks)
    {
        textureIndex = -1; frame = -1; ticks = -1;
        if (string.IsNullOrEmpty(name)) return false;
        int texPosition = name.IndexOf("tex", StringComparison.OrdinalIgnoreCase);
        if (texPosition < 0) return false;
        int texCursor = texPosition + 3, texStart = texCursor;
        while (texCursor < name.Length && char.IsDigit(name[texCursor])) texCursor++;
        if (texCursor == texStart) return false;
        if (!int.TryParse(name.AsSpan(texStart, texCursor - texStart), out textureIndex)) return false;

        int animPosition = name.IndexOf("anim", texCursor, StringComparison.OrdinalIgnoreCase);
        if (animPosition < 0) { textureIndex = -1; return false; }
        int frameCursor = animPosition + 4, frameStart = frameCursor;
        while (frameCursor < name.Length && char.IsDigit(name[frameCursor])) frameCursor++;
        if (frameCursor == frameStart) { textureIndex = -1; return false; }
        if (!int.TryParse(name.AsSpan(frameStart, frameCursor - frameStart), out frame)) { textureIndex = -1; return false; }

        // optional "_tN" suffix anywhere after the frame number
        int tickPosition = name.IndexOf("_t", frameCursor, StringComparison.OrdinalIgnoreCase);
        if (tickPosition >= 0)
        {
            int tickCursor = tickPosition + 2, tickStart = tickCursor;
            while (tickCursor < name.Length && char.IsDigit(name[tickCursor])) tickCursor++;
            if (tickCursor > tickStart && int.TryParse(name.AsSpan(tickStart, tickCursor - tickStart), out int parsedTicks))
                ticks = Math.Max(1, parsedTicks);
        }
        return true;
    }

    // Element-wise compare, tolerance relative to the matrices' largest entry (translations
    // can be large) so float noise in an inverse bind matrix never counts as a different bind.
    private static bool NearlyEqual(Matrix4x4 first, Matrix4x4 second)
    {
        float magnitude = 1f, difference = 0f;
        for (int row = 0; row < 4; row++)
        for (int column = 0; column < 4; column++)
        {
            magnitude = MathF.Max(magnitude, MathF.Max(MathF.Abs(first[row, column]), MathF.Abs(second[row, column])));
            difference = MathF.Max(difference, MathF.Abs(first[row, column] - second[row, column]));
        }
        return difference <= 1e-4f * magnitude;
    }

    internal sealed class GameData
    {
        public IList<Vector3> ReferencePositions = null!;
        public IList<Vector3>[] Positions = null!;
        public IList<Vector3>[] Normals = null!;
        public IList<Vector2>[] JointWeights = null!;
    }

    // ModelExtractor's per-influence attributes (VertexTexture1Game), or null without them.
    // A mesh carries 4 or 8 slots; slots are read from 0 up to the first absent one.
    internal static GameData? GameAccessors(MeshPrimitive primitive)
    {
        var referencePositions = primitive.GetVertexAccessor(VertexTexture1Game.RefPosName);
        if (referencePositions == null) return null;
        var positions = new List<IList<Vector3>>();
        var normals = new List<IList<Vector3>>();
        var jointWeights = new List<IList<Vector2>>();
        for (int k = 0; k < VertexTexture1Game.Slots; k++)
        {
            var positionAccessor = primitive.GetVertexAccessor(VertexTexture1Game.PosName + k);
            var normalAccessor = primitive.GetVertexAccessor(VertexTexture1Game.NrmName + k);
            var jointWeightAccessor = primitive.GetVertexAccessor(VertexTexture1Game.JointWeightName + k);
            if (positionAccessor == null && normalAccessor == null && jointWeightAccessor == null) break;
            if (positionAccessor == null || normalAccessor == null || jointWeightAccessor == null) return null;
            positions.Add(positionAccessor.AsVector3Array());
            normals.Add(normalAccessor.AsVector3Array());
            jointWeights.Add(jointWeightAccessor.AsVector2Array());
        }
        if (jointWeights.Count == 0) return null;
        return new GameData { ReferencePositions = referencePositions.AsVector3Array(), Positions = positions.ToArray(), Normals = normals.ToArray(), JointWeights = jointWeights.ToArray() };
    }

    /* The vertex's game influences, kept only while they still describe it: every joint is a
       `_bone` joint (encoded against its bind, so the stored bone-local values are what the
       importer would write), each joint has the same weight as in JOINTS/WEIGHTS, and POSITION
       is still where the extractor wrote it in the first influence's bone space (an edit made
       with the bind matrices, as ExtractedGlbEdit does, keeps that). Otherwise null. */
    private static List<StripWeight>? GameInfluences(ImportedModel importedModel, ImportedVertex vertex, GameData gameData, int vertexIndex)
    {
        var influences = new List<StripWeight>();
        var gameWeights = new Dictionary<int, float>();
        int weightSum = 0;
        for (int k = 0; k < gameData.JointWeights.Length && gameData.JointWeights[k][vertexIndex].X >= 0; k++)
        {
            int skinJoint = (int)gameData.JointWeights[k][vertexIndex].X;
            if (skinJoint >= importedModel.JointNodeIndices.Count) return null;
            int node = importedModel.JointNodeIndices[skinJoint];
            if (!importedModel.JointBind.ContainsKey(node) || importedModel.Nodes[node].ScaleBone < 0) return null;
            var position = gameData.Positions[k][vertexIndex];
            var normal = gameData.Normals[k][vertexIndex];
            short weight = (short)gameData.JointWeights[k][vertexIndex].Y;
            influences.Add(new StripWeight(node, weight, ((short)position.X, (short)position.Y, (short)position.Z), ((short)normal.X, (short)normal.Y, (short)normal.Z)));
            // JOINTS/WEIGHTS hold the positive weights normalised over their sum (retail has a few negative ones, card 291)
            if (weight <= 0) continue;
            gameWeights[node] = gameWeights.GetValueOrDefault(node) + weight;
            weightSum += weight;
        }
        var glbWeights = new Dictionary<int, float>();
        foreach (var influence in vertex.Influences)
        {
            glbWeights[influence.JointNode] = glbWeights.GetValueOrDefault(influence.JointNode) + influence.Weight;
        }
        if (!GameWeightsMatch(gameWeights, weightSum, glbWeights)) return null;
        if (!Matrix4x4.Invert(importedModel.JointBind[influences[0].Bone], out var inverse)) return null;
        if (!GameRefPosMatches(Vector3.Transform(vertex.Position, inverse), gameData.ReferencePositions[vertexIndex])) return null;
        return influences;
    }

    // The stored influences' positive raw weights (summing to weightSum) per joint against the
    // glb's JOINTS/WEIGHTS per joint: the same joints, each weight within 1e-3.
    internal static bool GameWeightsMatch(Dictionary<int, float> gameWeights, int weightSum, Dictionary<int, float> glbWeights)
    {
        if (weightSum <= 0) return false;
        foreach (var (joint, weight) in gameWeights)
        {
            if (weight > 0 && !glbWeights.ContainsKey(joint)) return false;
        }
        foreach (var (joint, glbWeight) in glbWeights)
        {
            if (MathF.Abs(glbWeight - gameWeights.GetValueOrDefault(joint) / weightSum) > 1e-3f) return false;
        }
        return true;
    }

    // POSITION taken into the first influence's bone space, still where the extractor wrote it.
    internal static bool GameRefPosMatches(Vector3 inBone, Vector3 referencePosition) =>
        Vector3.Distance(inBone, referencePosition) <= 0.01f + 1e-5f * referencePosition.Length();

    // JOINTS_0 holds skin-joint indices; map to the actual glb node index via the skin order.
    private static void AddInfluence(ImportedVertex vertex, ImportedModel importedModel, int skinJointIndex, float weight)
    {
        if (weight <= 0f) return;
        if (skinJointIndex < 0 || skinJointIndex >= importedModel.JointNodeIndices.Count) return;
        vertex.Influences.Add(new ImportedInfluence { JointNode = importedModel.JointNodeIndices[skinJointIndex], Weight = weight });
    }

    private static void ParseMaterial(Material? material, ImportedPrimitive primitive, ImportedModel importedModel)
    {
        if (material == null)
        {
            // No material: drawn like a flat-colour material, PRIM TME off on slot 0, so no texture
            // slot or slot-0 image is spent on it. Its page colour is the neutral grey MatKey maps White onto.
            primitive.TextureIndex = 0;
            primitive.Untextured = true;
            primitive.BaseColor = Vector4.One;
            return;
        }
        string name = material.Name ?? "";
        primitive.IsAlt = name.EndsWith("_alt", StringComparison.OrdinalIgnoreCase);
        /* A page the extractor wrote as "mat_untextured" is drawn with TME off and has no texture;
           the extractor clamps it to slot 0 (ModelExtractor's GetMaterial). It must not claim a
           texture slot: it would push the slot count past the template's RH2 count and
           RebuildPipeline's ApplyTexMerges would fold real textures into atlas bands (card 682:
           slots 6 and 7 merged onto slots 0 and 1, wrecking both). */
        bool untextured = IsUntexturedMaterial(name);
        primitive.Untextured = untextured;
        bool hasTexture = HasBaseColorTexture(material);
        int texPosition = name.IndexOf("mat_tex", StringComparison.OrdinalIgnoreCase);
        bool named = false;
        if (untextured)
        {
            primitive.TextureIndex = 0;
            named = true;
            texPosition = -1;
        }
        if (texPosition >= 0)
        {
            int start = texPosition + "mat_tex".Length, end = start;
            while (end < name.Length && char.IsDigit(name[end])) end++;
            if (end > start && int.TryParse(name.AsSpan(start, end - start), out int textureNumber))
            {
                // a custom glb's named slots are renumbered densely by the pre-pass in FromModel
                primitive.TextureIndex = importedModel.MatTexRemap.TryGetValue(material.LogicalIndex, out int dense) ? dense : textureNumber;
                named = true;
            }
        }
        if (!named && !hasTexture)
        {
            /* A custom glb's flat-colour material has no base-colour texture. It is treated like
               the extractor's `mat_untextured`: parked on slot 0, drawn with PRIM TME off, its base
               colour carried as the page colour (MatKey's c1). An auto slot would push the slot
               count past the template's RH2 count and make RebuildPipeline atlas-merge real
               textures (card 682). */
            untextured = true;
            primitive.Untextured = true;
            primitive.TextureIndex = 0;
            named = true;
        }
        if (!named)
        {
            // unnamed material: each distinct material gets its own slot (stable per LogicalIndex)
            if (!importedModel.MaterialTexAuto.TryGetValue(material.LogicalIndex, out int autoSlot))
            {
                var taken = new HashSet<int>(importedModel.MaterialTexAuto.Values);
                autoSlot = 0;
                while (taken.Contains(autoSlot)) autoSlot++;
                importedModel.MaterialTexAuto[material.LogicalIndex] = autoSlot;
            }
            primitive.TextureIndex = autoSlot;
        }
        /* The base colour factor feeds the RH0 material record's +0x20 row, a lighting input the
           engine scales at runtime (lRhModel_SetMaterialIntensity SET/ADD/MUL modes). Retail
           neutral is (178,178,178)/alpha 128, which the pipeline maps White onto. */
        primitive.BaseColor = Vector4.One;
        try
        {
            var baseColor = material.FindChannel("BaseColor");
            if (baseColor.HasValue) primitive.BaseColor = baseColor.Value.Color;
        }
        catch { }
        RestoreColourBlock(material, primitive);
        if (!importedModel.IsDotrExtractedGlb) ParseDotrColors(material, primitive);
        ParseBlend(material, name, primitive, importedModel, untextured);

        // Harvest the base-colour image for texture reimport; the first material seen per slot wins.
        try
        {
            if (primitive.TextureIndex >= 0 && !untextured)
            {
                if (hasTexture)
                {
                    if (!importedModel.TextureImages.ContainsKey(primitive.TextureIndex))
                    {
                        var image = material.FindChannel("BaseColor")!.Value.Texture!.PrimaryImage!.Content;
                        if (image.IsValid) importedModel.TextureImages[primitive.TextureIndex] = image;
                    }
                }
                else
                {
                    /* Flat-colour material: every page's texture-context record samples a texture,
                       so without an image the slot keeps the template's texture. The base colour
                       is baked into a small solid image and the page colour neutralised, so the
                       tint is not applied twice (texel * page colour). */
                    if (!importedModel.TextureImages.ContainsKey(primitive.TextureIndex))
                    {
                        var colour = primitive.BaseColor;
                        byte red = (byte)Math.Clamp((int)MathF.Round(colour.X * 255f), 0, 255);
                        byte green = (byte)Math.Clamp((int)MathF.Round(colour.Y * 255f), 0, 255);
                        byte blue = (byte)Math.Clamp((int)MathF.Round(colour.Z * 255f), 0, 255);
                        byte alpha = (byte)Math.Clamp((int)MathF.Round(colour.W * 255f), 0, 255);
                        importedModel.TextureImages[primitive.TextureIndex] = SolidImage(red, green, blue, alpha);
                    }
                    primitive.BaseColor = Vector4.One;
                }
            }
        }
        catch { /* texture harvest must never break geometry import */ }

        // A DotR-only copy of the texture (DoD exporter: animated mask positions baked into extra
        // cells the slot's records copy from) replaces the slot's base-colour image.
        try
        {
            if (primitive.TextureIndex >= 0 && !untextured && DotrImage(material) is { } dotrImage && dotrImage.Content.IsValid)
                importedModel.TextureImages[primitive.TextureIndex] = dotrImage.Content;
        }
        catch { }

        // The slot's retail texture animation on the extracted glb's material
        // (ModelExtractor.InjectTextureAnimationRecords); the assembler re-embeds it verbatim.
        try
        {
            if (primitive.TextureIndex >= 0 && !untextured && !importedModel.TextureAnimRecords.ContainsKey(primitive.TextureIndex)
                && material.Extras is System.Text.Json.Nodes.JsonObject extras
                && extras["DOTR_texAnimRecords"] is System.Text.Json.Nodes.JsonArray records && records.Count > 0)
                importedModel.TextureAnimRecords[primitive.TextureIndex] = PackTexAnimRecords(records);
        }
        catch { /* a malformed extras block just means no animation */ }

        // Record and cell each baked frame image came from (TextureAnimStamps / ApplyEditedAnimFrames).
        try
        {
            if (primitive.TextureIndex >= 0 && !untextured && !importedModel.TextureAnimStamps.ContainsKey(primitive.TextureIndex)
                && material.Extras is System.Text.Json.Nodes.JsonObject extras
                && extras["DOTR_texAnim"] is System.Text.Json.Nodes.JsonObject textureAnimation
                && textureAnimation["frames"] is System.Text.Json.Nodes.JsonArray frames)
            {
                var byFrame = new Dictionary<int, List<(int Rec, int Cell, uint Hash)>>();
                for (int frame = 0; frame < frames.Count; frame++)
                {
                    if (frames[frame] is not System.Text.Json.Nodes.JsonObject frameObject
                        || frameObject["stamps"] is not System.Text.Json.Nodes.JsonArray stamps) continue;
                    var list = new List<(int, int, uint)>();
                    foreach (var stamp in stamps)
                        if (stamp is System.Text.Json.Nodes.JsonObject stampObject)
                            list.Add(((int)stampObject["rec"]!, (int)stampObject["cell"]!, (uint)stampObject["hash"]!));
                    if (list.Count > 0) byFrame[frame] = list;
                }
                if (byFrame.Count > 0) importedModel.TextureAnimStamps[primitive.TextureIndex] = byFrame;
            }
        }
        catch { /* a malformed extras block just means no write-back */ }
    }

    /// uv' = offset + R(rotation) * (scale * uv), the KHR_texture_transform matrix, when the base
    /// colour texture has one and samples TEXCOORD_0 (the only set the importer reads).
    private static void ApplyBaseTextureTransform(Material? material, ImportedPrimitive primitive)
    {
        try
        {
            var channel = material?.FindChannel("BaseColor");
            if (channel is not { } baseChannel || baseChannel.Texture == null || baseChannel.TextureCoordinate != 0) return;
            var transform = baseChannel.TextureTransform;
            if (transform == null || (transform.TextureCoordinateOverride ?? 0) != 0) return;
            var scale = transform.Scale; var offset = transform.Offset; float rotation = transform.Rotation;
            if (scale == Vector2.One && offset == Vector2.Zero && rotation == 0f) return;
            float cosine = MathF.Cos(rotation), sine = MathF.Sin(rotation);
            foreach (var vertex in primitive.Vertices)
            {
                float scaledU = vertex.Uv.X * scale.X, scaledV = vertex.Uv.Y * scale.Y;
                vertex.Uv = new Vector2(cosine * scaledU + sine * scaledV + offset.X, -sine * scaledU + cosine * scaledV + offset.Y);
            }
        }
        catch { /* a malformed transform just leaves the UVs as they are */ }
    }

    /// The material's "DOTR_image" extras: a glb image to encode for its slot instead of the base colour.
    private static SharpGLTF.Schema2.Image? DotrImage(Material? material)
    {
        if (material?.Extras is not System.Text.Json.Nodes.JsonObject extras
            || extras["DOTR_image"] is not System.Text.Json.Nodes.JsonObject dotrImage
            || dotrImage["image"] is not { } imageNode) return null;
        int imageIndex = imageNode.GetValue<int>();
        var images = material.LogicalParent.LogicalImages;
        return imageIndex >= 0 && imageIndex < images.Count ? images[imageIndex] : null;
    }

    /// With a DOTR_image the base-colour texture sits at the top left of a larger image: its
    /// "uvScale" (base size / image size) maps the faces' UVs onto it, and an optional "uvOffset"
    /// (image UV) moves them to their own cell there (an atlas flipbook's display cell).
    private static void ApplyDotrImageUv(Material? material, ImportedPrimitive primitive)
    {
        try
        {
            if (DotrImage(material) == null
                || material!.Extras is not System.Text.Json.Nodes.JsonObject extras
                || extras["DOTR_image"]?["uvScale"] is not System.Text.Json.Nodes.JsonArray uvScale || uvScale.Count != 2) return;
            var uvScaleVector = new Vector2(uvScale[0]!.GetValue<float>(), uvScale[1]!.GetValue<float>());
            var offset = extras["DOTR_image"]?["uvOffset"] is System.Text.Json.Nodes.JsonArray offsetArray && offsetArray.Count == 2
                ? new Vector2(offsetArray[0]!.GetValue<float>(), offsetArray[1]!.GetValue<float>()) : Vector2.Zero;
            foreach (var vertex in primitive.Vertices) vertex.Uv = vertex.Uv * uvScaleVector + offset;
        }
        catch { }
    }

    /// True when the material's base-colour channel carries an actual image.
    private static bool HasBaseColorTexture(Material material)
    {
        try
        {
            var channel = material.FindChannel("BaseColor");
            return channel.HasValue && channel.Value.Texture?.PrimaryImage != null;
        }
        catch { return false; }
    }

    /// The extractor's name for a material with no texture (ModelExtractor.GetMaterial).
    private static bool IsUntexturedMaterial(string? name) =>
        name != null && name.StartsWith("mat_untextured", StringComparison.OrdinalIgnoreCase);

    /// Packs "DOTR_texAnimRecords" into the 24-byte entries SzModel_LoadTexAnim_Pipe
    /// reads (inverse of ModelExtractor.TexAnimRecords): flags@0 (bit0 last, bit1 own
    /// clock, bits 2-4 mode, bits 5+ direction), motions@1, cols|rows<<4 @2, state@3,
    /// p0@4, p1@6, size@8, src@0xC, dst@0x10; own clock @0x14 from "clock" (default zero).
    private static byte[] PackTexAnimRecords(System.Text.Json.Nodes.JsonArray records)
    {
        const int EntrySize = 0x18;
        var packed = new byte[records.Count * EntrySize];
        for (int i = 0; i < records.Count; i++)
        {
            if (records[i] is not System.Text.Json.Nodes.JsonObject record) continue;
            int entryStart = i * EntrySize;
            int IntField(string field) => record[field] is { } value ? value.GetValue<int>() : 0;
            int PairElement(string field, int index) => record[field] is System.Text.Json.Nodes.JsonArray array && array.Count > index
                ? array[index]!.GetValue<int>() : 0;
            bool ownClock = record["ownClock"] is { } ownClockNode && ownClockNode.GetValue<bool>();
            packed[entryStart] = (byte)((i == records.Count - 1 ? 1 : 0) | (ownClock ? 2 : 0)
                          | ((IntField("mode") & 7) << 2) | ((IntField("direction") & 7) << 5));
            packed[entryStart + 1] = (byte)IntField("motions");
            packed[entryStart + 2] = (byte)((IntField("cols") & 15) | ((IntField("rows") & 15) << 4));
            packed[entryStart + 3] = (byte)IntField("state");
            void WriteUInt16At(int offset, int wordValue) => Bin.WriteU16(packed, entryStart + offset, (ushort)wordValue);
            WriteUInt16At(0x04, IntField("p0"));
            WriteUInt16At(0x06, IntField("p1"));
            WriteUInt16At(0x08, PairElement("size", 0));
            WriteUInt16At(0x0A, PairElement("size", 1));
            WriteUInt16At(0x0C, PairElement("src", 0));
            WriteUInt16At(0x0E, PairElement("src", 1));
            WriteUInt16At(0x10, PairElement("dst", 0));
            WriteUInt16At(0x12, PairElement("dst", 1));
            // own clock's starting value: phases a blink record (mode 6 copies when clock % p0 == 0)
            WriteU32(packed, entryStart + 0x14, (uint)IntField("clock"));
        }
        return packed;
    }

    /// Restores the colour block (c0, c1, c2) the extractor saved in DOTR_blend. c0 and c2
    /// have no glb field and are always restored. The base colour only displays c1 (an
    /// alpha-chain material shows c0 + c1/2, an opaque neutral or zero c1 shows white), so
    /// while it is still the one the extractor wrote, the raw c1 is restored too.
    private static void RestoreColourBlock(Material material, ImportedPrimitive primitive)
    {
        try
        {
            if (material.Extras is not System.Text.Json.Nodes.JsonObject extras) return;
            if (extras["DOTR_blend"] is not System.Text.Json.Nodes.JsonObject blend) return;
            static (int, int, int)? ReadRgb(System.Text.Json.Nodes.JsonNode? node) =>
                node is System.Text.Json.Nodes.JsonArray array && array.Count == 3
                    ? (array[0]!.GetValue<int>(), array[1]!.GetValue<int>(), array[2]!.GetValue<int>()) : null;
            if (ReadRgb(blend["materialColor0"]) is { } color0) primitive.Color0 = color0;
            if (ReadRgb(blend["materialColor2"]) is { } color2) primitive.Color2 = color2;
            if (blend["displayColor"] is not System.Text.Json.Nodes.JsonArray shown || shown.Count != 3) return;
            var baseColor = primitive.BaseColor;
            const float tolerance = 1.5f / 255f;
            if (MathF.Abs(baseColor.X - shown[0]!.GetValue<float>()) > tolerance ||
                MathF.Abs(baseColor.Y - shown[1]!.GetValue<float>()) > tolerance ||
                MathF.Abs(baseColor.Z - shown[2]!.GetValue<float>()) > tolerance) return; // edited: keep it
            primitive.RawColor1 = (blend["materialColorR"]!.GetValue<int>(),
                            blend["materialColorG"]!.GetValue<int>(),
                            blend["materialColorB"]!.GetValue<int>());
            // an opaque page's alpha still feeds its alpha test (card 53's alpha-0 untextured pages are
            // never drawn): restore the raw c1.w unless the glb alpha was edited
            if (blend["materialAlpha"] is { } materialAlpha && MathF.Abs(baseColor.W - materialAlpha.GetValue<float>()) <= tolerance
                && blend["materialAlphaW"] is { } alphaW)
                primitive.BaseColor = baseColor with { W = alphaW.GetValue<float>() / 128f };
        }
        catch { }
    }

    /// A custom glb's "DOTR_colors" extras {c0, c1}: the material record's c0 (+0x10) and c1 (+0x20)
    /// rows, 0-255 (the DoD exporter writes DoD's ambient and diffuse: neutral 127 / 178).
    private static void ParseDotrColors(Material material, ImportedPrimitive primitive)
    {
        try
        {
            if (material.Extras is not System.Text.Json.Nodes.JsonObject extras
                || extras["DOTR_colors"] is not System.Text.Json.Nodes.JsonObject colors) return;
            static (int, int, int)? ReadRgb(System.Text.Json.Nodes.JsonNode? node) =>
                node is System.Text.Json.Nodes.JsonArray array && array.Count == 3
                    ? (Math.Clamp(array[0]!.GetValue<int>(), 0, 255), Math.Clamp(array[1]!.GetValue<int>(), 0, 255),
                       Math.Clamp(array[2]!.GetValue<int>(), 0, 255)) : null;
            if (ReadRgb(colors["c0"]) is { } color0) primitive.Color0 = color0;
            if (ReadRgb(colors["c1"]) is { } color1) primitive.RawColor1 = color1;
        }
        catch { }
    }

    /// The GS blend. An extracted glb names it ("_gs48" = ALPHA_1 0x48, additive). A custom glb
    /// marks it in DOTR_blend {"mode": "additive"} without the extractor's raw "gsAlpha" (the DoD
    /// exporter does): drawn in the alpha chain with retail Ameba's additive colour block
    /// (c1 178, c0 127, full alpha), its texture alpha dropped (see OpaqueAlphaSlots).
    private static void ParseBlend(Material material, string name, ImportedPrimitive primitive, ImportedModel importedModel, bool untextured)
    {
        var testMatch = System.Text.RegularExpressions.Regex.Match(name, @"_t([0-9A-Fa-f]{5})(?:_|$)");
        if (testMatch.Success) primitive.GsTest = Convert.ToUInt32(testMatch.Groups[1].Value, 16);
        var gsMatch = System.Text.RegularExpressions.Regex.Match(name, @"_gs([0-9A-Fa-f]{2})(?:_|$)");
        if (gsMatch.Success) { primitive.GsAlpha = Convert.ToByte(gsMatch.Groups[1].Value, 16); return; }
        try
        {
            if (material.Extras is not System.Text.Json.Nodes.JsonObject extras
                || extras["DOTR_blend"] is not System.Text.Json.Nodes.JsonObject blend
                || blend["gsAlpha"] != null
                || blend["mode"]?.GetValue<string>() != "additive") return;
            primitive.IsAlt = true;
            primitive.GsAlpha = 0x48;
            primitive.RawColor1 ??= (178, 178, 178);
            primitive.BaseColor = primitive.BaseColor with { W = 1f };
            if (!untextured && primitive.TextureIndex >= 0) importedModel.OpaqueAlphaSlots.Add(primitive.TextureIndex);
        }
        catch { }
    }

    /// A custom glb's translucent material without a DotR blend of its own (alphaMode BLEND or
    /// MASK, alpha in the texture and/or the base colour factor). DotR's alpha is one value per
    /// page (c1.w, 128 = 1.0) times a 1-bit texture alpha, so the texture alpha under this
    /// primitive's triangles is sampled: no translucent texels -> a cutout (TEST 0x703FD, AREF 63)
    /// if it has holes, else opaque; otherwise the alpha chain (normal blend) with c1.w = the mean
    /// alpha of the covered texels and the texture alpha cut to coverage (CoverageAlphaSlots).
    private static void ParsePlainAlpha(Material? material, ImportedPrimitive primitive, ImportedModel importedModel)
    {
        try
        {
            if (material == null || primitive.Untextured || primitive.IsAlt || primitive.GsTest != 0 || primitive.GsAlpha != 0x44) return;
            if (material.Alpha == AlphaMode.OPAQUE) return;
            if (material.Extras is System.Text.Json.Nodes.JsonObject extras
                && extras["DOTR_blend"] is System.Text.Json.Nodes.JsonObject blend
                && (blend["gsAlpha"] != null || blend["mode"]?.GetValue<string>() is "additive" or "multiply")) return;
            float factor = Math.Clamp(primitive.BaseColor.W, 0f, 1f);
            var plane = AlphaPlane(material, importedModel);
            long sampleCount = 0, holes = 0; double sum = 0;
            if (plane is { } alphaPlane)
                foreach (byte alpha in SampleAlpha(primitive, alphaPlane.W, alphaPlane.H, alphaPlane.A))
                {
                    sampleCount++;
                    if (alpha < 8) holes++; else sum += alpha / 255.0;
                }
            float mean = sampleCount > holes ? (float)(sum / (sampleCount - holes)) : 1f;
            if (material.Alpha == AlphaMode.MASK) mean = 1f;   // a cutout: coverage only
            float effectiveAlpha = mean * factor;
            if (effectiveAlpha >= 0.97f)
            {
                primitive.BaseColor = primitive.BaseColor with { W = 1f };
                if (holes == 0) return;
                primitive.GsTest = 0x703FD;
            }
            else
            {
                primitive.IsAlt = true;
                // retail's alpha-chain pages keep the neutral c1 (178); MatKey gives alt pages full range
                if (primitive.BaseColor.X >= 0.9999f && primitive.BaseColor.Y >= 0.9999f && primitive.BaseColor.Z >= 0.9999f)
                    primitive.RawColor1 ??= (178, 178, 178);
                primitive.BaseColor = primitive.BaseColor with { W = MathF.Max(effectiveAlpha, 1f / 128f) };
            }
            if (primitive.TextureIndex >= 0) importedModel.CoverageAlphaSlots.Add(primitive.TextureIndex);
        }
        catch { /* leave the page opaque */ }
    }

    /// The alpha plane of the material's base-colour image (null: no image or undecodable).
    private static (int W, int H, byte[] A)? AlphaPlane(Material material, ImportedModel importedModel)
    {
        // a DOTR_image replaces the base colour for the slot, and the faces' UVs already point into it
        var glbImage = DotrImage(material) ?? material.FindChannel("BaseColor")?.Texture?.PrimaryImage;
        if (glbImage == null) return null;
        if (importedModel.AlphaPlanes.TryGetValue(glbImage.LogicalIndex, out var cached)) return cached;
        (int, int, byte[])? result = null;
        var image = glbImage.Content;
        if (image.IsValid)
        {
            Image decoded = DecodeRgba(image);
            if (decoded.Width > 0 && decoded.Height > 0)
            {
                var rgba = Pixels(decoded);
                var alphas = new byte[decoded.Width * decoded.Height];
                for (int i = 0; i < alphas.Length; i++) alphas[i] = rgba[i * 4 + 3];
                result = (decoded.Width, decoded.Height, alphas);
            }
            Raylib.UnloadImage(decoded);
        }
        importedModel.AlphaPlanes[glbImage.LogicalIndex] = result;
        return result;
    }

    /// Texel alphas under the primitive's triangles (repeat-wrapped UVs, about 1k samples per
    /// triangle at most, its centroid when it covers no texel centre).
    private static IEnumerable<byte> SampleAlpha(ImportedPrimitive primitive, int width, int height, byte[] alphas)
    {
        byte AlphaAt(float x, float y)
        {
            int texelX = ((int)MathF.Floor(x) % width + width) % width, texelY = ((int)MathF.Floor(y) % height + height) % height;
            return alphas[texelY * width + texelX];
        }
        foreach (var (corner0, corner1, corner2) in primitive.Triangles)
        {
            Vector2 uv0 = primitive.Vertices[corner0].Uv * new Vector2(width, height), uv1 = primitive.Vertices[corner1].Uv * new Vector2(width, height),
                    uv2 = primitive.Vertices[corner2].Uv * new Vector2(width, height);
            float minX = MathF.Min(uv0.X, MathF.Min(uv1.X, uv2.X)), maxX = MathF.Max(uv0.X, MathF.Max(uv1.X, uv2.X));
            float minY = MathF.Min(uv0.Y, MathF.Min(uv1.Y, uv2.Y)), maxY = MathF.Max(uv0.Y, MathF.Max(uv1.Y, uv2.Y));
            float area = (uv1.X - uv0.X) * (uv2.Y - uv0.Y) - (uv2.X - uv0.X) * (uv1.Y - uv0.Y);
            bool sampledAny = false;
            if (MathF.Abs(area) > 1e-6f && (maxX - minX) * (maxY - minY) < 1e8f)
            {
                float step = MathF.Max(1f, MathF.Sqrt((maxX - minX) * (maxY - minY) / 1024f));
                for (float y = MathF.Floor(minY) + 0.5f; y <= maxY; y += step)
                for (float x = MathF.Floor(minX) + 0.5f; x <= maxX; x += step)
                {
                    float barycentric1 = ((x - uv0.X) * (uv2.Y - uv0.Y) - (uv2.X - uv0.X) * (y - uv0.Y)) / area;
                    float barycentric2 = ((uv1.X - uv0.X) * (y - uv0.Y) - (x - uv0.X) * (uv1.Y - uv0.Y)) / area;
                    if (barycentric1 < 0 || barycentric2 < 0 || barycentric1 + barycentric2 > 1) continue;
                    sampledAny = true;
                    yield return AlphaAt(x, y);
                }
            }
            if (!sampledAny) { var centroid = (uv0 + uv1 + uv2) / 3f; yield return AlphaAt(centroid.X, centroid.Y); }
        }
    }

    /// 8x8 single-colour PNG wrapped as a glTF MemoryImage (for textureless materials).
    private static SharpGLTF.Memory.MemoryImage SolidImage(byte red, byte green, byte blue, byte alpha)
    {
        var rgba = new byte[8 * 8 * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        { rgba[i] = red; rgba[i + 1] = green; rgba[i + 2] = blue; rgba[i + 3] = alpha; }
        return new SharpGLTF.Memory.MemoryImage(PngEncoder.Encode(8, 8, rgba));
    }

    /// Leaves a model that the game loads and animates like any other but that draws nothing:
    /// one triangle whose three corners are copies of one vertex (zero area, so the GS fills no
    /// pixel), on one fully transparent 8x8 texture. The node tree and the motions are kept, so
    /// every motion slot, node 1 (cameras, shadow) and the battle node refs stay as they were.
    /// The corners are three vertices, not one used three times: RebuildPipeline skips a
    /// triangle that repeats an index.
    public static void RemoveModel(ImportedModel model)
    {
        ImportedPrimitive? keptPrimitive = model.Primitives.FirstOrDefault(primitive => primitive.Triangles.Count > 0);
        if (keptPrimitive == null)
        {
            throw new InvalidOperationException("the model has no triangles to keep");
        }
        ImportedVertex source = keptPrimitive.Vertices[keptPrimitive.Triangles[0].First];
        var corners = new List<ImportedVertex>();
        for (int corner = 0; corner < 3; corner++)
        {
            corners.Add(new ImportedVertex
            {
                Position = source.Position,
                FramePos = source.FramePos,
                Normal = source.Normal,
                Uv = source.Uv,
                Influences = new List<ImportedInfluence>(source.Influences),
                Game = source.Game,
            });
        }
        keptPrimitive.Vertices = corners;
        keptPrimitive.Triangles = new List<(int First, int Second, int Third)> { (0, 1, 2) };
        keptPrimitive.TextureIndex = 0;
        model.Primitives = new List<ImportedPrimitive> { keptPrimitive };

        model.TextureImages = new Dictionary<int, SharpGLTF.Memory.MemoryImage> { [0] = SolidImage(0, 0, 0, 0) };
        model.TextureAnimFrames.Clear();
        model.TextureAnimTicks.Clear();
        model.TextureAnimUvScale.Clear();
        model.TextureAnimRecords.Clear();
        model.TextureAnimEditFrames.Clear();
        model.TextureAnimStamps.Clear();
        model.MaterialTexAuto.Clear();
        model.MatTexRemap.Clear();
        model.OpaqueAlphaSlots.Clear();
        model.CoverageAlphaSlots.Clear();
        model.AppliedTexMerges = null;
    }

    /// Slot-sharing key for unnamed materials: same base-colour image => same slot;
    /// flat-colour materials key by their colour value.
    private static string MaterialShareKey(Material material)
    {
        try
        {
            var channel = material.FindChannel("BaseColor");
            if (channel.HasValue && channel.Value.Texture?.PrimaryImage != null)
                return "img:" + channel.Value.Texture.PrimaryImage.LogicalIndex;
            if (channel.HasValue)
            {
                var colour = channel.Value.Color;
                return $"rgba:{colour.X:F4},{colour.Y:F4},{colour.Z:F4},{colour.W:F4}";
            }
        }
        catch { }
        return "mat:" + material.LogicalIndex;
    }

    /// Highest texture slot a "mat_texN" name may ask for (RH2 blocks per model); retail's
    /// largest monster is far below it, so a higher number is a typo.
    private const int MaxTexSlot = 63;

    /// Parses the explicit "mat_texN" naming convention (suffix "_alt" allowed elsewhere).
    private static bool TryParseMatTexIndex(string? name, out int textureIndex)
    {
        textureIndex = -1;
        if (string.IsNullOrEmpty(name)) return false;
        int texPosition = name.IndexOf("mat_tex", StringComparison.OrdinalIgnoreCase);
        if (texPosition < 0) return false;
        int start = texPosition + "mat_tex".Length, end = start;
        while (end < name.Length && char.IsDigit(name[end])) end++;
        return end > start && int.TryParse(name.AsSpan(start, end - start), out textureIndex);
    }

    /// The GS CLAMP byte the extractor encodes in a material name as "_c<hex>" (see
    /// ModelExtractor's matName: "..._gsXX_tXXXXX_cN[_rgb..][_k..]"). Bits 0-1 are WMS and
    /// bits 2-3 WMT; 1 means CLAMP, 0 REPEAT. False when the name carries no such tag.
    private static bool ParseGsClampTag(string? name, out int clamp)
    {
        clamp = 0;
        if (string.IsNullOrEmpty(name)) return false;
        int tagPosition = name.IndexOf("_c", StringComparison.Ordinal);
        if (tagPosition < 0) return false;
        int start = tagPosition + 2, end = start;
        while (end < name.Length && Uri.IsHexDigit(name[end])) end++;
        if (end == start || end - start > 2) return false;
        if (end < name.Length && name[end] != '_') return false;
        clamp = Convert.ToInt32(name.Substring(start, end - start), 16);
        return true;
    }

    private static Vector3 SafeNormalize(Vector3 vector)
    {
        float length = vector.Length();
        return length > 1e-6f ? vector / length : Vector3.UnitY;
    }

    /// Folds out-of-[0,1] UVs back into the unit tile (glTF/Blender REPEAT semantics) so the
    /// PS2's CLAMP sampler reproduces the tiled result instead of crushing the UV onto a
    /// texture edge (Charizard's black face/wings). Done per triangle with the floor of its UV
    /// centroid, so all three corners shift by the same whole-tile offset. Vertices shared by
    /// triangles in different tiles are duplicated, since they need different shifts.
    private static void WrapPrimitiveUvs(ImportedPrimitive primitive, bool foldS = true, bool foldT = true)
    {
        if (!foldS && !foldT) return;
        bool anyOutside = false;
        foreach (var vertex in primitive.Vertices)
            if ((foldS && (vertex.Uv.X < 0f || vertex.Uv.X >= 1f)) || (foldT && (vertex.Uv.Y < 0f || vertex.Uv.Y >= 1f))) { anyOutside = true; break; }
        if (!anyOutside) return;

        var newTriangles = new List<(int First, int Second, int Third)>(primitive.Triangles.Count);
        // (vertexIndex, tileX, tileY) -> resolved vertex index in the rewritten list
        var remap = new Dictionary<(int, int, int), int>();

        int Resolve(int vertexIndex, int tileX, int tileY)
        {
            var key = (vertexIndex, tileX, tileY);
            if (remap.TryGetValue(key, out int resolved)) return resolved;
            var source = primitive.Vertices[vertexIndex];
            int outIndex;
            if (tileX == 0 && tileY == 0)
            {
                outIndex = vertexIndex;
            }
            else
            {
                var duplicate = new ImportedVertex
                {
                    Position = source.Position, FramePos = source.FramePos, Normal = source.Normal,
                    Uv = new Vector2(source.Uv.X - tileX, source.Uv.Y - tileY),
                    Influences = source.Influences,   // share (read-only downstream)
                    Game = source.Game,
                };
                outIndex = primitive.Vertices.Count;
                primitive.Vertices.Add(duplicate);
            }
            remap[key] = outIndex;
            return outIndex;
        }

        foreach (var (corner0, corner1, corner2) in primitive.Triangles)
        {
            var uv0 = primitive.Vertices[corner0].Uv; var uv1 = primitive.Vertices[corner1].Uv; var uv2 = primitive.Vertices[corner2].Uv;
            float centroidU = (uv0.X + uv1.X + uv2.X) / 3f;
            float centroidV = (uv0.Y + uv1.Y + uv2.Y) / 3f;
            int tileX = foldS ? (int)MathF.Floor(centroidU) : 0, tileY = foldT ? (int)MathF.Floor(centroidV) : 0;
            newTriangles.Add((Resolve(corner0, tileX, tileY), Resolve(corner1, tileX, tileY), Resolve(corner2, tileX, tileY)));
        }
        primitive.Triangles = newTriangles;

        // fold tile-(0,0) originals still outside [0,1] (a vertex at exactly 1.0, or one authored at 1.x)
        foreach (var vertex in primitive.Vertices)
        {
            float wrappedU = vertex.Uv.X, wrappedV = vertex.Uv.Y;
            if (foldS)
            {
                wrappedU = vertex.Uv.X - MathF.Floor(vertex.Uv.X);
                if (wrappedU == 0f && vertex.Uv.X != 0f) wrappedU = 1f;
            }
            if (foldT)
            {
                wrappedV = vertex.Uv.Y - MathF.Floor(vertex.Uv.Y);
                if (wrappedV == 0f && vertex.Uv.Y != 0f) wrappedV = 1f;
            }
            vertex.Uv = new Vector2(wrappedU, wrappedV);
        }
    }

    /// Decodes the glb's embedded base-colour images (per "mat_texN" texture index) into raw
    /// RGBA TextureInputs for the RH2 re-encoder, with the same stb_image decode as the model
    /// viewer (DecodeRgba). A texture that fails to decode is skipped (its slot gets a flat
    /// white texture). Each frame is capped at Rh0Assembler.MaxTextureDim, the cap every native
    /// texture is built under (Rh2Encoder.ChooseTarget). This matters most for animated slots:
    /// a flipbook sheet's width grows with frame count, so an uncapped 1024px source makes a
    /// 4-frame sheet of 4096x1024 (~4 MB encoded), which fits neither a fixed 1 MiB monster.mrg
    /// slot nor a typical MODEL.MRG model's budget.
    public static List<Rh0Assembler.TextureInput> DecodeGlbTextures(ImportedModel model)
    {
        const int maxDimension = Rh0Assembler.MaxTextureDim;
        var inputs = new List<Rh0Assembler.TextureInput>();
        var animSlots = model.TextureAnimFrames.Keys.ToHashSet();

        foreach (var (textureIndex, image) in model.TextureImages)
        {
            if (animSlots.Contains(textureIndex)) continue; // animated slots are packed below
            try
            {
                Image decoded = DecodeRgba(image);
                if (decoded.Width <= 0 || decoded.Height <= 0) continue;
                byte[] rgba = Pixels(decoded);
                ApplyEditedAnimFrames(model, textureIndex, rgba, decoded.Width, decoded.Height);
                if (model.OpaqueAlphaSlots.Contains(textureIndex))
                    for (int alphaIndex = 3; alphaIndex < rgba.Length; alphaIndex += 4) rgba[alphaIndex] = 255;
                else if (model.CoverageAlphaSlots.Contains(textureIndex))
                    for (int alphaIndex = 3; alphaIndex < rgba.Length; alphaIndex += 4) rgba[alphaIndex] = rgba[alphaIndex] >= 8 ? (byte)255 : (byte)0;
                inputs.Add(new Rh0Assembler.TextureInput {
                    TextureIndex = textureIndex,
                    Rgba = rgba,
                    Width = decoded.Width,
                    Height = decoded.Height,
                    AnimationRecords = model.TextureAnimRecords.TryGetValue(textureIndex, out var records) ? records : null,
                    AnimRecordsInImagePixels = !model.IsDotrExtractedGlb,
                });
                Raylib.UnloadImage(decoded);
            }
            catch
            {
                /* skip this texture; the template RH2 stays */
            }
        }

        /* Animated slots: every frame is decoded and packed into one sprite sheet. The game plays
           a flipbook by copying cell i of a cols x rows grid onto cell 0 every draw
           (Rh0Assembler.BuildCustomTexAnimEntry), so the sheet is that grid with frame 0 in
           cell 0 and the faces sample cell 0 only: TextureAnimUvScale tells RebuildPipeline how
           far to squeeze their UVs. Every frame is resized to frame 0's size (clamped to
           maxDimension), then halved until a grid of at most 15 x 15 cells fits
           MaxAnimSheetPixels (TryAnimSheetLayout). */
        foreach (var (animatedTexture, frames) in model.TextureAnimFrames)
        {
            var images = new List<Image>();
            try
            {
                foreach (var frame in frames)
                {
                    if (images.Count == Rh0Assembler.MaxAnimFrames)
                    {
                        model.TexSlotWarnings.Add($"texture slot {animatedTexture}: {frames.Count} animation frames, " +
                            $"only the first {Rh0Assembler.MaxAnimFrames} are used");
                        break;
                    }
                    Image decoded = DecodeRgba(frame);
                    if (decoded.Width <= 0 || decoded.Height <= 0) continue;
                    images.Add(decoded);
                }
                if (images.Count < 2) continue;

                int frameCount = images.Count;
                int frameWidth = images[0].Width, frameHeight = images[0].Height;
                if (frameWidth > maxDimension || frameHeight > maxDimension)
                {
                    float scale = Math.Min((float)maxDimension / frameWidth, (float)maxDimension / frameHeight);
                    frameWidth = Math.Max(1, (int)Math.Round(frameWidth * scale));
                    frameHeight = Math.Max(1, (int)Math.Round(frameHeight * scale));
                }
                AnimSheetLayout layout;
                while (!TryAnimSheetLayout(frameCount, frameWidth, frameHeight, out layout))
                {
                    if (frameWidth == 1 && frameHeight == 1) break;
                    frameWidth = Math.Max(1, frameWidth / 2);
                    frameHeight = Math.Max(1, frameHeight / 2);
                }
                if (layout.Cols == 0)
                {
                    model.TexSlotWarnings.Add($"texture slot {animatedTexture}: {frameCount} animation frames do not fit one texture; not animated");
                    continue;
                }
                if (images.Any(frameImage => frameImage.Width != images[0].Width || frameImage.Height != images[0].Height))
                    model.TexSlotWarnings.Add($"texture slot {animatedTexture}: animation frames differ in size; all resized to frame 0's");

                // POT cells (like retail's) hold the frame at their top-left; cells past the last frame repeat it.
                int sheetWidth = layout.SheetW, sheetHeight = layout.SheetH, cellWidth = layout.CellW, cellHeight = layout.CellH;
                byte[] sheet = new byte[sheetWidth * sheetHeight * 4];
                byte[] cell = Array.Empty<byte>();
                for (int cellIndex = 0; cellIndex < layout.Cols * layout.Rows; cellIndex++)
                {
                    if (cellIndex < frameCount)
                    {
                        Image frameImage = images[cellIndex];
                        if (frameImage.Width != frameWidth || frameImage.Height != frameHeight)
                        {
                            Raylib.ImageResize(ref frameImage, frameWidth, frameHeight);
                            images[cellIndex] = frameImage;
                        }
                        cell = Pixels(frameImage);
                    }
                    int cellX = (cellIndex % layout.Cols) * cellWidth, cellY = (cellIndex / layout.Cols) * cellHeight;
                    for (int y = 0; y < frameHeight; y++)
                        Buffer.BlockCopy(cell, y * frameWidth * 4, sheet, ((cellY + y) * sheetWidth + cellX) * 4, frameWidth * 4);
                }

                inputs.Add(new Rh0Assembler.TextureInput {
                    TextureIndex = animatedTexture,
                    Rgba = sheet,
                    Width = sheetWidth,
                    Height = sheetHeight,
                    AnimationFrameCount = layout.Cols * layout.Rows,
                    AnimationColumns = layout.Cols,
                    AnimationRows = layout.Rows,
                    AnimationBlockWidth = cellWidth,
                    AnimationBlockHeight = cellHeight,
                    AnimationTicksPerFrame = model.TextureAnimTicks.TryGetValue(animatedTexture, out int customTicks)
                        ? customTicks
                        : 5, // the one retail sample's rate
                });
                model.TextureAnimUvScale[animatedTexture] = new Vector2((float)frameWidth / sheetWidth, (float)frameHeight / sheetHeight);
            }
            catch
            {
                /* no TextureInput for this slot: the template's texture and animation stay */
            }
            finally
            {
                foreach (var loadedImage in images) Raylib.UnloadImage(loadedImage);
            }
        }

        return inputs;
    }

    private struct AnimSheetLayout
    {
        public int Cols, Rows, CellW, CellH, SheetW, SheetH;
    }

    /// A sprite sheet is encoded at exactly its packed size (Rh0Assembler never reduces it), so it
    /// must fit the 1 MiB block on its own: 512 x 512 texels, 256 KB at 8 bpp, the same ceiling
    /// as Rh0Assembler.MaxTextureDim for a static texture. (A 512 x 1024 sheet overflowed a
    /// monster slot by 13 KB.)
    private const long MaxAnimSheetPixels = 512 * 512;

    /// Smallest grid of POT cells for frameCount frames of frameWidth x frameHeight: at most 15 x 15 cells (the entry's
    /// nibbles), the sheet POT with width >= 128 and height >= 64 (Rh2Encoder.BuildNative), at
    /// most 1024 on either side (the GS limit) and MaxAnimSheetPixels in all. Cols = 0 when
    /// nothing fits.
    private static bool TryAnimSheetLayout(int frameCount, int frameWidth, int frameHeight, out AnimSheetLayout best)
    {
        best = default;
        int cellWidth = Rh2Encoder.NextPot(frameWidth), cellHeight = Rh2Encoder.NextPot(frameHeight);
        long bestArea = long.MaxValue;
        for (int cols = 1; cols <= 15; cols++)
        {
            int rows = (frameCount + cols - 1) / cols;
            if (rows > 15) continue;
            int sheetWidth = Rh2Encoder.NextPot(Math.Max(cellWidth * cols, 128));
            int sheetHeight = Rh2Encoder.NextPot(Math.Max(cellHeight * rows, 64));
            if (sheetWidth > 1024 || sheetHeight > 1024 || (long)sheetWidth * sheetHeight > MaxAnimSheetPixels) continue;
            // fewer spare cells first (they repeat the last frame), then the smaller sheet
            long area = (long)(cols * rows - frameCount) << 32 | (long)sheetWidth * sheetHeight;
            if (area < bestArea)
            {
                bestArea = area;
                best = new AnimSheetLayout { Cols = cols, Rows = rows, CellW = cellWidth, CellH = cellHeight, SheetW = sheetWidth, SheetH = sheetHeight };
            }
        }
        return best.Cols > 0;
    }

    /// Folds an edited animation frame of an extracted glb back into the base image.
    /// An animated retail slot is one texture: each record makes the GS copy cell i of a
    /// cols x rows sheet inside it onto a fixed destination rectangle of the same texture
    /// (TexAnim), so a frame image is the base image with those cells stamped in. Repainting a
    /// cell in the base image already works; a repainted frame is mapped back to the cell the
    /// game copies it from. The extractor's per-frame stamps give the record and cell that made
    /// each destination rectangle and its pixel hash at extraction: an unchanged hash means the
    /// frame was not touched and the base image's own (possibly edited) cell wins; a changed
    /// hash means the frame was painted and its pixels replace the cell. The records stay
    /// untouched.
    private static void ApplyEditedAnimFrames(ImportedModel model, int textureIndex, byte[] rgba, int width, int height)
    {
        if (!model.TextureAnimEditFrames.TryGetValue(textureIndex, out var frames)) return;
        if (!model.TextureAnimStamps.TryGetValue(textureIndex, out var stampsByFrame)) return;
        if (!model.TextureAnimRecords.TryGetValue(textureIndex, out var records)) return;

        foreach (var (frame, image) in frames)
        {
            if (!stampsByFrame.TryGetValue(frame, out var stamps)) continue;
            try
            {
                Image decoded = DecodeRgba(image);
                try
                {
                    // record rectangles are in the retail texture's pixel space: a resized frame cannot be mapped back
                    if (decoded.Width != width || decoded.Height != height) continue;
                    byte[] framePixels = Pixels(decoded);

                    for (int k = 0; k < stamps.Count; k++)
                    {
                        var (record, cell, hash) = stamps[k];
                        int recordOffset = record * 0x18;
                        if (recordOffset + 0x18 > records.Length) continue;
                        int cols = Math.Max(1, records[recordOffset + 2] & 0xF);
                        int cellWidth = ReadU16(records, recordOffset + 0x08), cellHeight = ReadU16(records, recordOffset + 0x0A);
                        int sourceX = ReadU16(records, recordOffset + 0x0C) + cellWidth * (cell % cols);
                        int sourceY = ReadU16(records, recordOffset + 0x0E) + cellHeight * (cell / cols);
                        int destinationX = ReadU16(records, recordOffset + 0x10), destinationY = ReadU16(records, recordOffset + 0x12);
                        if (cellWidth <= 0 || cellHeight <= 0) continue;
                        if (ModelExtractor.RectHash(framePixels, width, height, destinationX, destinationY, cellWidth, cellHeight) == hash) continue;

                        for (int y = 0; y < cellHeight; y++)
                        {
                            int sourceRow = sourceY + y, destinationRow = destinationY + y;
                            if ((uint)sourceRow >= (uint)height || (uint)destinationRow >= (uint)height) continue;
                            for (int x = 0; x < cellWidth; x++)
                            {
                                int sourceColumn = sourceX + x, destinationColumn = destinationX + x;
                                if ((uint)sourceColumn >= (uint)width || (uint)destinationColumn >= (uint)width) continue;
                                // a later stamp of this frame drew over the pixel (the bake stamps in record order)
                                if (CoveredLater(records, stamps, k, destinationColumn, destinationRow)) continue;
                                int cellPixel = (sourceRow * width + sourceColumn) * 4, framePixel = (destinationRow * width + destinationColumn) * 4;
                                rgba[cellPixel] = framePixels[framePixel]; rgba[cellPixel + 1] = framePixels[framePixel + 1];
                                rgba[cellPixel + 2] = framePixels[framePixel + 2]; rgba[cellPixel + 3] = framePixels[framePixel + 3];
                            }
                        }
                    }
                }
                finally { Raylib.UnloadImage(decoded); }
            }
            catch { /* a frame that will not decode just means no write-back for it */ }
        }
    }


    /// Is (x,y) inside the destination rectangle of a stamp after stampIndex in this frame?
    private static bool CoveredLater(byte[] records, List<(int Rec, int Cell, uint Hash)> stamps, int stampIndex, int x, int y)
    {
        for (int j = stampIndex + 1; j < stamps.Count; j++)
        {
            int recordOffset = stamps[j].Rec * 0x18;
            if (recordOffset + 0x18 > records.Length) continue;
            int cellWidth = ReadU16(records, recordOffset + 0x08), cellHeight = ReadU16(records, recordOffset + 0x0A);
            int destinationX = ReadU16(records, recordOffset + 0x10), destinationY = ReadU16(records, recordOffset + 0x12);
            if (x >= destinationX && x < destinationX + cellWidth && y >= destinationY && y < destinationY + cellHeight) return true;
        }
        return false;
    }

    /// A glTF image decoded by Raylib (stb_image), in its own pixel format; Width is 0 when it
    /// does not decode. The viewer uploads these and the importer encodes them, so both see the
    /// same pixels. The caller unloads it.
    public static Image DecodeImage(SharpGLTF.Memory.MemoryImage image)
    {
        string extension = string.IsNullOrEmpty(image.FileExtension) ? "png" : image.FileExtension;
        return Raylib.LoadImageFromMemory("." + extension, image.Content.ToArray());
    }

    /// DecodeImage converted to RGBA8.
    private static Image DecodeRgba(SharpGLTF.Memory.MemoryImage image)
    {
        Image decoded = DecodeImage(image);
        if (decoded.Width > 0 && decoded.Height > 0)
            Raylib.ImageFormat(ref decoded, PixelFormat.UncompressedR8G8B8A8);
        return decoded;
    }

    /// A copy of an RGBA8 image's pixels.
    internal static byte[] Pixels(Image image)
    {
        byte[] rgba = new byte[image.Width * image.Height * 4];
        unsafe { System.Runtime.InteropServices.Marshal.Copy((IntPtr)image.Data, rgba, 0, rgba.Length); }
        return rgba;
    }
}
