using System.IO;
using System.Buffers.Binary;
using System.Numerics;
using Raylib_cs;
using SharpGLTF.Schema2;
using Buffer = System.Buffer;
using Image = Raylib_cs.Image;
using Material = SharpGLTF.Schema2.Material;

namespace DotrAssetModder;
// Mostly AI


// === Step 6 — GLB parser =============================================================
// Loads a .glb (via SharpGLTF 1.0.6) and flattens it into a clean intermediate the
// rebuilder pipeline consumes. The extractor wrote skinned meshes with an identity mesh
// transform, so the POSITION accessor already holds world-space *bind* positions; joints
// carry their world matrix directly (node scales are 1, so WorldMatrix == the read-side
// `world`; the global scale S is re-introduced later in the root node). Material names are
// `mat_tex{N}` (+`_alt`), encoding the texture index + alt-mesh flag.
//
// Validation targets (Blue-Eyes_White_Dragon.glb): 133 nodes, 1 skin / 71 joints,
// 4 primitives (mat_tex0..3), 4818 triangles, 3951 verts.
// =====================================================================================

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
    public Vector3 Pos;

    /// Frame-generation position (bounds/pivot/radius diagnostics in RebuildPipeline).
    /// Always equal to Pos - kept as a separate field since some diagnostics historically
    /// read a different source; do not use this for skinning.
    public Vector3 FramePos;

    public Vector3 Normal;                  // normal used by the geometry encoder
    public Vector2 Uv;
    public List<ImportedInfluence> Influences = new(); // 0 => rigid to the prim's mesh node

    /// Extracted glbs: the vertex's influences as RH0 stored them (ModelExtractor's
    /// VertexTexture1Game; Bone = the joint's glb node), in the game's order with zero weights
    /// kept, while they still match the vertex; null = derive them from Pos as for any glb.
    internal List<StripWeight>? Game;
}

public sealed class ImportedPrimitive
{
    /// glb logical node that instanced this mesh (-1 if unknown). For unskinned prims the
    /// vertices are world-baked at import and rigidly rigged to this node.
    public int NodeIndex = -1;
    public int TexIdx;
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
    public List<(int A, int B, int C)> Triangles = new();
}

/// Every channel is resampled at every integer frame 0..FrameCount-1 (t = frame / 30) with
/// its own sampler's interpolation (STEP, LINEAR or CUBICSPLINE), so entry i is frame i.
public sealed class ImportedAnimChannel
{
    public List<(float Time, Vector3 V)>? Translation;
    public List<(float Time, Quaternion Q)>? Rotation;
    public List<(float Time, Vector3 V)>? Scale;
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

    /// Embedded base-colour images per texture index (from material names "mat_texN"),
    /// kept as SharpGLTF MemoryImage (PNG/JPEG bytes). Decode + pass to the assembler as
    /// Rh0Assembler.TextureInput to re-encode them into the RH2 blocks; textures absent
    /// here keep the template model's RH2 verbatim.
    public Dictionary<int, SharpGLTF.Memory.MemoryImage> TextureImages = new();

    /// Animated-texture frame sequences, keyed by the same texIdx as TextureImages. Frames
    /// are sourced from extra embedded glb images named "tex{N}_anim{frame}" (frame =
    /// 0,1,2... any width, any count) - a naming convention chosen because glTF has no
    /// native concept of an image sequence / flipbook texture, so Blender's "Image
    /// Sequence" texture node needs flattening into individually-named still images at
    /// export time (give each frame's Image datablock that name in Blender before export).
    /// Frames are sorted by trailing frame number and packed into one sprite-sheet by
    /// GlbImporter.DecodeGlbTextures, which Rh0Assembler then uses to synthesize the
    /// matching TexAnm flipbook entry. A texIdx with no matching images falls through to
    /// the normal static TextureImages[texIdx] path unchanged - purely additive. Fewer
    /// than 2 frames found = not treated as animated (ignored, likely a naming accident);
    /// frame 0's image is harvested into TextureImages too either way so the slot still
    /// imports fine as a static texture.
    public Dictionary<int, List<SharpGLTF.Memory.MemoryImage>> TextureAnimFrames = new();

    /// Per-texture flipbook tick rate (hold time per frame, forwarded into the ANM
    /// entry's aux0/aux1 - see Rh0Assembler.BuildCustomTexAnimEntry), parsed from an
    /// optional "_tN" suffix on any of that slot's "tex{N}_anim{frame}" image names,
    /// e.g. "tex0_anim0_t10" = 10 ticks/frame. Defaults to 5 (matching the one real
    /// retail sample) for any texIdx with no explicit suffix found.
    public Dictionary<int, int> TextureAnimTicks = new();

    /// UV scale of each packed flipbook slot (DecodeGlbTextures): its faces sample only cell 0 of
    /// the sprite sheet, so RebuildPipeline.ApplyTexAnimUv squeezes their [0,1] UVs into it.
    public Dictionary<int, Vector2> TextureAnimUvScale = new();

    /// Set once RebuildPipeline.ApplyTexAnimUv has rewritten the UVs (Build may run twice).
    public bool AppliedTexAnimUv;

    /// Auto-assigned texture index per glb material (LogicalIndex), used when a material is
    /// not explicitly named "mat_texN". Pre-assigned deterministically in material
    /// (LogicalIndex) order, skipping slots taken by explicit mat_texN names — so the
    /// glb's materials array order IS the slot order, independent of node/mesh iteration.
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
    /// carried them but no longer matched (edited elsewhere) and were derived from Pos.
    public int GameVertices;
    public int GameVerticesDropped;

    /// Set by RebuildPipeline.ApplyTexMerges after it rewrites primitive UVs/TexIdx for
    /// UV-atlas slot merging; guards against double-applying when Build runs twice on the
    /// same instance. Carries the merge plan (base slot -> member slots in band order).
    public List<(int BaseSlot, List<int> MemberSlots)>? AppliedTexMerges;

    /// True for GLBs produced by this project's extractor. Extracted GLBs contain an
    /// `MrgRoot` wrapper and already have skinned positions baked into the extractor's
    /// round-trip coordinate space, so the importer must not treat them like generic
    /// Blender/Sketchfab GLBs.
    public bool IsDotrExtractedGlb;

    /// Inverse of the extractor's MrgRoot world, when RebuildPipeline.PrepareRoot dropped it
    /// without baking it into node 0: maps glb-space positions back to model space.
    public Matrix4x4? DroppedRootUndo;

    /// Deprecated/unused in DOTR-convention mode. Marker names are intentionally ignored.
    public Vector3? AuthorCameraLookAtWorld;

    /// Deprecated/unused in DOTR-convention mode. Marker names are intentionally ignored.
    public Vector3? AuthorWorldPosCenterWorld;

    /// RebuildPipeline may fit an external imported model into the donor/template model's
    /// coordinate frame before encoding. These are the fitted/output-space equivalents
    /// of the authoring markers, used later when writing the node-tree bounds record
    /// and for diagnostics.
    public Vector3? FittedCameraLookAtWorld;

    /// RebuildPipeline may fit an external imported model into the donor/template model's
    /// coordinate frame before encoding. This guard prevents applying the same transform
    /// twice when callers reuse an ImportedModel instance across preview/build passes.
    public bool AppliedTemplateFrameFit;

    public string Summary() =>
        $"nodes={Nodes.Count} joints={JointNodeIndices.Count} prims={Primitives.Count} " +
        $"tris={Primitives.Sum(p => p.Triangles.Count)} verts={Primitives.Sum(p => p.Vertices.Count)} " +
        $"anims={Animations.Count}";
}

public static class GlbImporter
{
    public static ImportedModel Load(string path) => FromModel(ModelRoot.Load(path));

    public static ImportedModel LoadFromBytes(byte[] glb) => FromModel(ModelRoot.ParseGLB(glb));

