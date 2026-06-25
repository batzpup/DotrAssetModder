using System.Numerics;

namespace DotrAssetModder;

// === Step 6 — Rebuild pipeline (Layer B integration) =================================
// Turns a parsed glb (ImportedModel) into the inputs the byte writers consume:
//   * List<EncNode>  -> NodeTreeWriter  (skeleton)
//   * List<EncPage>  -> GeometryWriter  (Section1/Section2/RelocWalk)
//   * GlobalScale    -> RH0 +0x64 animTransScale companion / root node scale
//
// Every numeric step here was validated end-to-end against blue-eyes_white_dragon
// (full encode->decode position round-trip max err 0.0005 over 6454 verts):
//   - global scale S = max|inverse(jointWorld)*vertexWorld| / 32000 (auto-fits s16)
//   - root node carries (S,S,S); all others scale 1, flags 0  => game RestWorldSkin
//     reproduces Scale(S)*glbWorld, which is exactly the matrix we encode against
//   - quat -> XYZ BAMS Euler:  ry=asin(-M13), rx=atan2(M23,M33), rz=atan2(M12,M11)
//     (analytic inverse of the extractor's qz*qy*qx; rebuilds RotX*RotY*RotZ)
//   - pos_raw = glbLocalTranslation / parentAccum  (root: /1, others: /S)
//   - per influence: bind pos = inverse(RWS_bone)*vertexWorld, normal via inverse 3x3
//   - strips partitioned into <=8-bone pages, each page's bones -> VU palette
//     {4,11,18,25,32,39,46,53}; INDEP header-W and SHARED bone = that VU slot
// =====================================================================================

public static class RebuildPipeline
{
    private static readonly int[] VuSlots = { 4, 11, 18, 25, 32, 39, 46, 53 };
    private const int MaxBonesPerPage = 8;

    private const float UvScale = 4096f;

    // The texture-context record sets CLAMP_1 = 5 (GS WMS/WMT = CLAMP), but glTF/Blender
    // author out-of-[0,1] UVs assuming REPEAT (tiling). Folding them back with frac() before
    // quantisation reproduces the tiled sample the artist saw; clamping instead would crush
    // a v of -1.9 onto a texture edge (often black) - the cause of Charizard's black face
    // patches and wing bands. Exact integers (1.0, -1.0) keep their high edge, not 0.
    private static float WrapUv(float t)
    {
        if (float.IsNaN(t) || float.IsInfinity(t)) return 0f;
        float f = t - MathF.Floor(t);
        if (f == 0f && t != 0f) f = 1f; // 1.0/-1.0/2.0 -> 1.0 (tile's far edge)
        return f;
    }

    private const float NormScale = 32767f;
    private const float WeightScale = 4096f;
    private const float S16Fit = 32000f; // target ceiling for |position| component
    private const double BamsPerRad = 32768.0 / Math.PI;



    private sealed class FrameDebugState
    {
        public int Root0;
        public string Root0Name = "";
        public bool SynthPivot;
        public int ConsumedRootSlot;
        public Vector3 BBoxMin;
        public Vector3 BBoxMax;
        public Vector3 BBoxCenter;
        public bool HasGeometry;
        public Vector3 PlacementPivot;
        public string PlacementPivotSource = "unknown";
        public Vector3 CameraTarget;
        public string CameraTargetSource = "unknown";
    }

    public sealed class TemplateFrame
    {
        /// World-space engine placement pivot: retail GNode(1).pos / animTransScale.
        public Vector3 PivotWorld;

        /// World-space camera/framing centre from the donor node-tree bounds record.
        public Vector3 BoundsCenterWorld;

        /// Optional donor bounding radius. Used for diagnostics/comparison only unless an
        /// explicit compatibility path is intentionally reintroduced elsewhere.
        public float BoundsRadius;

        public int NodeTreeOffset;
        public float AnimTransScale;
        public float RootScale;
        public Vector3 PivotRaw;

        public bool IsValid =>
            IsFinite(PivotWorld) && IsFinite(BoundsCenterWorld) && BoundsRadius > 1e-5f &&
            !float.IsNaN(BoundsRadius) && !float.IsInfinity(BoundsRadius);

