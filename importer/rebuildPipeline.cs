using System.Numerics;

namespace DotrAssetModder;

/* Turns a parsed glb (ImportedModel) into the inputs the byte writers consume:
     List<EncNode> -> NodeTreeWriter (skeleton)
     List<EncPage> -> GeometryWriter (Section1/Section2/RelocWalk)
     GlobalScale   -> RH0 +0x64 animTransScale companion / root node scale
   Validated on blue-eyes_white_dragon (encode->decode position round trip, max err 0.0005 over 6454 verts):
   - global scale S = max|inverse(jointWorld)*vertexWorld| / 32000 (fits s16)
   - the root node carries (S,S,S), all others scale 1, flags 0, so the game's RestWorldSkin
     reproduces Scale(S)*glbWorld, the matrix we encode against
   - quat -> XYZ BAMS Euler: ry=asin(-M13), rx=atan2(M23,M33), rz=atan2(M12,M11)
     (inverse of the extractor's qz*qy*qx; rebuilds RotX*RotY*RotZ)
   - pos_raw = glbLocalTranslation / parentAccum (root: /1, others: /S)
   - per influence: bind pos = inverse(RWS_bone)*vertexWorld, normal via the inverse 3x3
   - strips are split into pages of <=8 bones, each page's bones mapped to the VU palette
     {4,11,18,25,32,39,46,53}; INDEP header-W and SHARED bone = that VU slot */

public static class RebuildPipeline
{
    private static readonly int[] VuSlots = { 4, 11, 18, 25, 32, 39, 46, 53 };
    private const int MaxBonesPerPage = 8;

    private const float UvScale = 4096f;

    /* The texture-context record sets CLAMP_1 = 5 (GS WMS/WMT = CLAMP), but glTF/Blender author
       out-of-[0,1] UVs assuming REPEAT. Folding them with frac() before quantisation reproduces
       the tiled sample; clamping would crush e.g. v = -1.9 onto a texture edge (often black).
       Exact integers (1.0, -1.0) keep their high edge, not 0. */
    private static float WrapUv(float coordinate)
    {
        if (float.IsNaN(coordinate) || float.IsInfinity(coordinate)) return 0f;
        float fraction = coordinate - MathF.Floor(coordinate);
        if (fraction == 0f && coordinate != 0f) fraction = 1f; // 1.0/-1.0/2.0 -> 1.0 (tile's far edge)
        return fraction;
    }

    private const float NormScale = 32767f;
    private const float WeightScale = 4096f;
    private const float S16Fit = 32000f; // target ceiling for |position| component
    private const float S16Max = 32767.5f; // rounds into s16
    private const double BamsPerRad = 32768.0 / Math.PI;



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
        /// from it. For animated model types SzModel_GetCenter returns handle+0x80, which
        /// SzModel_DrawWithFunc_Pipe overwrites every draw from GetAnyCoordinate(1, ...)
        /// (GameNode(1)'s live world position), never from this record. So Centre is
        /// GameNode(1)'s position (the point SzModel_SetPos/GetCenterOfs use for placement),
        /// matching the live camera target. Radius = bounding-sphere radius about that centre.
        public System.Numerics.Vector3 BoundsCenter;

        public float BoundsRadius = 1f;

        public List<(int BaseSlot, List<int> MemberSlots)> TexMerges = new();

        /// Fixed-point values Q had to clamp (positions, normals, UVs, weights). Nonzero means
        /// the geometry is distorted in game.
        public int ClampedValues;