    private static ImportedModel FromModel(ModelRoot model)
    {
        var im = new ImportedModel
        {
            IsDotrExtractedGlb = model.LogicalNodes.Any(n =>
                string.Equals(n.Name, "MrgRoot", StringComparison.Ordinal))
        };

        // --- nodes / hierarchy ---
        var byLogical = new Dictionary<int, int>(); // logical index -> position in im.Nodes
        foreach (var n in model.LogicalNodes)
        {
            var lt = n.LocalTransform;
            var node = new ImportedNode
            {
                Index = n.LogicalIndex,
                Name = n.Name ?? "",
                Translation = lt.Translation,
                Rotation = lt.Rotation,
                Scale = lt.Scale,
                WorldMatrix = WorldOf(n),
            };
            byLogical[n.LogicalIndex] = im.Nodes.Count;
            im.Nodes.Add(node);
        }
        foreach (var n in model.LogicalNodes)
        {
            var node = im.Nodes[byLogical[n.LogicalIndex]];
            node.Parent = n.VisualParent != null ? n.VisualParent.LogicalIndex : -1;
            if (node.Parent < 0) im.RootNodes.Add(node.Index);
            foreach (var c in n.VisualChildren) node.Children.Add(c.LogicalIndex);
        }

        // --- extracted `_bone` scale nodes (ModelExtractor.WriteGlb) ---
        // A slot node's accumulated scale rides a `_bone` child so that, as in RH0, it reaches
        // only that node's own matrix. Fold each one back into its node: the RH0 tree has no
        // such node, and the skin joint and any rigid mesh on it belong to the node.
        // RebuildPipeline reads the child's scale (rest and animated) as the node's
        // accumulated scale.
        var boneOwner = new Dictionary<int, int>(); // `_bone` glb index -> owning node
        if (im.IsDotrExtractedGlb)
        {
            foreach (var n in im.Nodes)
            {
                if (n.Parent < 0 || n.Children.Count != 0) continue;
                if (!System.Text.RegularExpressions.Regex.IsMatch(n.Name, @"_bone(?:\.\d+)?$")) continue;
                var owner = im.Nodes[byLogical[n.Parent]];
                if (owner.ScaleBone >= 0) continue;
                owner.ScaleBone = n.Index;
                owner.Children.Remove(n.Index);
                boneOwner[n.Index] = owner.Index;
            }
        }
        int Owner(int g) => boneOwner.TryGetValue(g, out int o) ? o : g;

        // --- skin joints ---
        if (model.LogicalSkins.Count > 0)
        {
            var skin = model.LogicalSkins[0];
            for (int i = 0; i < skin.JointsCount; i++)
            {
                var (joint, inverseBind) = skin.GetJoint(i);
                int idx = Owner(joint.LogicalIndex);
                im.JointNodeIndices.Add(idx);
                im.Nodes[byLogical[idx]].IsJoint = true;
                // A `_bone` joint is bound where its part is visible, which for a part at
                // scale 0 at rest is not the rest pose: take the bind from the skin.
                if (idx != joint.LogicalIndex && Matrix4x4.Invert(inverseBind, out var bindWorld))
                    im.JointBind[idx] = bindWorld;
                // Any other joint whose bind pose is not its rest pose (DoD exports: every
                // joint is bound away from the node rest values) must encode its vertices
                // against the bind, or the mesh is skinned with the wrong offsets and limbs
                // stretch once the motion moves them.
                else if (idx == joint.LogicalIndex && Matrix4x4.Invert(inverseBind, out bindWorld) &&
                         !NearlyEqual(bindWorld, joint.WorldMatrix))
                    im.JointBind[idx] = bindWorld;
            }
        }

        // --- animations: every channel resampled at every integer frame ---
        // Sampling with each sampler's own interpolation turns STEP and CUBICSPLINE into
        // plain per-frame values (CUBICSPLINE outputs are tangent/value/tangent triplets,
        // which must never be read as keys), and lets RebuildPipeline fit the game's key
        // lists (per-axis Euler lerp, a list under 3 keys plays key 0 only) to the motion
        // rather than copying glTF keys one for one.
        // CreateCurveSampler(true) copies the keys out once; without it every sample re-reads
        // the accessors (card 60: 1 s instead of 17 ms).
        foreach (var anim in model.LogicalAnimations)
        {
            int frames = (int)MathF.Round(anim.Duration * AnimFps) + 1;
            var ia = new ImportedAnimation { Name = anim.Name ?? "", FrameCount = frames };
            foreach (var ch in anim.Channels)
            {
                if (ch.TargetNode == null) continue;
                int node = ch.TargetNode.LogicalIndex;
                if (!ia.Channels.TryGetValue(node, out var c)) { c = new ImportedAnimChannel(); ia.Channels[node] = c; }
                switch (ch.TargetNodePath)
                {
                    case PropertyPath.translation:
                        c.Translation = Resample(ch.GetTranslationSampler()?.CreateCurveSampler(true), frames);
                        break;
                    case PropertyPath.rotation:
                        c.Rotation = Resample(ch.GetRotationSampler()?.CreateCurveSampler(true), frames);
                        break;
                    case PropertyPath.scale:
                        c.Scale = Resample(ch.GetScaleSampler()?.CreateCurveSampler(true), frames);
                        break;
                }
            }
            // no node channel (e.g. texture-scroll animations through KHR_animation_pointer): not a motion
            if (ia.Channels.Count == 0) continue;
            im.Animations.Add(ia);
        }

        // Rigid extractor output (ModelExtractor.WriteGlb's rigid mode): no skin; each bone's
        // mesh sits on the bone's own node, which carries a `_m<slot>` name suffix. Rebuild the
        // joint set the skinned export would have declared - every slot node, in slot order -
        // so the pipeline sees the same shape (joint nodes, not mesh nodes) and a round trip
        // keeps the original matrix slots, including slots with no geometry.
        bool rigidDotr = im.IsDotrExtractedGlb && model.LogicalSkins.Count == 0;
        if (rigidDotr)
        {
            var slotted = new List<(int Slot, int Node)>();
            foreach (var n in model.LogicalNodes)
            {
                var m = System.Text.RegularExpressions.Regex.Match(n.Name ?? "", @"_m(\d+)(?:\.\d+)?$");
                if (m.Success) slotted.Add((int.Parse(m.Groups[1].Value), n.LogicalIndex));
            }
            foreach (var (_, idx) in slotted.OrderBy(s => s.Slot))
            {
                im.JointNodeIndices.Add(idx);
                im.Nodes[byLogical[idx]].IsJoint = true;
            }
        }

        // --- deterministic auto texture-slot assignment (material LogicalIndex order) ---
        // Explicitly named mat_texN materials own their slots; every other material gets
        // the next free slot in materials-array order. Doing this as a pre-pass (instead
        // of first-seen-during-node-iteration) makes the material -> slot mapping stable
        // and predictable from the glb alone.
        //
        // A slot number indexes the template model's RH2 blocks, and a slot nothing supplies
        // costs a 9 KB white placeholder (Rh0Assembler), so a gap is pure waste. An extracted
        // glb's mat_texN numbers ARE the retail slot numbers, so they are kept exactly and a
        // round trip is unchanged. A custom glb's numbers are only the author's labels, so
        // they are compacted to a dense 0..k-1 in ascending mat_texN order; unnamed materials
        // then take k, k+1, ... in materials-array order as before (sharing a slot when they
        // share a base-colour image or flat colour). A duplicate or out-of-range mat_texN is
        // reported in TexSlotWarnings.
        // mat_texN label -> dense slot for a custom glb (empty for an extracted one). The
        // "tex{N}_anim{frame}" pass below is keyed by the same author labels, so it has to
        // follow the same remap.
        var texLabelRemap = new Dictionary<int, int>();
        {
            var explicitSlot = new Dictionary<int, int>();  // material LogicalIndex -> mat_texN
            // mat_texN -> first material naming it and its image/colour. Several materials on
            // one slot is normal (retail splits a slot by alpha/GS state, and the extractor
            // writes one material per page), but two DIFFERENT images on one slot is a real
            // clash: only the first is harvested into TextureImages, the other is dropped.
            var firstOwner = new Dictionary<int, (int Mat, string Key)>();
            foreach (var m in model.LogicalMaterials)
            {
                if (!TryParseMatTexIndex(m.Name, out int t)) continue;
                if (t < 0 || t > MaxTexSlot)
                    im.TexSlotWarnings.Add($"material {m.LogicalIndex} \"{m.Name}\": texture slot {t} is out of range 0..{MaxTexSlot}");
                string key = MaterialShareKey(m);
                if (firstOwner.TryGetValue(t, out var first))
                {
                    if (first.Key != key)
                        im.TexSlotWarnings.Add($"material {m.LogicalIndex} \"{m.Name}\": texture slot {t} already holds a different image from material {first.Mat}; only material {first.Mat}'s image is imported");
                }
                else firstOwner[t] = (m.LogicalIndex, key);
                explicitSlot[m.LogicalIndex] = t;
            }
            if (!im.IsDotrExtractedGlb && explicitSlot.Count != 0)
            {
                // custom glb: compact the named slots (an out-of-range one lands in range here)
                var compact = new Dictionary<int, int>();
                int dense = 0;
                foreach (int s in new SortedSet<int>(explicitSlot.Values)) compact[s] = dense++;
                foreach (var kv in explicitSlot)
                    if (compact[kv.Value] != kv.Value) im.MatTexRemap[kv.Key] = compact[kv.Value];
                foreach (var kv in compact)
                    if (kv.Key != kv.Value) texLabelRemap[kv.Key] = kv.Value;
            }
            foreach (var w in im.TexSlotWarnings) Console.Error.WriteLine("GlbImporter: " + w);
            // the slots the named materials hold after that remap; auto assignment steps over them
            var explicitSlots = new HashSet<int>();
            foreach (var kv in explicitSlot)
                explicitSlots.Add(im.MatTexRemap.TryGetValue(kv.Key, out int d) ? d : kv.Value);
            int nextAuto = 0;
            var slotByKey = new Dictionary<string, int>();   // image/colour dedupe
            foreach (var m in model.LogicalMaterials)
            {
                if (explicitSlot.ContainsKey(m.LogicalIndex)) continue;
                if (IsUntexturedMaterial(m.Name)) continue;   // draws with TME off: no slot of its own
                // A custom glb's flat-colour material (no base-colour texture) is handled the
                // same way by ParseMaterial - drawn with TME off, its colour carried as the
                // page colour - so it must not be counted here either.
                if (!HasBaseColorTexture(m)) continue;
                // materials sharing the same base-colour image (or the same flat colour)
                // share one texture slot - texture slots are a scarce resource (template
                // RH2 count) and duplicating identical images wastes them
                string key = MaterialShareKey(m);
                if (slotByKey.TryGetValue(key, out int shared))
                {
                    im.MaterialTexAuto[m.LogicalIndex] = shared;
                    continue;
                }
                while (explicitSlots.Contains(nextAuto)) nextAuto++;
                slotByKey[key] = nextAuto;
                im.MaterialTexAuto[m.LogicalIndex] = nextAuto++;
            }
        }

        // --- mesh primitives (iterated per node instance, so the node transform is known) ---
        // Skinned prims: vertices are already in skin space (glTF ignores the node transform
        // for skinned meshes) -> keep as-is, influences come from JOINTS_0/WEIGHTS_0.
        // Unskinned prims (static models like props/vehicles): bake the node's world
        // transform into the vertices and rig them rigidly to that node, so the pipeline can
        // treat every vertex uniformly (the node becomes a one-bone "joint" downstream).
        foreach (var meshNode in model.LogicalNodes)
        {
            if (meshNode.Mesh == null) continue;
            // a rigid mesh on an extracted `_bone` belongs to the bone's node
            int meshOwner = Owner(meshNode.LogicalIndex);
            // A rigid-extract joint keeps joint-only status (as in the skinned export), so the
            // root/wrapper detection in RebuildPipeline treats both export shapes alike.
            if (byLogical.TryGetValue(meshOwner, out int meshNodePos)
                && !(rigidDotr && im.Nodes[meshNodePos].IsJoint))
                im.Nodes[meshNodePos].HasMesh = true;
            bool nodeSkinned = meshNode.Skin != null;
            Matrix4x4 nodeWorld = WorldOf(meshNode);
            if (!nodeSkinned)
            {
                // A node at scale 0 is how DotR models keep a part hidden until a motion
                // scales it up (Meteor Dragon's head and limbs), so it is not dropped: its
                // vertices are bound at the first animation frame where it is visible, and
                // the pipeline encodes them against that same matrix. Only a node that is
                // invisible in every frame is skipped.
                var bind = FirstVisibleWorld(im, meshNode.LogicalIndex, nodeWorld);
                if (bind is not { } bindWorld) continue;
                if (meshOwner != meshNode.LogicalIndex || bindWorld != nodeWorld)
                    im.JointBind[meshOwner] = bindWorld;
                nodeWorld = bindWorld;
            }
            foreach (var prim in meshNode.Mesh.Primitives)
            {
                var ip = new ImportedPrimitive { NodeIndex = meshOwner };
                ParseMaterial(prim.Material, ip, im);

                var pos = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                if (pos == null) continue; // nothing to do without positions
                var nrm = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var uv = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                var jnt = prim.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
                var wgt = prim.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
                // a vertex may carry up to 8 influences (ModelExtractor writes a second set)
                var jnt1 = prim.GetVertexAccessor("JOINTS_1")?.AsVector4Array();
                var wgt1 = prim.GetVertexAccessor("WEIGHTS_1")?.AsVector4Array();

                bool skinnedPrim = nodeSkinned && jnt != null && wgt != null;
                int vcount = pos.Count;
                var game = skinnedPrim && im.IsDotrExtractedGlb ? GameAccessors(prim) : null;
                for (int v = 0; v < vcount; v++)
                {
                    var iv = new ImportedVertex();
                    if (skinnedPrim)
                    {
                        // Skinned vertex positions must stay in skin/bind space for the encoder.
                        // Baking the mesh node world matrix here can make the skeleton/rest basis
                        // disagree with the vertices, causing 90-degree rotation or broken animation
                        // on external Armature-rooted GLBs.
                        iv.Pos = pos[v];
                        iv.Normal = nrm != null ? SafeNormalize(nrm[v]) : Vector3.UnitY;
                        iv.FramePos = iv.Pos;
                    }
                    else
                    {
                        // Static/unskinned meshes are rigidly attached to their mesh node, so bake
                        // the node world transform directly into the encoded vertices.
                        iv.Pos = Vector3.Transform(pos[v], nodeWorld);
                        iv.FramePos = iv.Pos;
                        iv.Normal = nrm != null ? SafeNormalize(Vector3.TransformNormal(nrm[v], nodeWorld)) : Vector3.UnitY;
                    }
                    iv.Uv = uv != null ? uv[v] : Vector2.Zero;

                    if (skinnedPrim)
                    {
                        var j = jnt[v]; var w = wgt[v];
                        AddInfluence(iv, im, (int)j.X, w.X);
                        AddInfluence(iv, im, (int)j.Y, w.Y);
                        AddInfluence(iv, im, (int)j.Z, w.Z);
                        AddInfluence(iv, im, (int)j.W, w.W);
                        if (jnt1 != null && wgt1 != null)
                        {
                            var j1 = jnt1[v]; var w1 = wgt1[v];
                            AddInfluence(iv, im, (int)j1.X, w1.X);
                            AddInfluence(iv, im, (int)j1.Y, w1.Y);
                            AddInfluence(iv, im, (int)j1.Z, w1.Z);
                            AddInfluence(iv, im, (int)j1.W, w1.W);
                        }
                    }
                    else
                    {
                        iv.Influences.Add(new ImportedInfluence { JointNode = meshOwner, Weight = 1f });
                    }
                    if (game != null && game.Jw[0][v].X >= 0)
                    {
                        iv.Game = GameInfluences(im, iv, game, v);
                        if (iv.Game != null) im.GameVertices++;
                        else im.GameVerticesDropped++;
                    }
                    ip.Vertices.Add(iv);
                }

                foreach (var (a, b, c) in prim.GetTriangleIndices())
                    ip.Triangles.Add((a, b, c));

                // KHR_texture_transform on the base colour (an atlas cell picked by scale/offset,
                // as DoD exports do): the GS samples the UVs as they are, so bake it into them.
                ApplyBaseTextureTransform(prim.Material, ip);
                ApplyDotrImageUv(prim.Material, ip);
                if (!im.IsDotrExtractedGlb) ParsePlainAlpha(prim.Material, ip, im);

                // Fold out-of-[0,1] UVs into tiles only on axes the GS really clamps. The
                // extractor writes the page's GS CLAMP byte into the material name as "_cN"
                // (ModelExtractor's matName), and a stage model's pages are usually REPEAT on
                // both axes: there, tiling UVs reproduce exactly (4.12 UV reaches +-8), so
                // folding them would both change the picture and duplicate every vertex that
                // straddles a tile - which destroys the adjacency the stripifier needs and
                // blew section 2/4 up to one strip per triangle on MODEL.MRG scenery. A glb
                // with no such tag (an outside authoring tool) keeps the old fold on both axes.
                bool foldS = true, foldT = true;
                if (ParseGsClampTag(prim.Material?.Name, out int clampTag))
                {
                    foldS = (clampTag & 0x3) == 1;
                    foldT = ((clampTag >> 2) & 0x3) == 1;
                }
                WrapPrimitiveUvs(ip, foldS, foldT);

                im.Primitives.Add(ip);
            }
        }

        // --- animated-texture frame sequences ("tex{N}_anim{frame}" image naming) ---
        // Scans ALL embedded images, not just ones a material currently references, since
        // Blender's glTF exporter can leave extra-frame images attached only via the
        // Image datablock name (only frame 0 needs a real material link so the mesh has
        // something to preview/shade with in Blender). Purely additive.
        {
            var byTex = new Dictionary<int, List<(int frame, SharpGLTF.Memory.MemoryImage img)>>();
            var ticksByTex = new Dictionary<int, int>();
            foreach (var img in model.LogicalImages)
            {
                if (TryParseAnimImageName(img.Name, out int texIdx, out int frame, out int ticks) && img.Content.IsValid)
                {
                    // N is the author's mat_texN label; a custom glb's labels were compacted
                    // above, so the frames have to move with their slot (an extracted glb's
                    // table is empty, so its names are kept exactly)
                    if (texLabelRemap.TryGetValue(texIdx, out int denseTex)) texIdx = denseTex;
                    // An extracted glb's frames are views of the base image the retail
                    // records copy from, not a flipbook to repack: keep them aside so the
                    // records (and the texture's size) stay retail's, and let
                    // ApplyEditedAnimFrames push an edited frame back into its source cell.
                    if (im.TextureAnimRecords.ContainsKey(texIdx))
                    {
                        if (!im.TextureAnimEditFrames.TryGetValue(texIdx, out var ef))
                            im.TextureAnimEditFrames[texIdx] = ef = new();
                        ef[frame] = img.Content;
                        continue;
                    }
                    if (!byTex.TryGetValue(texIdx, out var list))
                        byTex[texIdx] = list = new();
                    list.Add((frame, img.Content));
                    if (ticks > 0) ticksByTex[texIdx] = ticks; // last one found wins if multiple frames specify it differently
                }
            }
            foreach (var (texIdx, frames) in byTex)
            {
                if (frames.Count < 2) continue; // see TextureAnimFrames remarks - ignore accidental singletons
                frames.Sort((a, b) => a.frame.CompareTo(b.frame));
                im.TextureAnimFrames[texIdx] = frames.Select(f => f.img).ToList();
                if (ticksByTex.TryGetValue(texIdx, out int t)) im.TextureAnimTicks[texIdx] = t;
                // Frame 0 also becomes (or overrides) the static fallback image for this
                // slot, so a path that ignores TextureAnimFrames entirely still gets a
                // sane single texture instead of whatever the material channel happened
                // to reference.
                im.TextureImages[texIdx] = frames[0].img;
            }
        }

        return im;
    }