        private static bool IsFinite(Vector3 v) =>
            !float.IsNaN(v.X) && !float.IsInfinity(v.X) &&
            !float.IsNaN(v.Y) && !float.IsInfinity(v.Y) &&
            !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);
    }

    public sealed class Result
    {
        public List<EncNode> Nodes = new();
        public List<EncPage> Pages = new();
        public List<EncMotion> Motions = new();
        public float GlobalScale = 1f;
        public float AnimTransScale = 1f;
        public int MaxMeshSlot; // highest meshSlot on the 1+7k grid; sizes the Section1 matrix table

        /// Model bounding centre + radius (WORLD units), emitted as the node-tree bounds record
        /// at tree base +0x30. lSzModel_LoadCB seeds handle+0x90 (centre) / handle+0x1e4 (radius)
        /// from it. VERIFIED IN GHIDRA (SzModel_GetCenter, SzModel_DrawWithFunc_Pipe): for
        /// animated model types, SzModel_GetCenter always returns handle+0x80, which is
        /// overwritten every draw call from GetAnyCoordinate(1, ...) - GNode(1)'s live world
        /// position - never from this bounds record. So Centre here is GNode(1)'s position (the
        /// same point SzModel_SetPos/GetCenterOfs use for the one-time placement pin), so this
        /// record agrees with what the live camera target will be once animation runs, rather
        /// than disagreeing with it. Radius = bounding-sphere radius about that centre.
        public System.Numerics.Vector3 BoundsCenter;

        public float BoundsRadius = 1f;

        public List<(int BaseSlot, List<int> MemberSlots)> TexMerges = new();

        /// World auto-fit report. Factor==1 means no rescale. Reason is a human-readable
        /// explanation (e.g. "model too large") or null when untouched.
        public float FitFactor = 1f;

        public double FitOriginalWorld; // model's world extent before any rescale
        public double FitDonorWorld; // donor template's reference world extent
        public string? FitReason;

        public string Summary() =>
            $"nodes={Nodes.Count} pages={Pages.Count} motions={Motions.Count} scale={GlobalScale:G6} " +
            $"strips={Pages.Sum(p => p.Strips.Count)} " +
            $"maxBonesPerPage={(Pages.Count == 0 ? 0 : Pages.Max(p => p.BoneBinds.Count))}";
    }

    /// templateAnimTransScale: header+0x64 of the template model (1/rootScale). When given
    /// and the geometry fits s16 under it, the rebuilt model ADOPTS the template's scale so
    /// that the game's externally loaded motion files (lSzModel_LoadMotion/.nab — applied to
    /// whatever model occupies the slot, with NO node-count or unit validation,
    /// lSzModel_LoadMotionCB@0x1ee4c0) decode their translation keys at exactly the retail
    /// scale. Deriving our own S instead made every animated translation wrong by
    /// retailS/ourS — the cause of off-origin identity imports and flung-apart parts on
    /// static replacements.
    public static Result Build(ImportedModel im, float templateAnimTransScale = 0f,
        int templateTexCount = 0, float warnMinRatio = 0.125f, float warnMaxRatio = 2.0f,
        TemplateFrame? templateFrame = null,
        bool debugFrame = false, bool adoptTemplateScaleForCustomModels = false)
    {
        if (im.RootNodes.Count == 0) throw new InvalidOperationException("glb has no root node");

        // ---- 0. donor/template frame is diagnostic only ------------------------------
        // The imported GLB is the spatial authority. The donor/template MRG is still used
        // for binary/container/texture/layout data and for comparison in debug output, but
        // it must not decide the imported model's pivot, camera target, or bounds radius.
        // Otherwise the same GLB would behave differently depending on the monster slot it
        // replaces.
        float donorBoundsRadius = templateFrame is { IsValid: true } ? templateFrame.BoundsRadius : 0f;

        // ---- 0a. UV-atlas merge of excess texture slots ----
        // The template's TexTable has a fixed number of RH2 payload slots; pages on slots
        // beyond it either need the table grown (only possible when the 1 MiB block has
        // room) or end up sharing someone else's texture. Merging is the always-correct
        // option: auto-assigned slots >= templateTexCount are folded into base slot
        // (slot % count) as vertical bands, with the pages' V coordinates remapped into
        // their band. Explicit mat_texN names >= count are NOT merged (deliberate request
        // for a grown table, handled by Rh0Assembler's grow/shed path).
        var texMerges = ApplyTexMerges(im, templateTexCount);

        // ---- 0. root selection / wrapper collapse (GNode(1) pivot contract) ----
        // The engine anchors monster (model type 4/5/6) scale+rotation AND the duel 3D
        // viewer's world placement on the node at depth-first index 1 == the ROOT'S FIRST
        // CHILD:
        //   * SzModel_DrawWithFunc_Pipe@0x1ec350: pivot = GetGNode(1).pos / animTransScale,
        //     applied as Trans(-pivot) * ScaleRotTrans(scale, rot, +pivot)
        //   * SzModel_GetCenterOfs = the same value; SzDuel_Draw3DView_Pipe does
        //     SzModel_SetPos(0x1d, -centerOfs) to place the monster
        //   * lSzModel_TraceGNode: counter starts 0 at the root, child-before-sibling
        // Any wrapper node above the real root (our extractor's 'MrgRoot', Sketchfab's
        // 'Sketchfab_model'/'RootNode' chains, Blender empties) shifts every DFS index by
        // one, so GNode(1) becomes a node at (0,0,0) -> the model pivots/centers on the
        // world origin instead of its center node ("rotates around a point in front of
        // it"). PrepareRoot collapses those wrappers; for external models it additionally
        // requests a synthetic pivot (inserted after step 4).
        var (root0, synthPivot, consumedRootSlot) = PrepareRoot(im);
        var frameDebug = new FrameDebugState {
            Root0 = root0,
            Root0Name = GlbPos(im, root0) >= 0 ? im.Nodes[GlbPos(im, root0)].Name : "<unknown>",
            SynthPivot = synthPivot,
            ConsumedRootSlot = consumedRootSlot,
        };

        // Frame-generation positions (used for bounds/pivot/radius diagnostics) are just each
        // vertex's encoder position - skinned vertices stay in skin/bind space, unskinned
        // vertices are already world-baked (see GlbImporter's iv.Pos assignment).
        InitFramePositions(im);

        // ---- 1. DFS preorder; node[0] = root, extra roots re-parented under it ----
        var order = new List<int>(); // glb node indices, preorder
        var encIndexOf = new Dictionary<int, int>(); // glb index -> position in `order`

        bool IsImportHelperNode(int g)
        {
            int pos = GlbPos(im, g);
            if (pos < 0) return false;
            var n = im.Nodes[pos];
            // Ignore loose, root-level empty helper nodes without relying on specific names.
            // This lets old marker empties remain in a GLB without becoming DOTR GNodes,
            // while preserving real bones/armatures/mesh nodes.
            if (n.Parent < 0 && !n.HasMesh && !n.IsJoint && n.Children.Count == 0) return true;
            return IsPureMeshInstancingLeaf(im, g);
        }

        void Dfs(int g)
        {
            if (IsImportHelperNode(g)) return;
            encIndexOf[g] = order.Count;
            order.Add(g);
            foreach (int c in im.Nodes[GlbPos(im, g)].Children)
                if (!IsImportHelperNode(c))
                    Dfs(c);
        }

        Dfs(root0);
        // every scene root except the one PrepareRoot consumed (root0 / its wrapper chain)
        var extraRoots = new List<int>();
        for (int r = 0; r < im.RootNodes.Count; r++)
        {
            if (consumedRootSlot < 0 || r == consumedRootSlot) continue; // -1: all consumed
            if (IsImportHelperNode(im.RootNodes[r])) continue;
            extraRoots.Add(im.RootNodes[r]);
            Dfs(im.RootNodes[r]);
        }

        // ---- 1.5 effective joint set ----
        // Skin joints first (skin order), then every node referenced rigidly by unskinned
        // primitives (static models: GlbImporter rigs each mesh node as a one-bone joint).
        var effectiveJoints = new List<int>(im.JointNodeIndices);
        var seenJoints = new HashSet<int>(effectiveJoints);
        foreach (var prim in im.Primitives)
        foreach (var v in prim.Vertices)
        foreach (var inf in v.Influences)
            if (seenJoints.Add(inf.JointNode))
                effectiveJoints.Add(inf.JointNode);

        // accumulates the world-fit outcome for the caller/UI
        (float Factor, double OriginalWorld, double DonorWorld, string? Reason) fitReport =
            (1f, 0, 0, null);

        // ---- 2. global scale S ----
        // S must be big enough that inverse(RWS)*world = (1/S)*inverse(world)*world fits s16.
        var jointWorld = new Dictionary<int, Matrix4x4>();
        foreach (int g in effectiveJoints) jointWorld[g] = im.Nodes[GlbPos(im, g)].WorldMatrix;
        double maxLocal = 1e-6;
        foreach (var prim in im.Primitives)
        foreach (var v in prim.Vertices)
        foreach (var inf in Influences(v, im))
        {
            if (!jointWorld.TryGetValue(inf.JointNode, out var jw)) continue;
            if (!Matrix4x4.Invert(jw, out var invJw)) continue; // singular joint: skip
            var lp = Vector3.Transform(v.Pos, invJw);
            if (float.IsNaN(lp.X) || float.IsInfinity(lp.X)) continue;
            maxLocal = Math.Max(maxLocal, Math.Max(Math.Abs(lp.X), Math.Max(Math.Abs(lp.Y), Math.Abs(lp.Z))));
        }

        // ---- 2.5 size-warning report ----
        // Use a real model-size metric, not max(|world coordinate|). The older warning used
        // distance from global origin, so extracted round-trips looked 2-3x "too large"
        // merely because the authored slot lives far from (0,0,0). Radius-around-camera is
        // stable for both MrgRoot round-trips and external models.
        if (!im.IsDotrExtractedGlb && ComputeBounds(im, out var warnMin, out var warnMax))
        {
            var warnCenter = im.FittedCameraLookAtWorld ?? im.AuthorCameraLookAtWorld ?? ((warnMin + warnMax) * 0.5f);
            double srcRadius = Math.Max(ComputeRadius(im, warnCenter), 1e-9f);
            double donorSize = donorBoundsRadius > 1e-5f ? donorBoundsRadius : srcRadius;
            double ratio = srcRadius / Math.Max(donorSize, 1e-9);
            fitReport.OriginalWorld = srcRadius;
            fitReport.DonorWorld = donorSize;
            if (ratio > warnMaxRatio)
                fitReport.Reason =
                    $"model radius is ~{ratio:0.#}x the donor slot radius — it may look oversized in-game.";
            else if (ratio < warnMinRatio)
                fitReport.Reason =
                    $"model radius is ~{ratio:0.###}x the donor slot radius — it may look tiny in-game.";
        }
        else
        {
            fitReport.OriginalWorld = 0;
            fitReport.DonorWorld = donorBoundsRadius > 0 ? donorBoundsRadius : 0;
            fitReport.Reason = null;
        }

        float S = (float)(maxLocal / S16Fit);
        if (S <= 0f || float.IsNaN(S) || float.IsInfinity(S)) S = 1f;

        // Scale policy:
        //   * Extracted DotR GLBs may keep the template scale for round-trip / retail-motion compatibility.
        //   * Custom GLBs generate their own rootScale/animTransScale by default so the same GLB is
        //     not affected by whichever monster slot, or previous import, is used as the binary template.
        // Enable adoptTemplateScaleForCustomModels only for explicit compatibility experiments.
        bool mayAdoptTemplateScale = im.IsDotrExtractedGlb || adoptTemplateScaleForCustomModels;
        if (mayAdoptTemplateScale && templateAnimTransScale > 0f && !float.IsNaN(templateAnimTransScale) &&
            !float.IsInfinity(templateAnimTransScale))
        {
            float templS = 1f / templateAnimTransScale;
            if (maxLocal / templS <= 32700.0) S = templS; // adopt template scale only when requested/safe
        }

        // ---- 3. mesh-slot assignment ----
        // The engine stores each joint's per-frame matrix at Section1 + meshSlot*0x10 and
        // each entry is 0x70 bytes (lRhModel_SetupNoMotionRecursive@0x20de40), so slots MUST
        // be on the 1, 8, 15, ... (1+7k) grid — exactly the retail convention.
        var meshSlotOf = new Dictionary<int, int>();
        for (int i = 0; i < effectiveJoints.Count; i++) meshSlotOf[effectiveJoints[i]] = 1 + 7 * i;

        // ---- 4. author EncNodes (DFS order; reproduce glb world via the game's read math) ----
        var nodes = new List<EncNode>(order.Count);
        foreach (int g in order) nodes.Add(new EncNode());
        for (int i = 0; i < order.Count; i++)
        {
            int g = order[i];
            var src = im.Nodes[GlbPos(im, g)];
            bool isRoot = (g == root0);
            float parentAccum = isRoot ? 1f : S;

            var nd = nodes[i];
            // Per-node scale is honored (the engine applies it to the node's own matrix and
            // propagates to children unless flags bit2). The root folds the global S in.
            // Note: child translations are divided by the ROOT scale only, so deep chains of
            // scaled intermediate nodes are approximated — rare in practice.
            nd.Scale = isRoot
                ? (S * src.Scale.X, S * src.Scale.Y, S * src.Scale.Z)
                : (src.Scale.X, src.Scale.Y, src.Scale.Z);
            nd.Flags = 0;
            nd.MeshSlot = (ushort)(meshSlotOf.TryGetValue(g, out int ms) ? ms : 0);
            nd.RotBams = QuatToBams(src.Rotation);
            nd.Pos = (src.Translation.X / parentAccum, src.Translation.Y / parentAccum, src.Translation.Z / parentAccum);

            // parent / children remapped to enc indices (extra roots adopt root0)
            int gp = src.Parent;
            if (g == root0) nd.Parent = -1;
            else if (gp < 0) nd.Parent = 0; // extra root -> under node[0]
            else if (encIndexOf.TryGetValue(gp, out int epi)) nd.Parent = epi;
            else nd.Parent = 0; // parent was an import-helper node; adopt under root

            foreach (int c in src.Children)
                if (encIndexOf.TryGetValue(c, out int ci))
                    nd.Children.Add(ci);
        }
        foreach (int er in extraRoots)
            if (encIndexOf.TryGetValue(er, out int ei))
                nodes[0].Children.Add(ei);

        bool hasBounds = ComputeBounds(im, out var geomMin, out var geomMax);
        var geomCenter = hasBounds ? (geomMin + geomMax) * 0.5f : Vector3.Zero;
        frameDebug.HasGeometry = hasBounds;
        frameDebug.BBoxMin = hasBounds ? geomMin : Vector3.Zero;
        frameDebug.BBoxMax = hasBounds ? geomMax : Vector3.Zero;
        frameDebug.BBoxCenter = geomCenter;

        // ---- 4.5 GNode(1) pivot for external models ----
        // CONFIRMED RETAIL STRUCTURE (verified on blue-eyes_white_dragon.mrg's node tree AND
        // every one of its 5 MOT slots): node 0 is a neutral root (rest pose: zero rotation,
        // zero translation, ONLY the global scale folded in - see step 4's nd.Scale handling)
        // that the engine and on-disk MOT data never animate (every slot's node-0 track has
        // transCount=rotCount=0). Node 1 - the root's first child - carries real, substantial
        // keyframe data in EVERY slot (e.g. 147 translation + 49 rotation keys over 391 frames
        // on the longest slot) and is what both placement (SzModel_SetPos via the STATIC rest-
        // pose read GetCenterOfs, Addendum §2) and the per-frame camera aim (SzModel_GetCenter,
        // which re-derives node 1's LIVE world transform every frame via GetAnyCoordinate(1),
        // Addendum §3/§4) are anchored to. An animated node 1 is precisely how the camera
        // follows the model's centre of mass through its own motion - retail dragons visibly
        // shift their look-at point during their attack animation because node 1 itself is
        // keyframed in that MOT slot. Below node 1, retail inserts one or more neutral,
        // non-bound (meshSlot=0) container nodes (node 2, node 3, ...) before the first real
        // bone bind appears (node 4 on this sample) - an offset link the rig hangs off of
        // without disturbing the pivot itself.
        //
        // For custom GLBs this means: PrepareRoot's structural wrapper-peel (no naming
        // required) recognizes a dedicated pivot wrapper purely by position - a non-content,
        // single-child node sitting as the second peeled wrapper above the real
        // mesh/armature/animation content becomes node_1 regardless of what it's named. If
        // the artist keyframes that node, those translation/rotation keys flow through the
        // normal animation-channel loop in step 8 like any other node and become node 1's
        // real MOT track.
        //
        // Extractor glbs keep the retail tree verbatim, so retail node 1 (already animated,
        // per the above) is preserved as-is and externally loaded retail motions (.nab,
        // applied by node index with zero validation - lSzModel_LoadMotionCB@0x1ee4c0) stay
        // aligned.
        //
        // External models with NO separate pivot wrapper at all (root0 already branches or
        // carries content directly - e.g. an Armature with several top-level bone chains and
        // no dedicated empty above it) fall back to InsertGeneratedPivot, which synthesizes a
        // STATIC (unanimated) node 1 anchored at the geometry's centre. This is a degraded
        // mode, not an equivalent substitute for the retail pattern above: it fixes the
        // "pinned to literal world (0,0,0) regardless of where the artist modeled the mesh"
        // placement/camera-aim mismatch, but it cannot give a moving camera-follow target,
        // because there is no authored motion to carry. Models that want the camera to track
        // their motion (the retail convention) need a real, separate, ideally-animated pivot
        // wrapper above the armature/mesh content - there is no way to synthesize that
        // automatically from geometry alone.
        if (synthPivot)
            InsertGeneratedPivot(im, nodes, encIndexOf, S, frameDebug, hasBounds ? geomMin : null, hasBounds ? geomMax : null);
        else if (nodes.Count > 1)
        {
            frameDebug.PlacementPivot = new Vector3(nodes[1].Pos.X * S, nodes[1].Pos.Y * S, nodes[1].Pos.Z * S);
            frameDebug.PlacementPivotSource = "existing GNode(1)";
        }

        // ---- 4.6/4.7 node-tree bounds record (centre + radius) ----
        // VERIFIED DIRECTLY IN GHIDRA (SzModel_DrawWithFunc_Pipe@0x1ec350, SzModel_GetCenter@0x1f2050,
        // lSzModel_LoadCB@0x1e6880), correcting an earlier mistaken belief in this codebase:
        //
        // SzModel_GetCenter(modelId, out) always returns handle+0x80/+0x84/+0x88/+0x8c, period -
        // there is no branch that reads anything else. For animated model types (4/5/6, i.e. every
        // monster), SzModel_DrawWithFunc_Pipe overwrites handle+0x80 EVERY DRAW CALL via
        // GetAnyCoordinate(1, outMatrix) - GNode(1)'s current animated world position, captured
        // fresh right after that frame's pose is drawn. handle+0x90 is a DIFFERENT field: it is
        // seeded once at load time (lSzModel_LoadCB) from this on-disk bounds record
        // (nodeTreeBase+0x30..+0x3C), but nothing in SzModel_DrawWithFunc_Pipe's animated branch
        // ever reads +0x90, and SzModel_GetCenter never reads it either - it is a load-time
        // placeholder / static-model-only field, not an alternate route to the camera target.
        //
        // So for any animated monster, the camera ALWAYS aims at GNode(1)'s live position, full
        // stop - there is no second "geometric centre" camera target to anchor instead. The bounds
        // record written here exists for: the one-frame gap before the first GetAnyCoordinate(1)
        // call lands (handle+0x90 -> +0x80 only happens in the non-animated else-branch), the
        // load-time radius (handle+0x1e4, used for deform scaling, *(float*)(iVar2+0x1e8) = clamp(40/
        // radius, 0.3, 1)), and as diagnostic/comparison data. It should track GNode(1), not the
        // geometry centroid, so the bounds record agrees with what the live camera target will be
        // once the first animated frame runs - anchoring it on the geometric centroid instead made
        // it disagree with the actual runtime camera target for animated monsters, the same
        // confusion that motivated routing around GNode(1) here.  If GNode(1) sits at a place that
        // makes for bad camera framing (e.g. the literal feet), the fix is to place GNode(1) better
        // (see step 4.5's InsertGeneratedPivot / the explicit pivot convention), not to give the
        // camera a different target than the one the engine actually uses.
        Vector3 boundsCenter = nodes.Count > 1
            ? new Vector3(nodes[1].Pos.X * S, nodes[1].Pos.Y * S, nodes[1].Pos.Z * S)
            : Vector3.Zero;
        frameDebug.CameraTarget = boundsCenter;
        frameDebug.CameraTargetSource = nodes.Count > 1 ? "GNode(1) (matches live SzModel_GetCenter)" : "no GNode(1): origin";
        // Radius = bounding-sphere radius about GNode(1) over all WORLD-space vertices. Clamp to a
        // small positive value: the engine computes 40/radius (clamped [0.3,1]) for deform, so a
        // zero/denormal radius would skew framing/deform. Single-vertex/degenerate models get 1.0.
        float boundsRadius = 0f;
        foreach (var prim in im.Primitives)
        foreach (var v in prim.Vertices)
        {
            var p = v.FramePos;
            if (float.IsNaN(p.X) || float.IsInfinity(p.X) ||
                float.IsNaN(p.Y) || float.IsInfinity(p.Y) ||
                float.IsNaN(p.Z) || float.IsInfinity(p.Z)) continue;
            boundsRadius = Math.Max(boundsRadius, Vector3.Distance(p, boundsCenter));
        }
        // Radius is always generated from the imported geometry around the selected camera
        // target. The donor/template radius is diagnostic only; using it here would make the
        // result depend on the replaced slot.
        if (!(boundsRadius > 1e-3f) || float.IsNaN(boundsRadius) || float.IsInfinity(boundsRadius))
            boundsRadius = 1f;

        // ---- 5. per-joint RestWorldSkin = Scale(S) * glbWorld ; cache inverse + 3x3 inverse ----
        var rwsInv = new Dictionary<int, Matrix4x4>();
        var scaleS = Matrix4x4.CreateScale(S);
        foreach (var kv in jointWorld)
        {
            var rws = scaleS * kv.Value; // row-vector: apply scale then world
            Matrix4x4.Invert(rws, out var inv);
            rwsInv[kv.Key] = inv;
        }

        // ---- 6. stripify triangles per material (long tri-strips, like retail) ----
        // One strip per triangle blows Section2 up several-fold (each tri repeats 3 full
        // verts + a 24B header; SHARED verts repeat their whole influence blob) and
        // overflows the 1 MiB block. Greedy edge-walk stripification gets back to
        // ~1.1 verts/tri. Winding inside a strip is free: the GS never back-face culls
        // and the extractor re-orients triangles by vertex normals on read.
        var protos = new List<(MatKey Key, EncStrip Strip)>();
        foreach (var prim in im.Primitives)
        {
            var matKey = new MatKey(prim.TexIdx, prim.IsAlt, prim.BaseColor);
            foreach (var strip in StripifyPrimitive(prim, im, rwsInv, meshSlotOf))
                protos.Add((matKey, strip));
        }

        // ---- 7. group by material, partition each group into <=8-bone pages, assign VU slots ----
        var pages = new List<EncPage>();
        foreach (var grp in protos.GroupBy(p => p.Key))
        {
            var strips = grp.Select(p => p.Strip).ToList();
            foreach (var member in Partition(strips))
            {
                var bones = new SortedSet<int>();
                foreach (var s in member)
                foreach (int b in StripBones(s))
                    bones.Add(b);

                var slot2vu = new Dictionary<int, int>();
                int k = 0;
                foreach (int ms in bones) slot2vu[ms] = VuSlots[k++];

                var page = new EncPage {
                    IsAlt = grp.Key.IsAlt,
                    TexIdx = grp.Key.TexIdx,
                    Color = ((float)grp.Key.R, (float)grp.Key.G, (float)grp.Key.B),
                    Alpha = grp.Key.A,
                };
                foreach (var ms in bones)
                    page.BoneBinds.Add(new EncBoneBind { VuAddr = slot2vu[ms], Slot = ms, VifEcho = 0 });

                foreach (var s in member)
                {
                    if (s.Kind == StripKind.Indep) s.IndepVuAddr = slot2vu[s.IndepVuAddr]; // placeholder ms -> VU slot
                    else RemapSharedBones(s, slot2vu);
                    page.Strips.Add(s);
                }
                pages.Add(page);
            }
        }

        // ---- 8. animations: glb channels -> EncMotion (frame=time*30; quat->BAMS) ----
        const float Fps = 30f;
        // RH0 +0x64 animTransScale: the retail contract (byte-verified on blue-eyes and
        // timeater) is animTransScale == 1 / rootScale. The duel draw path
        // (SzModel_DrawWithFunc_Pipe@0x1ec350) anchors monster scale/rotation at
        // GetGNode(1).pos / animTransScale — converting node-tree units back to world units —
        // and the motion interpolator multiplies MOT translation keys by it at runtime.
        // With aTS=1/S: root keys are stored as world/aTS (engine: key*aTS = world, no parent
        // scale) and child keys as world (engine: key*aTS*parentScale(S) = world).
        float AnimTransScale = 1f / S;
        var motions = new List<EncMotion>();
        int animFallback = 0;
        foreach (var anim in im.Animations)
        {
            int slotId = ParseSlotId(anim.Name, animFallback++);
            var motion = new EncMotion { SlotId = slotId };
            int maxFrame = 0;
            foreach (var kv in anim.Channels)
            {
                if (!encIndexOf.TryGetValue(kv.Key, out int enc)) continue;
                var ch = kv.Value;
                var track = new EncMotTrack();
                float transDiv = enc == 0 ? AnimTransScale : AnimTransScale * S;
                if (ch.Translation is { Count: > 0 } tk)
                {
                    track.Trans = new List<(int, Vector3)>(tk.Count);
                    foreach (var (t, v) in tk)
                    {
                        int f = (int)MathF.Round(t * Fps);
                        track.Trans.Add((f, transDiv != 0f ? v / transDiv : v));
                        if (f > maxFrame) maxFrame = f;
                    }
                }
                if (ch.Rotation is { Count: > 0 } rk)
                {
                    // Keyframes are sorted by time (glTF sampler requirement), so consecutive
                    // entries are temporally adjacent: each one's BAMS triple is chosen to be
                    // continuous with the previous, instead of an independent principal-value
                    // decomposition per frame (see QuatToBamsContinuous for why that matters).
                    track.Rot = new List<(int, int, int, int)>(rk.Count);
                    (int X, int Y, int Z)? prevBams = null;
                    foreach (var (t, q) in rk)
                    {
                        int f = (int)MathF.Round(t * Fps);
                        var bams = QuatToBamsContinuous(q, prevBams);
                        prevBams = bams;
                        track.Rot.Add((f, bams.X, bams.Y, bams.Z));
                        if (f > maxFrame) maxFrame = f;
                    }
                }
                if (track.Trans != null || track.Rot != null) motion.Tracks[enc] = track;
            }
            motion.FrameCount = maxFrame + 1;
            motions.Add(motion);
        }

        if (debugFrame)
            DumpFrameDebug(im, templateFrame, frameDebug, nodes, S, AnimTransScale, boundsCenter, boundsRadius);

        return new Result {
            Nodes = nodes, Pages = pages, Motions = motions,
            GlobalScale = S, AnimTransScale = AnimTransScale,
            BoundsCenter = boundsCenter, BoundsRadius = boundsRadius,
            MaxMeshSlot = effectiveJoints.Count > 0 ? 1 + 7 * (effectiveJoints.Count - 1) : 0,
            TexMerges = texMerges,
            FitFactor = fitReport.Factor,
            FitOriginalWorld = fitReport.OriginalWorld,
            FitDonorWorld = fitReport.DonorWorld,
            FitReason = fitReport.Reason,
        };
    }

    // ---- UV-atlas slot merging ----------------------------------------------------------
    /// Folds auto-assigned texture slots beyond the template's count into existing slots as
    /// vertical atlas bands. Mutates im.Primitives (TexIdx + vertex UVs) exactly once per
    /// ImportedModel instance (guarded by im.AppliedTexMerges). UVs are clamped to [0,1]
    /// first: the retail texture-context record sets CLAMP_1 = 5 (WMS/WMT = clamp), so
    /// tiling UVs never worked on this engine anyway and clamping loses nothing.
    private static List<(int BaseSlot, List<int> MemberSlots)> ApplyTexMerges(
        ImportedModel im, int templateTexCount)
    {
        if (im.AppliedTexMerges != null) return im.AppliedTexMerges;
        var merges = new List<(int BaseSlot, List<int> MemberSlots)>();
        int T = templateTexCount;
        if (T > 0)
        {
            var autoSlots = new HashSet<int>(im.MaterialTexAuto.Values);
            var used = new SortedSet<int>();
            foreach (var p in im.Primitives)
                if (p.TexIdx >= 0)
                    used.Add(p.TexIdx);

            var groups = new SortedDictionary<int, List<int>>();
            foreach (int e in used)
                if (e >= T && autoSlots.Contains(e))
                {
                    int b = e % T;
                    if (!groups.TryGetValue(b, out var lst)) groups[b] = lst = new List<int>();
                    lst.Add(e);
                }

            foreach (var kv in groups)
            {
                int b = kv.Key;
                var members = new List<int>();
                if (used.Contains(b)) members.Add(b); // base keeps band 0 only if it has pages
                members.AddRange(kv.Value);
                int n = members.Count;
                if (n == 0) continue;

                // V-band remap first (TexIdx still distinguishes members here), then retarget
                for (int k = 0; k < n; k++)
                {
                    int slot = members[k];
                    if (n == 1 && slot == b) break; // nothing to squeeze
                    foreach (var prim in im.Primitives)
                    {
                        if (prim.TexIdx != slot) continue;
                        foreach (var v in prim.Vertices)
                        {
                            // UVs were folded into [0,1] per-triangle at import; just
                            // squeeze V into this member's band. (Defensive WrapUv guards
                            // any stray value, e.g. exactly 1.0.)
                            float u = WrapUv(v.Uv.X);
                            float vv = WrapUv(v.Uv.Y);
                            v.Uv = new Vector2(u, (vv + k) / n);
                        }
                    }
                }
                foreach (int e in kv.Value)
                foreach (var prim in im.Primitives)
                    if (prim.TexIdx == e)
                        prim.TexIdx = b;

                merges.Add((b, members));
            }
        }
        im.AppliedTexMerges = merges;
        return merges;
    }

    private static bool ComputeBounds(ImportedModel im, out Vector3 min, out Vector3 max)
    {
        var pts = CollectValidFramePositions(im);
        if (pts.Count == 0)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
            return false;
        }

        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        foreach (var p in pts)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return true;
    }

    private static float ComputeRadius(ImportedModel im, Vector3 center)
    {
        var pts = CollectValidFramePositions(im);
        if (pts.Count == 0) return 0f;

        float radius = 0f;
        foreach (var p in pts)
            radius = Math.Max(radius, Vector3.Distance(p, center));
        return radius;
    }

    private static List<Vector3> CollectValidFramePositions(ImportedModel im)
    {
        var pts = new List<Vector3>();
        foreach (var prim in im.Primitives)
        foreach (var v in prim.Vertices)
        {
            var p = v.FramePos;
            if (float.IsNaN(p.X) || float.IsInfinity(p.X) ||
                float.IsNaN(p.Y) || float.IsInfinity(p.Y) ||
                float.IsNaN(p.Z) || float.IsInfinity(p.Z)) continue;
            pts.Add(p);
        }
        return pts;
    }

    private static void InitFramePositions(ImportedModel im)
    {
        foreach (var prim in im.Primitives)
        foreach (var v in prim.Vertices)
            v.FramePos = v.Pos;
    }

    // ---- root preparation / pivot guarantee -------------------------------------------

    /// Picks the encoding root, identifies node_0 (model root) and node_1 (pivot/camera
    /// target) by STRUCTURE rather than by name, and strips/synthesizes wrapper nodes so
    /// every model - extracted or freshly authored - ends up with the canonical
    /// node_0 -> node_1 -> (armature/mesh/offset content) shape.
    /// Returns (rootGlbIndex, synthesizePivot, consumedRootSlot):
    ///   * A "wrapper" is any node with no mesh, no skin-joint role, no animation channel of
    ///     its own, and exactly one child - i.e. it exists purely to occupy a slot in the
    ///     chain, whatever it happens to be named. Up to two such wrappers are peeled off the
    ///     top of the tree: the first becomes node_0, the second becomes node_1. This works
    ///     identically for extractor output (whose outer wrapper and retail node 1 are both
    ///     unnamed-or-arbitrarily-named wrappers by this definition), a hand-authored
    ///     mrgRoot/node_0/node_1/... chain, or any other naming an artist happens to use -
    ///     no string ever needs to match.
    ///   * Exactly one wrapper found (e.g. just a pivot empty with no outer root empty, or a
    ///     single extractor wrapper directly above retail's node 1): that wrapper becomes
    ///     node_1 and is re-wrapped under a freshly synthesized neutral node_0.
    ///   * No wrapper found at all (root0 already carries content, or already branches into
    ///     multiple children - e.g. an Armature with several top-level bone chains and no
    ///     dedicated pivot node above it): trivial single-child decorative wrapper chains are
    ///     still collapsed as before, but no node_1 exists yet, so the caller synthesizes a
    ///     static one (InsertGeneratedPivot).
    private static (int Root, bool SynthPivot, int ConsumedRootSlot) PrepareRoot(ImportedModel im)
    {
        static bool LooseEmptyRoot(ImportedModel im, int g)
        {
            var n = im.Nodes[g];
            return n.Parent < 0 && !n.HasMesh && !n.IsJoint && n.Children.Count == 0;
        }

        // prefer the first scene root that actually has children (the skinned-mesh node
        // our extractor emits is a childless sibling root). Loose empty root helpers are
        // ignored generically, without checking names.
        int slot = 0;
        for (int r = 0; r < im.RootNodes.Count; r++)
            if (!LooseEmptyRoot(im, im.RootNodes[r]) && im.Nodes[im.RootNodes[r]].Children.Count > 0)
            {
                slot = r;
                break;
            }
        int root0 = im.RootNodes[slot];

        // Real scene roots only (excludes pure-mesh-instancing leaves, e.g. the extractor's
        // MrgMesh-shaped sibling root). BUG FIX: old check used im.RootNodes.Count > 1
        // directly, so a single real wrapper root (MrgRoot) + a childless skinned-mesh leaf
        // sitting as its own scene root (every MODEL.MRG-style extracted glb: scene roots =
        // [MrgRoot, MrgMesh]) was wrongly treated as "multiple real roots" -> forced a
        // synthetic neutral root + synthetic pivot even though there's exactly one real root
        // and the wrapper-peel path below would've handled it fine (the mesh leaf is already
        // filtered everywhere else via this same predicate, e.g. RootRealChildren /
        // IsImportHelperNode). Was inflating the node tree by 2 junk nodes (placeholder-
        // position synthetic pivot + extra empty wrapper) on every such model.
        var realRoots = im.RootNodes.Where(r => !IsPureMeshInstancingLeaf(im, r)).ToList();

        // -- external glb with MULTIPLE REAL scene roots: synthesize a neutral root --
        // Re-parenting extra roots under a TRANSFORMED root0 multiplies their transforms by
        // root0's (engine T = pos * parentAccumScale): e.g. a car whose Body root has scale
        // 20 would scale every other root's translation and subtree by another x20. A fresh
        // identity root (carrying only the global S, exactly like retail roots) keeps every
        // original root's world transform intact as its local one.
        if (realRoots.Count > 1)
        {
            var synth = new ImportedNode {
                Index = im.Nodes.Count,
                Name = "dotr_root",
                Parent = -1,
            };
            foreach (int r in im.RootNodes)
            {
                if (LooseEmptyRoot(im, r)) continue;
                synth.Children.Add(r);
                im.Nodes[r].Parent = synth.Index;
            }
            im.Nodes.Add(synth);
            im.RootNodes = new List<int> { synth.Index }; // idempotent on Build re-runs
            return (synth.Index, true, -1); // -1: ALL scene roots consumed
        }

        // -- structural wrapper peel: position decides node_0 / node_1, not naming --
        // A "wrapper" is any node carrying no mesh, no skin-joint role, and no animation
        // channel of its own - i.e. it exists purely to occupy a slot in the chain, whatever
        // it happens to be named ('MrgRoot', 'mrgRoot', 'node_0', a typo, or nothing at all).
        // "Children" here means REAL children only: a pure mesh-instancing leaf sibling
        // (MrgMesh, see IsPureMeshInstancingLeaf) is invisible to this walk entirely - every
        // MODEL.MRG-style export has exactly this shape (MrgRoot -> [node_0->node_1,
        // MrgMesh]), where MrgMesh is a SIBLING of the real node_0/node_1 chain, not part of
        // it, and must not make MrgRoot look like a 2-real-child branch point.
        // Peel exactly two such single-child wrappers off the top of the tree if present:
        // the FIRST one found becomes node_0 (the model root, never animated/referenced -
        // matches retail's node 0, which is always at rest with zero rotation/translation),
        // the SECOND becomes node_1 (the pivot/camera target - matches retail's node 1, which
        // IS routinely animated, e.g. an attack-slot MOT track). The peel stops as soon as a
        // node is reached that carries real content (a mesh, a skin-joint role, an animation
        // channel) or has more than one REAL child (a branch point, e.g. an Armature with
        // several top-level bone chains) - that node and everything below it is preserved
        // verbatim as the rest of the tree, regardless of what it or anything above it was
        // named.
        // Children with pure mesh-instancing leaves filtered out - ONLY meaningful at
        // root0 itself, to recognize the MrgRoot shape (a transform-chain child plus a
        // sibling MrgMesh, e.g. MrgRoot -> [node_0, MrgMesh]). Deliberately NOT applied at
        // every level of the walk below: a node deeper in the tree (e.g. Armature) can
        // ALSO have several skinned mesh-instancing-leaf children alongside its real bone
        // children, and filtering those out there would make it look like a transparent
        // single-child wrapper too, incorrectly absorbing a genuine branch/content node
        // (verified against jerryNew.glb: Armature has 4 mesh-leaf children + 1 real bone
        // child: universal filtering made it look 1-real-child and wrongly pulled it into
        // the chain instead of stopping there).
        List<int> RootRealChildren(int g)
        {
            var real = new List<int>();
            foreach (int c in im.Nodes[g].Children)
                if (!IsPureMeshInstancingLeaf(im, c))
                    real.Add(c);
            return real;
        }

        bool isWrapper(int g, bool isRoot)
        {
            var n = im.Nodes[g];
            int childCount = isRoot ? RootRealChildren(g).Count : n.Children.Count;
            // NOTE: deliberately does NOT check animated(g). Verified against real retail
            // MODEL.MRG data (pack block 37's MOT track table) that node_0 itself can carry
            // a full animation track (1001 rotation keys in that sample) - animation is not
            // a disqualifier for either node_0 or node_1, for monsters or for this prop
            // class. Only real content (a mesh, a skin-joint role) or branching into
            // multiple real children stops the wrapper peel.
            return !n.HasMesh && !n.IsJoint && childCount == 1 && !referenced(g);
        }

        var wrapperChain = new List<int>();
        int cursor = root0;
        bool firstStep = true;
        while (isWrapper(cursor, firstStep))
        {
            wrapperChain.Add(cursor);
            cursor = firstStep ? RootRealChildren(cursor)[0] : im.Nodes[cursor].Children[0];
            firstStep = false;
        }
        // cursor now points at the first node that carries real content (mesh/joint/
        // animation) or branches into multiple children - everything in wrapperChain sits
        // strictly above that point.

        // Is cursor itself shaped like a valid node_1 (a transform/joint placeholder with
        // no mesh of its own and no children), rather than being deeper real content past
        // an already-identified node_1? This is NOT simply "did wrapperChain stop here" -
        // node_1 sometimes passes isWrapper itself (when it has a real child, e.g. retail's
        // node_1 -> model_offset/Armature chain, the monster/extracted-GLB shape) and so is
        // already wrapperChain's own last element with cursor pointing PAST it; other times
        // node_1 is a bare leaf skin-joint with nothing beneath it (every confirmed
        // MODEL.MRG-style prop: node_0_m1 in model0ab, node_1_m1 in model_037/039/189) and
        // so it FAILS isWrapper's child-count test and becomes cursor itself. Verified
        // against all 4 confirmed real shapes plus a hypothetical unskinned-rigid-mesh-as-
        // direct-content edge case (HasMesh excludes that case correctly: real geometry is
        // never mistaken for the node_1 placeholder).
        bool cursorIsNode1Candidate = im.Nodes[cursor].Children.Count == 0 && !im.Nodes[cursor].HasMesh;

        void BakeDroppedWrappers(ImportedNode keep, int dropCountAbove)
        {
            if (dropCountAbove <= 0) return;
            // Wrappers were dropped ABOVE `keep` (e.g. a real MrgRoot carrying a non-
            // identity translation/rotation, per ModelExtractor.WriteGlb's -pivot
            // recentring shift). `keep`'s WorldMatrix already includes their contribution
            // (SharpGLTF computes it through the full original ancestor chain), so bake
            // that into its new local TRS now that it is becoming a root with nothing
            // above it - otherwise the dropped wrappers' transform is silently lost and
            // the model shifts position.
            if (Matrix4x4.Decompose(keep.WorldMatrix, out var sc, out var rot, out var tr))
            {
                keep.Scale = sc;
                keep.Rotation = rot;
                keep.Translation = tr;
            }
            else
            {
                keep.Translation = keep.WorldMatrix.Translation;
            }
        }

        if (cursorIsNode1Candidate)
        {
            // node_1 = cursor. node_0 = the last wrapper in the chain (everything before it
            // is outer junk, e.g. a literal MrgRoot, and gets dropped), or a freshly
            // synthesized neutral root if there was no wrapper at all above cursor (e.g. a
            // bare node_1-shaped node sitting directly as the scene root).
            int node1 = cursor;
            if (wrapperChain.Count > 0)
            {
                int node0 = wrapperChain[^1];
                var node0n = im.Nodes[node0];
                BakeDroppedWrappers(node0n, wrapperChain.Count - 1);
                node0n.Parent = -1;
                return (node0, false, slot);
            }
            var freshRoot = new ImportedNode {
                Index = im.Nodes.Count,
                Name = "node_0",
                Parent = -1,
                Children = new List<int> { node1 },
            };
            im.Nodes.Add(freshRoot);
            im.Nodes[node1].Parent = freshRoot.Index;
            return (freshRoot.Index, false, slot);
        }

        if (wrapperChain.Count >= 2)
        {
            // cursor is real branching/content (e.g. Armature with several top-level bone
            // chains), so node_1 must already be the chain's own last element (it passed
            // isWrapper because it has exactly one real child - cursor - itself): the
            // monster/extracted-GLB shape (node_0 -> node_1 -> model_offset/Armature).
            int node0 = wrapperChain[^2];
            var node0n = im.Nodes[node0];
            BakeDroppedWrappers(node0n, wrapperChain.Count - 2);
            node0n.Parent = -1;
            return (node0, false, slot);
        }

        // Fewer than 2 wrapper levels and cursor is real content directly (not node_1-
        // shaped): there is no separate node_1 here at all - e.g. a single MrgRoot/node_0
        // wrapper sitting directly above real geometry, or root0 already carries content/
        // branches immediately (wrapperChain.Count == 0). No node_1 exists yet, so the
        // caller synthesizes a static one (InsertGeneratedPivot). If exactly one wrapper
        // was found, it becomes the root (dropping nothing further above it); otherwise
        // root0 is used as-is.
        if (wrapperChain.Count == 1)
        {
            int node0 = wrapperChain[0];
            im.Nodes[node0].Parent = -1;
            return (node0, true, slot);
        }
        var newRoot = im.Nodes[root0];
        newRoot.Parent = -1;
        return (root0, true, slot);

        bool referenced(int g)
        {
            foreach (var p in im.Primitives)
            {
                if (p.NodeIndex == g) return true;
                foreach (var v in p.Vertices)
                foreach (var inf in v.Influences)
                    if (inf.JointNode == g)
                        return true;
            }
            return false;
        }
    }

    /// Inserts a STATIC (unanimated) leaf pivot node at enc index 1 (the root's FIRST child).
    /// This is a FALLBACK, not the recommended pattern - see the confirmed retail structure
    /// documented at this function's call site (step 4.5): real DOTR monsters keyframe node 1
    /// in every MOT slot, which is what lets the camera follow the model's centre of mass
    /// through its own animation (SzModel_GetCenter re-derives node 1's LIVE transform every
    /// frame). A node inserted here carries no animation channels, so it can only provide the
    /// one-time STATIC placement half of the contract (SzModel_SetPos via GetCenterOfs) - the
    /// per-frame camera-aim point will sit still at this same location for the model's entire
    /// lifetime, never following the rig. There is no way to synthesize camera-follow motion
    /// from geometry alone; a model that wants it needs an artist-animated 'origin'/
    /// 'dotr_center' node, which never reaches this function (PrepareRoot hoists it to
    /// first-child and returns synthPivot=false, so the artist's authored point AND its
    /// animation channels are used verbatim via the normal step-8 channel loop).
    ///
    /// Previously this function placed the synthetic pivot at literal world (0,0,0)
    /// unconditionally, which is only correct if the artist happened to model around the
    /// scene origin; for anything else it produced a placement/camera mismatch that looked
    /// "inconsistent" (floating, sunk, or off-center) depending on the model's authored offset
    /// from (0,0,0). The corrected default anchors to the geometry itself: horizontal centre
    /// (X/Z) of the bounding box, vertical BASE (min Y, i.e. "feet") rather than vertical
    /// centre - this mirrors where retail monsters' own GNode(1) tends to sit (confirmed on
    /// the blue-eyes sample: GNode(1) world pivot Y=-9.16 vs. geometric bbox centre Y=+9.24,
    /// i.e. low in the body, not mid-body) and gives a stable, geometry-derived static
    /// placement instead of an arbitrary authoring-space coordinate. It remains strictly a
    /// fallback: prefer an explicit, animated pivot node whenever camera-follow matters.
    private static void InsertGeneratedPivot(ImportedModel im, List<EncNode> nodes,
        Dictionary<int, int> encIndexOf, float S, FrameDebugState debug,
        Vector3? geomMin = null, Vector3? geomMax = null)
    {
        if (nodes.Count == 0 || S <= 0f) return;

        Vector3 pivotWorld;
        string source;
        if (geomMin is { } gMin && geomMax is { } gMax)
        {
            pivotWorld = new Vector3((gMin.X + gMax.X) * 0.5f, gMin.Y, (gMin.Z + gMax.Z) * 0.5f);
            source = "FALLBACK (static, no camera-follow): geometry horizontal centre / vertical base - no explicit animated 'origin'/'dotr_center' node found";
        }
        else
        {
            pivotWorld = Vector3.Zero;
            source = "FALLBACK (static, no camera-follow): world origin - no geometry and no explicit pivot node found";
        }
        debug.PlacementPivot = pivotWorld;
        debug.PlacementPivotSource = source;

        // remap every index >= 1 to +1
        foreach (var nd in nodes)
        {
            if (nd.Parent >= 1) nd.Parent += 1;
            for (int i = 0; i < nd.Children.Count; i++)
                if (nd.Children[i] >= 1)
                    nd.Children[i] += 1;
        }
        foreach (var key in encIndexOf.Keys.ToList())
            if (encIndexOf[key] >= 1)
                encIndexOf[key] += 1;

        // The pivot is the root's first child with parent accum == 1 (root scale is folded
        // into the ROOT node, not into this synthetic child - see step 4's nd.Scale handling),
        // so its raw Pos is the world pivot divided by the root's own accumulated scale S,
        // matching how every other first-level node's translation is encoded.
        var pivot = new EncNode {
            Scale = (1f, 1f, 1f),
            Flags = 0,
            MeshSlot = 0,
            RotBams = (0, 0, 0),
            Pos = (pivotWorld.X / S, pivotWorld.Y / S, pivotWorld.Z / S),
            Parent = 0,
        };
        nodes.Insert(1, pivot);
        nodes[0].Children.Insert(0, 1);
    }

    private static void DumpFrameDebug(ImportedModel im, TemplateFrame? templateFrame, FrameDebugState dbg,
        List<EncNode> nodes, float rootScale, float animTransScale, Vector3 boundsCenter, float boundsRadius)
    {
        static string V(Vector3 v) => $"({v.X:G9}, {v.Y:G9}, {v.Z:G9})";
        static string N(string? s) => string.IsNullOrWhiteSpace(s) ? "<unnamed>" : s!;

        Console.WriteLine("=== DOTR FRAME DEBUG ===");
        Console.WriteLine("GLB:");
        Console.WriteLine($"  IsDotrExtractedGlb: {im.IsDotrExtractedGlb}");
        Console.WriteLine($"  Nodes: {im.Nodes.Count}");
        Console.WriteLine($"  Joints: {im.JointNodeIndices.Count}");
        Console.WriteLine($"  Root nodes: {string.Join(", ", im.RootNodes.Select(r =>
        {
            int pos = GlbPos(im, r);
            return pos >= 0 ? $"{r}:{N(im.Nodes[pos].Name)}" : $"{r}:<missing>";
        }))}");
        Console.WriteLine($"  Has MrgRoot: {im.Nodes.Any(n => n.Name == "MrgRoot")}");
        Console.WriteLine("  Authoring markers: ignored in DOTR-convention mode");
        Console.WriteLine($"  Geometry bbox valid: {dbg.HasGeometry}");
        Console.WriteLine($"  Geometry bbox min: {V(dbg.BBoxMin)}");
        Console.WriteLine($"  Geometry bbox max: {V(dbg.BBoxMax)}");
        Console.WriteLine($"  Geometry bbox center: {V(dbg.BBoxCenter)}");
        Console.WriteLine();

        Console.WriteLine("Template MRG diagnostics only:");
        if (templateFrame is { IsValid: true })
        {
            Console.WriteLine($"  nodeTree offset: 0x{templateFrame.NodeTreeOffset:X}");
            Console.WriteLine($"  animTransScale: {templateFrame.AnimTransScale:G9}");
            Console.WriteLine($"  rootScale: {templateFrame.RootScale:G9}");
            Console.WriteLine($"  GNode(1) raw pos: {V(templateFrame.PivotRaw)}");
            Console.WriteLine($"  GNode(1) world pivot: {V(templateFrame.PivotWorld)}");
            Console.WriteLine($"  bounds center: {V(templateFrame.BoundsCenterWorld)}");
            Console.WriteLine($"  bounds radius: {templateFrame.BoundsRadius:G9}");
        }
        else
        {
            Console.WriteLine("  <unavailable>");
        }
        Console.WriteLine();

        Console.WriteLine("Generated:");
        Console.WriteLine($"  root0: {dbg.Root0}:{N(dbg.Root0Name)}");
        Console.WriteLine($"  synthPivot: {dbg.SynthPivot}");
        Console.WriteLine($"  consumedRootSlot: {dbg.ConsumedRootSlot}");
        Console.WriteLine($"  generated node count: {nodes.Count}");
        Console.WriteLine($"  generated animTransScale: {animTransScale:G9}");
        Console.WriteLine($"  generated rootScale: {rootScale:G9}");
        if (nodes.Count > 1)
        {
            var raw = new Vector3(nodes[1].Pos.X, nodes[1].Pos.Y, nodes[1].Pos.Z);
            var world = raw * rootScale;
            Console.WriteLine($"  generated GNode(1) raw pos: {V(raw)}");
            Console.WriteLine($"  generated GNode(1) world pivot: {V(world)}");
        }
        else
        {
            Console.WriteLine("  generated GNode(1): <missing>");
        }
        Console.WriteLine($"  selected pivot source: {dbg.PlacementPivotSource}");
        Console.WriteLine($"  selected pivot world: {V(dbg.PlacementPivot)}");
        Console.WriteLine($"  bounds center source: {dbg.CameraTargetSource}");
        Console.WriteLine($"  selected bounds center world: {V(dbg.CameraTarget)}");
        Console.WriteLine($"  generated bounds center: {V(boundsCenter)}");
        Console.WriteLine($"  generated bounds radius: {boundsRadius:G9}");
        Console.WriteLine("  note: DOTR convention uses GNode(1) at origin as the animated control/camera coordinate.");
        Console.WriteLine("========================");
    }

    // glb animation names from the extractor are "slot_N"; fall back to a sequential slot (0..7).
    private static int ParseSlotId(string name, int fallback)
    {
        int p = name.IndexOf("slot_", StringComparison.OrdinalIgnoreCase);
        if (p >= 0)
        {
            int s = p + 5, e = s;
            while (e < name.Length && char.IsDigit(name[e])) e++;
            if (e > s && int.TryParse(name.AsSpan(s, e - s), out int n) && n >= 0 && n <= 7) return n;
        }
        return Math.Clamp(fallback, 0, 7);
    }

    // ---- stripification ----------------------------------------------------------------
    /// Per-batch VU ceiling (mirrors GeometryWriter.VuBudget): a single strip must fit one
    /// batch, i.e. 1 + sum(vnum) <= 224. This also keeps the 8-bit VIF unpack `num` < 255.
    private const int StripVuBudget = 224;

    /// Splits a primitive's triangles into rigid groups (all 3 verts single-influence on the
    /// same joint -> INDEP strips for that joint) and skinned ones (-> SHARED strips, capped
    /// at MaxBonesPerPage distinct joints per strip so page partitioning always succeeds).
    /// Pathological triangles (no valid influence on a vertex, or >8 distinct joints) fall
    /// back to the per-triangle path (BuildTriangleStrip), which caps/drops as before.
    private static IEnumerable<EncStrip> StripifyPrimitive(ImportedPrimitive prim, ImportedModel im,
        Dictionary<int, Matrix4x4> rwsInv, Dictionary<int, int> meshSlotOf)
    {
        // per-vertex valid joints (filtered exactly like materialisation filters influences)
        var jointsCache = new Dictionary<int, int[]>();

        int[] ValidJoints(int vi)
        {
            if (jointsCache.TryGetValue(vi, out var a)) return a;
            a = Influences(prim.Vertices[vi], im)
                .Where(f => rwsInv.ContainsKey(f.JointNode) && meshSlotOf.ContainsKey(f.JointNode))
                .Select(f => f.JointNode).ToArray();
            jointsCache[vi] = a;
            return a;
        }

        int VnumShared(int vi) => 1 + 3 * ValidJoints(vi).Length; // (3 + 9*ninf) shorts / 3

        var rigid = new SortedDictionary<int, List<(int A, int B, int C)>>(); // joint -> tris
        var shared = new List<(int A, int B, int C)>();
        var fallback = new List<(int A, int B, int C)>();

        foreach (var tri in prim.Triangles)
        {
            var (a, b, c) = tri;
            if (a == b || b == c || c == a) continue; // degenerate
            int[] j0 = ValidJoints(a), j1 = ValidJoints(b), j2 = ValidJoints(c);
            if (j0.Length == 0 || j1.Length == 0 || j2.Length == 0)
            {
                fallback.Add(tri);
                continue;
            }
            if (j0.Length == 1 && j1.Length == 1 && j2.Length == 1 && j0[0] == j1[0] && j1[0] == j2[0])
            {
                if (!rigid.TryGetValue(j0[0], out var lst)) rigid[j0[0]] = lst = new List<(int, int, int)>();
                lst.Add(tri);
                continue;
            }
            var distinct = new HashSet<int>(j0);
            distinct.UnionWith(j1);
            distinct.UnionWith(j2);
            if (distinct.Count > MaxBonesPerPage)
            {
                fallback.Add(tri);
                continue;
            }
            shared.Add(tri);
        }

        // ---- rigid -> INDEP strips (one bone per strip; vnum = 3 per vertex) ----
        foreach (var kv in rigid)
        {
            int joint = kv.Key;
            int ms = meshSlotOf[joint];
            var inv = rwsInv[joint];
            foreach (var idx in Stripify(kv.Value, _ => 3, null, 0))
            {
                var strip = new EncStrip { Kind = StripKind.Indep, IndepVuAddr = ms /* placeholder mesh slot */ };
                foreach (int vi in idx)
                {
                    var v = prim.Vertices[vi];
                    var (x, y, z) = QPos(v.Pos, inv);
                    var (nx, ny, nz) = QNorm(v.Normal, inv);
                    strip.Verts.Add(new EncVertex {
                        U = Q(v.Uv.X * UvScale), V = Q(v.Uv.Y * UvScale), Flag = 0,
                        Nx = nx, Ny = ny, Nz = nz, X = x, Y = y, Z = z,
                    });
                }
                yield return strip;
            }
        }

        // ---- skinned -> SHARED strips (<= MaxBonesPerPage distinct joints per strip) ----
        foreach (var idx in Stripify(shared, VnumShared, ValidJoints, MaxBonesPerPage))
        {
            var strip = new EncStrip { Kind = StripKind.Shared };
            foreach (int vi in idx)
            {
                var v = prim.Vertices[vi];
                var ev = new EncVertex { U = Q(v.Uv.X * UvScale), V = Q(v.Uv.Y * UvScale), Influences = new() };
                foreach (var inf in Influences(v, im))
                {
                    if (!rwsInv.TryGetValue(inf.JointNode, out var inv) || !meshSlotOf.TryGetValue(inf.JointNode, out int ms)) continue;
                    var (x, y, z) = QPos(v.Pos, inv);
                    var (nx, ny, nz) = QNorm(v.Normal, inv);
                    ev.Influences.Add(new EncInfluence {
                        Bone = (ushort)ms /* placeholder mesh slot */,
                        Weight = Q(inf.Weight * WeightScale), Flag = 0,
                        Nx = nx, Ny = ny, Nz = nz, X = x, Y = y, Z = z,
                    });
                }
                strip.Verts.Add(ev); // classification guarantees >= 1 valid influence
            }
            yield return strip;
        }

        // ---- pathological tris -> old per-triangle path (caps bones / drops unrigged) ----
        foreach (var (ta, tb, tc) in fallback)
        {
            var s = BuildTriangleStrip(prim.Vertices[ta], prim.Vertices[tb], prim.Vertices[tc], im, rwsInv, meshSlotOf);
            if (s != null) yield return s;
        }
    }

    /// Greedy edge-walk tri-stripper over vertex-index triangles. Returns strips as vertex
    /// index sequences with standard strip semantics (tri i = v[i-2], v[i-1], v[i]); winding
    /// is irrelevant (the GS doesn't cull; the extractor re-orients by normals). Growth stops
    /// when the strip would exceed the per-batch VU budget (1 + sum(vnumOf) <= StripVuBudget)
    /// or, when `jointsOf` is given, span more than `maxJoints` distinct bones.
    private static List<List<int>> Stripify(List<(int A, int B, int C)> tris,
        Func<int, int> vnumOf, Func<int, int[]>? jointsOf, int maxJoints)
    {
        var strips = new List<List<int>>();
        if (tris.Count == 0) return strips;

        var edgeMap = new Dictionary<(int, int), List<int>>();

        void AddEdge(int p, int q, int t)
        {
            var k = p < q ? (p, q) : (q, p);
            if (!edgeMap.TryGetValue(k, out var l)) edgeMap[k] = l = new List<int>();
            l.Add(t);
        }

        for (int t = 0; t < tris.Count; t++)
        {
            var (a, b, c) = tris[t];
            AddEdge(a, b, t);
            AddEdge(b, c, t);
            AddEdge(c, a, t);
        }

        var used = new bool[tris.Count];

        int Third(int t, int p, int q)
        {
            var (a, b, c) = tris[t];
            if (a != p && a != q) return a;
            if (b != p && b != q) return b;
            return c;
        }

        (List<int> Strip, List<int> Tris) Walk(int seed, int a, int b, int c)
        {
            var strip = new List<int> { a, b, c };
            var stripTris = new List<int> { seed };
            var localUsed = new HashSet<int> { seed };
            int vu = 1 + vnumOf(a) + vnumOf(b) + vnumOf(c);
            HashSet<int>? joints = null;
            if (jointsOf != null)
            {
                joints = new HashSet<int>();
                foreach (int v in strip) joints.UnionWith(jointsOf(v));
            }

            bool TryGrow(int r, int growVu)
            {
                if (vu + growVu > StripVuBudget) return false;
                if (joints != null)
                {
                    int grow = 0;
                    foreach (int j in jointsOf!(r))
                        if (!joints.Contains(j))
                            grow++;
                    if (joints.Count + grow > maxJoints) return false;
                }
                return true;
            }

            // forward: extend from the trailing edge (strip[^2], strip[^1])
            while (true)
            {
                int p = strip[^2], q = strip[^1];
                var k = p < q ? (p, q) : (q, p);
                if (!edgeMap.TryGetValue(k, out var cand)) break;
                int next = -1;
                foreach (int t in cand)
                    if (!used[t] && !localUsed.Contains(t))
                    {
                        next = t;
                        break;
                    }
                if (next < 0) break;
                int r = Third(next, p, q);
                if (!TryGrow(r, vnumOf(r))) break;
                if (joints != null) joints.UnionWith(jointsOf!(r));
                strip.Add(r);
                vu += vnumOf(r);
                stripTris.Add(next);
                localUsed.Add(next);
            }

            // backward: extend from the LEADING edge (strip[0], strip[1]) - a seed sitting
            // in the middle of what would otherwise be one long run only got captured
            // forward above; this claims the other half too, as part of the SAME strip
            // (sharing vu/joints/localUsed so the combined strip still respects one budget
            // and the same triangle is never claimed by both legs).
            while (true)
            {
                int p = strip[0], q = strip[1];
                var k = p < q ? (p, q) : (q, p);
                if (!edgeMap.TryGetValue(k, out var cand)) break;
                int next = -1;
                foreach (int t in cand)
                    if (!used[t] && !localUsed.Contains(t))
                    {
                        next = t;
                        break;
                    }
                if (next < 0) break;
                int r = Third(next, p, q);
                if (!TryGrow(r, vnumOf(r))) break;
                if (joints != null) joints.UnionWith(jointsOf!(r));
                strip.Insert(0, r);
                vu += vnumOf(r);
                stripTris.Add(next);
                localUsed.Add(next);
            }

            return (strip, stripTris);
        }

        for (int t = 0; t < tris.Count; t++)
        {
            if (used[t]) continue;
            var (a, b, c) = tris[t];
            var w1 = Walk(t, a, b, c);
            var w2 = Walk(t, b, c, a);
            var w3 = Walk(t, c, a, b);
            var best = w1.Strip.Count >= w2.Strip.Count
                ? (w1.Strip.Count >= w3.Strip.Count ? w1 : w3)
                : (w2.Strip.Count >= w3.Strip.Count ? w2 : w3);
            foreach (int u in best.Tris) used[u] = true;
            strips.Add(best.Strip);
        }
        return strips;
    }

    // ---- triangle -> strip (fallback for pathological triangles) ----------------------
    private static EncStrip? BuildTriangleStrip(ImportedVertex v0, ImportedVertex v1, ImportedVertex v2,
        ImportedModel im, Dictionary<int, Matrix4x4> rwsInv, Dictionary<int, int> meshSlotOf)
    {
        var verts = new[] { v0, v1, v2 };
        // copy each vertex's influence list so capping never mutates the source model
        var vinfs = verts.Select(v => new List<ImportedInfluence>(Influences(v, im))).ToArray();

        bool rigid = vinfs.All(l => l.Count == 1) &&
                     vinfs[0][0].JointNode == vinfs[1][0].JointNode &&
                     vinfs[1][0].JointNode == vinfs[2][0].JointNode;

        if (rigid)
        {
            int joint = vinfs[0][0].JointNode;
            if (!rwsInv.TryGetValue(joint, out var inv) || !meshSlotOf.TryGetValue(joint, out int ms)) return null;
            var strip = new EncStrip { Kind = StripKind.Indep, IndepVuAddr = ms /* placeholder mesh slot */ };
            foreach (var v in verts)
            {
                var (x, y, z) = QPos(v.Pos, inv);
                var (nx, ny, nz) = QNorm(v.Normal, inv);
                strip.Verts.Add(new EncVertex {
                    U = Q(v.Uv.X * UvScale), V = Q(v.Uv.Y * UvScale), Flag = 0,
                    Nx = nx, Ny = ny, Nz = nz, X = x, Y = y, Z = z,
                });
            }
            return strip;
        }

        CapTriangleBones(vinfs, MaxBonesPerPage); // PS2 page can bind at most 8 matrices
        var sh = new EncStrip { Kind = StripKind.Shared };
        for (int t = 0; t < 3; t++)
        {
            var v = verts[t];
            var ev = new EncVertex { U = Q(v.Uv.X * UvScale), V = Q(v.Uv.Y * UvScale), Influences = new() };
            foreach (var inf in vinfs[t])
            {
                if (!rwsInv.TryGetValue(inf.JointNode, out var inv) || !meshSlotOf.TryGetValue(inf.JointNode, out int ms)) continue;
                var (x, y, z) = QPos(v.Pos, inv);
                var (nx, ny, nz) = QNorm(v.Normal, inv);
                ev.Influences.Add(new EncInfluence {
                    Bone = (ushort)ms /* placeholder mesh slot */,
                    Weight = Q(inf.Weight * WeightScale), Flag = 0,
                    Nx = nx, Ny = ny, Nz = nz, X = x, Y = y, Z = z,
                });
            }
            if (ev.Influences.Count == 0) return null;
            sh.Verts.Add(ev);
        }
        return sh;
    }

    // If a triangle references more than maxBones distinct joints (impossible for the PS2 page
    // to bind), drop the globally weakest joint and renormalise, until it fits. Only triggers
    // on pathological edits; the shipped models peak at 5 distinct bones per triangle.
    private static void CapTriangleBones(List<ImportedInfluence>[] vinfs, int maxBones)
    {
        var distinct = new HashSet<int>();
        foreach (var lst in vinfs)
        foreach (var inf in lst)
            distinct.Add(inf.JointNode);
        while (distinct.Count > maxBones)
        {
            var total = new Dictionary<int, float>();
            foreach (var lst in vinfs)
            foreach (var inf in lst)
                total[inf.JointNode] = total.GetValueOrDefault(inf.JointNode) + inf.Weight;
            int drop = total.OrderBy(kv => kv.Value).First().Key;
            foreach (var lst in vinfs) lst.RemoveAll(inf => inf.JointNode == drop);
            distinct.Remove(drop);
            foreach (var lst in vinfs)
            {
                float s = lst.Sum(i => i.Weight);
                if (s <= 0f) continue;
                for (int i = 0; i < lst.Count; i++)
                {
                    var x = lst[i];
                    x.Weight /= s;
                    lst[i] = x;
                }
            }
        }
    }

    private static void RemapSharedBones(EncStrip s, Dictionary<int, int> slot2vu)
    {
        foreach (var v in s.Verts)
        {
            if (v.Influences == null) continue;
            for (int i = 0; i < v.Influences.Count; i++)
            {
                var inf = v.Influences[i];
                inf.Bone = (ushort)slot2vu[inf.Bone]; // placeholder mesh slot -> VU slot
                v.Influences[i] = inf;
            }
        }
    }

    // ---- greedy <=8-bone partition (validated: full coverage, every page <=8) --------
    private static List<List<EncStrip>> Partition(List<EncStrip> strips)
    {
        var pages = new List<List<EncStrip>>();
        var remaining = new List<EncStrip>(strips);
        while (remaining.Count > 0)
        {
            remaining.Sort((a, b) => StripBones(b).Count.CompareTo(StripBones(a).Count));
            var page = new HashSet<int>(StripBones(remaining[0]));
            var members = new List<EncStrip> { remaining[0] };
            remaining.RemoveAt(0);
            bool progress = true;
            while (progress)
            {
                progress = false;
                int bestIdx = -1, bestNew = int.MaxValue;
                for (int i = 0; i < remaining.Count; i++)
                {
                    var bs = StripBones(remaining[i]);
                    int union = page.Count + bs.Count(b => !page.Contains(b));
                    if (union <= MaxBonesPerPage && (union - page.Count) < bestNew)
                    {
                        bestNew = union - page.Count;
                        bestIdx = i;
                    }
                }
                if (bestIdx >= 0)
                {
                    foreach (int b in StripBones(remaining[bestIdx])) page.Add(b);
                    members.Add(remaining[bestIdx]);
                    remaining.RemoveAt(bestIdx);
                    progress = true;
                }
            }
            pages.Add(members);
        }
        return pages;
    }

    private static HashSet<int> StripBones(EncStrip s)
    {
        var set = new HashSet<int>();
        if (s.Kind == StripKind.Indep) set.Add(s.IndepVuAddr); // placeholder = mesh slot pre-remap
        else
            foreach (var v in s.Verts)
                if (v.Influences != null)
                    foreach (var inf in v.Influences)
                        set.Add(inf.Bone);
        return set;
    }

    // ---- helpers --------------------------------------------------------------------
    private static int GlbPos(ImportedModel im, int glbIndex) => glbIndex; // ImportedModel.Nodes is indexed by logical index

    /// A "pure mesh-instancing leaf": HasMesh, no children, not itself a skin joint, and
    /// crucially none of its own vertices are influenced by its own node index (i.e. its
    /// geometry is fully described by OTHER joints' influences). This is exactly the shape
    /// every MODEL.MRG-style export produces: MrgRoot -> [node_0 -> node_1, MrgMesh], where
    /// MrgMesh instances the skinned mesh but the skin's only joint is node_1, not MrgMesh
    /// itself (verified on model_037/039/189.glb and model0ab.glb). Distinguished from a
    /// genuine UNSKINNED/rigid mesh node (props/vehicles, GlbImporter's "bake node world
    /// transform, rig as a one-bone joint" path): that case explicitly sets each vertex's
    /// influence to meshNode.LogicalIndex itself (GlbImporter.cs's unskinned branch), so the
    /// self-influence check below correctly keeps it as a real tree node (it must occupy a
    /// DFS slot to receive a meshSlot). A node matching this predicate must be pruned from
    /// the tree-walk / not counted as a "real" child when detecting wrapper chains: meshSlot
    /// assignment (effectiveJoints/meshSlotOf) is already derived purely from skin/influence
    /// data, never from node-tree position, so the encoder needs nothing from this node
    /// being present in the encoded tree at all.
    private static bool IsPureMeshInstancingLeaf(ImportedModel im, int g)
    {
        int pos = GlbPos(im, g);
        if (pos < 0) return false;
        var n = im.Nodes[pos];
        if (!n.HasMesh || n.IsJoint || n.Children.Count != 0) return false;
        foreach (var prim in im.Primitives)
        {
            if (prim.NodeIndex != g) continue;
            foreach (var v in prim.Vertices)
            foreach (var inf in v.Influences)
                if (inf.JointNode == g)
                    return false; // self-influenced: this IS the rigid/unskinned case
        }
        return true;
    }

    private static List<ImportedInfluence> Influences(ImportedVertex v, ImportedModel im)
    {
        if (v.Influences.Count > 0) return v.Influences;
        // unrigged vertex: bind to the first joint so it still has a bone
        if (im.JointNodeIndices.Count > 0)
            return new List<ImportedInfluence> { new ImportedInfluence { JointNode = im.JointNodeIndices[0], Weight = 1f } };
        return v.Influences;
    }

    private static (short X, short Y, short Z) QPos(Vector3 world, Matrix4x4 invRws)
    {
        var p = Vector3.Transform(world, invRws);
        return (Q(p.X), Q(p.Y), Q(p.Z));
    }

    private static (short Nx, short Ny, short Nz) QNorm(Vector3 worldN, Matrix4x4 invRws)
    {
        var n = Vector3.TransformNormal(worldN, invRws);
        float len = n.Length();
        if (len < 1e-8f) return (0, 0, (short)NormScale);
        n = n / len * NormScale;
        return (Q(n.X), Q(n.Y), Q(n.Z));
    }

    private static short Q(float v)
    {
        int r = (int)MathF.Round(v);
        if (r > 32767) r = 32767;
        else if (r < -32768) r = -32768;
        return (short)r;
    }

    private static (int X, int Y, int Z) QuatToBams(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(q);
        double m13 = Math.Clamp(m.M13, -1.0, 1.0);
        double ry = Math.Asin(-m13);
        double rx = Math.Atan2(m.M23, m.M33);
        double rz = Math.Atan2(m.M12, m.M11);
        return (BamsRound(rx), BamsRound(ry), BamsRound(rz));
    }

    /// Same analytic XYZ-Euler decomposition as <see cref="QuatToBams"/>, but resolves the
    /// representation ambiguity (every rotation has at least two valid Euler triples — the
    /// "principal" branch this decomposition returns, and an alternate branch ry'=pi-ry,
    /// rx'=rx+pi, rz'=rz+pi — plus each angle independently wraps mod a full BAMS circle)
    /// by choosing whichever BAMS-equivalent candidate is closest to the previous encoded
    /// frame, instead of always taking the principal value.
    ///
    /// Why this matters: the game's MOT interpolator does a plain linear blend between
    /// consecutive keyframe BAMS values (DotR_FileFormats.md §9), so it has no concept of
    /// "this is the same orientation, just a different angle representation" — it just
    /// interpolates the raw numbers. Decoding each glTF keyframe quaternion independently
    /// (this method's predecessor) reproduces the correct POSE at each keyframe in isolation,
    /// but is free to flip to a different, equally-valid Euler branch on any frame, which
    /// turns a perfectly smooth source rotation into a track the linear interpolator spins
    /// through almost a full extra revolution to get from one keyframe to the next. Verified
    /// against the shipped blue-eyes_white_dragon.mrg: re-decoding several joints' attack/
    /// motion-slot rotation tracks frame-independently produced single-frame jumps up to
    /// ~359 degrees where the original retail data only ever steps a few degrees per frame.
    private static (int X, int Y, int Z) QuatToBamsContinuous(Quaternion q, (int X, int Y, int Z)? prev)
    {
        var principal = QuatToBams(q);
        if (prev is not { } p) return principal;

        // Alternate Euler branch for the same rotation (XYZ convention): negate the pivot
        // angle around its singularity (ry -> pi - ry, equivalently -(ry - pi)) and shift the
        // other two by a half turn each. Implemented in BAMS directly: HalfTurn = 0x8000.
        const int HalfTurn = 0x8000;
        var alt = (
            X: principal.X + HalfTurn,
            Y: HalfTurn - principal.Y,
            Z: principal.Z + HalfTurn);

        (int X, int Y, int Z) BestWrap((int X, int Y, int Z) cand, (int X, int Y, int Z) target)
        {
            // Each axis independently may also need a +/- full-circle shift to land in the
            // wrap nearest the previous frame (e.g. previous=32700, principal=-32750 is only
            // 134 BAMS away once wrapped, not ~65450 away as raw integers).
            int Nearest(int v, int t)
            {
                int d = (v - t) % 65536;
                if (d > 32768) d -= 65536;
                else if (d < -32768) d += 65536;
                return t + d;
            }
            return (Nearest(cand.X, target.X), Nearest(cand.Y, target.Y), Nearest(cand.Z, target.Z));
        }

        var principalNear = BestWrap(principal, p);
        var altNear = BestWrap(alt, p);

        long Cost((int X, int Y, int Z) c) =>
            Math.Abs((long)c.X - p.X) + Math.Abs((long)c.Y - p.Y) + Math.Abs((long)c.Z - p.Z);

        return Cost(principalNear) <= Cost(altNear) ? principalNear : altNear;
    }

    private static int BamsRound(double rad) => (int)Math.Round(rad * BamsPerRad);

    private readonly struct MatKey : IEquatable<MatKey>
    {
        public readonly int TexIdx;
        public readonly bool IsAlt;
        public readonly int R, G, B;
        public readonly float A;

        public MatKey(int tex, bool alt, Vector4 baseColor)
        {
            TexIdx = tex;
            IsAlt = alt;
            // The +0x20 row is a lighting input the engine scales at runtime; retail's
            // neutral is (178,178,178), so exact White (the glTF default / "no tint")
            // maps to that for normal pages - emitting 255 bakes in a permanent ~1.43x
            // over-brightness vs retail. Alt (additive glow) pages keep full range.
            bool white = baseColor.X >= 0.9999f && baseColor.Y >= 0.9999f && baseColor.Z >= 0.9999f;
            if (white && !alt)
            {
                R = G = B = 178;
            }
            else
            {
                R = (int)MathF.Round(Math.Clamp(baseColor.X, 0, 1) * 255f);
                G = (int)MathF.Round(Math.Clamp(baseColor.Y, 0, 1) * 255f);
                B = (int)MathF.Round(Math.Clamp(baseColor.Z, 0, 1) * 255f);
            }
            // lRhModel_SetMaterialIntensity gates the +0x20 row's SET/MUL handling on
            // R != 0 - a pure-black red component would change engine behaviour, so clamp.
            if (R == 0) R = 1;
            A = Math.Clamp(baseColor.W, 0, 1) * 128f;
        }

        public bool Equals(MatKey o) => TexIdx == o.TexIdx && IsAlt == o.IsAlt && R == o.R && G == o.G && B == o.B && A == o.A;
        public override bool Equals(object? o) => o is MatKey m && Equals(m);
        public override int GetHashCode() => HashCode.Combine(TexIdx, IsAlt, R, G, B, A);
    }
}