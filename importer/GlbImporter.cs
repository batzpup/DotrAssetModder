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
}

public sealed class ImportedPrimitive
{
    /// glb logical node that instanced this mesh (-1 if unknown). For unskinned prims the
    /// vertices are world-baked at import and rigidly rigged to this node.
    public int NodeIndex = -1;
    public int TexIdx;
    public bool IsAlt;
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

    /// Duplicate or out-of-range "mat_texN" slots seen by the texture-slot pre-pass, one line
    /// each (also echoed to stderr). Reported rather than silently mishandled.
    public List<string> TexSlotWarnings = new();

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
            }
        }

        // --- animations: every channel resampled at every integer frame ---
        // Sampling with each sampler's own interpolation turns STEP and CUBICSPLINE into
        // plain per-frame values (CUBICSPLINE outputs are tangent/value/tangent triplets,
        // which must never be read as keys), and lets RebuildPipeline fit the game's key
        // lists (per-axis Euler lerp, a list under 3 keys plays key 0 only) to the motion
        // rather than copying glTF keys one for one.
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
                        c.Translation = Resample(ch.GetTranslationSampler()?.CreateCurveSampler(), frames);
                        break;
                    case PropertyPath.rotation:
                        c.Rotation = Resample(ch.GetRotationSampler()?.CreateCurveSampler(), frames);
                        break;
                    case PropertyPath.scale:
                        c.Scale = Resample(ch.GetScaleSampler()?.CreateCurveSampler(), frames);
                        break;
                }
            }
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
                    ip.Vertices.Add(iv);
                }

                foreach (var (a, b, c) in prim.GetTriangleIndices())
                    ip.Triangles.Add((a, b, c));

                WrapPrimitiveUvs(ip);   // fold out-of-[0,1] UVs into tiles (REPEAT semantics)

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
    /// p0@4, p1@6, size@8, src@0xC, dst@0x10; the runtime counter @0x14 stays zero.
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
    private static void WrapPrimitiveUvs(ImportedPrimitive ip)
    {
        // quick out: nothing outside [0,1)
        bool any = false;
        foreach (var v in ip.Vertices)
            if (v.Uv.X < 0f || v.Uv.X >= 1f || v.Uv.Y < 0f || v.Uv.Y >= 1f) { any = true; break; }
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
                    Pos = src.Pos, Normal = src.Normal,
                    Uv = new Vector2(src.Uv.X - tx, src.Uv.Y - ty),
                    Influences = src.Influences,   // share (read-only downstream)
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
            int tx = (int)MathF.Floor(cx), ty = (int)MathF.Floor(cy);
            newTris.Add((Resolve(a, tx, ty), Resolve(b, tx, ty), Resolve(c, tx, ty)));
        }
        ip.Triangles = newTris;

        // finally fold any tile-(0,0) originals still sitting outside [0,1] (e.g. a vertex at
        // exactly 1.0, or one only ever used by tile-(0,0) tris but authored at 1.x)
        foreach (var v in ip.Vertices)
        {
            float wx = v.Uv.X - MathF.Floor(v.Uv.X);
            float wy = v.Uv.Y - MathF.Floor(v.Uv.Y);
            if (wx == 0f && v.Uv.X != 0f) wx = 1f;
            if (wy == 0f && v.Uv.Y != 0f) wy = 1f;
            v.Uv = new Vector2(wx, wy);
        }
    }

    /// Decode the glb's embedded base-colour images (per "mat_texN" texture index) into raw
    /// RGBA TextureInputs for the RH2 re-encoder. Uses the same SaveToFile -> Raylib.LoadImage
    /// path as the model viewer (version-safe across SharpGLTF releases). Best-effort: a
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
            string tmp = null;
            try
            {
                string ext = string.IsNullOrEmpty(img.FileExtension) ? "png" : img.FileExtension;
                tmp = Path.Combine(Path.GetTempPath(), "dotr_imp_" + Guid.NewGuid().ToString("N") + "." + ext);
                img.SaveToFile(tmp);

                Image cpu = Raylib.LoadImage(tmp);
                if (cpu.Width <= 0 || cpu.Height <= 0) continue;
                Raylib.ImageFormat(ref cpu, PixelFormat.UncompressedR8G8B8A8);

                byte[] rgba = new byte[cpu.Width * cpu.Height * 4];
                unsafe
                {
                    System.Runtime.InteropServices.Marshal.Copy((IntPtr)cpu.Data, rgba, 0, rgba.Length);
                }
                inputs.Add(new Rh0Assembler.TextureInput {
                    TexIdx = texIdx,
                    Rgba = rgba,
                    Width = cpu.Width,
                    Height = cpu.Height,
                    AnimRecords = model.TextureAnimRecords.TryGetValue(texIdx, out var ar) ? ar : null,
                });
                Raylib.UnloadImage(cpu);
            }
            catch
            {
                /* skip this texture; the template RH2 stays */
            }
            finally
            {
                try
                {
                    if (tmp != null && File.Exists(tmp)) File.Delete(tmp);
                }
                catch
                {
                }
            }
        }

        // --- animated slots: decode every frame, pack into one sprite sheet ---
        foreach (var (texIdx, frames) in model.TextureAnimFrames)
        {
            try
            {
                var decoded = new List<(byte[] rgba, int w, int h)>();
                foreach (var f in frames)
                {
                    string tmp = null;
                    try
                    {
                        string ext = string.IsNullOrEmpty(f.FileExtension) ? "png" : f.FileExtension;
                        tmp = Path.Combine(Path.GetTempPath(), "dotr_imp_anim_" + Guid.NewGuid().ToString("N") + "." + ext);
                        f.SaveToFile(tmp);
                        Image cpu = Raylib.LoadImage(tmp);
                        if (cpu.Width <= 0 || cpu.Height <= 0) continue;

                        // Clamp BEFORE extracting bytes - a flipbook sheet's width grows
                        // with frame count (cols*blockW), so an unclamped per-frame size
                        // multiplies directly into the final sheet. Retail's own flipbook
                        // sample is tiny (128x64 stored for 8 frames, ~32x16 px/cell) -
                        // animated cells are meant to be small, not full-resolution copies
                        // of the source art. Resize (not crop/pad) so the whole frame's
                        // content survives, just at a lower resolution.
                        if (cpu.Width > maxDim || cpu.Height > maxDim)
                        {
                            float scale = Math.Min((float)maxDim / cpu.Width, (float)maxDim / cpu.Height);
                            int nw = Math.Max(1, (int)Math.Round(cpu.Width * scale));
                            int nh = Math.Max(1, (int)Math.Round(cpu.Height * scale));
                            Raylib.ImageResize(ref cpu, nw, nh);
                        }

                        Raylib.ImageFormat(ref cpu, PixelFormat.UncompressedR8G8B8A8);
                        byte[] rgba = new byte[cpu.Width * cpu.Height * 4];
                        unsafe { System.Runtime.InteropServices.Marshal.Copy((IntPtr)cpu.Data, rgba, 0, rgba.Length); }
                        decoded.Add((rgba, cpu.Width, cpu.Height));
                        Raylib.UnloadImage(cpu);
                    }
                    finally
                    {
                        try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
                    }
                }
                if (decoded.Count < 2) continue; // matches TextureAnimFrames' own >=2 rule

                // All frames must share one size for a flipbook (the engine's "grid" entry
                // is one fixed blockW/blockH for every cell). Mismatched frame sizes most
                // likely mean the Blender export wasn't set up consistently (or rounded
                // differently during the maxDim resize above); rather than silently
                // distort frames, fall back to treating this slot as static (frame 0
                // only) so the import doesn't fail outright.
                int rawW = decoded[0].w, rawH = decoded[0].h;
                bool uniform = decoded.All(d => d.w == rawW && d.h == rawH);
                if (!uniform)
                {
                    inputs.Add(new Rh0Assembler.TextureInput { TexIdx = texIdx, Rgba = decoded[0].rgba, Width = rawW, Height = rawH });
                    continue;
                }

                // Pad each (already maxDim-clamped) cell up to a power-of-two, then pad
                // the FULL sheet (bw*n, bh) up to Rh2Encoder.BuildNative's real
                // requirement - width a POT multiple of 128, height a POT multiple of 64
                // - so it can be passed in as an exact target with no resampling at all
                // (Resample's sw==dw&&sh==dh short-circuit). This keeps every frame's
                // cell boundary on an exact texel grid.
                int n = decoded.Count;
                int bw = Math.Max(1, Rh2Encoder.NextPot(rawW));
                int bh = Math.Max(1, Rh2Encoder.NextPot(rawH));

                int sheetW = Rh2Encoder.NextPot(bw * n);
                sheetW = ((sheetW + 127) / 128) * 128;
                if (!IsPow2Local(sheetW)) sheetW = Rh2Encoder.NextPot(sheetW);
                int sheetH = Rh2Encoder.NextPot(bh);
                sheetH = ((sheetH + 63) / 64) * 64;
                if (!IsPow2Local(sheetH)) sheetH = Rh2Encoder.NextPot(sheetH);
                sheetW = Math.Max(sheetW, 128);
                sheetH = Math.Max(sheetH, 64);

                byte[] sheet = new byte[sheetW * sheetH * 4];
                for (int f = 0; f < n; f++)
                {
                    var (rgba, w, h) = decoded[f];
                    int cellX = f * bw;
                    if (cellX >= sheetW) break; // shouldn't happen given the rounding above, but stay safe
                    for (int y = 0; y < h && y < bh && y < sheetH; y++)
                        Buffer.BlockCopy(rgba, y * w * 4, sheet, ((y * sheetW) + cellX) * 4, Math.Min(w, sheetW - cellX) * 4);
                    // any remaining padding bytes stay transparent-black (default array init)
                }

                inputs.Add(new Rh0Assembler.TextureInput {
                    TexIdx = texIdx,
                    Rgba = sheet,
                    Width = sheetW,
                    Height = sheetH,
                    AnimFrameCount = n,
                    AnimBlockW = bw,
                    AnimBlockH = bh,
                    AnimTicksPerFrame = model.TextureAnimTicks.TryGetValue(texIdx, out int customTicks)
                        ? customTicks
                        : 5, // default matches the one real retail sample (see TextureInput remarks)
                });
            }
            catch
            {
                /* skip this texture's animation; falls through with no TextureInput for
                   this slot, so the template's existing texture/anim is kept untouched */
            }
        }

        return inputs;
    }

    private static bool IsPow2Local(int v) => v > 0 && (v & (v - 1)) == 0;
}