    public const float AnimFps = 30f;

    // Node.WorldMatrix rejects a node whose local matrix is not invertible; DotR models hide
    // parts with tiny or zero scales (card 83 nests 1e-5 per level down to ~1e-24).
    private static Matrix4x4 WorldOf(Node n) =>
        n.VisualParent == null ? n.LocalTransform.Matrix : n.LocalTransform.Matrix * WorldOf(n.VisualParent);

    private static List<(float Time, T V)>? Resample<T>(SharpGLTF.Animations.ICurveSampler<T>? sampler, int frames)
    {
        if (sampler == null) return null;
        var outp = new List<(float, T)>(frames);
        for (int f = 0; f < frames; f++)
        {
            float t = f / AnimFps;
            outp.Add((t, sampler.GetPoint(t)));
        }
        return outp;
    }

    /// glb world matrix of node g at a frame of an animation (rest values where it has no
    /// channel); anim == null is the rest pose.
    public static Matrix4x4 WorldAt(ImportedModel im, int g, ImportedAnimation? anim, int frame)
    {
        var n = im.Nodes[g];
        var t = n.Translation;
        var r = n.Rotation;
        var s = n.Scale;
        if (anim != null && anim.Channels.TryGetValue(g, out var c))
        {
            if (c.Translation is { Count: > 0 } ct) t = ct[Math.Min(frame, ct.Count - 1)].V;
            if (c.Rotation is { Count: > 0 } cr) r = cr[Math.Min(frame, cr.Count - 1)].Q;
            if (c.Scale is { Count: > 0 } cs) s = cs[Math.Min(frame, cs.Count - 1)].V;
        }
        var local = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
        return n.Parent < 0 ? local : local * WorldAt(im, n.Parent, anim, frame);
    }

    /// The rest world matrix if it is invertible, else the first animation frame's that is;
    /// null when the node is at scale 0 in every frame.
    private static Matrix4x4? FirstVisibleWorld(ImportedModel im, int g, Matrix4x4 rest)
    {
        if (!IsSingular(rest)) return rest;
        foreach (var anim in im.Animations)
            for (int f = 0; f < anim.FrameCount; f++)
            {
                var w = WorldAt(im, g, anim, f);
                if (!IsSingular(w)) return w;
            }
        return null;
    }

    /// Scale 0 on some axis. Relative to the largest axis, since DotR worlds carry the model's
    /// root scale (often ~1e-3) and an absolute determinant test would trip on whole models.
    public static bool IsSingular(Matrix4x4 m)
    {
        float a = new Vector3(m.M11, m.M12, m.M13).Length();
        float b = new Vector3(m.M21, m.M22, m.M23).Length();
        float c = new Vector3(m.M31, m.M32, m.M33).Length();
        float max = Math.Max(a, Math.Max(b, c));
        return !(max > 0f) || Math.Min(a, Math.Min(b, c)) < max * 1e-6f;
    }

