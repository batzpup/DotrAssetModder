using System.IO;
using System.Numerics;
using Raylib_cs;
using SharpGLTF.Schema2;
using Buffer = System.Buffer;
using Image = Raylib_cs.Image;
using Material = SharpGLTF.Schema2.Material;

namespace DotrAssetModder;

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
    public List<ImportedVertex> Vertices = new();
    public List<(int A, int B, int C)> Triangles = new();
}

public sealed class ImportedAnimChannel
{
    public List<(float Time, Vector3 V)>? Translation;
    public List<(float Time, Quaternion Q)>? Rotation;
}

public sealed class ImportedAnimation
{
    public string Name = "";
    public Dictionary<int, ImportedAnimChannel> Channels = new(); // key = glb node index
}

public sealed class ImportedModel
{
    public List<ImportedNode> Nodes = new();        // indexed by glb logical index
    public List<int> RootNodes = new();             // top-level node indices
    public List<int> JointNodeIndices = new();      // skin joints, in skin order
    public List<ImportedPrimitive> Primitives = new();
    public List<ImportedAnimation> Animations = new();

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

    /// Set by RebuildPipeline.ApplyTexMerges after it rewrites primitive UVs/TexIdx for
    /// UV-atlas slot merging; guards against double-applying when Build runs twice on the
    /// same instance. Carries the merge plan (base slot -> member slots in band order).
    public List<(int BaseSlot, List<int> MemberSlots)>? AppliedTexMerges;

    /// True for GLBs produced by this project's extractor. Extracted GLBs contain an
    /// `MrgRoot` wrapper and already have skinned positions baked into the extractor's
    /// round-trip coordinate space, so the importer must not treat them like generic
    /// Blender/Sketchfab GLBs.
    public bool IsDotrExtractedGlb;

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
                WorldMatrix = n.WorldMatrix,
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

        // --- skin joints ---
        if (model.LogicalSkins.Count > 0)
        {
            var skin = model.LogicalSkins[0];
            for (int i = 0; i < skin.JointsCount; i++)
            {
                int idx = skin.GetJoint(i).Joint.LogicalIndex;
                im.JointNodeIndices.Add(idx);
                im.Nodes[byLogical[idx]].IsJoint = true;
            }
        }

        // --- deterministic auto texture-slot assignment (material LogicalIndex order) ---
        // Explicitly named mat_texN materials own their slots; every other material gets
        // the next free slot in materials-array order. Doing this as a pre-pass (instead
        // of first-seen-during-node-iteration) makes the material -> slot mapping stable
        // and predictable from the glb alone.
        {
            var explicitSlots = new HashSet<int>();
            foreach (var m in model.LogicalMaterials)
                if (TryParseMatTexIndex(m.Name, out int t)) explicitSlots.Add(t);
            int nextAuto = 0;
            var slotByKey = new Dictionary<string, int>();   // image/colour dedupe
            foreach (var m in model.LogicalMaterials)
            {
                if (TryParseMatTexIndex(m.Name, out _)) continue;
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
            if (byLogical.TryGetValue(meshNode.LogicalIndex, out int meshNodePos))
                im.Nodes[meshNodePos].HasMesh = true;
            bool nodeSkinned = meshNode.Skin != null;
            Matrix4x4 nodeWorld = meshNode.WorldMatrix;
            if (!nodeSkinned)
            {
                // scale-0 nodes are a common "hidden mesh" trick (e.g. a car's lights-on
                // variant scaled to zero). The world matrix is singular: the bake collapses
                // every vertex to a point and inverse(RWS) is undefined, which poisons the
                // global-scale pass with garbage. The mesh is invisible by intent - skip it.
                float det =
                    nodeWorld.M11 * (nodeWorld.M22 * nodeWorld.M33 - nodeWorld.M23 * nodeWorld.M32) -
                    nodeWorld.M12 * (nodeWorld.M21 * nodeWorld.M33 - nodeWorld.M23 * nodeWorld.M31) +
                    nodeWorld.M13 * (nodeWorld.M21 * nodeWorld.M32 - nodeWorld.M22 * nodeWorld.M31);
                if (MathF.Abs(det) < 1e-12f) continue;
            }
            foreach (var prim in meshNode.Mesh.Primitives)
            {
                var ip = new ImportedPrimitive { NodeIndex = meshNode.LogicalIndex };
                ParseMaterial(prim.Material, ip, im);

                var pos = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                if (pos == null) continue; // nothing to do without positions
                var nrm = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var uv = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                var jnt = prim.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
                var wgt = prim.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();

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
                    }
                    else
                    {
                        iv.Influences.Add(new ImportedInfluence { JointNode = meshNode.LogicalIndex, Weight = 1f });
                    }
                    ip.Vertices.Add(iv);
                }

                foreach (var (a, b, c) in prim.GetTriangleIndices())
                    ip.Triangles.Add((a, b, c));

                WrapPrimitiveUvs(ip);   // fold out-of-[0,1] UVs into tiles (REPEAT semantics)

                im.Primitives.Add(ip);
            }
        }

        // --- animations (linear samplers; the extractor writes linear curves) ---
        foreach (var anim in model.LogicalAnimations)
        {
            var ia = new ImportedAnimation { Name = anim.Name ?? "" };
            foreach (var ch in anim.Channels)
            {
                if (ch.TargetNode == null) continue;
                int node = ch.TargetNode.LogicalIndex;
                if (!ia.Channels.TryGetValue(node, out var c)) { c = new ImportedAnimChannel(); ia.Channels[node] = c; }
                switch (ch.TargetNodePath)
                {
                    case PropertyPath.translation:
                        var ts = ch.GetTranslationSampler();
                        if (ts != null) c.Translation = ts.GetLinearKeys().Select(k => (k.Key, k.Value)).ToList();
                        break;
                    case PropertyPath.rotation:
                        var rs = ch.GetRotationSampler();
                        if (rs != null) c.Rotation = rs.GetLinearKeys().Select(k => (k.Key, k.Value)).ToList();
                        break;
                }
            }
            im.Animations.Add(ia);
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
            // Primitive with no material at all: park it on slot 0 with a neutral solid
            // texture if nothing else supplies one, so the template's old texture can't
            // bleed through (the engine has no untextured draw path).
            ip.TexIdx = 0;
            ip.BaseColor = Vector4.One;
            if (!im.TextureImages.ContainsKey(0))
                im.TextureImages[0] = SolidImage(178, 178, 178, 255);
            return;
        }
        string name = mat.Name ?? "";
        ip.IsAlt = name.EndsWith("_alt", StringComparison.OrdinalIgnoreCase);
        int p = name.IndexOf("mat_tex", StringComparison.OrdinalIgnoreCase);
        bool named = false;
        if (p >= 0)
        {
            int start = p + "mat_tex".Length, end = start;
            while (end < name.Length && char.IsDigit(name[end])) end++;
            if (end > start && int.TryParse(name.AsSpan(start, end - start), out int tex)) { ip.TexIdx = tex; named = true; }
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
        bool hasTexture = false;
        try
        {
            var bc = mat.FindChannel("BaseColor");
            if (bc.HasValue)
            {
                ip.BaseColor = bc.Value.Color;
                hasTexture = bc.Value.Texture?.PrimaryImage != null;
            }
        }
        catch { }

        // Harvest the material's base-colour image for texture reimport (best-effort;
        // first material seen per texture slot wins - slots are deduped by image upstream).
        try
        {
            if (ip.TexIdx >= 0)
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