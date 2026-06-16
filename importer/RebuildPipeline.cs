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

    public sealed class TemplateFrame
    {
        /// World-space engine placement pivot: retail GNode(1).pos / animTransScale.
        public Vector3 PivotWorld;

        /// World-space camera/framing centre from the donor node-tree bounds record.
        public Vector3 BoundsCenterWorld;

        /// Optional donor bounding radius. When valid, it is emitted unchanged so camera
        /// distance/deform paths keep the donor slot's original size class.
        public float BoundsRadius;

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
        /// from it; SzModel_GetCenter returns the per-frame world transform of the centre, which the
        /// duel/3D-battle camera and the 3D monster view aim their VERTICAL look-at at. Centre =
        /// the model's GEOMETRIC bbox centre so those views frame the body's middle (anchoring it on
        /// GNode(1) made the camera aim at the feet -> monsters too high). The model's placement pivot
        /// is GNode(1)/'origin', a SEPARATE knob (SzModel_GetCenterOfs reads the node tree directly,
        /// not this record). Radius = bounding-sphere radius about that centre.
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
        TemplateFrame? templateFrame = null, bool fitExternalToTemplateFrame = true,
        bool scaleExternalToTemplateFrame = true, float externalCameraLookAtHeightRatio = 0.55f)
    {
        if (im.RootNodes.Count == 0) throw new InvalidOperationException("glb has no root node");

        // ---- 0. donor/template coordinate-frame adoption ----
        // The game is not purely data-driven here: overworld, duel, Act3DView and battle all
        // use hardcoded/per-card assumptions around the model slot's original GNode(1) pivot,
        // bounds centre and radius. Replacing Blue-Eyes, for example, works best when the new
        // geometry is fitted into Blue-Eyes' existing engine frame, not when the importer invents
        // a fresh feet-origin or centre-origin frame.
        //
        // Therefore: keep the donor's camera centre/radius and GNode(1) placement pivot, and
        // translate the imported GLB's geometry/skeleton so its visual bbox centre lands on the
        // donor bounds centre. This mirrors what the extractor's MrgRoot wrapper represents:
        // a Blender-friendly view layered over the engine's native coordinate frame.
        Vector3? donorPivotWorld = null;
        Vector3? donorBoundsCenterWorld = null;
        float donorBoundsRadius = 0f;
        bool useTemplateFrameForExternal = templateFrame is { IsValid: true } &&
            fitExternalToTemplateFrame && !im.IsDotrExtractedGlb;
        if (templateFrame is { IsValid: true })
        {
            donorPivotWorld = templateFrame.PivotWorld;
            donorBoundsCenterWorld = templateFrame.BoundsCenterWorld;
            donorBoundsRadius = templateFrame.BoundsRadius;
            if (useTemplateFrameForExternal)
                ApplyTemplateFrameFit(im, templateFrame, scaleExternalToTemplateFrame, externalCameraLookAtHeightRatio);
        }

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

        // ---- 1. DFS preorder; node[0] = root, extra roots re-parented under it ----
        var order = new List<int>(); // glb node indices, preorder
        var encIndexOf = new Dictionary<int, int>(); // glb index -> position in `order`

        bool IsImportHelperNode(int g) =>
            GlbPos(im, g) >= 0 && GlbImporter.IsAuthoringMarker(im.Nodes[GlbPos(im, g)].Name);

        void Dfs(int g)
        {
            if (IsImportHelperNode(g)) return;
            encIndexOf[g] = order.Count;
            order.Add(g);
            foreach (int c in im.Nodes[GlbPos(im, g)].Children)
                if (!IsImportHelperNode(c)) Dfs(c);
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
        // distance from global origin, so extracted round-trips and donor-frame-fitted models
        // looked 2-3x "too large" merely because the donor slot lives far from (0,0,0).
        // Radius-around-camera is stable for both MrgRoot round-trips and external models.
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
        if (S <= 0f || float.IsNaN(S)) S = 1f;
        if (templateAnimTransScale > 0f && !float.IsNaN(templateAnimTransScale) &&
            !float.IsInfinity(templateAnimTransScale))
        {
            float templS = 1f / templateAnimTransScale;
            if (maxLocal / templS <= 32700.0) S = templS; // adopt template scale when it fits
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

        // ---- 4.5 synthetic GNode(1) pivot for external models ----
        // Extractor glbs ('MrgRoot') keep the retail tree verbatim, so retail node 1 is
        // already the pivot AND externally loaded retail motions (.nab, applied by node
        // index with zero validation - lSzModel_LoadMotionCB@0x1ee4c0) stay aligned.
        // External models get a leaf node inserted as the root's first child at the
        // world-space bounding-box center of the geometry.
        if (synthPivot)
            InsertCenterPivot(im, nodes, encIndexOf, S, donorPivotWorld);
        else if (useTemplateFrameForExternal && donorPivotWorld.HasValue && nodes.Count > 1)
            nodes[1].Pos = (donorPivotWorld.Value.X / S, donorPivotWorld.Value.Y / S, donorPivotWorld.Value.Z / S);

        // ---- 4.6 world-accurate explicit pivot ('origin') ----
        // GNode(1) is what the duel / 3D-battle / enemy-preview camera centers on for ANIMATED
        // monsters: SzModel_DrawWithFunc_Pipe@0x1ec350 books center(+0x80) = GetAnyCoordinate(1),
        // and coordinate number 1 == node DFS index 1 == GNode(1) (require_coordinate_index resets
        // to 0 in RhModel_Draw@0x20d3b8, root visited first; booked in lRhModel_SetupNoMotionRecursive).
        // The pin (SzModel_SetPos(-GetCenterOfs)) puts GNode(1) at world origin, so the camera aims
        // at world 0 and the framing is decided by where the geometry sits relative to GNode(1):
        // GNode(1) at the FEET -> body entirely above origin -> "too high"; GNode(1) mid-body ->
        // geometry straddles origin -> centered (retail convention). The bounds sphere (+0x90) only
        // drives STATIC models, so it can't fix this. Keep the empty's X/Z (overworld rotation pivot
        // is about the vertical axis, so its Y is irrelevant there) and lift GNode(1).Y to the
        // geometry vertical centre.
        // Explicit pivot nodes are now honoured as-authored. Do not silently lift their Y
        // to the bbox centre: that makes Act3DView look better but breaks overworld/duel
        // placement. External replacement GLBs should use the donor-frame path above;
        // extracted MrgRoot GLBs should preserve the retail GNode(1) exactly.
        // ---- 4.7 node-tree bounds record (centre + radius) ----
        // The duel/3D-battle camera (SzDuel_Draw3DView_Pipe@0x1bf820) and the 3D monster view /
        // enemy preview (Taku_Camp_Teki_Model_Disp@0x21be30) point the camera's VERTICAL look-at at
        // the engine's booked centre (SzModel_GetCenter = handle+0x80), which lSzModel_LoadCB@0x1e6880
        // seeds from THIS record (nodeTreeBase+0x30) and SzModel_DrawWithFunc_Pipe@0x1ec350 transforms
        // into world space each frame. The model's vertical PLACEMENT, by contrast, is pinned by
        // GNode(1): the duel does a one-time SetPos(0x1d, -GetCenterOfs) and GetCenterOfs =
        // GNode(1).pos / animTransScale, read straight from the node tree — it does NOT consult this
        // record. So these are two independent knobs: GNode(1) decides where the model SITS, this
        // centre decides where the camera LOOKS.
        //
        // Anchoring this centre on GNode(1) (the previous behaviour) makes the camera aim at GNode(1).
        // For real monsters GNode(1) sits low (root/feet), so the camera framed the feet and the body
        // rendered above the aim point -> monsters appeared "too high" in the duel, 3D battle and
        // monster view. The correct anchor is the model's GEOMETRIC centre (the standard meaning of a
        // bounds record): the camera then frames the body's middle and the monster sits centred. This
        // is stable under idle/turn spin (rotation about the vertical axis leaves the centre's Y
        // unchanged) and the framing paths read only .y, so it cannot wobble. Units are already
        // correct: v.Pos is the glb WORLD position and the reconstruction reproduces glb world 1:1, so
        // the bbox centre is in the same space the engine transforms handle+0x90 from (radius lands
        // ~40, inside the retail ~40-133 band).
        Vector3 boundsCenter;
        {
            var bmin = new Vector3(float.MaxValue);
            var bmax = new Vector3(float.MinValue);
            bool anyV = false;
            foreach (var prim in im.Primitives)
            foreach (var v in prim.Vertices)
            {
                var p = v.Pos;
                if (float.IsNaN(p.X) || float.IsInfinity(p.X) ||
                    float.IsNaN(p.Y) || float.IsInfinity(p.Y) ||
                    float.IsNaN(p.Z) || float.IsInfinity(p.Z)) continue;
                bmin = Vector3.Min(bmin, p);
                bmax = Vector3.Max(bmax, p);
                anyV = true;
            }
            // No usable geometry (e.g. an all-empties model): fall back to GNode(1)'s world position
            // so the camera at least aims at the pivot rather than the origin.
            boundsCenter = anyV ? (bmin + bmax) * 0.5f
                : nodes.Count > 1 ? new Vector3(nodes[1].Pos.X * S, nodes[1].Pos.Y * S, nodes[1].Pos.Z * S)
                : Vector3.Zero;
            // For external donor-frame imports, the camera point is the transformed
            // Blender `camera_lookat` marker, or the transformed bbox centre when no marker
            // exists. Do NOT blindly force the donor's original bounds centre here: that can
            // preserve world placement while making the viewer/battle camera aim below a
            // differently-shaped replacement model. Extracted MrgRoot GLBs still round-trip
            // their own computed/retail centre.
            if (useTemplateFrameForExternal && im.FittedCameraLookAtWorld.HasValue)
                boundsCenter = im.FittedCameraLookAtWorld.Value;
            else if (!useTemplateFrameForExternal && donorBoundsCenterWorld.HasValue)
                boundsCenter = donorBoundsCenterWorld.Value;
        }
        // Radius = bounding-sphere radius about the centre over all WORLD-space vertices. Clamp to
        // a small positive value: the engine computes 40/radius (clamped [0.3,1]) for deform, so a
        // zero/denormal radius would skew framing/deform. Single-vertex/degenerate models get 1.0.
        float boundsRadius = 0f;
        foreach (var prim in im.Primitives)
        foreach (var v in prim.Vertices)
        {
            var p = v.Pos;
            if (float.IsNaN(p.X) || float.IsInfinity(p.X)) continue;
            boundsRadius = Math.Max(boundsRadius, Vector3.Distance(p, boundsCenter));
        }
        // External replacements get a radius around their own fitted camera point.
        // Reusing the donor radius is what caused bogus size warnings and can also skew
        // camera/deform behaviour for very different silhouettes. Keep donor radius only
        // for non-external/template cases.
        if (!useTemplateFrameForExternal && donorBoundsRadius > 1e-5f && !float.IsNaN(donorBoundsRadius) && !float.IsInfinity(donorBoundsRadius))
            boundsRadius = donorBoundsRadius;
        else if (!(boundsRadius > 1e-3f) || float.IsNaN(boundsRadius) || float.IsInfinity(boundsRadius))
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
                    track.Rot = new List<(int, int, int, int)>(rk.Count);
                    foreach (var (t, q) in rk)
                    {
                        int f = (int)MathF.Round(t * Fps);
                        var (rx, ry, rz) = QuatToBams(q);
                        track.Rot.Add((f, rx, ry, rz));
                        if (f > maxFrame) maxFrame = f;
                    }
                }
                if (track.Trans != null || track.Rot != null) motion.Tracks[enc] = track;
            }
            motion.FrameCount = maxFrame + 1;
            motions.Add(motion);
        }

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

    private static void ApplyTemplateFrameFit(ImportedModel im, TemplateFrame templateFrame, bool allowScale, float cameraLookAtHeightRatio)
    {
        if (im.AppliedTemplateFrameFit) return;

        if (!ComputeBounds(im, out var min, out var max)) return;
        var bboxCenter = (min + max) * 0.5f;

        // Blender-facing convention for external replacements:
        //   worldPosCenter = the source point that should become the donor GNode(1) pivot.
        //                    If absent, use Blender/world origin. This is important for
        //                    rotation: bbox centre can be behind/off-centre on monsters with
        //                    wings, tails, cloaks, arms, etc., which makes the model rotate
        //                    around a point behind itself.
        //   camera_lookat  = the source point that should become the model bounds/camera centre.
        //                    If absent, use a stable authoring fallback above the model base
        //                    at sourcePivot.X/Z, rather than the full bbox centre. Full bbox
        //                    centre is often too high/behind for asymmetric monsters.
        //
        // Authoring rule: for normal Blender replacements put the desired world/rotation
        // pivot at 0,0,0, or add an explicit empty named worldPosCenter. Add camera_lookat
        // when the default 55% height target is not good enough.
        var sourcePivot = im.AuthorWorldPosCenterWorld ?? Vector3.Zero;
        cameraLookAtHeightRatio = Math.Clamp(cameraLookAtHeightRatio, 0.05f, 0.95f);
        float h = MathF.Max(max.Y - min.Y, 1e-5f);
        var fallbackCamera = new Vector3(
            sourcePivot.X,
            min.Y + h * cameraLookAtHeightRatio,
            sourcePivot.Z);
        var sourceCamera = im.AuthorCameraLookAtWorld ?? fallbackCamera;
        float sourceRadius = ComputeRadius(im, sourceCamera);

        float scale = 1f;
        if (allowScale)
        {
            bool markerScale = false;
            var sourceDelta = sourceCamera - sourcePivot;
            var donorDelta = templateFrame.BoundsCenterWorld - templateFrame.PivotWorld;

            // If the user supplied camera_lookat/worldPosCenter, match their vertical
            // placement-to-camera distance to the donor slot. This preserves the original
            // hardcoded slot framing while still letting the replacement pick what "feet"
            // and "look at" mean. Without markers, radius scale is a better default.
            if (im.AuthorCameraLookAtWorld.HasValue && im.AuthorWorldPosCenterWorld.HasValue)
            {
                if (MathF.Abs(sourceDelta.Y) > 1e-5f && MathF.Abs(donorDelta.Y) > 1e-5f)
                {
                    scale = MathF.Abs(donorDelta.Y / sourceDelta.Y);
                    markerScale = scale > 1e-6f && !float.IsNaN(scale) && !float.IsInfinity(scale);
                }
                else
                {
                    float sl = sourceDelta.Length();
                    float dl = donorDelta.Length();
                    if (sl > 1e-5f && dl > 1e-5f)
                    {
                        scale = dl / sl;
                        markerScale = scale > 1e-6f && !float.IsNaN(scale) && !float.IsInfinity(scale);
                    }
                }
            }

            // Do not auto-scale purely from radius when no explicit marker pair exists.
            // Radius matching made some replacements report/behave several times larger
            // than the donor and changes authored world placement unexpectedly. With no
            // marker pair, preserve the Blender-authored size and surface only a warning.
            if (!markerScale) scale = 1f;

            if (!(scale > 1e-6f) || float.IsNaN(scale) || float.IsInfinity(scale)) scale = 1f;
        }

        // Placement solve: sourcePivot maps exactly to donor GNode(1). The camera point is
        // then whatever the transformed camera_lookat/bbox centre becomes; that value is
        // emitted into the node-tree bounds record later.
        var delta = templateFrame.PivotWorld - sourcePivot * scale;
        var xform = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(delta);
        var fittedCamera = Vector3.Transform(sourceCamera, xform);

        foreach (var prim in im.Primitives)
        foreach (var v in prim.Vertices)
            v.Pos = Vector3.Transform(v.Pos, xform);

        // Move root locals and cached world matrices with the same global transform so
        // inverse bind / RWS math stays coherent. Do not use donor bounds as a hard camera
        // override for external models; the transformed camera marker is the authored target.
        foreach (int r in im.RootNodes)
        {
            int rp = GlbPos(im, r);
            if (rp < 0) continue;
            im.Nodes[rp].Translation = im.Nodes[rp].Translation * scale + delta;
            im.Nodes[rp].Scale *= scale;
        }
        for (int i = 0; i < im.Nodes.Count; i++)
            im.Nodes[i].WorldMatrix = im.Nodes[i].WorldMatrix * xform;

        im.FittedCameraLookAtWorld = fittedCamera;
        im.AppliedTemplateFrameFit = true;
    }

    private static bool ComputeBounds(ImportedModel im, out Vector3 min, out Vector3 max)
    {
        bool any = false;
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        foreach (var prim in im.Primitives)
        foreach (var v in prim.Vertices)
        {
            var p = v.Pos;
            if (float.IsNaN(p.X) || float.IsInfinity(p.X) ||
                float.IsNaN(p.Y) || float.IsInfinity(p.Y) ||
                float.IsNaN(p.Z) || float.IsInfinity(p.Z)) continue;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            any = true;
        }
        return any;
    }

    private static float ComputeRadius(ImportedModel im, Vector3 center)
    {
        float radius = 0f;
        foreach (var prim in im.Primitives)
        foreach (var v in prim.Vertices)
        {
            var p = v.Pos;
            if (float.IsNaN(p.X) || float.IsInfinity(p.X) ||
                float.IsNaN(p.Y) || float.IsInfinity(p.Y) ||
                float.IsNaN(p.Z) || float.IsInfinity(p.Z)) continue;
            radius = Math.Max(radius, Vector3.Distance(p, center));
        }
        return radius;
    }

    // ---- root preparation / pivot guarantee -------------------------------------------

    /// Picks the encoding root and strips wrapper nodes above it.
    /// Returns (rootGlbIndex, synthesizePivot):
    ///   * Extractor glbs: the scene root is the synthetic 'MrgRoot' wrapper
    ///     (ModelExtractor.WriteGlb) with the retail root as its only child. Dropping
    ///     exactly that wrapper restores the retail tree - retail GNode(1) is the pivot
    ///     again and node indices match retail motion files. No pivot is synthesised.
    ///   * External glbs: trivial single-child wrapper chains (no mesh, not a skin joint,
    ///     not animated, no rigid vertices) are collapsed and their accumulated transform
    ///     is baked into the new root's local TRS (its WorldMatrix is unchanged by the
    ///     collapse, so children and world-baked vertices stay valid). A direct child
    ///     named 'dotr_center' is honoured as an explicit pivot (moved to first-child
    ///     position); otherwise the caller inserts a synthetic one.
    private static (int Root, bool SynthPivot, int ConsumedRootSlot) PrepareRoot(ImportedModel im)
    {
        // prefer the first scene root that actually has children (the skinned-mesh node
        // our extractor emits is a childless sibling root)
        int slot = 0;
        for (int r = 0; r < im.RootNodes.Count; r++)
            if (!GlbImporter.IsAuthoringMarker(im.Nodes[im.RootNodes[r]].Name) &&
                im.Nodes[im.RootNodes[r]].Children.Count > 0)
            {
                slot = r;
                break;
            }
        int root0 = im.RootNodes[slot];

        // -- extractor glb: drop exactly the MrgRoot wrapper, trust the retail tree --
        var rn = im.Nodes[root0];
        if (rn.Name == "MrgRoot" && rn.Children.Count == 1)
        {
            int retailRoot = rn.Children[0];
            im.Nodes[retailRoot].Parent = -1; // MrgRoot is identity (or a pure recenter
            // translation the world matrices already
            // carry): no baking needed.
            // Honour a user-added 'origin' empty: hoist it to first-child so §4.6 reads it
            // as the GNode(1) pivot. Absent one, retail node 1 stays the pivot.
            HoistPivotChild(im, retailRoot);
            return (retailRoot, false, slot);
        }

        // -- external glb with MULTIPLE scene roots: synthesize a neutral root --
        // Re-parenting extra roots under a TRANSFORMED root0 multiplies their transforms by
        // root0's (engine T = pos * parentAccumScale): e.g. a car whose Body root has scale
        // 20 would scale every other root's translation and subtree by another x20. A fresh
        // identity root (carrying only the global S, exactly like retail roots) keeps every
        // original root's world transform intact as its local one.
        if (im.RootNodes.Count > 1)
        {
            var synth = new ImportedNode {
                Index = im.Nodes.Count,
                Name = "dotr_root",
                Parent = -1,
            };
            foreach (int r in im.RootNodes)
            {
                if (GlbImporter.IsAuthoringMarker(im.Nodes[r].Name)) continue;
                synth.Children.Add(r);
                im.Nodes[r].Parent = synth.Index;
            }
            im.Nodes.Add(synth);
            im.RootNodes = new List<int> { synth.Index }; // idempotent on Build re-runs
            return (synth.Index, true, -1); // -1: ALL scene roots consumed
        }

        // -- external glb: collapse trivial wrapper chains --
        bool animated(int g)
        {
            foreach (var a in im.Animations)
                if (a.Channels.ContainsKey(g))
                    return true;
            return false;
        }

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

        bool collapsed = false;
        while (true)
        {
            var n = im.Nodes[root0];
            if (n.Children.Count != 1) break;
            if (n.IsJoint || animated(root0) || referenced(root0)) break;
            root0 = n.Children[0];
            collapsed = true;
        }
        var newRoot = im.Nodes[root0];
        newRoot.Parent = -1;
        if (collapsed)
        {
            // bake the dropped wrappers: the node's world matrix is unchanged, so its new
            // local transform IS its world transform (Sketchfab wrappers carry real
            // rotations/scales, e.g. -90degX*1000 then +90degX*0.01 in ob.glb)
            if (Matrix4x4.Decompose(newRoot.WorldMatrix, out var sc, out var rot, out var tr))
            {
                newRoot.Scale = sc;
                newRoot.Rotation = rot;
                newRoot.Translation = tr;
            }
            else
            {
                newRoot.Translation = newRoot.WorldMatrix.Translation;
            }
        }

        // explicit pivot: a direct child named 'origin' (or 'dotr_center') becomes the
        // first child so it is read as GNode(1)
        for (int i = 0; i < newRoot.Children.Count; i++)
        {
            if (!IsPivotName(im.Nodes[newRoot.Children[i]].Name))
                continue;
            int c = newRoot.Children[i];
            newRoot.Children.RemoveAt(i);
            newRoot.Children.Insert(0, c);
            return (root0, false, slot);
        }
        return (root0, true, slot);
    }

    /// A node the user designates as the model's origin / pivot (the engine's GNode(1)).
    /// 'origin' is the documented Blender-facing name; 'dotr_center' is kept as a legacy alias.
    private static bool IsPivotName(string? n) =>
        n != null && (n.Equals("origin", StringComparison.OrdinalIgnoreCase)
                      || n.Equals("dotr_center", StringComparison.OrdinalIgnoreCase));

    /// Move a pivot-named leaf child of <paramref name="root"/> into first-child position so
    /// the DFS reads it as GNode(1). Used for extractor ('MrgRoot') glbs, where the retail
    /// tree is otherwise kept verbatim. Returns true if a pivot child was hoisted.
    private static bool HoistPivotChild(ImportedModel im, int root)
    {
        var ch = im.Nodes[root].Children;
        for (int i = 0; i < ch.Count; i++)
            if (IsPivotName(im.Nodes[ch[i]].Name) && im.Nodes[ch[i]].Children.Count == 0)
            {
                if (i != 0)
                {
                    int p = ch[i];
                    ch.RemoveAt(i);
                    ch.Insert(0, p);
                }
                return true;
            }
        return false;
    }

    /// Inserts a leaf pivot node at enc index 1 (the root's FIRST child) so GNode(1)
    /// lands on it. The engine computes pivot = node.pos / animTransScale = node.pos * S,
    /// so storing worldCenter / S makes the pivot exactly the geometry's world-space
    /// bounding-box center (vertex Pos in ImportedModel is world-space bind position,
    /// which equals the final rendered frame for the reconstruction this pipeline emits).
    /// The node has no mesh slot and nothing references it, so its drawn placement is
    /// irrelevant; only GetGNode(1)/GetCenterOfs read it. All existing enc indices >= 1
    /// shift by +1 (Parent/Children lists and encIndexOf, which step 8 uses for motions).
    private static void InsertCenterPivot(ImportedModel im, List<EncNode> nodes,
        Dictionary<int, int> encIndexOf, float S, Vector3? donorPivotWorld = null)
    {
        if (nodes.Count == 0 || S <= 0f) return;

        bool any = false;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var prim in im.Primitives)
        foreach (var v in prim.Vertices)
        {
            min = Vector3.Min(min, v.Pos);
            max = Vector3.Max(max, v.Pos);
            any = true;
        }
        if (!any) return;
        var center = donorPivotWorld ?? (min + max) * 0.5f;

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

        var pivot = new EncNode {
            Scale = (1f, 1f, 1f),
            Flags = 0,
            MeshSlot = 0,
            RotBams = (0, 0, 0),
            Pos = (center.X / S, center.Y / S, center.Z / S),
            Parent = 0,
        };
        nodes.Insert(1, pivot);
        nodes[0].Children.Insert(0, 1);
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
                if (vu + vnumOf(r) > StripVuBudget) break;
                if (joints != null)
                {
                    int grow = 0;
                    foreach (int j in jointsOf!(r))
                        if (!joints.Contains(j))
                            grow++;
                    if (joints.Count + grow > maxJoints) break;
                    joints.UnionWith(jointsOf(r));
                }
                strip.Add(r);
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