    /// Matches image names of the form "tex{N}_anim{frame}" (case-insensitive), with an
    /// optional trailing "_tN" tick-rate suffix, e.g. "tex1_anim0", "tex1_anim0_t10"
    /// (10 ticks/frame hold time), "TEX2_ANIM03_T3". N, frame, and the tick count are
    /// plain non-negative integers. Returns false for anything else, including the
    /// unrelated "mat_texN" material-naming convention (different prefix, different
    /// purpose). `ticks` is -1 when no "_tN" suffix is present (caller should fall back
    /// to a default in that case, not treat -1 as a real value).
    private static bool TryParseAnimImageName(string? name, out int texIdx, out int frame, out int ticks)
    {
        texIdx = -1; frame = -1; ticks = -1;
        if (string.IsNullOrEmpty(name)) return false;
        int p = name.IndexOf("tex", StringComparison.OrdinalIgnoreCase);
        if (p < 0) return false;
        int i = p + 3, texStart = i;
        while (i < name.Length && char.IsDigit(name[i])) i++;
        if (i == texStart) return false;
        if (!int.TryParse(name.AsSpan(texStart, i - texStart), out texIdx)) return false;

        int a = name.IndexOf("anim", i, StringComparison.OrdinalIgnoreCase);
        if (a < 0) { texIdx = -1; return false; }
        int j = a + 4, frameStart = j;
        while (j < name.Length && char.IsDigit(name[j])) j++;
        if (j == frameStart) { texIdx = -1; return false; }
        if (!int.TryParse(name.AsSpan(frameStart, j - frameStart), out frame)) { texIdx = -1; return false; }

        // optional "_tN" suffix anywhere after the frame number
        int t = name.IndexOf("_t", j, StringComparison.OrdinalIgnoreCase);
        if (t >= 0)
        {
            int k = t + 2, tickStart = k;
            while (k < name.Length && char.IsDigit(name[k])) k++;
            if (k > tickStart && int.TryParse(name.AsSpan(tickStart, k - tickStart), out int parsedTicks))
                ticks = Math.Max(1, parsedTicks);
        }
        return true;
    }

    // DOTR-convention mode intentionally does not use string-named authoring markers.
    // New/custom GLBs should be authored in DOTR space: scene/model origin is the
    // placement/control point, and geometry is positioned relative to that origin.
    internal static bool IsCameraLookAtMarker(string? name) => false;

    internal static bool IsWorldPosCenterMarker(string? name) => false;

    internal static bool IsAuthoringMarker(string? name) => false;

    // Element-wise compare, tolerance relative to the matrices' largest entry (translations
    // can be large) so float noise in an inverse bind matrix never counts as a different bind.
    private static bool NearlyEqual(Matrix4x4 a, Matrix4x4 b)
    {
        float mag = 1f, diff = 0f;
        for (int r = 0; r < 4; r++)
        for (int c = 0; c < 4; c++)
        {
            mag = MathF.Max(mag, MathF.Max(MathF.Abs(a[r, c]), MathF.Abs(b[r, c])));
            diff = MathF.Max(diff, MathF.Abs(a[r, c] - b[r, c]));
        }
        return diff <= 1e-4f * mag;
    }

    internal sealed class GameData
    {
        public IList<Vector3> RefPos = null!;
        public IList<Vector3>[] Pos = null!;
        public IList<Vector3>[] Nrm = null!;
        public IList<Vector2>[] Jw = null!;
    }

    // ModelExtractor's per-influence attributes (VertexTexture1Game), or null without them.
    // A mesh carries 4 or 8 slots; the slots read are 0.. up to the first absent one.
    // AnimatedGltfModel skins the preview with them too.
    internal static GameData? GameAccessors(MeshPrimitive prim)
    {
        var refPos = prim.GetVertexAccessor(VertexTexture1Game.RefPosName);
        if (refPos == null) return null;
        var pos = new List<IList<Vector3>>();
        var nrm = new List<IList<Vector3>>();
        var jws = new List<IList<Vector2>>();
        for (int k = 0; k < VertexTexture1Game.Slots; k++)
        {
            var p = prim.GetVertexAccessor(VertexTexture1Game.PosName + k);
            var n = prim.GetVertexAccessor(VertexTexture1Game.NrmName + k);
            var jw = prim.GetVertexAccessor(VertexTexture1Game.JointWeightName + k);
            if (p == null && n == null && jw == null) break;
            if (p == null || n == null || jw == null) return null;
            pos.Add(p.AsVector3Array());
            nrm.Add(n.AsVector3Array());
            jws.Add(jw.AsVector2Array());
        }
        if (jws.Count == 0) return null;
        return new GameData { RefPos = refPos.AsVector3Array(), Pos = pos.ToArray(), Nrm = nrm.ToArray(), Jw = jws.ToArray() };
    }

    // Vertex v's game influences, kept only while they still describe it: every joint a `_bone`
    // joint (encoded against its bind, so the stored bone-local values are what the importer
    // would write), the same weight per joint as JOINTS/WEIGHTS, and POSITION still where the
    // extractor wrote it in the first influence's bone space (an edit made with the bind
    // matrices, as ExtractedGlbEdit does, keeps that). Otherwise null.
    private static List<StripWeight>? GameInfluences(ImportedModel im, ImportedVertex iv, GameData g, int v)
    {
        var list = new List<StripWeight>();
        var gameW = new Dictionary<int, float>();
        int wsum = 0;
        for (int k = 0; k < g.Jw.Length && g.Jw[k][v].X >= 0; k++)
        {
            int j = (int)g.Jw[k][v].X;
            if (j >= im.JointNodeIndices.Count) return null;
            int node = im.JointNodeIndices[j];
            if (!im.JointBind.ContainsKey(node) || im.Nodes[node].ScaleBone < 0) return null;
            var p = g.Pos[k][v];
            var n = g.Nrm[k][v];
            short w = (short)g.Jw[k][v].Y;
            list.Add(new StripWeight(node, w, ((short)p.X, (short)p.Y, (short)p.Z), ((short)n.X, (short)n.Y, (short)n.Z)));
            // JOINTS/WEIGHTS hold the positive weights normalised over their sum (retail has a
            // few negative ones, card 291), so compare against those only.
            if (w <= 0) continue;
            gameW[node] = gameW.GetValueOrDefault(node) + w;
            wsum += w;
        }
        var glbW = new Dictionary<int, float>();
        foreach (var inf in iv.Influences)
        {
            glbW[inf.JointNode] = glbW.GetValueOrDefault(inf.JointNode) + inf.Weight;
        }
        if (!GameWeightsMatch(gameW, wsum, glbW)) return null;
        if (!Matrix4x4.Invert(im.JointBind[list[0].Bone], out var inv)) return null;
        if (!GameRefPosMatches(Vector3.Transform(iv.Pos, inv), g.RefPos[v])) return null;
        return list;
    }

    // The stored influences' positive raw weights (summing to wsum) per joint, against the
    // glb's JOINTS/WEIGHTS per joint: the same joints, each weight within 1e-3.
    internal static bool GameWeightsMatch(Dictionary<int, float> gameW, int wsum, Dictionary<int, float> glbW)
    {
        if (wsum <= 0) return false;
        foreach (var (joint, w) in gameW)
        {
            if (w > 0 && !glbW.ContainsKey(joint)) return false;
        }
        foreach (var (joint, w) in glbW)
        {
            if (MathF.Abs(w - gameW.GetValueOrDefault(joint) / wsum) > 1e-3f) return false;
        }
        return true;
    }

    // POSITION taken into the first influence's bone space, still where the extractor wrote it.
    internal static bool GameRefPosMatches(Vector3 inBone, Vector3 refPos) =>
        Vector3.Distance(inBone, refPos) <= 0.01f + 1e-5f * refPos.Length();

    // JOINTS_0 holds skin-joint indices; map to the actual glb node index via the skin order.
    private static void AddInfluence(ImportedVertex iv, ImportedModel im, int skinJointIndex, float weight)
    {
        if (weight <= 0f) return;
        if (skinJointIndex < 0 || skinJointIndex >= im.JointNodeIndices.Count) return;
        iv.Influences.Add(new ImportedInfluence { JointNode = im.JointNodeIndices[skinJointIndex], Weight = weight });
    }