        /// Animations left out because no motion slot was free (AssignSlotIds).
        public List<string> DroppedAnimations = new();
    }

    /// templateAnimTransScale: header+0x64 of the template model (1/rootScale). When given
    /// and the geometry fits s16 under it, the rebuilt model adopts the template's scale so
    /// that externally loaded motion files (lSzModel_LoadMotion/.nab, applied to whatever
    /// model occupies the slot with no node-count or unit validation,
    /// lSzModel_LoadMotionCB@0x1f0830) decode their translation keys at the retail scale.
    /// A scale of our own would make every animated translation wrong by retailS/ourS.
    public static Result Build(ImportedModel model, float templateAnimTransScale, int templateTexCount,
        float templateRootScale, float boundsRadiusOverride, bool boundsOnly)
    {
        if (model.RootNodes.Count == 0) throw new InvalidOperationException("glb has no root node");
        clampedValues = 0;
        var droppedAnimations = new List<string>();

        /* ---- 0a. UV-atlas merge of excess texture slots ----
           The template's TexTable has a fixed number of RH2 payload slots. Auto-assigned slots
           >= templateTexCount are folded into base slot (slot % count) as vertical bands, with
           the pages' V coordinates remapped into their band. Explicit mat_texN names >= count
           are not merged: they request a grown table (Rh0Assembler's grow/shed path). If the
           1 MiB block cannot hold the grown table, Rh0Assembler sheds the slot and Rebuilder
           reports it (Rh0Assembler.LastShedTexSlots). GlbImporter's MaxTexSlot (63) only
           catches typos. */
        ApplyTexAnimUv(model);
        var texMerges = ApplyTexMerges(model, templateTexCount);

        /* ---- 0. root selection / wrapper collapse (GameNode(1) pivot contract) ----
           The engine anchors monster (model type 4/5/6) scale+rotation and the duel 3D viewer's
           world placement on the node at depth-first index 1, the root's first child:
           - SzModel_DrawWithFunc_Pipe@0x1ee6c0: pivot = GetGNode(1).pos / animTransScale,
             applied as Trans(-pivot) * ScaleRotTrans(scale, rot, +pivot)
           - SzModel_GetCenterOfs = the same value; SzDuel_Draw3DView_Pipe does
             SzModel_SetPos(0x1d, -centerOfs) to place the monster
           - lSzModel_TraceGNode: counter starts 0 at the root, child-before-sibling
           A wrapper node above the real root (the extractor's 'MrgRoot', Sketchfab's
           'Sketchfab_model'/'RootNode' chains, Blender empties) shifts every DFS index by one,
           so GameNode(1) becomes a node at (0,0,0) and the model pivots on the world origin.
           PrepareRoot collapses those wrappers; for external models it also requests a
           synthetic pivot (inserted after step 4). */
        var (root0, synthPivot, consumedRootSlot) = PrepareRoot(model);

        /* Frame-generation positions (bounds/pivot/radius diagnostics) are each vertex's
           encoder position: skinned vertices stay in skin/bind space, unskinned vertices are
           already world-baked. */
        InitFramePositions(model);

        // ---- 1. DFS preorder; node[0] = root, extra roots re-parented under it ----
        var order = new List<int>(); // glb node indices, preorder
        var encIndexOf = new Dictionary<int, int>(); // glb index -> position in `order`

        bool IsImportHelperNode(int glbIndex)
        {
            int nodeIndex = glbIndex;
            if (nodeIndex < 0) return false;
            var node = model.Nodes[nodeIndex];
            // Loose root-level empty helper nodes are skipped (no name matching); they never become GameNodes.
            if (node.Parent < 0 && !node.HasMesh && !node.IsJoint && node.Children.Count == 0) return true;
            return IsPureMeshInstancingLeaf(model, glbIndex);
        }

        void VisitDepthFirst(int glbIndex)
        {
            if (IsImportHelperNode(glbIndex)) return;
            encIndexOf[glbIndex] = order.Count;
            order.Add(glbIndex);
            foreach (int childIndex in model.Nodes[glbIndex].Children)
                if (!IsImportHelperNode(childIndex))
                    VisitDepthFirst(childIndex);
        }

        VisitDepthFirst(root0);
        // every scene root except the one PrepareRoot consumed
        var extraRoots = new List<int>();
        for (int rootSlot = 0; rootSlot < model.RootNodes.Count; rootSlot++)
        {
            if (consumedRootSlot < 0 || rootSlot == consumedRootSlot) continue; // -1: all consumed
            if (IsImportHelperNode(model.RootNodes[rootSlot])) continue;
            extraRoots.Add(model.RootNodes[rootSlot]);
            VisitDepthFirst(model.RootNodes[rootSlot]);
        }

        // ---- 1.5 effective joint set ----
        // Skin joints first, then every node referenced rigidly by unskinned primitives (GlbImporter rigs each mesh node as a one-bone joint).
        var effectiveJoints = new List<int>(model.JointNodeIndices);
        var seenJoints = new HashSet<int>(effectiveJoints);
        foreach (var primitive in model.Primitives)
        foreach (var vertex in primitive.Vertices)
        foreach (var influence in vertex.Influences)
            if (seenJoints.Add(influence.JointNode))
                effectiveJoints.Add(influence.JointNode);

        // ---- 2. global scale S ----
        /* S must be big enough that inverse(RWS)*world = (1/S)*inverse(world)*world fits s16.
           A `_bone` joint's vertices are encoded against its own bind matrix (step 5), so S is
           sized from the other joints alone. Any other joint bound away from rest (an unskinned
           part at scale 0 at rest) uses its bind matrix throughout. */
        bool IsBoneOwner(int glbIndex) => model.Nodes[glbIndex].ScaleBone >= 0;
        var jointWorld = new Dictionary<int, Matrix4x4>();
        foreach (int joint in effectiveJoints)
            jointWorld[joint] = !IsBoneOwner(joint) && model.JointBind.TryGetValue(joint, out var bindWorld)
                ? bindWorld
                : model.Nodes[joint].WorldMatrix;
        double maxLocal = 1e-6;
        bool anyPlainJoint = false;
        foreach (var primitive in model.Primitives)
        foreach (var vertex in primitive.Vertices)
        foreach (var influence in Influences(vertex, model))
        {
            if (IsBoneOwner(influence.JointNode)) continue;
            anyPlainJoint = true;
            if (!jointWorld.TryGetValue(influence.JointNode, out var jointMatrix)) continue;
            if (!Matrix4x4.Invert(jointMatrix, out var inverseJoint)) continue; // singular joint: skip
            var localPosition = Vector3.Transform(vertex.Position, inverseJoint);
            if (float.IsNaN(localPosition.X) || float.IsInfinity(localPosition.X)) continue;
            maxLocal = Math.Max(maxLocal, Math.Max(Math.Abs(localPosition.X), Math.Max(Math.Abs(localPosition.Y), Math.Abs(localPosition.Z))));
        }

        float keepTemplateAnimTransScale = 0f; // >0: write the template's own animTransScale, not 1/S
        float globalScale = anyPlainJoint ? (float)(maxLocal / S16Fit) : 1f;
        if (globalScale <= 0f || float.IsNaN(globalScale) || float.IsInfinity(globalScale)) globalScale = 1f;

        /* `_bone` joints: their bind matrix already carries the accumulated scale, so the
           bone-local positions it gives are the retail s16 values; scale down (sigma > 1) only
           if an edit pushed them out of s16 range. Their RH0 accumulated scale is sigma * the
           bone's glb scale (SolvePose). A vertex with game influences (ImportedVertex.Game)
           writes its stored s16 copies at sigma 1, so it is left out unless sigma > 1 anyway
           (then the copies are dropped and every vertex is encoded from its Position). */
        double MaxBoneLocal(bool skipGame)
        {
            double largest = 0;
            foreach (var primitive in model.Primitives)
            foreach (var vertex in primitive.Vertices)
            {
                if (skipGame && vertex.Game != null) continue;
                foreach (var influence in Influences(vertex, model))
                {
                    if (!IsBoneOwner(influence.JointNode) || !model.JointBind.TryGetValue(influence.JointNode, out var bindWorld)) continue;
                    if (!Matrix4x4.Invert(bindWorld, out var inverseBind)) continue;
                    var localPosition = Vector3.Transform(vertex.Position, inverseBind);
                    if (float.IsNaN(localPosition.X) || float.IsInfinity(localPosition.X)) continue;
                    largest = Math.Max(largest, Math.Max(Math.Abs(localPosition.X), Math.Max(Math.Abs(localPosition.Y), Math.Abs(localPosition.Z))));
                }
            }
            return largest;
        }
        double maxBoneLocal = MaxBoneLocal(true);
        if (maxBoneLocal > S16Max) maxBoneLocal = MaxBoneLocal(false);
        float boneSigma = maxBoneLocal > S16Max ? (float)(maxBoneLocal / S16Fit) : 1f;

        /* Scale policy: extracted DotR GLBs may keep the template scale (round trip, retail motions).
           Custom GLBs derive their own rootScale/animTransScale, so the result does not depend on
           which monster slot is the binary template. */
        if (model.IsDotrExtractedGlb && templateAnimTransScale > 0f && !float.IsNaN(templateAnimTransScale) &&
            !float.IsInfinity(templateAnimTransScale))
        {
            /* The template's stored root scale is the authority; 1/animTransScale is only the
               monster-MRG shorthand. MODEL.MRG's STAGE7/00 and /03 store a root scale ten times
               1/ats, so the reciprocal would rebuild them at a tenth of retail size. */
            float templateScale = templateRootScale > 1e-9f ? templateRootScale : 1f / templateAnimTransScale;
            // adopt the template scale only when the geometry still fits s16
            if (!anyPlainJoint || maxLocal / templateScale <= 32700.0)
            {
                globalScale = templateScale;
                /* When the template breaks `ats == 1/rootScale`, keep its ats in the rebuilt header:
                   scale and motion-translation unit are independent there, and 1/S would divide
                   every external motion's translations by 10. Monsters hold the contract and keep
                   the derived value. */
                if (Math.Abs(templateScale * templateAnimTransScale - 1f) > 1e-3f)
                    keepTemplateAnimTransScale = templateAnimTransScale;
            }
        }

        // ---- 3. mesh-slot assignment ----
        /* The engine stores each joint's per-frame matrix at Section1 + meshSlot*0x10 and each
           entry is 0x70 bytes (lRhModel_SetupNoMotionRecursive@0x210010), so slots must be on
           the 1, 8, 15, ... (1+7k) grid. */
        var meshSlotOf = new Dictionary<int, int>();
        for (int i = 0; i < effectiveJoints.Count; i++) meshSlotOf[effectiveJoints[i]] = 1 + 7 * i;

        // ---- 4. author EncNodes (DFS order; reproduces glb world via the game's read math) ----
        var nodes = new List<EncNode>(order.Count);
        foreach (int glbIndex in order) nodes.Add(new EncNode());
        for (int i = 0; i < order.Count; i++)
        {
            int glbIndex = order[i];
            var sourceNode = model.Nodes[glbIndex];
            var encNode = nodes[i];
            encNode.Flags = 0;
            encNode.MeshSlot = (ushort)(meshSlotOf.TryGetValue(glbIndex, out int meshSlot) ? meshSlot : 0);

            int glbParent = sourceNode.Parent;
            if (glbIndex == root0) encNode.Parent = -1;
            else if (glbParent < 0) encNode.Parent = 0; // extra root -> under node[0]
            else if (encIndexOf.TryGetValue(glbParent, out int encodedParent)) encNode.Parent = encodedParent;
            else encNode.Parent = 0; // parent was an import-helper node; adopt under root

            foreach (int childGlbIndex in sourceNode.Children)
                if (encIndexOf.TryGetValue(childGlbIndex, out int childEncIndex))
                    encNode.Children.Add(childEncIndex);
        }
        foreach (int extraRoot in extraRoots)
            if (encIndexOf.TryGetValue(extraRoot, out int extraRootEncIndex))
                nodes[0].Children.Add(extraRootEncIndex);

        /* Rest scale/position/rotation are solved from the glb rest pose with each parent's
           accumulated scale (SolvePose). A child of a node at scale 0 has no defined rest
           position; it takes the one from the first frame that has one. */
        {
            var restPose = SolvePose(nodes, order.ToArray(), model, null, 0, globalScale, boneSigma);
            for (int i = 0; i < nodes.Count; i++)
            {
                var encNode = nodes[i];
                encNode.Scale = (restPose.Scale[i].X, restPose.Scale[i].Y, restPose.Scale[i].Z);
                encNode.RotationBams = QuatToBams(restPose.Rotation[i]);
                var position = restPose.Position[i];
                if (!restPose.PositionValid[i])
                    position = FirstValidPos(nodes, order.ToArray(), model, i, globalScale, boneSigma) ?? Vector3.Zero;
                encNode.Position = (position.X, position.Y, position.Z);
            }
        }

        /* The extractor names each node `node_N..._fXXXX` with its retail flags. Bits 1, 2 and 4
           (no translation, rotation, own scale) go back only where the skipped value is already
           the identity (a motion track clears its bit in the game anyway); the other bits (16 on
           some retail nodes, use unknown) are carried unchanged. */
        for (int i = 0; i < nodes.Count; i++)
        {
            var flagsMatch = System.Text.RegularExpressions.Regex.Match(model.Nodes[order[i]].Name, @"_f([0-9A-F]{4})(?:_|$)");
            if (!flagsMatch.Success) continue;
            int flags = Convert.ToInt32(flagsMatch.Groups[1].Value, 16);
            var encNode = nodes[i];
            if (encNode.Position != (0f, 0f, 0f)) flags &= ~1;
            if (encNode.RotationBams != (0, 0, 0)) flags &= ~2;
            if (encNode.Scale != (1f, 1f, 1f)) flags &= ~4;
            encNode.Flags = (ushort)flags;
        }

        bool hasBounds = ComputeBounds(model, out var geometryMin, out var geometryMax);

        /* ---- 4.5 GameNode(1) pivot for external models ----
           Retail structure (verified on blue-eyes_white_dragon.mrg's node tree and all 5 of its
           MOT slots): node 0 is a neutral root (zero rotation and translation, only the global
           scale folded in) that is never animated (every slot's node-0 track has
           transCount=rotCount=0). Node 1, the root's first child, carries real keyframes in
           every slot (e.g. 147 translation + 49 rotation keys over 391 frames on the longest)
           and anchors both placement (SzModel_SetPos via the static rest-pose read
           GetCenterOfs) and the per-frame camera aim (SzModel_GetCenter re-derives node 1's
           live world transform each frame via GetAnyCoordinate(1)). That animated node 1 is how
           the camera follows the model's centre of mass through a motion. Below node 1, retail
           has one or more neutral non-bound (meshSlot=0) container nodes (node 2, 3, ...)
           before the first real bone bind (node 4 on this sample).

           Custom GLBs: PrepareRoot's structural wrapper peel recognizes a pivot wrapper purely
           by position (a non-content single-child node that is the second peeled wrapper above
           the mesh/armature/animation content becomes node_1, whatever its name). Its
           translation/rotation keys flow through the animation-channel loop in step 8 and
           become node 1's MOT track.

           Extractor glbs keep the retail tree verbatim, so retail node 1 is preserved and
           externally loaded retail motions (.nab, applied by node index with no validation,
           lSzModel_LoadMotionCB@0x1f0830) stay aligned.

           External models with no separate pivot wrapper (root0 already branches or carries
           content, e.g. an Armature with several top-level bone chains) fall back to
           InsertGeneratedPivot: a static node 1 at the geometry's centre. It fixes the
           placement/camera-aim mismatch of a pivot at world (0,0,0), but cannot give a moving
           camera target; that needs a real, animated pivot wrapper authored in the model. */
        if (synthPivot)
            InsertGeneratedPivot(nodes, encIndexOf, globalScale, hasBounds ? geometryMin : null, hasBounds ? geometryMax : null);

        // ---- 4.6/4.7 camera target ----
        /* SzModel_GetCenter@0x1f2050 returns handle+0x80, which SzModel_DrawWithFunc_Pipe@0x1ee6c0
           sets every draw of an animated monster from GetAnyCoordinate(1): GameNode(1)'s live
           position. The bounding sphere (node tree +0x30) is not the camera target; it is
           measured in step 6.5 from the encoded geometry. */
        Vector3 cameraTarget = nodes.Count > 1
            ? new Vector3(nodes[1].Position.X * globalScale, nodes[1].Position.Y * globalScale, nodes[1].Position.Z * globalScale)
            : Vector3.Zero;

        // ---- 5. per-joint RestWorldSkin = Scale(S) * glbWorld ; cache inverse + 3x3 inverse ----
        // A `_bone` joint encodes against its bind matrix scaled by sigma: bone-local position = v * inverse(bind) / sigma (the retail values).
        var inverseRestWorldSkin = new Dictionary<int, Matrix4x4>();
        var globalScaleMatrix = Matrix4x4.CreateScale(globalScale);
        foreach (var entry in jointWorld)
        {
            var restWorldSkin = IsBoneOwner(entry.Key) && model.JointBind.TryGetValue(entry.Key, out var bindWorld)
                ? Matrix4x4.CreateScale(boneSigma) * bindWorld
                : globalScaleMatrix * entry.Value; // row-vector: apply scale then world
            Matrix4x4.Invert(restWorldSkin, out var inverse);
            inverseRestWorldSkin[entry.Key] = inverse;
        }

        // ---- 6. stripify triangles per material (long tri-strips, like retail) ----
        /* One strip per triangle blows Section2 up several-fold (each tri repeats 3 full verts + a
           24B header; SHARED verts repeat their whole influence blob) and overflows the 1 MiB
           block. Greedy edge-walk stripification gets ~1.1 verts/tri. Winding inside a strip is
           free: the GS never back-face culls and the extractor re-orients triangles by vertex
           normals on read. */
        var materialStrips = new List<(MatKey Key, EncStrip Strip)>();
        foreach (var primitive in model.Primitives)
        {
            var materialKey = new MatKey(primitive.TextureIndex, primitive.IsAlt, primitive.BaseColor, primitive.RawColor1, primitive.Color0, primitive.Color2, primitive.Untextured, primitive.GsAlpha, primitive.GsTest);
            foreach (var strip in StripifyPrimitive(primitive, model, inverseRestWorldSkin, meshSlotOf, boneSigma == 1f))
                materialStrips.Add((materialKey, strip));
        }

        // ---- 6.5 per-node bounding spheres (node +0x30) ----
        /* Retail: a slot node's sphere is centred on the bbox of its bone-local positions, with the
           radius in world units; a node without a slot has none. Node 0's is the model's (written
           by NodeTreeWriter); retail's rule on all 683 MONSTER.MRG cards: take each slot's bbox of
           bone-local positions, its 8 corners through the slot's rest world matrix with the scale
           kept in the matrix stack (RestWorldStacked); centre = centre of the box around every
           corner, radius = half that box's diagonal. A slot whose positions are all one point
           (retail sphere radius 0) is left out. RhModel_Draw@0x20f4e0 culls the model by this
           sphere (RhView_CheckViewVolume) and lSzModel_LoadCB@0x1e8bf0 takes the radius for the
           board shrink, clamp(40 / r, 0.3, 1). */
        Vector3 boundsCenter = Vector3.Zero;
        float boundsRadius = 1f;
        {
            var restWorld = RestWorldStacked(nodes);
            var boxMin = new Vector3(float.MaxValue);
            var boxMax = new Vector3(float.MinValue);
            var slotPoints = new Dictionary<int, List<Vector3>>();
            void AddPoint(int slot, short x, short y, short z)
            {
                if (!slotPoints.TryGetValue(slot, out var points)) slotPoints[slot] = points = new List<Vector3>();
                points.Add(new Vector3(x, y, z));
            }
            foreach (var (_, strip) in materialStrips)
            foreach (var vertex in strip.Vertices)
            {
                if (strip.Kind == StripKind.Indep) AddPoint(strip.IndependentVuAddress, vertex.X, vertex.Y, vertex.Z);
                else if (vertex.Influences != null)
                    foreach (var influence in vertex.Influences) AddPoint(influence.Bone, influence.X, influence.Y, influence.Z);
            }
            for (int i = 0; i < nodes.Count; i++)
            {
                var encNode = nodes[i];
                if (encNode.MeshSlot == 0 || !slotPoints.TryGetValue(encNode.MeshSlot, out var points)) continue;
                var slotMin = points.Aggregate(Vector3.Min);
                var slotMax = points.Aggregate(Vector3.Max);
                var centre = (slotMin + slotMax) * 0.5f;
                float radius = points.Max(point => Vector3.Distance(point, centre));
                float accumulatedScale = 1f;
                for (int k = i; k >= 0; k = nodes[k].Parent)
                    accumulatedScale *= Math.Max(Math.Abs(nodes[k].Scale.X), Math.Max(Math.Abs(nodes[k].Scale.Y), Math.Abs(nodes[k].Scale.Z)));
                encNode.Sphere = (centre.X, centre.Y, centre.Z, radius * accumulatedScale);
                if (slotMin == slotMax)
                {
                    continue;
                }
                for (int k = 0; k < 8; k++)
                {
                    var corner = new Vector3((k & 1) == 0 ? slotMin.X : slotMax.X, (k & 2) == 0 ? slotMin.Y : slotMax.Y, (k & 4) == 0 ? slotMin.Z : slotMax.Z);
                    var worldCorner = Vector3.Transform(corner, restWorld[i]);
                    boxMin = Vector3.Min(boxMin, worldCorner);
                    boxMax = Vector3.Max(boxMax, worldCorner);
                }
            }
            if (boxMin.X <= boxMax.X)
            {
                boundsCenter = (boxMin + boxMax) * 0.5f;
                boundsRadius = Vector3.Distance(boxMin, boxMax) * 0.5f;
            }
            // the engine divides by the radius (40 / r): keep it positive
            if (!(boundsRadius > 1e-3f) || float.IsInfinity(boundsRadius))
            {
                boundsRadius = 1f;
            }
        }
        // Rebuilder.MeasureRadius only needs the sphere; skip the pages and motion fitting (most of the time).
        if (boundsOnly)
        {
            return new Result { BoundsCenter = boundsCenter, BoundsRadius = boundsRadius };
        }

        // ---- 7. group by material, partition each group into <=8-bone pages, assign VU slots ----
        var pages = new List<EncPage>();
        foreach (var materialGroup in materialStrips.GroupBy(entry => entry.Key))
        {
            var strips = materialGroup.Select(entry => entry.Strip).ToList();
            foreach (var member in Partition(strips))
            {
                var bones = new SortedSet<int>();
                foreach (var strip in member)
                foreach (int bone in StripBones(strip))
                    bones.Add(bone);

                var slotToVu = new Dictionary<int, int>();
                int k = 0;
                foreach (int meshSlot in bones) slotToVu[meshSlot] = VuSlots[k++];

                var page = new EncPage {
                    IsAlt = materialGroup.Key.IsAlt,
                    GsAlpha = materialGroup.Key.GsAlpha,
                    GsTest = materialGroup.Key.GsTest,
                    TextureIndex = materialGroup.Key.TextureIndex,
                    Color = ((float)materialGroup.Key.Red, (float)materialGroup.Key.Green, (float)materialGroup.Key.Blue),
                    Alpha = materialGroup.Key.Alpha,
                    Color0 = materialGroup.Key.Color0,
                    Color2 = materialGroup.Key.Color2,
                    Untextured = materialGroup.Key.Untextured,
                };
                foreach (var meshSlot in bones)
                    page.BoneBinds.Add(new EncBoneBind { VuAddr = slotToVu[meshSlot], Slot = meshSlot, VifEcho = 0 });

                foreach (var strip in member)
                {
                    if (strip.Kind == StripKind.Indep) strip.IndependentVuAddress = slotToVu[strip.IndependentVuAddress]; // placeholder ms -> VU slot
                    else RemapSharedBones(strip, slotToVu);
                    page.Strips.Add(strip);
                }
                pages.Add(page);
            }
        }

        // ---- 8. animations: per-frame RH0 poses -> fitted key lists ----
        /* Every channel arrives sampled at every frame (GlbImporter). Each frame is solved into
           RH0 node values (SolvePose), then each node's position, rotation and scale tracks are
           reduced to the fewest keys (FitKeys) that the game's interpolation reproduces.
           RH0 +0x64 animTransScale: the retail contract (byte-verified on blue-eyes and
           timeater) is animTransScale == 1 / rootScale. The duel draw path
           (SzModel_DrawWithFunc_Pipe@0x1ee6c0) anchors monster scale/rotation at
           GetGNode(1).pos / animTransScale (node-tree units back to world units), and the
           motion interpolator multiplies non-root position keys by it at runtime
           (lRhModel_SetupFloatMotionRecursive); root keys are used as they are. */
        float animTransScale = keepTemplateAnimTransScale > 0f ? keepTemplateAnimTransScale : 1f / globalScale;
        var encodedToGlb = new int[nodes.Count];
        Array.Fill(encodedToGlb, -1);
        foreach (var (glbNode, encIndex) in encIndexOf) encodedToGlb[encIndex] = glbNode;
        // Key-fit tolerance: 1e-5 of the farthest vertex from GameNode(1); changing it changes the motions.
        float fitRadius = ComputeRadius(model, cameraTarget);
        if (!(fitRadius > 1e-3f) || float.IsInfinity(fitRadius))
        {
            fitRadius = 1f;
        }
        float worldTolerance = fitRadius * 1e-5f;
        var motions = new List<EncMotion>();
        int[] slotOfAnimation = AssignSlotIds(model.Animations);
        int animIndex = 0;
        foreach (var anim in model.Animations)
        {
            int slotId = slotOfAnimation[animIndex++];
            if (slotId < 0)
            {
                droppedAnimations.Add(anim.Name);
                continue;
            }
            int frames = Math.Max(anim.FrameCount, 1);
            var poses = new Rh0Pose[frames];
            for (int frame = 0; frame < frames; frame++)
                poses[frame] = SolvePose(nodes, encodedToGlb, model, anim, frame, globalScale, boneSigma);
            var motion = new EncMotion { SlotId = slotId, FrameCount = frames };
            for (int i = 0; i < nodes.Count; i++)
            {
                if (encodedToGlb[i] < 0) continue; // synthetic node: never animated
                var encNode = nodes[i];
                var track = new EncMotTrack();
                int parentIndex = encNode.Parent;

                // position: raw value per frame; non-root keys are stored / animTransScale
                var positions = new Vector3[frames];
                var positionValid = new bool[frames];
                for (int frame = 0; frame < frames; frame++) { positions[frame] = poses[frame].Position[i]; positionValid[frame] = poses[frame].PositionValid[i]; }
                var restPosition = new Vector3(encNode.Position.X, encNode.Position.Y, encNode.Position.Z);
                var posKeys = FitKeys(positions, positionValid, restPosition, (frame, difference) =>
                {
                    // error in world units: raw * parent accumulated scale
                    var parentAccumulated = parentIndex < 0 ? Vector3.One : poses[frame].AccumulatedScale[parentIndex];
                    return (difference * parentAccumulated).Length() <= worldTolerance;
                });
                if (posKeys != null)
                {
                    float divisor = i == 0 ? 1f : animTransScale;
                    track.Trans = posKeys.Select(keyFrame => (keyFrame, positions[Math.Min(keyFrame, frames - 1)] / divisor)).ToList();
                }

                // rotation: BAMS, each frame's Euler triple continuous with the previous one
                var rotations = new Vector3[frames];
                var rotationValid = new bool[frames];
                (int X, int Y, int Z)? previous = encNode.RotationBams;
                for (int frame = 0; frame < frames; frame++)
                {
                    var bams = QuatToBamsContinuous(poses[frame].Rotation[i], previous);
                    previous = bams;
                    rotations[frame] = new Vector3(bams.X, bams.Y, bams.Z);
                    rotationValid[frame] = true;
                }
                var restRotation = new Vector3(encNode.RotationBams.X, encNode.RotationBams.Y, encNode.RotationBams.Z);
                // equivalent triples (wrapped by 0x10000, or the other Euler branch) are the same pose
                bool restRotEquivalent = rotations.All(rotation => SamePose(rotation, restRotation));
                var rotKeys = restRotEquivalent ? null : FitKeys(rotations, rotationValid, restRotation, (frame, difference) =>
                    Math.Abs(difference.X) <= RotTolBams && Math.Abs(difference.Y) <= RotTolBams && Math.Abs(difference.Z) <= RotTolBams,
                    maxStep: 0x7F00);
                if (rotKeys != null)
                    track.Rot = rotKeys.Select(keyFrame => rotations[Math.Min(keyFrame, frames - 1)])
                        .Zip(rotKeys, (rotation, keyFrame) => (keyFrame, (int)rotation.X, (int)rotation.Y, (int)rotation.Z)).ToList();

                // scale: RH0 local scale. A type-7 motion leaves the root's own scale alone.
                if (i != 0)
                {
                    var scales = new Vector3[frames];
                    var scaleValid = new bool[frames];
                    for (int frame = 0; frame < frames; frame++) { scales[frame] = poses[frame].Scale[i]; scaleValid[frame] = true; }
                    var restScale = new Vector3(encNode.Scale.X, encNode.Scale.Y, encNode.Scale.Z);
                    var scaleKeys = FitKeys(scales, scaleValid, restScale, (frame, difference) =>
                    {
                        var parentAccumulated = parentIndex < 0 ? Vector3.One : poses[frame].AccumulatedScale[parentIndex];
                        return (difference * parentAccumulated).Length() <= ScaleTol * Math.Max(parentAccumulated.Length(), 1e-12f);
                    });
                    if (scaleKeys != null) track.Scale = scaleKeys.Select(keyFrame => (keyFrame, scales[Math.Min(keyFrame, frames - 1)])).ToList();
                }

                if (track.Trans != null || track.Rot != null || track.Scale != null) motion.Tracks[i] = track;
            }
            motions.Add(motion);
        }

        return new Result {
            Nodes = nodes, Pages = pages, Motions = motions,
            GlobalScale = globalScale, AnimTransScale = animTransScale,
            BoundsCenter = boundsCenter, BoundsRadius = WrittenRadius(boundsRadius, boundsRadiusOverride),
            MaxMeshSlot = effectiveJoints.Count > 0 ? 1 + 7 * (effectiveJoints.Count - 1) : 0,
            TexMerges = texMerges,
            ClampedValues = clampedValues,
            DroppedAnimations = droppedAnimations,
        };
    }

    // ---- RH0 pose solving ----
    // 2 BAMS = 0.011 deg. 0.75 (the game's own integer rounding) fitted noise: ~9% more keys than retail (card 671's attack: 15,362 vs 14,087).
    private const float RotTolBams = 2f;
    private const float ScaleTol = 1e-4f;    // relative to the parent's accumulated scale

    /// One pose of the RH0 tree, per encoded node: raw position (pre parent scale), local
    /// rotation, local scale, and accumulated scale. PositionValid is false where the parent's
    /// accumulated scale is 0 on some axis, which makes the raw position meaningless
    /// (the game multiplies it by 0).
    private sealed class Rh0Pose
    {
        public Vector3[] Position, Scale, AccumulatedScale;
        public Quaternion[] Rotation;
        public bool[] PositionValid;

        public Rh0Pose(int nodeCount)
        {
            Position = new Vector3[nodeCount];
            Scale = new Vector3[nodeCount];
            AccumulatedScale = new Vector3[nodeCount];
            Rotation = new Quaternion[nodeCount];
            PositionValid = new bool[nodeCount];
        }
    }

    /// Solves RH0 node values for a glb pose (anim == null: rest). RH0 scale propagates to
    /// the children and multiplies their positions (lRhModel_SetupNoMotionRecursive @
    /// 0x210010: local = T(position * parent scale) * R), while a glb translation is scaled by
    /// its ancestors' glb scales G_parent (glTF world = parent TRS * child TRS), so:
    ///   accumulated A = S * s for the root, sigma * (glb world scale of the `_bone`) for a
    ///   node that has one, else A_parent * s;
    ///   local scale = A / A_parent; raw position = T * G_parent / A_parent (the root's: T).
    /// encGlb maps encoded node -> glb node (-1: synthetic, keeps its EncNode values).
    private static Rh0Pose SolvePose(List<EncNode> nodes, int[] encodedToGlb, ImportedModel model,
        ImportedAnimation? anim, int frame, float globalScale, float sigma)
    {
        int nodeCount = nodes.Count;
        var pose = new Rh0Pose(nodeCount);
        var glbScale = new Vector3[nodeCount]; // glb world scale (product of local scales)
        for (int i = 0; i < nodeCount; i++)
        {
            int parentIndex = nodes[i].Parent;
            var parentAccumulated = parentIndex < 0 ? Vector3.One : pose.AccumulatedScale[parentIndex];
            var parentGlbScale = parentIndex < 0 ? Vector3.One : glbScale[parentIndex];
            int glbIndex = encodedToGlb[i];
            if (glbIndex < 0)
            {
                var encNode = nodes[i];
                pose.Position[i] = new Vector3(encNode.Position.X, encNode.Position.Y, encNode.Position.Z);
                pose.PositionValid[i] = true;
                pose.Rotation[i] = Quaternion.Identity;
                pose.Scale[i] = new Vector3(encNode.Scale.X, encNode.Scale.Y, encNode.Scale.Z);
                pose.AccumulatedScale[i] = parentAccumulated * pose.Scale[i];
                glbScale[i] = parentGlbScale;
                continue;
            }
            var sourceNode = model.Nodes[glbIndex];
            var (translation, rotation, scale) = LocalAt(model, glbIndex, anim, frame);
            glbScale[i] = parentGlbScale * scale;
            Vector3 accumulated;
            if (parentIndex < 0) accumulated = globalScale * scale;
            else if (sourceNode.ScaleBone >= 0) accumulated = sigma * glbScale[i] * LocalAt(model, sourceNode.ScaleBone, anim, frame).Scale;
            else accumulated = parentAccumulated * scale;
            pose.AccumulatedScale[i] = accumulated;
            pose.Scale[i] = parentIndex < 0 ? accumulated : SafeDiv(accumulated, parentAccumulated, 1f);
            pose.Rotation[i] = rotation;
            if (parentIndex < 0)
            {
                pose.Position[i] = translation;
                pose.PositionValid[i] = true;
            }
            else
            {
                // A glb child's translation is in its parent's local space, so the ancestors' glb scale (parentGlbScale) shrinks it too. Extracted glbs keep scale on `_bone` helpers only: it is 1 there.
                pose.Position[i] = SafeDiv(translation * parentGlbScale, parentAccumulated, 0f);
                pose.PositionValid[i] = !Tiny(parentAccumulated.X) && !Tiny(parentAccumulated.Y) && !Tiny(parentAccumulated.Z);
            }
        }
        return pose;
    }

    private static (Vector3 Translation, Quaternion Rotation, Vector3 Scale) LocalAt(ImportedModel model, int glbIndex, ImportedAnimation? anim, int frame)
    {
        var node = model.Nodes[glbIndex];
        var translation = node.Translation;
        var rotation = node.Rotation;
        var scale = node.Scale;
        if (anim != null && anim.Channels.TryGetValue(glbIndex, out var channels))
        {
            if (channels.Translation is { Count: > 0 } translationKeys) translation = translationKeys[Math.Min(frame, translationKeys.Count - 1)].Value;
            if (channels.Rotation is { Count: > 0 } rotationKeys) rotation = rotationKeys[Math.Min(frame, rotationKeys.Count - 1)].Rotation;
            if (channels.Scale is { Count: > 0 } scaleKeys) scale = scaleKeys[Math.Min(frame, scaleKeys.Count - 1)].Value;
        }
        return (translation, rotation, scale);
    }

    private static Vector3? FirstValidPos(List<EncNode> nodes, int[] encodedToGlb, ImportedModel model, int nodeIndex, float globalScale, float sigma)
    {
        foreach (var anim in model.Animations)
            for (int frame = 0; frame < anim.FrameCount; frame++)
            {
                var pose = SolvePose(nodes, encodedToGlb, model, anim, frame, globalScale, sigma);
                if (pose.PositionValid[nodeIndex]) return pose.Position[nodeIndex];
            }
        return null;
    }

    private static bool Tiny(float value) => MathF.Abs(value) < 1e-12f;

    private static Vector3 SafeDiv(Vector3 numerator, Vector3 denominator, float fallback) => new(
        Tiny(denominator.X) ? fallback : numerator.X / denominator.X,
        Tiny(denominator.Y) ? fallback : numerator.Y / denominator.Y,
        Tiny(denominator.Z) ? fallback : numerator.Z / denominator.Z);

    private static bool SamePose(Vector3 bams, Vector3 rest)
    {
        var poseMatrix = Matrix4x4.CreateRotationX((float)(bams.X / BamsPerRad)) *
                Matrix4x4.CreateRotationY((float)(bams.Y / BamsPerRad)) *
                Matrix4x4.CreateRotationZ((float)(bams.Z / BamsPerRad));
        var restMatrix = Matrix4x4.CreateRotationX((float)(rest.X / BamsPerRad)) *
                Matrix4x4.CreateRotationY((float)(rest.Y / BamsPerRad)) *
                Matrix4x4.CreateRotationZ((float)(rest.Z / BamsPerRad));
        // 1.5 BAMS is ~1.4e-4 rad
        const float tolerance = 1.4e-4f;
        return MathF.Abs(poseMatrix.M11 - restMatrix.M11) < tolerance && MathF.Abs(poseMatrix.M12 - restMatrix.M12) < tolerance && MathF.Abs(poseMatrix.M13 - restMatrix.M13) < tolerance &&
               MathF.Abs(poseMatrix.M21 - restMatrix.M21) < tolerance && MathF.Abs(poseMatrix.M22 - restMatrix.M22) < tolerance && MathF.Abs(poseMatrix.M23 - restMatrix.M23) < tolerance &&
               MathF.Abs(poseMatrix.M31 - restMatrix.M31) < tolerance && MathF.Abs(poseMatrix.M32 - restMatrix.M32) < tolerance && MathF.Abs(poseMatrix.M33 - restMatrix.M33) < tolerance;
    }

    /// Fits a key list to per-frame values for lRhModel_GetInterpolateMotionItem @ 0x210CF0,
    /// which lerps each component between the keys around a frame. Returns the key frames,
    /// or null when every frame matches `rest` (no track: the node keeps its default).
    /// closeEnough(frame, error) says whether an interpolated value is close enough; frames with
    /// valid[f] false are free. The rules the game imposes: the first key is frame 0, frames
    /// strictly increase, and a list with fewer than 3 keys plays key 0 only - so a constant
    /// is one key, and a 2-key fit gets its last key repeated one frame later (identical
    /// motion up to the last frame, then held). maxStep caps the change between consecutive
    /// keys (angles: the game interpolates the short way round 0x10000).
    private static List<int>? FitKeys(Vector3[] values, bool[] valid, Vector3 rest, Func<int, Vector3, bool> closeEnough,
        float maxStep = float.PositiveInfinity)
    {
        int frameCount = values.Length;
        // a value where it does not matter: hold the nearest one that does
        int firstValid = Array.IndexOf(valid, true);
        if (firstValid < 0) return null;
        for (int frame = 0; frame < frameCount; frame++)
            if (!valid[frame]) values[frame] = frame < firstValid ? values[firstValid] : values[frame - 1];

        bool constant = true, atRest = true;
        for (int frame = 0; frame < frameCount; frame++)
        {
            if (!valid[frame]) continue;
            constant &= closeEnough(frame, values[frame] - values[0]);
            atRest &= closeEnough(frame, values[frame] - rest);
        }
        if (atRest) return null;
        if (constant) return new List<int> { 0 };

        var keys = new List<int> { 0 };
        int segmentStart = 0;
        while (segmentStart < frameCount - 1)
        {
            int segmentEnd = segmentStart + 1;
            // extend the segment a..b while every frame between is reproduced
            while (segmentEnd + 1 < frameCount && SegmentFits(values, valid, segmentStart, segmentEnd + 1, closeEnough, maxStep)) segmentEnd++;
            keys.Add(segmentEnd);
            segmentStart = segmentEnd;
        }
        if (keys.Count == 2) keys.Add(keys[1] + 1);
        return keys;
    }

    private static bool SegmentFits(Vector3[] values, bool[] valid, int start, int end, Func<int, Vector3, bool> closeEnough, float maxStep)
    {
        var change = values[end] - values[start];
        if (MathF.Abs(change.X) > maxStep || MathF.Abs(change.Y) > maxStep || MathF.Abs(change.Z) > maxStep) return false;
        for (int frame = start + 1; frame < end; frame++)
        {
            if (!valid[frame]) continue;
            float blend = (frame - start) / (float)(end - start);
            if (!closeEnough(frame, values[start] + change * blend - values[frame])) return false;
        }
        return true;
    }

    // ---- flipbook slots ----
    /// Squeezes the UVs of faces on a packed flipbook slot into cell 0 of its sprite sheet (the
    /// cell the game copies every frame onto; scale from GlbImporter.DecodeGlbTextures). Mutates
    /// model.Primitives once per ImportedModel (guarded by AppliedTexAnimUv). UVs are wrapped
    /// into [0,1] first (WrapUv).
    private static void ApplyTexAnimUv(ImportedModel model)
    {
        if (model.AppliedTexAnimUv || model.TextureAnimUvScale.Count == 0) return;
        foreach (var primitive in model.Primitives)
        {
            if (!model.TextureAnimUvScale.TryGetValue(primitive.TextureIndex, out var uvScale)) continue;
            foreach (var vertex in primitive.Vertices)
                vertex.Uv = new Vector2(WrapUv(vertex.Uv.X) * uvScale.X, WrapUv(vertex.Uv.Y) * uvScale.Y);
        }
        model.AppliedTexAnimUv = true;
    }

    // ---- UV-atlas slot merging ----
    /// Folds auto-assigned texture slots beyond the template's count into existing slots as
    /// vertical atlas bands. Mutates model.Primitives (TextureIndex + vertex UVs) once per
    /// ImportedModel (guarded by AppliedTexMerges). UVs are wrapped into [0,1] first (WrapUv).
    private static List<(int BaseSlot, List<int> MemberSlots)> ApplyTexMerges(
        ImportedModel model, int templateTexCount)
    {
        if (model.AppliedTexMerges != null) return model.AppliedTexMerges;
        var merges = new List<(int BaseSlot, List<int> MemberSlots)>();
        int templateSlotCount = templateTexCount;
        if (templateSlotCount > 0)
        {
            var autoSlots = new HashSet<int>(model.MaterialTexAuto.Values);
            var used = new SortedSet<int>();
            foreach (var primitive in model.Primitives)
                if (primitive.TextureIndex >= 0)
                    used.Add(primitive.TextureIndex);

            // A flipbook sheet or a slot with texture-animation records addresses its texels directly: it can neither become a band nor host bands.
            bool IsFixed(int slot) => model.TextureAnimUvScale.ContainsKey(slot) || model.TextureAnimRecords.ContainsKey(slot);
            var groups = new SortedDictionary<int, List<int>>();
            foreach (int autoSlot in used)
                if (autoSlot >= templateSlotCount && autoSlots.Contains(autoSlot) && !IsFixed(autoSlot))
                {
                    int baseSlot = autoSlot % templateSlotCount;
                    for (int k = 0; k < templateSlotCount && IsFixed(baseSlot); k++) baseSlot = (baseSlot + 1) % templateSlotCount;
                    if (IsFixed(baseSlot)) continue; // every base slot is animated: left for the assembler to shed
                    if (!groups.TryGetValue(baseSlot, out var bandMembers)) groups[baseSlot] = bandMembers = new List<int>();
                    bandMembers.Add(autoSlot);
                }

            foreach (var bandGroup in groups)
            {
                int baseSlot = bandGroup.Key;
                var members = new List<int>();
                if (used.Contains(baseSlot)) members.Add(baseSlot); // base keeps band 0 only if it has pages
                members.AddRange(bandGroup.Value);
                int memberCount = members.Count;
                if (memberCount == 0) continue;

                // V-band remap first (TextureIndex still tells the members apart), then retarget
                for (int k = 0; k < memberCount; k++)
                {
                    int slot = members[k];
                    if (memberCount == 1 && slot == baseSlot) break; // nothing to squeeze
                    foreach (var primitive in model.Primitives)
                    {
                        if (primitive.TextureIndex != slot) continue;
                        foreach (var vertex in primitive.Vertices)
                        {
                            // UVs were folded into [0,1] per triangle at import; WrapUv only guards stray values (e.g. exactly 1.0)
                            float wrappedU = WrapUv(vertex.Uv.X);
                            float wrappedV = WrapUv(vertex.Uv.Y);
                            vertex.Uv = new Vector2(wrappedU, (wrappedV + k) / memberCount);
                        }
                    }
                }
                foreach (int autoSlot in bandGroup.Value)
                foreach (var primitive in model.Primitives)
                    if (primitive.TextureIndex == autoSlot)
                        primitive.TextureIndex = baseSlot;

                merges.Add((baseSlot, members));
            }
        }
        model.AppliedTexMerges = merges;
        return merges;
    }

    private static bool ComputeBounds(ImportedModel model, out Vector3 min, out Vector3 max)
    {
        var positions = CollectValidFramePositions(model);
        if (positions.Count == 0)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
            return false;
        }

        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        foreach (var position in positions)
        {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }
        return true;
    }

    // Radius written to node 0: the import window's override, else the measured one. It only sets the board shrink, clamp(40 / r, 0.3, 1); fitting tolerances keep the measured one.
    private static float WrittenRadius(float measured, float user)
    {
        if (user > 1e-3f && !float.IsInfinity(user))
            return user;
        return measured;
    }

    /* Rest world matrices with each node's scale kept in the matrix stack (world = S * R * T *
       parent world), the frame retail measured node 0's sphere in. The game (ModelExtractor.EvalPose)
       carries scale beside the stack instead; the two differ only under non-uniform scale.
       Nodes are in DFS preorder, so a parent comes before its children. */
    private static Matrix4x4[] RestWorldStacked(List<EncNode> nodes)
    {
        var world = new Matrix4x4[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            var encNode = nodes[i];
            var local = Matrix4x4.Identity;
            if ((encNode.Flags & 3) != 3)
            {
                if ((encNode.Flags & 2) == 0)
                {
                    local = MatrixMath.RotX(encNode.RotationBams.X) * MatrixMath.RotY(encNode.RotationBams.Y) * MatrixMath.RotZ(encNode.RotationBams.Z);
                }
                if ((encNode.Flags & 1) == 0)
                {
                    local.Translation = new Vector3(encNode.Position.X, encNode.Position.Y, encNode.Position.Z);
                }
            }
            if ((encNode.Flags & 4) == 0)
            {
                local = MatrixMath.Scale(encNode.Scale.X, encNode.Scale.Y, encNode.Scale.Z) * local;
            }
            if (encNode.Parent < 0)
            {
                world[i] = local;
            }
            else
            {
                world[i] = local * world[encNode.Parent];
            }
        }
        return world;
    }

    private static float ComputeRadius(ImportedModel model, Vector3 center)
    {
        var positions = CollectValidFramePositions(model);
        if (positions.Count == 0) return 0f;

        float radius = 0f;
        foreach (var position in positions)
            radius = Math.Max(radius, Vector3.Distance(position, center));
        return radius;
    }

    private static List<Vector3> CollectValidFramePositions(ImportedModel model)
    {
        var positions = new List<Vector3>();
        foreach (var primitive in model.Primitives)
        foreach (var vertex in primitive.Vertices)
        {
            var position = vertex.FramePos;
            if (float.IsNaN(position.X) || float.IsInfinity(position.X) ||
                float.IsNaN(position.Y) || float.IsInfinity(position.Y) ||
                float.IsNaN(position.Z) || float.IsInfinity(position.Z)) continue;
            positions.Add(position);
        }
        return positions;
    }

    private static void InitFramePositions(ImportedModel model)
    {
        var undo = model.DroppedRootUndo ?? Matrix4x4.Identity;
        foreach (var primitive in model.Primitives)
        foreach (var vertex in primitive.Vertices)
            vertex.FramePos = Vector3.Transform(vertex.Position, undo);
    }

    // ---- root preparation / pivot guarantee ----

    /// Picks the encoding root, identifies node_0 (model root) and node_1 (pivot/camera
    /// target) by structure rather than by name, and strips/synthesizes wrapper nodes so
    /// every model ends up with the canonical node_0 -> node_1 -> (armature/mesh/offset
    /// content) shape.
    /// Returns (rootGlbIndex, synthesizePivot, consumedRootSlot):
    ///   * A "wrapper" is any node with no mesh, no skin-joint role and exactly one child.
    ///     Up to two are peeled off the top of the tree: the first becomes node_0, the second
    ///     node_1. No name ever needs to match.
    ///   * Exactly one wrapper found (a pivot empty with no outer root, or a single extractor
    ///     wrapper directly above retail's node 1): it becomes node_1 and is re-wrapped under a
    ///     synthesized neutral node_0.
    ///   * No wrapper found (root0 carries content or branches, e.g. an Armature with several
    ///     top-level bone chains): no node_1 exists, so the caller synthesizes a static one
    ///     (InsertGeneratedPivot).
    private static (int Root, bool SynthPivot, int ConsumedRootSlot) PrepareRoot(ImportedModel model)
    {
        static bool LooseEmptyRoot(ImportedModel modelToSearch, int glbIndex)
        {
            var node = modelToSearch.Nodes[glbIndex];
            return node.Parent < 0 && !node.HasMesh && !node.IsJoint && node.Children.Count == 0;
        }

        /* Extractor output keeps the retail tree verbatim under MrgRoot, which carries only the
           -pivot recentring (ModelExtractor.WriteGlb). Drop just MrgRoot: retail's neutral nodes
           0..3 above the first bone look like wrappers to the peel below, and peeling them would
           shift every DFS index (effect reference points, externally loaded motions). The shift
           is in every joint world and vertex alike, so it cancels in the bone-local positions;
           node 0 keeps its own local transform. InitFramePositions undoes the shift for bounds. */
        if (model.IsDotrExtractedGlb)
        {
            int mrgSlot = model.RootNodes.FindIndex(rootIndex => model.Nodes[rootIndex].Name == "MrgRoot");
            if (mrgSlot >= 0)
            {
                int mrgRootIndex = model.RootNodes[mrgSlot];
                var realChildren = model.Nodes[mrgRootIndex].Children.Where(child => !IsPureMeshInstancingLeaf(model, child)).ToList();
                if (realChildren.Count == 1)
                {
                    Matrix4x4.Invert(model.Nodes[mrgRootIndex].WorldMatrix, out var undo);
                    model.DroppedRootUndo = undo;
                    model.Nodes[realChildren[0]].Parent = -1;
                    return (realChildren[0], false, mrgSlot);
                }
            }
        }

        // prefer the first scene root that has children (the extractor's skinned-mesh node is a childless sibling root)
        int slot = 0;
        for (int rootSlot = 0; rootSlot < model.RootNodes.Count; rootSlot++)
            if (!LooseEmptyRoot(model, model.RootNodes[rootSlot]) && model.Nodes[model.RootNodes[rootSlot]].Children.Count > 0)
            {
                slot = rootSlot;
                break;
            }
        int root0 = model.RootNodes[slot];

        /* Real scene roots only: pure-mesh-instancing leaves (the extractor's MrgMesh sibling
           root) do not count, so an extracted glb with scene roots [MrgRoot, MrgMesh] has one
           real root and takes the wrapper-peel path below. */
        var realRoots = model.RootNodes.Where(rootIndex => !IsPureMeshInstancingLeaf(model, rootIndex)).ToList();

        /* Several real scene roots: synthesize a neutral root. Re-parenting extra roots under a
           transformed root0 multiplies their transforms by root0's (engine T = pos *
           parentAccumScale): a Body root with scale 20 would scale every other root's translation
           and subtree by x20 again. A fresh identity root (carrying only the global S, like
           retail roots) keeps each root's world transform as its local one. */
        if (realRoots.Count > 1)
        {
            var syntheticRoot = new ImportedNode {
                Index = model.Nodes.Count,
                Name = "dotr_root",
                Parent = -1,
            };
            foreach (int rootIndex in model.RootNodes)
            {
                if (LooseEmptyRoot(model, rootIndex)) continue;
                syntheticRoot.Children.Add(rootIndex);
                model.Nodes[rootIndex].Parent = syntheticRoot.Index;
            }
            model.Nodes.Add(syntheticRoot);
            model.RootNodes = new List<int> { syntheticRoot.Index }; // idempotent on Build re-runs
            return (syntheticRoot.Index, true, -1); // -1: all scene roots consumed
        }

        /* -- structural wrapper peel: position decides node_0 / node_1, not naming --
           A wrapper is a node with no mesh, no skin-joint role and one child. Up to two are
           peeled off the top: the first becomes node_0 (never animated, like retail's node 0,
           which is at rest with zero rotation/translation), the second node_1 (pivot/camera
           target; retail's node 1 is routinely animated). The peel stops at a node with real
           content (mesh, skin-joint role) or more than one real child (a branch point, e.g. an
           Armature with several top-level bone chains); that node and everything below it is
           kept verbatim.
           At root0 only, pure mesh-instancing leaf children (MrgMesh, see
           IsPureMeshInstancingLeaf) are ignored: a MODEL.MRG-style export is
           MrgRoot -> [node_0 -> node_1, MrgMesh], and MrgMesh is a sibling of the real chain
           that must not make MrgRoot look like a branch point. Deeper nodes are not filtered:
           an Armature can have several mesh-leaf children beside its one real bone child (jerryNew.glb:
           4 mesh leaves + 1 bone child), and filtering would wrongly make it a pass-through wrapper. */
        List<int> RootRealChildren(int glbIndex)
        {
            var realChildren = new List<int>();
            foreach (int child in model.Nodes[glbIndex].Children)
                if (!IsPureMeshInstancingLeaf(model, child))
                    realChildren.Add(child);
            return realChildren;
        }

        bool IsWrapper(int glbIndex, bool isRoot)
        {
            var node = model.Nodes[glbIndex];
            int childCount = isRoot ? RootRealChildren(glbIndex).Count : node.Children.Count;
            /* Animation does not disqualify a wrapper: in retail MODEL.MRG (pack block 37's MOT
               track table) node_0 itself can carry a full track (1001 rotation keys in that
               sample). Only real content or several real children stops the peel. */
            return !node.HasMesh && !node.IsJoint && childCount == 1 && !IsReferenced(glbIndex);
        }

        var wrapperChain = new List<int>();
        int cursor = root0;
        bool firstStep = true;
        while (IsWrapper(cursor, firstStep))
        {
            wrapperChain.Add(cursor);
            cursor = firstStep ? RootRealChildren(cursor)[0] : model.Nodes[cursor].Children[0];
            firstStep = false;
        }
        /* cursor is the first node with real content or several children; wrapperChain sits
           strictly above it.

           Is cursor itself a valid node_1 (no mesh, no children), rather than content past an
           already-identified node_1? node_1 may pass IsWrapper (it has a real child, e.g. the
           monster/extracted shape node_1 -> model_offset/Armature) and so be wrapperChain's last
           element with cursor past it; or it is a bare leaf skin-joint (every confirmed
           MODEL.MRG-style prop: node_0_m1 in model0ab, node_1_m1 in model_037/039/189), fails
           IsWrapper's child-count test, and becomes cursor. HasMesh keeps real geometry from
           being taken for the placeholder. */
        bool cursorIsNode1Candidate = model.Nodes[cursor].Children.Count == 0 && !model.Nodes[cursor].HasMesh;

        void BakeDroppedWrappers(ImportedNode keep, int dropCountAbove)
        {
            if (dropCountAbove <= 0) return;
            /* Wrappers were dropped above `keep` (e.g. a MrgRoot with the -pivot recentring shift
               from ModelExtractor.WriteGlb). Its WorldMatrix already includes them, so bake that
               into its new local TRS as it becomes a root; otherwise the model shifts. */
            if (Matrix4x4.Decompose(keep.WorldMatrix, out var scale, out var rotation, out var translation))
            {
                keep.Scale = scale;
                keep.Rotation = rotation;
                keep.Translation = translation;
            }
            else
            {
                keep.Translation = keep.WorldMatrix.Translation;
            }
        }

        if (cursorIsNode1Candidate)
        {
            // node_1 = cursor. node_0 = the last wrapper in the chain (earlier ones, e.g. MrgRoot, are dropped), or a synthesized neutral root if no wrapper sits above cursor.
            int node1 = cursor;
            if (wrapperChain.Count > 0)
            {
                int node0 = wrapperChain[^1];
                var rootNode = model.Nodes[node0];
                BakeDroppedWrappers(rootNode, wrapperChain.Count - 1);
                rootNode.Parent = -1;
                return (node0, false, slot);
            }
            var freshRoot = new ImportedNode {
                Index = model.Nodes.Count,
                Name = "node_0",
                Parent = -1,
                Children = new List<int> { node1 },
            };
            model.Nodes.Add(freshRoot);
            model.Nodes[node1].Parent = freshRoot.Index;
            return (freshRoot.Index, false, slot);
        }

        if (wrapperChain.Count >= 2)
        {
            // cursor is real branching/content, so node_1 is the chain's last element (its one real child is cursor): the monster/extracted shape node_0 -> node_1 -> model_offset/Armature.
            int node0 = wrapperChain[^2];
            var rootNode = model.Nodes[node0];
            BakeDroppedWrappers(rootNode, wrapperChain.Count - 2);
            rootNode.Parent = -1;
            return (node0, false, slot);
        }

        /* Fewer than 2 wrapper levels and cursor is real content (not node_1-shaped): no node_1
           exists (one wrapper directly above real geometry, or root0 itself carries content or
           branches), so the caller synthesizes a static one (InsertGeneratedPivot). A single
           wrapper becomes the root; otherwise root0 is used as it is. */
        if (wrapperChain.Count == 1)
        {
            int node0 = wrapperChain[0];
            model.Nodes[node0].Parent = -1;
            return (node0, true, slot);
        }
        var newRoot = model.Nodes[root0];
        newRoot.Parent = -1;
        return (root0, true, slot);

        bool IsReferenced(int glbIndex)
        {
            foreach (var primitive in model.Primitives)
            {
                if (primitive.NodeIndex == glbIndex) return true;
                foreach (var vertex in primitive.Vertices)
                foreach (var influence in vertex.Influences)
                    if (influence.JointNode == glbIndex)
                        return true;
            }
            return false;
        }
    }

    /// Inserts a static (unanimated) leaf pivot node at enc index 1 (the root's first child).
    /// Fallback only (see step 4.5): retail monsters keyframe node 1 in every MOT slot so the
    /// camera follows the model's centre of mass, while this node only gives the one-time
    /// placement (SzModel_SetPos via GetCenterOfs); its camera-aim point never moves. A model
    /// that wants camera follow needs an animated 'origin'/'dotr_center' node, which
    /// PrepareRoot hoists to first child with synthPivot=false and never reaches here.
    /// Anchor: horizontal centre (X/Z) of the geometry bbox and its vertical base (min Y),
    /// which mirrors where retail monsters' GameNode(1) sits (blue-eyes: world pivot Y=-9.16
    /// vs bbox centre Y=+9.24, low in the body).
    private static void InsertGeneratedPivot(List<EncNode> nodes,
        Dictionary<int, int> encIndexOf, float globalScale,
        Vector3? geometryMin = null, Vector3? geometryMax = null)
    {
        if (nodes.Count == 0 || globalScale <= 0f) return;

        Vector3 pivotWorld = Vector3.Zero;
        if (geometryMin is { } minimum && geometryMax is { } maximum)
        {
            pivotWorld = new Vector3((minimum.X + maximum.X) * 0.5f, minimum.Y, (minimum.Z + maximum.Z) * 0.5f);
        }

        // remap every index >= 1 to +1
        foreach (var encNode in nodes)
        {
            if (encNode.Parent >= 1) encNode.Parent += 1;
            for (int i = 0; i < encNode.Children.Count; i++)
                if (encNode.Children[i] >= 1)
                    encNode.Children[i] += 1;
        }
        foreach (var glbIndex in encIndexOf.Keys.ToList())
            if (encIndexOf[glbIndex] >= 1)
                encIndexOf[glbIndex] += 1;

        // The root scale S sits on the root node, so the pivot's raw position is the world pivot / S, like every other first-level node.
        var pivot = new EncNode {
            Scale = (1f, 1f, 1f),
            Flags = 0,
            MeshSlot = 0,
            RotationBams = (0, 0, 0),
            Position = (pivotWorld.X / globalScale, pivotWorld.Y / globalScale, pivotWorld.Z / globalScale),
            Parent = 0,
        };
        nodes.Insert(1, pivot);
        nodes[0].Children.Insert(0, 1);
    }

    /* Motion slots are battle roles (NOTES/models.md in DotR-PC): 0 idle, 2 attack, 4 death, 5 win,
       6 damage; 1 and 3 are optional and unidentified. The extractor names animations "slot_N";
       other names are matched to a role by keyword, and the rest take the free slots in role order
       (0, 2, 4, 5, 6, then 1, 3). -1 = no slot left (a second animation for a taken slot, or more
       than 7 animations). */
    private static readonly int[] SlotFillOrder = { 0, 2, 4, 5, 6, 1, 3 };

    private static int[] AssignSlotIds(List<ImportedAnimation> animations)
    {
        var slots = new int[animations.Count];
        var used = new HashSet<int>();
        for (int i = 0; i < animations.Count; i++)
        {
            int slotId = ParseSlotId(animations[i].Name);
            if (slotId < 0) slotId = RoleSlotId(animations[i].Name);
            if (slotId >= 0 && !used.Add(slotId)) slotId = -1;
            else if (slotId < 0) slotId = -2; // no slot or role in the name: assigned below
            slots[i] = slotId;
        }
        for (int i = 0; i < animations.Count; i++)
        {
            if (slots[i] != -2) continue;
            int free = Array.FindIndex(SlotFillOrder, slot => !used.Contains(slot));
            slots[i] = free >= 0 ? SlotFillOrder[free] : -1;
            if (free >= 0) used.Add(slots[i]);
        }
        return slots;
    }

    internal static int RoleSlotId(string name)
    {
        // whole words only ("wing" is not "win"); names split on anything that is not a letter
        var words = System.Text.RegularExpressions.Regex.Split(name.ToLowerInvariant(), "[^a-z]+");
        bool HasWord(params string[] candidates) => words.Any(candidates.Contains);
        if (HasWord("idle", "wait")) return 0;
        if (HasWord("attack")) return 2;
        if (HasWord("death", "die", "dead")) return 4;
        if (HasWord("win", "victory")) return 5;
        if (HasWord("damage", "hurt", "hit")) return 6;
        return -1;
    }

    internal static int ParseSlotId(string name)
    {
        int slotTextIndex = name.IndexOf("slot_", StringComparison.OrdinalIgnoreCase);
        if (slotTextIndex >= 0)
        {
            int digitsStart = slotTextIndex + 5, digitsEnd = digitsStart;
            while (digitsEnd < name.Length && char.IsDigit(name[digitsEnd])) digitsEnd++;
            if (digitsEnd > digitsStart && int.TryParse(name.AsSpan(digitsStart, digitsEnd - digitsStart), out int slotNumber) && slotNumber >= 0 && slotNumber <= 7) return slotNumber;
        }
        return -1;
    }

    // ---- stripification ----
    /// Per-batch VU ceiling (mirrors GeometryWriter.VuBudget): a single strip must fit one
    /// batch, i.e. 1 + sum(vnum) <= 224. This also keeps the 8-bit VIF unpack `num` < 255.
    private const int StripVuBudget = 224;

    /// Splits a primitive's triangles into rigid groups (all 3 verts single-influence on the
    /// same joint -> INDEP strips for that joint) and skinned ones (-> SHARED strips, capped
    /// at MaxBonesPerPage distinct joints per strip so page partitioning always succeeds).
    /// Pathological triangles (no valid influence on a vertex, or >8 distinct joints) fall
    /// back to the per-triangle path (BuildTriangleStrip), which caps/drops.
    /// keepGame: a vertex's game influences (ImportedVertex.Game) are written as they are,
    /// zero weights included, instead of being derived from its one position; only while the
    /// `_bone` joints encode at their own bind scale (sigma 1), the space they were stored in.
    private static IEnumerable<EncStrip> StripifyPrimitive(ImportedPrimitive primitive, ImportedModel model,
        Dictionary<int, Matrix4x4> inverseRestWorldSkin, Dictionary<int, int> meshSlotOf, bool keepGame)
    {
        // per-vertex valid joints
        var jointsCache = new Dictionary<int, int[]>();

        int[] ValidJoints(int vertexIndex)
        {
            if (jointsCache.TryGetValue(vertexIndex, out var cached)) return cached;
            var vertex = primitive.Vertices[vertexIndex];
            var candidates = keepGame && vertex.Game != null
                ? vertex.Game.Select(influence => influence.Bone)
                : Influences(vertex, model).Select(influence => influence.JointNode);
            cached = candidates.Where(joint => inverseRestWorldSkin.ContainsKey(joint) && meshSlotOf.ContainsKey(joint)).ToArray();
            jointsCache[vertexIndex] = cached;
            return cached;
        }

        int VnumShared(int vertexIndex) => 1 + 3 * ValidJoints(vertexIndex).Length; // (3 + 9*ninf) shorts / 3

        var rigid = new SortedDictionary<int, List<(int First, int Second, int Third)>>(); // joint -> tris
        var shared = new List<(int First, int Second, int Third)>();
        var fallback = new List<(int First, int Second, int Third)>();

        foreach (var triangle in primitive.Triangles)
        {
            var (corner0, corner1, corner2) = triangle;
            if (corner0 == corner1 || corner1 == corner2 || corner2 == corner0) continue; // degenerate
            int[] corner0Joints = ValidJoints(corner0), corner1Joints = ValidJoints(corner1), corner2Joints = ValidJoints(corner2);
            if (corner0Joints.Length == 0 || corner1Joints.Length == 0 || corner2Joints.Length == 0)
            {
                fallback.Add(triangle);
                continue;
            }
            if (corner0Joints.Length == 1 && corner1Joints.Length == 1 && corner2Joints.Length == 1 && corner0Joints[0] == corner1Joints[0] && corner1Joints[0] == corner2Joints[0])
            {
                if (!rigid.TryGetValue(corner0Joints[0], out var triangles)) rigid[corner0Joints[0]] = triangles = new List<(int, int, int)>();
                triangles.Add(triangle);
                continue;
            }
            var distinct = new HashSet<int>(corner0Joints);
            distinct.UnionWith(corner1Joints);
            distinct.UnionWith(corner2Joints);
            if (distinct.Count > MaxBonesPerPage)
            {
                fallback.Add(triangle);
                continue;
            }
            shared.Add(triangle);
        }

        // ---- rigid -> INDEP strips (one bone per strip; vnum = 3 per vertex) ----
        foreach (var rigidGroup in rigid)
        {
            int joint = rigidGroup.Key;
            int meshSlot = meshSlotOf[joint];
            var inverse = inverseRestWorldSkin[joint];
            foreach (var stripIndices in Stripify(rigidGroup.Value, _ => 3, null, 0))
            {
                var strip = new EncStrip { Kind = StripKind.Indep, IndependentVuAddress = meshSlot /* placeholder mesh slot */ };
                foreach (int vertexIndex in stripIndices)
                {
                    var vertex = primitive.Vertices[vertexIndex];
                    var (x, y, z) = QuantizedPosition(vertex.Position, inverse);
                    var (normalX, normalY, normalZ) = QuantizedNormal(vertex.Normal, inverse);
                    if (keepGame && vertex.Game is [var gameInfluence])
                    {
                        (x, y, z) = gameInfluence.Position;
                        (normalX, normalY, normalZ) = gameInfluence.Normal;
                    }
                    strip.Vertices.Add(new EncVertex {
                        U = Quantize(vertex.Uv.X * UvScale), V = Quantize(vertex.Uv.Y * UvScale), Flag = 0,
                        NormalX = normalX, NormalY = normalY, NormalZ = normalZ, X = x, Y = y, Z = z,
                    });
                }
                yield return strip;
            }
        }

        // ---- skinned -> SHARED strips (<= MaxBonesPerPage distinct joints per strip) ----
        foreach (var stripIndices in Stripify(shared, VnumShared, ValidJoints, MaxBonesPerPage))
        {
            var strip = new EncStrip { Kind = StripKind.Shared };
            foreach (int vertexIndex in stripIndices)
            {
                var vertex = primitive.Vertices[vertexIndex];
                var encVertex = new EncVertex { U = Quantize(vertex.Uv.X * UvScale), V = Quantize(vertex.Uv.Y * UvScale), Influences = new() };
                if (keepGame && vertex.Game != null)
                {
                    foreach (var gameInfluence in vertex.Game)
                    {
                        if (!inverseRestWorldSkin.ContainsKey(gameInfluence.Bone) || !meshSlotOf.TryGetValue(gameInfluence.Bone, out int meshSlot)) continue;
                        encVertex.Influences.Add(new EncInfluence {
                            Bone = (ushort)meshSlot /* placeholder mesh slot */,
                            Weight = gameInfluence.Weight, Flag = 0,
                            NormalX = gameInfluence.Normal.X, NormalY = gameInfluence.Normal.Y, NormalZ = gameInfluence.Normal.Z, X = gameInfluence.Position.X, Y = gameInfluence.Position.Y, Z = gameInfluence.Position.Z,
                        });
                    }
                }
                else
                {
                    foreach (var influence in Influences(vertex, model))
                    {
                        if (!inverseRestWorldSkin.TryGetValue(influence.JointNode, out var inverse) || !meshSlotOf.TryGetValue(influence.JointNode, out int meshSlot)) continue;
                        var (x, y, z) = QuantizedPosition(vertex.Position, inverse);
                        var (normalX, normalY, normalZ) = QuantizedNormal(vertex.Normal, inverse);
                        encVertex.Influences.Add(new EncInfluence {
                            Bone = (ushort)meshSlot /* placeholder mesh slot */,
                            Weight = Quantize(influence.Weight * WeightScale), Flag = 0,
                            NormalX = normalX, NormalY = normalY, NormalZ = normalZ, X = x, Y = y, Z = z,
                        });
                    }
                }
                strip.Vertices.Add(encVertex); // classification guarantees >= 1 valid influence
            }
            yield return strip;
        }

        // ---- pathological tris -> per-triangle path (caps bones / drops unrigged) ----
        foreach (var (fallbackCorner0, fallbackCorner1, fallbackCorner2) in fallback)
        {
            var strip = BuildTriangleStrip(primitive.Vertices[fallbackCorner0], primitive.Vertices[fallbackCorner1], primitive.Vertices[fallbackCorner2], model, inverseRestWorldSkin, meshSlotOf);
            if (strip != null) yield return strip;
        }
    }

    /// Greedy edge-walk tri-stripper over vertex-index triangles. Returns strips as vertex
    /// index sequences with standard strip semantics (tri i = v[i-2], v[i-1], v[i]); winding
    /// is irrelevant (the GS doesn't cull; the extractor re-orients by normals). Growth stops
    /// when the strip would exceed the per-batch VU budget (1 + sum(vnumOf) <= StripVuBudget)
    /// or, when `jointsOf` is given, span more than `maxJoints` distinct bones.
    ///
    /// Seed and each step follow the least-adjacency rule (SGI's tri-stripper): take the
    /// triangle with the fewest still-free neighbours first. Triangle order instead strands
    /// the mesh's fringe as one-triangle strips, each costing a 24 B header plus, on a SHARED
    /// strip, a repeated influence blob per vertex (~3% over retail across the archive).
    private static List<List<int>> Stripify(List<(int First, int Second, int Third)> triangles,
        Func<int, int> vnumOf, Func<int, int[]>? jointsOf, int maxJoints)
    {
        var strips = new List<List<int>>();
        if (triangles.Count == 0) return strips;

        var edgeMap = new Dictionary<(int, int), List<int>>();

        void AddEdge(int first, int second, int triangle)
        {
            var edgeKey = first < second ? (first, second) : (second, first);
            if (!edgeMap.TryGetValue(edgeKey, out var edgeTriangles)) edgeMap[edgeKey] = edgeTriangles = new List<int>();
            edgeTriangles.Add(triangle);
        }

        for (int triangle = 0; triangle < triangles.Count; triangle++)
        {
            var (corner0, corner1, corner2) = triangles[triangle];
            AddEdge(corner0, corner1, triangle);
            AddEdge(corner1, corner2, triangle);
            AddEdge(corner2, corner0, triangle);
        }

        var used = new bool[triangles.Count];

        // neighbour lists + live count of still-free neighbours, for the least-adjacency rule
        var neighbours = new List<int>[triangles.Count];
        for (int triangle = 0; triangle < triangles.Count; triangle++) neighbours[triangle] = new List<int>();
        foreach (var edgeTriangles in edgeMap.Values)
            for (int i = 0; i < edgeTriangles.Count; i++)
            for (int j = i + 1; j < edgeTriangles.Count; j++)
            {
                if (!neighbours[edgeTriangles[i]].Contains(edgeTriangles[j])) neighbours[edgeTriangles[i]].Add(edgeTriangles[j]);
                if (!neighbours[edgeTriangles[j]].Contains(edgeTriangles[i])) neighbours[edgeTriangles[j]].Add(edgeTriangles[i]);
            }
        var freeNeighbours = new int[triangles.Count];
        for (int triangle = 0; triangle < triangles.Count; triangle++) freeNeighbours[triangle] = neighbours[triangle].Count;

        void Claim(int triangle)
        {
            used[triangle] = true;
            foreach (int neighbour in neighbours[triangle]) freeNeighbours[neighbour]--;
        }

        /// The free triangle on this edge with the fewest free neighbours of its own, or -1.
        int LeastAdjacent(List<int> candidates, HashSet<int> localUsed)
        {
            int best = -1, bestFree = int.MaxValue;
            foreach (int triangle in candidates)
            {
                if (used[triangle] || localUsed.Contains(triangle)) continue;
                if (freeNeighbours[triangle] >= bestFree) continue;
                bestFree = freeNeighbours[triangle];
                best = triangle;
            }
            return best;
        }

        int ThirdCorner(int triangle, int first, int second)
        {
            var (corner0, corner1, corner2) = triangles[triangle];
            if (corner0 != first && corner0 != second) return corner0;
            if (corner1 != first && corner1 != second) return corner1;
            return corner2;
        }

        (List<int> Strip, List<int> Tris) Walk(int seed, int corner0, int corner1, int corner2)
        {
            var strip = new List<int> { corner0, corner1, corner2 };
            var stripTris = new List<int> { seed };
            var localUsed = new HashSet<int> { seed };
            int vuCost = 1 + vnumOf(corner0) + vnumOf(corner1) + vnumOf(corner2);
            HashSet<int>? joints = null;
            if (jointsOf != null)
            {
                joints = new HashSet<int>();
                foreach (int vertex in strip) joints.UnionWith(jointsOf(vertex));
            }

            bool TryGrow(int newCorner, int growCost)
            {
                if (vuCost + growCost > StripVuBudget) return false;
                if (joints != null)
                {
                    int newJoints = 0;
                    foreach (int joint in jointsOf!(newCorner))
                        if (!joints.Contains(joint))
                            newJoints++;
                    if (joints.Count + newJoints > maxJoints) return false;
                }
                return true;
            }

            // forward: extend from the trailing edge (strip[^2], strip[^1])
            while (true)
            {
                int edgeFirst = strip[^2], edgeSecond = strip[^1];
                var edgeKey = edgeFirst < edgeSecond ? (edgeFirst, edgeSecond) : (edgeSecond, edgeFirst);
                if (!edgeMap.TryGetValue(edgeKey, out var candidates)) break;
                int next = LeastAdjacent(candidates, localUsed);
                if (next < 0) break;
                int newCorner = ThirdCorner(next, edgeFirst, edgeSecond);
                if (!TryGrow(newCorner, vnumOf(newCorner))) break;
                if (joints != null) joints.UnionWith(jointsOf!(newCorner));
                strip.Add(newCorner);
                vuCost += vnumOf(newCorner);
                stripTris.Add(next);
                localUsed.Add(next);
            }

            // backward: extend from the leading edge (strip[0], strip[1]) so a seed in the middle of a run claims the other half in the same strip (shared vuCost/joints/localUsed: one budget, no triangle claimed twice)
            while (true)
            {
                int edgeFirst = strip[0], edgeSecond = strip[1];
                var edgeKey = edgeFirst < edgeSecond ? (edgeFirst, edgeSecond) : (edgeSecond, edgeFirst);
                if (!edgeMap.TryGetValue(edgeKey, out var candidates)) break;
                int next = LeastAdjacent(candidates, localUsed);
                if (next < 0) break;
                int newCorner = ThirdCorner(next, edgeFirst, edgeSecond);
                if (!TryGrow(newCorner, vnumOf(newCorner))) break;
                if (joints != null) joints.UnionWith(jointsOf!(newCorner));
                strip.Insert(0, newCorner);
                vuCost += vnumOf(newCorner);
                stripTris.Add(next);
                localUsed.Add(next);
            }

            return (strip, stripTris);
        }

        // Seeds in least-adjacency order, lazily: a stale entry is re-queued under its current count (no decrease-key).
        var seeds = new PriorityQueue<int, int>();
        for (int triangle = 0; triangle < triangles.Count; triangle++) seeds.Enqueue(triangle, freeNeighbours[triangle]);
        int done = 0;
        while (done < triangles.Count && seeds.TryDequeue(out int seedTriangle, out int priority))
        {
            if (used[seedTriangle]) continue;
            if (priority != freeNeighbours[seedTriangle]) { seeds.Enqueue(seedTriangle, freeNeighbours[seedTriangle]); continue; }
            var (corner0, corner1, corner2) = triangles[seedTriangle];
            var firstWalk = Walk(seedTriangle, corner0, corner1, corner2);
            var secondWalk = Walk(seedTriangle, corner1, corner2, corner0);
            var thirdWalk = Walk(seedTriangle, corner2, corner0, corner1);
            var best = firstWalk.Strip.Count >= secondWalk.Strip.Count
                ? (firstWalk.Strip.Count >= thirdWalk.Strip.Count ? firstWalk : thirdWalk)
                : (secondWalk.Strip.Count >= thirdWalk.Strip.Count ? secondWalk : thirdWalk);
            foreach (int claimedTriangle in best.Tris)
            {
                Claim(claimedTriangle);
                done++;
                foreach (int neighbour in neighbours[claimedTriangle])
                    if (!used[neighbour])
                        seeds.Enqueue(neighbour, freeNeighbours[neighbour]);
            }
            strips.Add(best.Strip);
        }
        return strips;
    }

    // ---- triangle -> strip (fallback for pathological triangles) ----
    private static EncStrip? BuildTriangleStrip(ImportedVertex vertex0, ImportedVertex vertex1, ImportedVertex vertex2,
        ImportedModel model, Dictionary<int, Matrix4x4> inverseRestWorldSkin, Dictionary<int, int> meshSlotOf)
    {
        var vertices = new[] { vertex0, vertex1, vertex2 };
        // copy each vertex's influence list so capping never mutates the source model
        var vertexInfluences = vertices.Select(vertex => new List<ImportedInfluence>(Influences(vertex, model))).ToArray();

        bool rigid = vertexInfluences.All(influences => influences.Count == 1) &&
                     vertexInfluences[0][0].JointNode == vertexInfluences[1][0].JointNode &&
                     vertexInfluences[1][0].JointNode == vertexInfluences[2][0].JointNode;

        if (rigid)
        {
            int joint = vertexInfluences[0][0].JointNode;
            if (!inverseRestWorldSkin.TryGetValue(joint, out var inverse) || !meshSlotOf.TryGetValue(joint, out int meshSlot)) return null;
            var strip = new EncStrip { Kind = StripKind.Indep, IndependentVuAddress = meshSlot /* placeholder mesh slot */ };
            foreach (var vertex in vertices)
            {
                var (x, y, z) = QuantizedPosition(vertex.Position, inverse);
                var (normalX, normalY, normalZ) = QuantizedNormal(vertex.Normal, inverse);
                strip.Vertices.Add(new EncVertex {
                    U = Quantize(vertex.Uv.X * UvScale), V = Quantize(vertex.Uv.Y * UvScale), Flag = 0,
                    NormalX = normalX, NormalY = normalY, NormalZ = normalZ, X = x, Y = y, Z = z,
                });
            }
            return strip;
        }

        CapTriangleBones(vertexInfluences, MaxBonesPerPage); // PS2 page can bind at most 8 matrices
        var sharedStrip = new EncStrip { Kind = StripKind.Shared };
        for (int cornerIndex = 0; cornerIndex < 3; cornerIndex++)
        {
            var vertex = vertices[cornerIndex];
            var encVertex = new EncVertex { U = Quantize(vertex.Uv.X * UvScale), V = Quantize(vertex.Uv.Y * UvScale), Influences = new() };
            foreach (var influence in vertexInfluences[cornerIndex])
            {
                if (!inverseRestWorldSkin.TryGetValue(influence.JointNode, out var inverse) || !meshSlotOf.TryGetValue(influence.JointNode, out int meshSlot)) continue;
                var (x, y, z) = QuantizedPosition(vertex.Position, inverse);
                var (normalX, normalY, normalZ) = QuantizedNormal(vertex.Normal, inverse);
                encVertex.Influences.Add(new EncInfluence {
                    Bone = (ushort)meshSlot /* placeholder mesh slot */,
                    Weight = Quantize(influence.Weight * WeightScale), Flag = 0,
                    NormalX = normalX, NormalY = normalY, NormalZ = normalZ, X = x, Y = y, Z = z,
                });
            }
            if (encVertex.Influences.Count == 0) return null;
            sharedStrip.Vertices.Add(encVertex);
        }
        return sharedStrip;
    }

    // A triangle with more than maxBones distinct joints cannot be bound by a PS2 page: drop the weakest joint (by summed weight) and renormalise until it fits. Shipped models peak at 5 bones per triangle.
    private static void CapTriangleBones(List<ImportedInfluence>[] vertexInfluences, int maxBones)
    {
        var distinct = new HashSet<int>();
        foreach (var influences in vertexInfluences)
        foreach (var influence in influences)
            distinct.Add(influence.JointNode);
        while (distinct.Count > maxBones)
        {
            var total = new Dictionary<int, float>();
            foreach (var influences in vertexInfluences)
            foreach (var influence in influences)
                total[influence.JointNode] = total.GetValueOrDefault(influence.JointNode) + influence.Weight;
            int drop = total.OrderBy(entry => entry.Value).First().Key;
            foreach (var influences in vertexInfluences) influences.RemoveAll(influence => influence.JointNode == drop);
            distinct.Remove(drop);
            foreach (var influences in vertexInfluences)
            {
                float weightSum = influences.Sum(influence => influence.Weight);
                if (weightSum <= 0f) continue;
                for (int i = 0; i < influences.Count; i++)
                {
                    var scaled = influences[i];
                    scaled.Weight /= weightSum;
                    influences[i] = scaled;
                }
            }
        }
    }

    private static void RemapSharedBones(EncStrip strip, Dictionary<int, int> slotToVu)
    {
        foreach (var vertex in strip.Vertices)
        {
            if (vertex.Influences == null) continue;
            for (int i = 0; i < vertex.Influences.Count; i++)
            {
                var influence = vertex.Influences[i];
                influence.Bone = (ushort)slotToVu[influence.Bone]; // placeholder mesh slot -> VU slot
                vertex.Influences[i] = influence;
            }
        }
    }

    // ---- greedy <=8-bone partition ----
    private static List<List<EncStrip>> Partition(List<EncStrip> strips)
    {
        var pages = new List<List<EncStrip>>();
        var remaining = new List<EncStrip>(strips);
        while (remaining.Count > 0)
        {
            remaining.Sort((first, second) => StripBones(second).Count.CompareTo(StripBones(first).Count));
            var page = new HashSet<int>(StripBones(remaining[0]));
            var members = new List<EncStrip> { remaining[0] };
            remaining.RemoveAt(0);
            bool progress = true;
            while (progress)
            {
                progress = false;
                int bestIndex = -1, bestNewBones = int.MaxValue;
                for (int i = 0; i < remaining.Count; i++)
                {
                    var bones = StripBones(remaining[i]);
                    int union = page.Count + bones.Count(bone => !page.Contains(bone));
                    if (union <= MaxBonesPerPage && (union - page.Count) < bestNewBones)
                    {
                        bestNewBones = union - page.Count;
                        bestIndex = i;
                    }
                }
                if (bestIndex >= 0)
                {
                    foreach (int bone in StripBones(remaining[bestIndex])) page.Add(bone);
                    members.Add(remaining[bestIndex]);
                    remaining.RemoveAt(bestIndex);
                    progress = true;
                }
            }
            pages.Add(members);
        }
        return pages;
    }

    private static HashSet<int> StripBones(EncStrip strip)
    {
        var bones = new HashSet<int>();
        if (strip.Kind == StripKind.Indep) bones.Add(strip.IndependentVuAddress); // placeholder = mesh slot pre-remap
        else
            foreach (var vertex in strip.Vertices)
                if (vertex.Influences != null)
                    foreach (var influence in vertex.Influences)
                        bones.Add(influence.Bone);
        return bones;
    }

    // ---- helpers ----

    /// A "pure mesh-instancing leaf": HasMesh, no children, not a skin joint, and none of its
    /// vertices is influenced by its own node index (its geometry is described by other joints'
    /// influences). Every MODEL.MRG-style export has this shape: MrgRoot -> [node_0 -> node_1,
    /// MrgMesh], where MrgMesh instances the skinned mesh but the skin's only joint is node_1
    /// (verified on model_037/039/189.glb and model0ab.glb). An unskinned/rigid mesh node
    /// (props, vehicles) is different: GlbImporter's unskinned branch sets each vertex's
    /// influence to the mesh node itself, so the self-influence check keeps it as a real tree
    /// node that occupies a DFS slot and receives a meshSlot. A matching node is pruned from
    /// the tree walk and not counted as a real child in wrapper detection; meshSlot assignment
    /// comes from skin/influence data, never from tree position.
    private static bool IsPureMeshInstancingLeaf(ImportedModel model, int glbIndex)
    {
        int nodeIndex = glbIndex;
        if (nodeIndex < 0) return false;
        var node = model.Nodes[nodeIndex];
        if (!node.HasMesh || node.IsJoint || node.Children.Count != 0) return false;
        foreach (var primitive in model.Primitives)
        {
            if (primitive.NodeIndex != glbIndex) continue;
            foreach (var vertex in primitive.Vertices)
            foreach (var influence in vertex.Influences)
                if (influence.JointNode == glbIndex)
                    return false; // self-influenced: this IS the rigid/unskinned case
        }
        return true;
    }

    private static List<ImportedInfluence> Influences(ImportedVertex vertex, ImportedModel model)
    {
        if (vertex.Influences.Count > 0) return vertex.Influences;
        // unrigged vertex: bind to the first joint so it still has a bone
        if (model.JointNodeIndices.Count > 0)
            return new List<ImportedInfluence> { new ImportedInfluence { JointNode = model.JointNodeIndices[0], Weight = 1f } };
        return vertex.Influences;
    }

    private static (short X, short Y, short Z) QuantizedPosition(Vector3 world, Matrix4x4 inverseRestWorldSkin)
    {
        var position = Vector3.Transform(world, inverseRestWorldSkin);
        return (Quantize(position.X), Quantize(position.Y), Quantize(position.Z));
    }

    private static (short Nx, short Ny, short Nz) QuantizedNormal(Vector3 worldNormal, Matrix4x4 inverseRestWorldSkin)
    {
        var normal = Vector3.TransformNormal(worldNormal, inverseRestWorldSkin);
        float length = normal.Length();
        if (length < 1e-8f) return (0, 0, (short)NormScale);
        normal = normal / length * NormScale;
        return (Quantize(normal.X), Quantize(normal.Y), Quantize(normal.Z));
    }

    /// Counts Quantize's clamps for the Build running on this thread (Result.ClampedValues).
    [ThreadStatic] static int clampedValues;

    private static short Quantize(float value)
    {
        int rounded = (int)MathF.Round(value);
        if (rounded > 32767) { rounded = 32767; clampedValues++; }
        else if (rounded < -32768) { rounded = -32768; clampedValues++; }
        return (short)rounded;
    }

    /// XYZ Euler (BAMS) of a rotation, for the game's RotX * RotY * RotZ. Worked in double with
    /// atan2 throughout: asin(-m13) loses ~5 BAMS near ry = +-90 degrees, which retail uses a
    /// lot (ry = 0x3FFF). There (gimbal lock) only rx and rz together are defined: rx is
    /// taken as rxHint (the previous frame's, for continuity) and rz from what remains.
    private static (int X, int Y, int Z) QuatToBams(Quaternion rotation, int rotationXHint = 0)
    {
        double x = rotation.X, y = rotation.Y, z = rotation.Z, w = rotation.W;
        double length = Math.Sqrt(x * x + y * y + z * z + w * w);
        if (length > 0) { x /= length; y /= length; z /= length; w /= length; }
        // System.Numerics (row-vector) rotation matrix elements
        double m11 = 1 - 2 * (y * y + z * z), m12 = 2 * (x * y + z * w), m13 = 2 * (x * z - y * w);
        double m21 = 2 * (x * y - z * w), m22 = 1 - 2 * (x * x + z * z), m23 = 2 * (y * z + x * w);
        double m33 = 1 - 2 * (x * x + y * y);
        double cosYMagnitude = Math.Sqrt(m11 * m11 + m12 * m12);
        double rotationY = Math.Atan2(-m13, cosYMagnitude);
        double rotationX = cosYMagnitude > 1e-6 ? Math.Atan2(m23, m33) : rotationXHint / BamsPerRad;
        // M = Rx * Ry * Rz, so Rz = Ry^T * Rx^T * M; rz from its first row
        double cosX = Math.Cos(rotationX), sinX = Math.Sin(rotationX), cosY = Math.Cos(rotationY), sinY = Math.Sin(rotationY);
        // first row of Ry^T * Rx^T: (cy, sx*sy, cx*sy), applied to M's columns 1 and 2
        double m31 = 2 * (x * z + y * w), m32 = 2 * (y * z - x * w);
        double rotationZM11 = cosY * m11 + sinX * sinY * m21 + cosX * sinY * m31;
        double rotationZM12 = cosY * m12 + sinX * sinY * m22 + cosX * sinY * m32;
        double rotationZ = Math.Atan2(rotationZM12, rotationZM11);
        return (BamsRound(rotationX), BamsRound(rotationY), BamsRound(rotationZ));
    }

    /// Same XYZ-Euler decomposition as <see cref="QuatToBams"/>, but picks among the
    /// equivalent triples (the principal branch, the alternate ry'=pi-ry, rx'=rx+pi,
    /// rz'=rz+pi, each angle also wrapped mod a full BAMS circle) the one closest to the
    /// previous frame. The game's MOT interpolator blends consecutive keyframe BAMS values
    /// linearly (DotR_FileFormats.md §9), so a branch flip between frames makes it spin through
    /// almost a full extra revolution. Frame-independent decoding of blue-eyes_white_dragon.mrg's
    /// attack rotation tracks gave single-frame jumps up to ~359 degrees, where retail steps
    /// a few degrees per frame.
    private static (int X, int Y, int Z) QuatToBamsContinuous(Quaternion rotation, (int X, int Y, int Z)? previous)
    {
        var principal = QuatToBams(rotation, previous?.X ?? 0);
        if (previous is not { } previousValue) return principal;

        // Alternate Euler branch for the same rotation: ry -> pi - ry, the other two shifted by a half turn (HalfTurn = 0x8000 BAMS).
        const int HalfTurn = 0x8000;
        var alternate = (
            X: principal.X + HalfTurn,
            Y: HalfTurn - principal.Y,
            Z: principal.Z + HalfTurn);

        (int X, int Y, int Z) BestWrap((int X, int Y, int Z) candidate, (int X, int Y, int Z) target)
        {
            // Each axis may also need a +/- full-circle shift to land nearest the previous frame (previous=32700, candidate=-32750 is 134 BAMS away wrapped, not ~65450).
            int Nearest(int value, int reference)
            {
                int difference = (value - reference) % 65536;
                if (difference > 32768) difference -= 65536;
                else if (difference < -32768) difference += 65536;
                return reference + difference;
            }
            return (Nearest(candidate.X, target.X), Nearest(candidate.Y, target.Y), Nearest(candidate.Z, target.Z));
        }

        var principalNear = BestWrap(principal, previousValue);
        var altNear = BestWrap(alternate, previousValue);

        long Cost((int X, int Y, int Z) candidate) =>
            Math.Abs((long)candidate.X - previousValue.X) + Math.Abs((long)candidate.Y - previousValue.Y) + Math.Abs((long)candidate.Z - previousValue.Z);

        return Cost(principalNear) <= Cost(altNear) ? principalNear : altNear;
    }

    private static int BamsRound(double radians) => (int)Math.Round(radians * BamsPerRad);

    private readonly struct MatKey : IEquatable<MatKey>
    {
        public readonly int TextureIndex;
        public readonly bool IsAlt;
        public readonly int Red, Green, Blue;
        public readonly float Alpha;
        public readonly (int R, int G, int B) Color0, Color2;
        public readonly bool Untextured;
        public readonly byte GsAlpha;
        public readonly uint GsTest;

        public MatKey(int textureIndex, bool isAlt, Vector4 baseColor, (int R, int G, int B)? rawColor1,
                      (int R, int G, int B) color0, (int R, int G, int B) color2, bool untextured, byte gsAlpha = 0x44, uint gsTest = 0)
        {
            TextureIndex = textureIndex;
            IsAlt = isAlt;
            GsAlpha = gsAlpha;
            GsTest = gsTest;
            Untextured = untextured;
            Color0 = color0;
            Color2 = color2;
            Alpha = Math.Clamp(baseColor.W, 0, 1) * 128f;
            if (rawColor1 is { } rawColor)
            {
                // the extractor's c1, unedited: written back as retail had it
                (Red, Green, Blue) = rawColor;
                return;
            }
            /* The +0x20 row is a lighting input the engine scales at runtime; retail's neutral is
               (178,178,178), so exact white (the glTF default) maps to that on normal pages;
               255 would be a permanent ~1.43x over-brightness. Alt (additive glow) pages keep
               full range. */
            bool white = baseColor.X >= 0.9999f && baseColor.Y >= 0.9999f && baseColor.Z >= 0.9999f;
            if (white && !isAlt)
            {
                Red = Green = Blue = 178;
            }
            else
            {
                Red = (int)MathF.Round(Math.Clamp(baseColor.X, 0, 1) * 255f);
                Green = (int)MathF.Round(Math.Clamp(baseColor.Y, 0, 1) * 255f);
                Blue = (int)MathF.Round(Math.Clamp(baseColor.Z, 0, 1) * 255f);
            }
            // lRhModel_SetMaterialIntensity gates the +0x20 row's SET/MUL handling on R != 0, so R is at least 1.
            if (Red == 0) Red = 1;
        }

        public bool Equals(MatKey other) => TextureIndex == other.TextureIndex && IsAlt == other.IsAlt && Red == other.Red && Green == other.Green && Blue == other.Blue && Alpha == other.Alpha
                                        && Color0 == other.Color0 && Color2 == other.Color2 && Untextured == other.Untextured && GsAlpha == other.GsAlpha && GsTest == other.GsTest;
        public override bool Equals(object? other) => other is MatKey key && Equals(key);
        public override int GetHashCode() => HashCode.Combine(HashCode.Combine(TextureIndex, IsAlt, Red, Green, Blue, Alpha), Color0, Color2, Untextured, GsAlpha, GsTest);
    }
}