    private static void ParseMaterial(Material? mat, ImportedPrimitive ip, ImportedModel im)
    {
        if (mat == null)
        {
            // Primitive with no material at all: like a flat-colour material, draw it with
            // PRIM TME off on slot 0 so no texture slot and no slot-0 image are spent on it
            // (a solid image here used to win over the real slot-0 texture when the
            // material-less primitive happened to be parsed first). Its page colour is the
            // neutral grey MatKey maps White onto.
            ip.TexIdx = 0;
            ip.Untextured = true;
            ip.BaseColor = Vector4.One;
            return;
        }
        string name = mat.Name ?? "";
        ip.IsAlt = name.EndsWith("_alt", StringComparison.OrdinalIgnoreCase);
        // A page the extractor wrote as "mat_untextured" is drawn with TME off, so it has no
        // texture of its own; the extractor clamps it to slot 0 (ModelExtractor's GetMaterial).
        // It must not claim a texture slot: an extracted model's own untextured materials would
        // otherwise push the slot count past the template's RH2 count, and RebuildPipeline's
        // ApplyTexMerges would fold real textures into atlas bands (card 682: slots 6 and 7
        // merged onto slots 0 and 1, wrecking both).
        bool untextured = IsUntexturedMaterial(name);
        ip.Untextured = untextured;
        // Needed before the slot decision below: a material with no base-colour texture
        // claims no slot either (see the !named branch).
        bool hasTexture = HasBaseColorTexture(mat);
        int p = name.IndexOf("mat_tex", StringComparison.OrdinalIgnoreCase);
        bool named = false;
        if (untextured)
        {
            ip.TexIdx = 0;
            named = true;
            p = -1;
        }
        if (p >= 0)
        {
            int start = p + "mat_tex".Length, end = start;
            while (end < name.Length && char.IsDigit(name[end])) end++;
            if (end > start && int.TryParse(name.AsSpan(start, end - start), out int tex))
            {
                // a custom glb's named slots are renumbered densely by the pre-pass in FromModel
                ip.TexIdx = im.MatTexRemap.TryGetValue(mat.LogicalIndex, out int dense) ? dense : tex;
                named = true;
            }
        }
        if (!named && !hasTexture)
        {
            // A custom glb's flat-colour material: no base-colour texture, so there is nothing
            // to put in a texture slot. Treat it exactly like the extractor's `mat_untextured`
            // - park it on slot 0, draw it with PRIM TME off and carry its base colour as the
            // page colour (MatKey's c1) - instead of baking a solid image into an auto slot.
            // Those auto slots used to push the slot count past the template's RH2 count and
            // make RebuildPipeline atlas-merge real textures (card 682).
            untextured = true;
            ip.Untextured = true;
            ip.TexIdx = 0;
            named = true;
        }
        if (!named)
        {
            // Unnamed material: give each DISTINCT material its own texture slot (stable per
            // material LogicalIndex), instead of collapsing everything onto slot 0.
            if (!im.MaterialTexAuto.TryGetValue(mat.LogicalIndex, out int auto_))
            {
                var taken = new HashSet<int>(im.MaterialTexAuto.Values);
                auto_ = 0;
                while (taken.Contains(auto_)) auto_++;
                im.MaterialTexAuto[mat.LogicalIndex] = auto_;
            }
            ip.TexIdx = auto_;
        }
        // Base colour factor feeds the RH0 material record's +0x20 row - a lighting input
        // the engine scales at runtime (lRhModel_SetMaterialIntensity SET/ADD/MUL modes);
        // retail neutral is (178,178,178)/alpha 128, which the pipeline maps White onto.
        ip.BaseColor = Vector4.One;
        try
        {
            var bc = mat.FindChannel("BaseColor");
            if (bc.HasValue) ip.BaseColor = bc.Value.Color;
        }
        catch { }
        RestoreColourBlock(mat, ip);
        if (!im.IsDotrExtractedGlb) ParseDotrColors(mat, ip);
        ParseBlend(mat, name, ip, im, untextured);

        // Harvest the material's base-colour image for texture reimport (best-effort;
        // first material seen per texture slot wins - slots are deduped by image upstream).
        try
        {
            if (ip.TexIdx >= 0 && !untextured)
            {
                if (hasTexture)
                {
                    if (!im.TextureImages.ContainsKey(ip.TexIdx))
                    {
                        var img = mat.FindChannel("BaseColor")!.Value.Texture!.PrimaryImage!.Content;
                        if (img.IsValid) im.TextureImages[ip.TexIdx] = img;
                    }
                }
                else
                {
                    // Textureless (flat-colour) material: the engine has no untextured path -
                    // every page's texture-context record samples a texture - so without an
                    // image the slot keeps the TEMPLATE's texture ("old model texture" bleeding
                    // through). Bake the base colour into a small solid image instead, and
                    // neutralise the page colour so the tint isn't applied twice
                    // (texel * page colour).
                    if (!im.TextureImages.ContainsKey(ip.TexIdx))
                    {
                        var c = ip.BaseColor;
                        byte rr = (byte)Math.Clamp((int)MathF.Round(c.X * 255f), 0, 255);
                        byte gg = (byte)Math.Clamp((int)MathF.Round(c.Y * 255f), 0, 255);
                        byte bb = (byte)Math.Clamp((int)MathF.Round(c.Z * 255f), 0, 255);
                        byte aa = (byte)Math.Clamp((int)MathF.Round(c.W * 255f), 0, 255);
                        im.TextureImages[ip.TexIdx] = SolidImage(rr, gg, bb, aa);
                    }
                    ip.BaseColor = Vector4.One;
                }
            }
        }
        catch { /* texture harvest must never break geometry import */ }

        // A DotR-only copy of the texture (DoD exporter: animated mask positions baked into extra
        // cells, which the slot's records copy from): it replaces the base-colour image as the slot's
        // picture, whichever material of the slot was seen first.
        try
        {
            if (ip.TexIdx >= 0 && !untextured && DotrImage(mat) is { } di && di.Content.IsValid)
                im.TextureImages[ip.TexIdx] = di.Content;
        }
        catch { }

        // The slot's retail texture animation, carried on the extracted glb's material
        // (ModelExtractor.InjectTextureAnimationRecords). Re-embedded verbatim by the
        // assembler, so an unedited round trip keeps the animation the extractor baked away.
        try
        {
            if (ip.TexIdx >= 0 && !untextured && !im.TextureAnimRecords.ContainsKey(ip.TexIdx)
                && mat.Extras is System.Text.Json.Nodes.JsonObject mex
                && mex["DOTR_texAnimRecords"] is System.Text.Json.Nodes.JsonArray recs && recs.Count > 0)
                im.TextureAnimRecords[ip.TexIdx] = PackTexAnimRecords(recs);
        }
        catch { /* a malformed extras block just means no animation */ }

        // Which record and cell each baked frame image came from, so an edited frame can be
        // written back into its source cell (see TextureAnimStamps / ApplyEditedAnimFrames).
        try
        {
            if (ip.TexIdx >= 0 && !untextured && !im.TextureAnimStamps.ContainsKey(ip.TexIdx)
                && mat.Extras is System.Text.Json.Nodes.JsonObject aex
                && aex["DOTR_texAnim"] is System.Text.Json.Nodes.JsonObject anim
                && anim["frames"] is System.Text.Json.Nodes.JsonArray frames)
            {
                var byFrame = new Dictionary<int, List<(int Rec, int Cell, uint Hash)>>();
                for (int f = 0; f < frames.Count; f++)
                {
                    if (frames[f] is not System.Text.Json.Nodes.JsonObject fo
                        || fo["stamps"] is not System.Text.Json.Nodes.JsonArray sts) continue;
                    var list = new List<(int, int, uint)>();
                    foreach (var st in sts)
                        if (st is System.Text.Json.Nodes.JsonObject so)
                            list.Add(((int)so["rec"]!, (int)so["cell"]!, (uint)so["hash"]!));
                    if (list.Count > 0) byFrame[f] = list;
                }
                if (byFrame.Count > 0) im.TextureAnimStamps[ip.TexIdx] = byFrame;
            }
        }
        catch { /* a malformed extras block just means no write-back */ }
    }

    /// uv' = offset + R(rotation) * (scale * uv), the KHR_texture_transform matrix, when the base
    /// colour texture has one and samples TEXCOORD_0 (the only set the importer reads).
    private static void ApplyBaseTextureTransform(Material? mat, ImportedPrimitive ip)
    {
        try
        {
            var ch = mat?.FindChannel("BaseColor");
            if (ch is not { } c || c.Texture == null || c.TextureCoordinate != 0) return;
            var tt = c.TextureTransform;
            if (tt == null || (tt.TextureCoordinateOverride ?? 0) != 0) return;
            var s = tt.Scale; var o = tt.Offset; float r = tt.Rotation;
            if (s == Vector2.One && o == Vector2.Zero && r == 0f) return;
            float cs = MathF.Cos(r), sn = MathF.Sin(r);
            foreach (var v in ip.Vertices)
            {
                float u = v.Uv.X * s.X, w = v.Uv.Y * s.Y;
                v.Uv = new Vector2(cs * u + sn * w + o.X, -sn * u + cs * w + o.Y);
            }
        }
        catch { /* a malformed transform just leaves the UVs as they are */ }
    }

    /// The material's "DOTR_image" extras: a glb image to encode for its slot instead of the base colour.
    private static SharpGLTF.Schema2.Image? DotrImage(Material? mat)
    {
        if (mat?.Extras is not System.Text.Json.Nodes.JsonObject ex
            || ex["DOTR_image"] is not System.Text.Json.Nodes.JsonObject di
            || di["image"] is not { } idx) return null;
        int i = idx.GetValue<int>();
        var imgs = mat.LogicalParent.LogicalImages;
        return i >= 0 && i < imgs.Count ? imgs[i] : null;
    }

    /// With a DOTR_image the base-colour texture sits at the top left of a larger image: its
    /// "uvScale" (base size / image size) maps the faces' UVs onto it, and an optional "uvOffset"
    /// (image UV) moves them to their own cell there (an atlas flipbook's display cell).
    private static void ApplyDotrImageUv(Material? mat, ImportedPrimitive ip)
    {
        try
        {
            if (DotrImage(mat) == null
                || mat!.Extras is not System.Text.Json.Nodes.JsonObject ex
                || ex["DOTR_image"]?["uvScale"] is not System.Text.Json.Nodes.JsonArray s || s.Count != 2) return;
            var k = new Vector2(s[0]!.GetValue<float>(), s[1]!.GetValue<float>());
            var o = ex["DOTR_image"]?["uvOffset"] is System.Text.Json.Nodes.JsonArray oa && oa.Count == 2
                ? new Vector2(oa[0]!.GetValue<float>(), oa[1]!.GetValue<float>()) : Vector2.Zero;
            foreach (var v in ip.Vertices) v.Uv = v.Uv * k + o;
        }
        catch { }
    }

    /// True when the material's base-colour channel carries an actual image.
    private static bool HasBaseColorTexture(Material m)
    {
        try
        {
            var ch = m.FindChannel("BaseColor");
            return ch.HasValue && ch.Value.Texture?.PrimaryImage != null;
        }
        catch { return false; }
    }

    /// The extractor's name for a material with no texture (ModelExtractor.GetMaterial).
    private static bool IsUntexturedMaterial(string? name) =>
        name != null && name.StartsWith("mat_untextured", StringComparison.OrdinalIgnoreCase);

    /// Pack "DOTR_texAnimRecords" back into the 24-byte entries SzModel_LoadTexAnim_Pipe
    /// reads, the inverse of ModelExtractor.TexAnimRecords: flags@0 (bit0 last, bit1 own
    /// clock, bits 2-4 mode, bits 5+ direction), motions@1, cols|rows<<4 @2, state@3,
    /// p0@4, p1@6, size@8, src@0xC, dst@0x10; own clock @0x14 from "clock" (default zero).
    private static byte[] PackTexAnimRecords(System.Text.Json.Nodes.JsonArray recs)
    {
        const int EntrySize = 0x18;
        var o = new byte[recs.Count * EntrySize];
        for (int i = 0; i < recs.Count; i++)
        {
            if (recs[i] is not System.Text.Json.Nodes.JsonObject r) continue;
            int b = i * EntrySize;
            int I(string k) => r[k] is { } v ? v.GetValue<int>() : 0;
            int Pair(string k, int j) => r[k] is System.Text.Json.Nodes.JsonArray a && a.Count > j
                ? a[j]!.GetValue<int>() : 0;
            bool ownClock = r["ownClock"] is { } oc && oc.GetValue<bool>();
            o[b] = (byte)((i == recs.Count - 1 ? 1 : 0) | (ownClock ? 2 : 0)
                          | ((I("mode") & 7) << 2) | ((I("direction") & 7) << 5));
            o[b + 1] = (byte)I("motions");
            o[b + 2] = (byte)((I("cols") & 15) | ((I("rows") & 15) << 4));
            o[b + 3] = (byte)I("state");
            void W16(int at, int v) => BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(b + at), (ushort)v);
            W16(0x04, I("p0"));
            W16(0x06, I("p1"));
            W16(0x08, Pair("size", 0));
            W16(0x0A, Pair("size", 1));
            W16(0x0C, Pair("src", 0));
            W16(0x0E, Pair("src", 1));
            W16(0x10, Pair("dst", 0));
            W16(0x12, Pair("dst", 1));
            // own clock's starting value: phases a blink record (mode 6 copies when clock % p0 == 0)
            BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(b + 0x14), (uint)I("clock"));
        }
        return o;
    }

    /// Put back the colour block (c0, c1, c2) the extractor saved in DOTR_blend. c0 and c2
    /// have no glb field and are always restored. The base colour is only a display of c1
    /// (an alpha-chain material shows c0 + c1/2, an opaque neutral or zero c1 shows white),
    /// so while it is still the one the extractor wrote, the raw c1 is restored as well.
    private static void RestoreColourBlock(Material mat, ImportedPrimitive ip)
    {
        try
        {
            if (mat.Extras is not System.Text.Json.Nodes.JsonObject ex) return;
            if (ex["DOTR_blend"] is not System.Text.Json.Nodes.JsonObject blend) return;
            static (int, int, int)? Rgb(System.Text.Json.Nodes.JsonNode? n) =>
                n is System.Text.Json.Nodes.JsonArray a && a.Count == 3
                    ? (a[0]!.GetValue<int>(), a[1]!.GetValue<int>(), a[2]!.GetValue<int>()) : null;
            if (Rgb(blend["materialColor0"]) is { } c0) ip.Color0 = c0;
            if (Rgb(blend["materialColor2"]) is { } c2) ip.Color2 = c2;
            if (blend["displayColor"] is not System.Text.Json.Nodes.JsonArray shown || shown.Count != 3) return;
            var c = ip.BaseColor;
            const float tol = 1.5f / 255f;
            if (MathF.Abs(c.X - shown[0]!.GetValue<float>()) > tol ||
                MathF.Abs(c.Y - shown[1]!.GetValue<float>()) > tol ||
                MathF.Abs(c.Z - shown[2]!.GetValue<float>()) > tol) return; // edited: keep it
            ip.RawColor1 = (blend["materialColorR"]!.GetValue<int>(),
                            blend["materialColorG"]!.GetValue<int>(),
                            blend["materialColorB"]!.GetValue<int>());
            // an opaque page's alpha still matters to its alpha test (card 53's alpha-0
            // untextured pages are never drawn); keep c1.w unless the glb alpha was edited
            if (blend["materialAlpha"] is { } ma && MathF.Abs(c.W - ma.GetValue<float>()) <= tol
                && blend["materialAlphaW"] is { } aw)
                ip.BaseColor = c with { W = aw.GetValue<float>() / 128f };
        }
        catch { }
    }

    /// A custom glb's "DOTR_colors" extras {c0, c1}: the material record's c0 (+0x10) and c1 (+0x20)
    /// rows, 0-255 (the DoD exporter writes DoD's ambient and diffuse: neutral 127 / 178).
    private static void ParseDotrColors(Material mat, ImportedPrimitive ip)
    {
        try
        {
            if (mat.Extras is not System.Text.Json.Nodes.JsonObject ex
                || ex["DOTR_colors"] is not System.Text.Json.Nodes.JsonObject col) return;
            static (int, int, int)? Rgb(System.Text.Json.Nodes.JsonNode? n) =>
                n is System.Text.Json.Nodes.JsonArray a && a.Count == 3
                    ? (Math.Clamp(a[0]!.GetValue<int>(), 0, 255), Math.Clamp(a[1]!.GetValue<int>(), 0, 255),
                       Math.Clamp(a[2]!.GetValue<int>(), 0, 255)) : null;
            if (Rgb(col["c0"]) is { } c0) ip.Color0 = c0;
            if (Rgb(col["c1"]) is { } c1) ip.RawColor1 = c1;
        }
        catch { }
    }

    /// The GS blend. An extracted glb names it ("_gs48" = ALPHA_1 0x48, additive). A custom glb
    /// marks it in DOTR_blend {"mode": "additive"} without the extractor's raw "gsAlpha" (the DoD
    /// exporter does): drawn in the alpha chain with retail Ameba's additive colour block
    /// (c1 178, c0 127, full alpha), its texture alpha dropped (see OpaqueAlphaSlots).
    private static void ParseBlend(Material mat, string name, ImportedPrimitive ip, ImportedModel im, bool untextured)
    {
        var tst = System.Text.RegularExpressions.Regex.Match(name, @"_t([0-9A-Fa-f]{5})(?:_|$)");
        if (tst.Success) ip.GsTest = Convert.ToUInt32(tst.Groups[1].Value, 16);
        var gs = System.Text.RegularExpressions.Regex.Match(name, @"_gs([0-9A-Fa-f]{2})(?:_|$)");
        if (gs.Success) { ip.GsAlpha = Convert.ToByte(gs.Groups[1].Value, 16); return; }
        try
        {
            if (mat.Extras is not System.Text.Json.Nodes.JsonObject ex
                || ex["DOTR_blend"] is not System.Text.Json.Nodes.JsonObject blend
                || blend["gsAlpha"] != null
                || blend["mode"]?.GetValue<string>() != "additive") return;
            ip.IsAlt = true;
            ip.GsAlpha = 0x48;
            ip.RawColor1 ??= (178, 178, 178);
            ip.BaseColor = ip.BaseColor with { W = 1f };
            if (!untextured && ip.TexIdx >= 0) im.OpaqueAlphaSlots.Add(ip.TexIdx);
        }
        catch { }
    }

    /// A custom glb's translucent material without a DotR blend of its own (alphaMode BLEND or
    /// MASK, alpha in the texture and/or the base colour factor). DotR's alpha is one value per
    /// page (c1.w, 128 = 1.0) times a 1-bit texture alpha, so the texture alpha under this
    /// primitive's triangles is sampled: no translucent texels -> a cutout (TEST 0x703FD, AREF 63)
    /// if it has holes, else opaque; otherwise the alpha chain (normal blend) with c1.w = the mean
    /// alpha of the covered texels and the texture alpha cut to coverage (CoverageAlphaSlots).
    private static void ParsePlainAlpha(Material? mat, ImportedPrimitive ip, ImportedModel im)
    {
        try
        {
            if (mat == null || ip.Untextured || ip.IsAlt || ip.GsTest != 0 || ip.GsAlpha != 0x44) return;
            if (mat.Alpha == AlphaMode.OPAQUE) return;
            if (mat.Extras is System.Text.Json.Nodes.JsonObject ex
                && ex["DOTR_blend"] is System.Text.Json.Nodes.JsonObject blend
                && (blend["gsAlpha"] != null || blend["mode"]?.GetValue<string>() is "additive" or "multiply")) return;
            float factor = Math.Clamp(ip.BaseColor.W, 0f, 1f);
            var plane = AlphaPlane(mat, im);
            long n = 0, holes = 0; double sum = 0;
            if (plane is { } pl)
                foreach (byte a in SampleAlpha(ip, pl.W, pl.H, pl.A))
                {
                    n++;
                    if (a < 8) holes++; else sum += a / 255.0;
                }
            float mean = n > holes ? (float)(sum / (n - holes)) : 1f;
            if (mat.Alpha == AlphaMode.MASK) mean = 1f;   // a cutout: coverage only
            float eff = mean * factor;
            if (eff >= 0.97f)
            {
                ip.BaseColor = ip.BaseColor with { W = 1f };
                if (holes == 0) return;
                ip.GsTest = 0x703FD;
            }
            else
            {
                ip.IsAlt = true;
                // retail's alpha-chain pages keep the neutral c1 (178); MatKey gives alt pages full range
                if (ip.BaseColor.X >= 0.9999f && ip.BaseColor.Y >= 0.9999f && ip.BaseColor.Z >= 0.9999f)
                    ip.RawColor1 ??= (178, 178, 178);
                ip.BaseColor = ip.BaseColor with { W = MathF.Max(eff, 1f / 128f) };
            }
            if (ip.TexIdx >= 0) im.CoverageAlphaSlots.Add(ip.TexIdx);
        }
        catch { /* leave the page opaque */ }
    }

    /// The alpha plane of the material's base-colour image (null: no image or undecodable).
    private static (int W, int H, byte[] A)? AlphaPlane(Material mat, ImportedModel im)
    {
        // a DOTR_image replaces the base colour for the slot, and the faces' UVs already point into it
        var gi = DotrImage(mat) ?? mat.FindChannel("BaseColor")?.Texture?.PrimaryImage;
        if (gi == null) return null;
        if (im.AlphaPlanes.TryGetValue(gi.LogicalIndex, out var cached)) return cached;
        (int, int, byte[])? res = null;
        var img = gi.Content;
        if (img.IsValid)
        {
            Image cpu = DecodeRgba(img);
            if (cpu.Width > 0 && cpu.Height > 0)
            {
                var rgba = Pixels(cpu);
                var a = new byte[cpu.Width * cpu.Height];
                for (int i = 0; i < a.Length; i++) a[i] = rgba[i * 4 + 3];
                res = (cpu.Width, cpu.Height, a);
            }
            Raylib.UnloadImage(cpu);
        }
        im.AlphaPlanes[gi.LogicalIndex] = res;
        return res;
    }

    /// Texel alphas under the primitive's triangles (repeat-wrapped UVs, about 1k samples per
    /// triangle at most, its centroid when it covers no texel centre).
    private static IEnumerable<byte> SampleAlpha(ImportedPrimitive ip, int w, int h, byte[] a)
    {
        byte At(float x, float y)
        {
            int ix = ((int)MathF.Floor(x) % w + w) % w, iy = ((int)MathF.Floor(y) % h + h) % h;
            return a[iy * w + ix];
        }
        foreach (var (i0, i1, i2) in ip.Triangles)
        {
            Vector2 p0 = ip.Vertices[i0].Uv * new Vector2(w, h), p1 = ip.Vertices[i1].Uv * new Vector2(w, h),
                    p2 = ip.Vertices[i2].Uv * new Vector2(w, h);
            float x0 = MathF.Min(p0.X, MathF.Min(p1.X, p2.X)), x1 = MathF.Max(p0.X, MathF.Max(p1.X, p2.X));
            float y0 = MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y)), y1 = MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y));
            float area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
            bool any = false;
            if (MathF.Abs(area) > 1e-6f && (x1 - x0) * (y1 - y0) < 1e8f)
            {
                float step = MathF.Max(1f, MathF.Sqrt((x1 - x0) * (y1 - y0) / 1024f));
                for (float y = MathF.Floor(y0) + 0.5f; y <= y1; y += step)
                for (float x = MathF.Floor(x0) + 0.5f; x <= x1; x += step)
                {
                    float b1 = ((x - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (y - p0.Y)) / area;
                    float b2 = ((p1.X - p0.X) * (y - p0.Y) - (x - p0.X) * (p1.Y - p0.Y)) / area;
                    if (b1 < 0 || b2 < 0 || b1 + b2 > 1) continue;
                    any = true;
                    yield return At(x, y);
                }
            }
            if (!any) { var c = (p0 + p1 + p2) / 3f; yield return At(c.X, c.Y); }
        }
    }

    /// 8x8 single-colour PNG wrapped as a glTF MemoryImage (for textureless materials).
    private static SharpGLTF.Memory.MemoryImage SolidImage(byte r, byte g, byte b, byte a)
    {
        var rgba = new byte[8 * 8 * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        { rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = a; }
        return new SharpGLTF.Memory.MemoryImage(PngEncoder.Encode(8, 8, rgba));
    }

    /// Slot-sharing key for unnamed materials: same base-colour image => same slot;
    /// flat-colour materials key by their colour value.
    private static string MaterialShareKey(Material m)
    {
        try
        {
            var ch = m.FindChannel("BaseColor");
            if (ch.HasValue && ch.Value.Texture?.PrimaryImage != null)
                return "img:" + ch.Value.Texture.PrimaryImage.LogicalIndex;
            if (ch.HasValue)
            {
                var c = ch.Value.Color;
                return $"rgba:{c.X:F4},{c.Y:F4},{c.Z:F4},{c.W:F4}";
            }
        }
        catch { }
        return "mat:" + m.LogicalIndex;
    }

    /// Highest texture slot a "mat_texN" name may ask for. RH2 blocks per model; retail's
    /// largest monster is far below this, so anything above is a typo, not a real slot.
    private const int MaxTexSlot = 63;

    /// Parses the explicit "mat_texN" naming convention (suffix "_alt" allowed elsewhere).
    private static bool TryParseMatTexIndex(string? name, out int tex)
    {
        tex = -1;
        if (string.IsNullOrEmpty(name)) return false;
        int p = name.IndexOf("mat_tex", StringComparison.OrdinalIgnoreCase);
        if (p < 0) return false;
        int start = p + "mat_tex".Length, end = start;
        while (end < name.Length && char.IsDigit(name[end])) end++;
        return end > start && int.TryParse(name.AsSpan(start, end - start), out tex);
    }

    /// The GS CLAMP byte the extractor encodes in a material name as "_c<hex>" (see
    /// ModelExtractor's matName: "..._gsXX_tXXXXX_cN[_rgb..][_k..]"). Bits 0-1 are WMS and
    /// bits 2-3 WMT; 1 means CLAMP, 0 REPEAT. False when the name carries no such tag.
    private static bool ParseGsClampTag(string? name, out int clamp)
    {
        clamp = 0;
        if (string.IsNullOrEmpty(name)) return false;
        int p = name.IndexOf("_c", StringComparison.Ordinal);
        if (p < 0) return false;
        int start = p + 2, end = start;
        while (end < name.Length && Uri.IsHexDigit(name[end])) end++;
        if (end == start || end - start > 2) return false;
        if (end < name.Length && name[end] != '_') return false;
        clamp = Convert.ToInt32(name.Substring(start, end - start), 16);
        return true;
    }

    private static Vector3 SafeNormalize(Vector3 v)
    {
        float len = v.Length();
        return len > 1e-6f ? v / len : Vector3.UnitY;
    }

    /// Folds out-of-[0,1] UVs back into the unit tile (glTF/Blender REPEAT semantics) so the
    /// PS2's CLAMP sampler reproduces the artist's tiled result instead of crushing the UV
    /// onto a texture edge (Charizard's black face/wings). Done PER TRIANGLE using the floor
    /// of the triangle's UV centroid, so all three corners shift by the same whole-tile
    /// offset and the texture never swims across a face. Vertices shared between triangles
    /// that land in different tiles are duplicated, because they need different shifts.
    private static void WrapPrimitiveUvs(ImportedPrimitive ip, bool foldS = true, bool foldT = true)
    {
        if (!foldS && !foldT) return;
        // quick out: nothing outside [0,1) on an axis that is being folded
        bool any = false;
        foreach (var v in ip.Vertices)
            if ((foldS && (v.Uv.X < 0f || v.Uv.X >= 1f)) || (foldT && (v.Uv.Y < 0f || v.Uv.Y >= 1f))) { any = true; break; }
        if (!any) return;

        var newTris = new List<(int A, int B, int C)>(ip.Triangles.Count);
        // (vertexIndex, tileX, tileY) -> resolved vertex index in the rewritten list
        var remap = new Dictionary<(int, int, int), int>();

        int Resolve(int vi, int tx, int ty)
        {
            var key = (vi, tx, ty);
            if (remap.TryGetValue(key, out int got)) return got;
            var src = ip.Vertices[vi];
            int outIdx;
            if (tx == 0 && ty == 0)
            {
                outIdx = vi;                 // tile (0,0): reuse the original vertex slot
            }
            else
            {
                var dup = new ImportedVertex
                {
                    Pos = src.Pos, FramePos = src.FramePos, Normal = src.Normal,
                    Uv = new Vector2(src.Uv.X - tx, src.Uv.Y - ty),
                    Influences = src.Influences,   // share (read-only downstream)
                    Game = src.Game,
                };
                outIdx = ip.Vertices.Count;
                ip.Vertices.Add(dup);
            }
            remap[key] = outIdx;
            return outIdx;
        }

        foreach (var (a, b, c) in ip.Triangles)
        {
            var ua = ip.Vertices[a].Uv; var ub = ip.Vertices[b].Uv; var uc = ip.Vertices[c].Uv;
            float cx = (ua.X + ub.X + uc.X) / 3f;
            float cy = (ua.Y + ub.Y + uc.Y) / 3f;
            int tx = foldS ? (int)MathF.Floor(cx) : 0, ty = foldT ? (int)MathF.Floor(cy) : 0;
            newTris.Add((Resolve(a, tx, ty), Resolve(b, tx, ty), Resolve(c, tx, ty)));
        }
        ip.Triangles = newTris;

        // finally fold any tile-(0,0) originals still sitting outside [0,1] (e.g. a vertex at
        // exactly 1.0, or one only ever used by tile-(0,0) tris but authored at 1.x)
        foreach (var v in ip.Vertices)
        {
            float wx = v.Uv.X, wy = v.Uv.Y;
            if (foldS)
            {
                wx = v.Uv.X - MathF.Floor(v.Uv.X);
                if (wx == 0f && v.Uv.X != 0f) wx = 1f;
            }
            if (foldT)
            {
                wy = v.Uv.Y - MathF.Floor(v.Uv.Y);
                if (wy == 0f && v.Uv.Y != 0f) wy = 1f;
            }
            v.Uv = new Vector2(wx, wy);
        }
    }

    /// Decode the glb's embedded base-colour images (per "mat_texN" texture index) into raw
    /// RGBA TextureInputs for the RH2 re-encoder. Decoded by LoadRgba, the same stb_image
    /// decode as the model viewer. Best-effort: a
    /// texture that fails to decode is skipped and keeps the template's original RH2.
    /// `maxDim`: same per-axis cap used everywhere else native textures get built (see
    /// Rh2Encoder.ChooseTarget / Rh0Assembler's maxTexDim) - default 512, matching the
    /// rest of this codebase's default. THIS MATTERS MORE for animated slots than static
    /// ones: a flipbook sheet's width grows with frame count, so an unclamped per-frame
    /// cell size (e.g. a raw 1012px-wide source rounding up to 1024) multiplies directly
    /// by frame count in the final sheet - a 4-frame flipbook from an uncapped 1024px
    /// source becomes a 4096x1024 sheet (~4 MB encoded), which cannot fit in either a
    /// fixed 1 MiB monster.mrg slot or a typical MODEL.MRG model's real budget. Clamping
    /// each cell to maxDim first keeps sheet size bounded and predictable the same way
    /// static textures already are.
    public static List<Rh0Assembler.TextureInput> DecodeGlbTextures(ImportedModel model, int maxDim = 512)
    {
        var inputs = new List<Rh0Assembler.TextureInput>();
        var animSlots = model.TextureAnimFrames.Keys.ToHashSet();

        foreach (var (texIdx, img) in model.TextureImages)
        {
            if (animSlots.Contains(texIdx)) continue; // handled by the anim branch below instead
            try
            {
                Image cpu = DecodeRgba(img);
                if (cpu.Width <= 0 || cpu.Height <= 0) continue;
                byte[] rgba = Pixels(cpu);
                ApplyEditedAnimFrames(model, texIdx, rgba, cpu.Width, cpu.Height);
                if (model.OpaqueAlphaSlots.Contains(texIdx))
                    for (int a = 3; a < rgba.Length; a += 4) rgba[a] = 255;
                else if (model.CoverageAlphaSlots.Contains(texIdx))
                    for (int a = 3; a < rgba.Length; a += 4) rgba[a] = rgba[a] >= 8 ? (byte)255 : (byte)0;
                inputs.Add(new Rh0Assembler.TextureInput {
                    TexIdx = texIdx,
                    Rgba = rgba,
                    Width = cpu.Width,
                    Height = cpu.Height,
                    AnimRecords = model.TextureAnimRecords.TryGetValue(texIdx, out var ar) ? ar : null,
                    AnimRecordsInImagePixels = !model.IsDotrExtractedGlb,
                });
                Raylib.UnloadImage(cpu);
            }
            catch
            {
                /* skip this texture; the template RH2 stays */
            }
        }

        // --- animated slots: decode every frame, pack into one sprite sheet ---
        // The game plays a flipbook by copying cell i of a cols x rows grid onto cell 0 every draw
        // (Rh0Assembler.BuildCustomTexAnimEntry), so the sheet is that grid with frame 0 in cell 0,
        // and the faces must sample cell 0 only: TextureAnimUvScale tells RebuildPipeline how far to
        // squeeze their UVs. Every frame is resized to frame 0's size (clamped to maxDim), then
        // halved until a grid of at most 15 x 15 cells fits MaxAnimSheetPixels (TryAnimSheetLayout).
        foreach (var (texIdx, frames) in model.TextureAnimFrames)
        {
            var images = new List<Image>();
            try
            {
                foreach (var f in frames)
                {
                    if (images.Count == Rh0Assembler.MaxAnimFrames)
                    {
                        model.TexSlotWarnings.Add($"texture slot {texIdx}: {frames.Count} animation frames, " +
                            $"only the first {Rh0Assembler.MaxAnimFrames} are used");
                        break;
                    }
                    Image cpu = DecodeRgba(f);
                    if (cpu.Width <= 0 || cpu.Height <= 0) continue;
                    images.Add(cpu);
                }
                if (images.Count < 2) continue; // matches TextureAnimFrames' own >=2 rule

                int n = images.Count;
                int fw = images[0].Width, fh = images[0].Height;
                if (fw > maxDim || fh > maxDim)
                {
                    float scale = Math.Min((float)maxDim / fw, (float)maxDim / fh);
                    fw = Math.Max(1, (int)Math.Round(fw * scale));
                    fh = Math.Max(1, (int)Math.Round(fh * scale));
                }
                AnimSheetLayout layout;
                while (!TryAnimSheetLayout(n, fw, fh, out layout))
                {
                    if (fw == 1 && fh == 1) break;
                    fw = Math.Max(1, fw / 2);
                    fh = Math.Max(1, fh / 2);
                }
                if (layout.Cols == 0)
                {
                    model.TexSlotWarnings.Add($"texture slot {texIdx}: {n} animation frames do not fit one texture; not animated");
                    continue;
                }
                if (images.Any(i => i.Width != images[0].Width || i.Height != images[0].Height))
                    model.TexSlotWarnings.Add($"texture slot {texIdx}: animation frames differ in size; all resized to frame 0's");

                // Cells (POT, like retail's) hold the frame at their top-left; the loop runs over all
                // cols * rows cells, so cells past the last frame repeat it.
                int sheetW = layout.SheetW, sheetH = layout.SheetH, bw = layout.CellW, bh = layout.CellH;
                byte[] sheet = new byte[sheetW * sheetH * 4];
                byte[] cell = new byte[fw * fh * 4];
                for (int c = 0; c < layout.Cols * layout.Rows; c++)
                {
                    if (c < n)
                    {
                        Image img = images[c];
                        if (img.Width != fw || img.Height != fh)
                        {
                            Raylib.ImageResize(ref img, fw, fh);
                            images[c] = img;
                        }
                        unsafe { System.Runtime.InteropServices.Marshal.Copy((IntPtr)img.Data, cell, 0, cell.Length); }
                    }
                    int cx = (c % layout.Cols) * bw, cy = (c / layout.Cols) * bh;
                    for (int y = 0; y < fh; y++)
                        Buffer.BlockCopy(cell, y * fw * 4, sheet, ((cy + y) * sheetW + cx) * 4, fw * 4);
                }

                inputs.Add(new Rh0Assembler.TextureInput {
                    TexIdx = texIdx,
                    Rgba = sheet,
                    Width = sheetW,
                    Height = sheetH,
                    AnimFrameCount = layout.Cols * layout.Rows,
                    AnimCols = layout.Cols,
                    AnimRows = layout.Rows,
                    AnimBlockW = bw,
                    AnimBlockH = bh,
                    AnimTicksPerFrame = model.TextureAnimTicks.TryGetValue(texIdx, out int customTicks)
                        ? customTicks
                        : 5, // default matches the one real retail sample (see TextureInput remarks)
                });
                model.TextureAnimUvScale[texIdx] = new Vector2((float)fw / sheetW, (float)fh / sheetH);
            }
            catch
            {
                /* skip this texture's animation; falls through with no TextureInput for
                   this slot, so the template's existing texture/anim is kept untouched */
            }
            finally
            {
                foreach (var img in images) Raylib.UnloadImage(img);
            }
        }

        return inputs;
    }

    private struct AnimSheetLayout
    {
        public int Cols, Rows, CellW, CellH, SheetW, SheetH;
    }

    /// A sprite sheet is encoded at exactly its packed size (Rh0Assembler never reduces it), so it
    /// has to fit the 1 MiB block on its own terms: 512 x 512 texels, 256 KB at 8 bpp, the same
    /// ceiling as the default maxDim of a static texture. (A 512 x 1024 sheet overflowed a monster
    /// slot by 13 KB.)
    private const long MaxAnimSheetPixels = 512 * 512;

    /// Smallest grid of POT cells for n frames of fw x fh: at most 15 x 15 cells (the entry's
    /// nibbles), the sheet POT with width >= 128 and height >= 64 (Rh2Encoder.BuildNative), at
    /// most 1024 on either side (the GS limit) and MaxAnimSheetPixels in all. Cols = 0 when
    /// nothing fits.
    private static bool TryAnimSheetLayout(int n, int fw, int fh, out AnimSheetLayout best)
    {
        best = default;
        int bw = Rh2Encoder.NextPot(fw), bh = Rh2Encoder.NextPot(fh);
        long bestArea = long.MaxValue;
        for (int cols = 1; cols <= 15; cols++)
        {
            int rows = (n + cols - 1) / cols;
            if (rows > 15) continue;
            int w = Rh2Encoder.NextPot(Math.Max(bw * cols, 128));
            int h = Rh2Encoder.NextPot(Math.Max(bh * rows, 64));
            if (w > 1024 || h > 1024 || (long)w * h > MaxAnimSheetPixels) continue;
            // fewer spare cells first (they repeat the last frame), then the smaller sheet
            long area = (long)(cols * rows - n) << 32 | (long)w * h;
            if (area < bestArea)
            {
                bestArea = area;
                best = new AnimSheetLayout { Cols = cols, Rows = rows, CellW = bw, CellH = bh, SheetW = w, SheetH = h };
            }
        }
        return best.Cols > 0;
    }

    /// Fold an edited animation frame of an extracted glb back into the base image.
    ///
    /// An animated retail slot is ONE texture: each record makes the GS copy cell i of a
    /// cols x rows sheet inside that texture onto a fixed destination rectangle of the same
    /// texture (TexAnim), so a frame image is just the base image with those cells stamped
    /// in. Repainting a cell in the base image therefore already works; repainting a FRAME
    /// has to be mapped back to the cell the game copies it from, which is what this does.
    /// The extractor's per-frame stamps say which record and cell made each destination
    /// rectangle and what its pixels hashed to when extracted: an unchanged hash means the
    /// frame was not touched and the base image's own (possibly edited) cell wins, a changed
    /// hash means the frame was painted and its pixels replace the cell. The records
    /// themselves are untouched, so the animation still plays exactly as retail's.
    private static void ApplyEditedAnimFrames(ImportedModel model, int texIdx, byte[] rgba, int w, int h)
    {
        if (!model.TextureAnimEditFrames.TryGetValue(texIdx, out var frames)) return;
        if (!model.TextureAnimStamps.TryGetValue(texIdx, out var stampsByFrame)) return;
        if (!model.TextureAnimRecords.TryGetValue(texIdx, out var recs)) return;

        foreach (var (frame, img) in frames)
        {
            if (!stampsByFrame.TryGetValue(frame, out var stamps)) continue;
            try
            {
                Image cpu = DecodeRgba(img);
                try
                {
                    // The records' rectangles are in the retail texture's pixel space, so a
                    // frame that is no longer the base image's size cannot be mapped back.
                    if (cpu.Width != w || cpu.Height != h) continue;
                    byte[] fr = Pixels(cpu);

                    for (int k = 0; k < stamps.Count; k++)
                    {
                        var (rec, cell, hash) = stamps[k];
                        int o = rec * 0x18;
                        if (o + 0x18 > recs.Length) continue;
                        int cols = Math.Max(1, recs[o + 2] & 0xF);
                        int bw = U16L(recs, o + 0x08), bh = U16L(recs, o + 0x0A);
                        int sx = U16L(recs, o + 0x0C) + bw * (cell % cols);
                        int sy = U16L(recs, o + 0x0E) + bh * (cell / cols);
                        int dx = U16L(recs, o + 0x10), dy = U16L(recs, o + 0x12);
                        if (bw <= 0 || bh <= 0) continue;
                        if (ModelExtractor.RectHash(fr, w, h, dx, dy, bw, bh) == hash) continue;  // frame untouched

                        for (int y = 0; y < bh; y++)
                        {
                            int syy = sy + y, dyy = dy + y;
                            if ((uint)syy >= (uint)h || (uint)dyy >= (uint)h) continue;
                            for (int x = 0; x < bw; x++)
                            {
                                int sxx = sx + x, dxx = dx + x;
                                if ((uint)sxx >= (uint)w || (uint)dxx >= (uint)w) continue;
                                // A later stamp of the same frame drew over this pixel, so
                                // it is not this cell's (the bake stamps in record order).
                                if (CoveredLater(recs, stamps, k, dxx, dyy)) continue;
                                int d = (syy * w + sxx) * 4, sO = (dyy * w + dxx) * 4;
                                rgba[d] = fr[sO]; rgba[d + 1] = fr[sO + 1];
                                rgba[d + 2] = fr[sO + 2]; rgba[d + 3] = fr[sO + 3];
                            }
                        }
                    }
                }
                finally { Raylib.UnloadImage(cpu); }
            }
            catch { /* a frame that will not decode just means no write-back for it */ }
        }
    }


    /// Is (x,y) inside the destination rectangle of a stamp after index k of this frame?
    private static bool CoveredLater(byte[] recs, List<(int Rec, int Cell, uint Hash)> stamps, int k, int x, int y)
    {
        for (int j = k + 1; j < stamps.Count; j++)
        {
            int o = stamps[j].Rec * 0x18;
            if (o + 0x18 > recs.Length) continue;
            int bw = U16L(recs, o + 0x08), bh = U16L(recs, o + 0x0A);
            int dx = U16L(recs, o + 0x10), dy = U16L(recs, o + 0x12);
            if (x >= dx && x < dx + bw && y >= dy && y < dy + bh) return true;
        }
        return false;
    }

    private static int U16L(byte[] b, int o) => b[o] | (b[o + 1] << 8);

    private static bool IsPow2Local(int v) => v > 0 && (v & (v - 1)) == 0;

    /// A glTF image decoded by Raylib (stb_image), in its own pixel format; Width is 0 when it
    /// does not decode. The viewer uploads these and the importer encodes them, so both see the
    /// same pixels. The caller unloads it.
    public static Image DecodeImage(SharpGLTF.Memory.MemoryImage img)
    {
        string ext = string.IsNullOrEmpty(img.FileExtension) ? "png" : img.FileExtension;
        return Raylib.LoadImageFromMemory("." + ext, img.Content.ToArray());
    }

    /// DecodeImage converted to RGBA8.
    private static Image DecodeRgba(SharpGLTF.Memory.MemoryImage img)
    {
        Image cpu = DecodeImage(img);
        if (cpu.Width > 0 && cpu.Height > 0)
            Raylib.ImageFormat(ref cpu, PixelFormat.UncompressedR8G8B8A8);
        return cpu;
    }

    /// A copy of an RGBA8 image's pixels.
    private static byte[] Pixels(Image cpu)
    {
        byte[] rgba = new byte[cpu.Width * cpu.Height * 4];
        unsafe { System.Runtime.InteropServices.Marshal.Copy((IntPtr)cpu.Data, rgba, 0, rgba.Length); }
        return rgba;
    }
}
