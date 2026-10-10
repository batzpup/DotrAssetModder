using System.Numerics;
using SharpGLTF.Schema2;

namespace DotrAssetModder;

// Applies the Convert window's options to a glb extracted from MONSTER.MRG
// (ModelExtractor.ExtractGlb), keeping the game's node tree, so Rebuilder.Import puts the
// card back into its own block the way it imports any glb. The options mean what they mean
// on an import:
//   Scale / Rotate / Move: the body relative to node 0 (scale and turn about node 1 where the
//     Pivot puts it, then move, in node 0's axes); node 1 does not move.
//   Pivot: node 1 (its rest offset and every key) relative to node 0; the body does not move.
// The glb is the extractor's: MrgRoot -> node 0 -> node 1 -> ..., each node carrying only
// translation and rotation, its accumulated scale on a `_bone` leaf child. Node 1 turns
// inside the motions, so a child of node 1 takes the edit as a key at every frame the
// importer samples (GlbImporter.AnimFps); a child of node 0 beside node 1 takes it as is.
// Skinned vertices are world positions: they and the inverse binds move together, so the
// bone-local vertices the importer encodes stay the same.
// A card that draws geometry with node 1's own matrix (163: most of the model, 207: four
// vertices) cannot be edited: that geometry turns with node 1 in every motion, so it can only
// follow the body if node 1 does (user's call: not editable). Radius alone still is.
internal static class ExtractedGlbEdit
{
    public static byte[] Apply(byte[] glb, float scale, Vector3 rotateDeg, Vector3 move, Vector3 pivot)
    {
        ModelRoot model = ModelRoot.ParseGLB(glb);
        Node node1 = NodeOne(model, out Node root, out Node node0);
        List<Node> underNode1 = node1.VisualChildren.Where(n => !IsScaleBone(n)).ToList();
        List<Node> besideNode1 = node0.VisualChildren.Where(n => n != node1 && !IsScaleBone(n)).ToList();

        // M in node 0's axes, row-vector order: scale, then X, Y, Z turns (Mat4.Euler "XYZ"), both
        // about node 1 where the Pivot puts it (where the game turns and shrinks it), then move
        Vector3 turnAt = node1.LocalTransform.Translation + pivot;
        Matrix4x4 m = Matrix4x4.CreateTranslation(-turnAt)
                      * Matrix4x4.CreateScale(scale)
                      * Matrix4x4.CreateRotationX(rotateDeg.X * MathF.PI / 180f)
                      * Matrix4x4.CreateRotationY(rotateDeg.Y * MathF.PI / 180f)
                      * Matrix4x4.CreateRotationZ(rotateDeg.Z * MathF.PI / 180f)
                      * Matrix4x4.CreateTranslation(turnAt + move);
        bool bodyMoves = m != Matrix4x4.Identity;
        bool pivotMoves = pivot != Vector3.Zero;
        if ((bodyMoves || pivotMoves) && NodeOneDraws(model, node1))
        {
            throw new InvalidOperationException("this card draws part of the model with node 1, which an edit cannot move with the body");
        }
        Matrix4x4 pivotShift = Matrix4x4.CreateTranslation(pivot);

        // A child of node 1 keeps its world (in node 0's frame) times M while node 1 takes the pivot:
        // C' N1' = C N1 M, with N1' = N1 T(P).
        Matrix4x4 UnderNode1(Matrix4x4 c, Matrix4x4 n1)
        {
            Matrix4x4.Invert(n1 * pivotShift, out Matrix4x4 inv);
            return c * n1 * m * inv;
        }

        // Every new key is worked out from the glb as extracted, before anything is written.
        var writes = new List<Action>();
        foreach (Animation anim in model.LogicalAnimations)
        {
            int frames = (int)MathF.Round(anim.Duration * GlbImporter.AnimFps) + 1;
            Sampled n1 = new Sampled(node1, anim);
            if (pivotMoves && n1.HasT)
            {
                var keys = new SortedDictionary<float, Vector3>();
                for (int f = 0; f < frames; f++)
                {
                    keys[f / GlbImporter.AnimFps] = n1.T(f / GlbImporter.AnimFps) + pivot;
                }
                writes.Add(() => anim.CreateTranslationChannel(node1, keys, true));
            }
            if (bodyMoves || pivotMoves)
            {
                foreach (Node c in underNode1)
                {
                    Sampled cs = new Sampled(c, anim);
                    if (n1.HasT || n1.HasR || cs.HasT || cs.HasR)
                    {
                        writes.Add(KeyEveryFrame(anim, c, frames, t => UnderNode1(cs.Local(t), n1.Local(t))));
                    }
                }
            }
            if (bodyMoves)
            {
                foreach (Node c in besideNode1)
                {
                    Sampled cs = new Sampled(c, anim);
                    if (cs.HasT || cs.HasR)
                    {
                        writes.Add(KeyEveryFrame(anim, c, frames, t => cs.Local(t) * m));
                    }
                }
            }
        }

        // the skin: the body's world at rest moves by W (scene space), vertices with it
        Matrix4x4 node0World = node0.LocalMatrix * root.LocalMatrix;
        Matrix4x4.Invert(node0World, out Matrix4x4 node0WorldInv);
        Matrix4x4 w = node0WorldInv * m * node0World;
        Matrix4x4.Invert(w, out Matrix4x4 wInv);
        if (bodyMoves)
        {
            var done = new HashSet<int>();
            foreach (Node n in model.LogicalNodes.Where(n => n.Skin != null && n.Mesh != null))
            {
                foreach (MeshPrimitive prim in n.Mesh.Primitives)
                {
                    Accessor? pos = prim.GetVertexAccessor("POSITION");
                    if (pos != null && done.Add(pos.LogicalIndex))
                    {
                        var a = pos.AsVector3Array();
                        for (int i = 0; i < a.Count; i++)
                        {
                            a[i] = Vector3.Transform(a[i], w);
                        }
                        pos.UpdateBounds();
                    }
                    Accessor? nrm = prim.GetVertexAccessor("NORMAL");
                    if (nrm != null && done.Add(nrm.LogicalIndex))
                    {
                        var a = nrm.AsVector3Array();
                        for (int i = 0; i < a.Count; i++)
                        {
                            Vector3 v = Vector3.TransformNormal(a[i], w);
                            a[i] = v == Vector3.Zero ? v : Vector3.Normalize(v);
                        }
                        nrm.UpdateBounds();
                    }
                }
            }
            foreach (Skin skin in model.LogicalSkins)
            {
                var ibm = skin.GetInverseBindMatricesAccessor().AsMatrix4x4Array();
                for (int i = 0; i < skin.JointsCount; i++)
                {
                    ibm[i] = Affine(wInv * ibm[i]);
                }
            }
        }

        // rest pose
        Matrix4x4 n1Rest = node1.LocalMatrix;
        if (bodyMoves || pivotMoves)
        {
            foreach (Node c in underNode1)
            {
                SetLocal(c, UnderNode1(c.LocalMatrix, n1Rest));
            }
        }
        if (bodyMoves)
        {
            foreach (Node c in besideNode1)
            {
                SetLocal(c, c.LocalMatrix * m);
            }
        }
        if (pivotMoves)
        {
            SetLocal(node1, n1Rest * pivotShift);
        }
        foreach (Action write in writes)
        {
            write();
        }
        return model.WriteGLB().ToArray();
    }

    // False for a card whose node 1 draws part of the model: only Radius can be edited there.
    public static bool Editable(byte[] glb)
    {
        ModelRoot model = ModelRoot.ParseGLB(glb);
        return !NodeOneDraws(model, NodeOne(model, out _, out _));
    }

    // MrgRoot -> node 0 -> node 1 (node 0's first child that is not its `_bone`)
    private static Node NodeOne(ModelRoot model, out Node root, out Node node0)
    {
        root = model.LogicalNodes.First(n => n.Name == "MrgRoot");
        node0 = root.VisualChildren.Single();
        return node0.VisualChildren.First(n => !IsScaleBone(n));
    }

    // node 1's `_bone` is a skin joint or carries a mesh (the extractor's rigid mode)
    private static bool NodeOneDraws(ModelRoot model, Node node1)
    {
        foreach (Node bone in node1.VisualChildren.Where(IsScaleBone))
        {
            if (bone.Mesh != null)
            {
                return true;
            }
            foreach (Skin skin in model.LogicalSkins)
            {
                for (int i = 0; i < skin.JointsCount; i++)
                {
                    if (skin.GetJoint(i).Joint == bone)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    // the extractor's `_bone` scale leaf (GlbImporter folds the same nodes into their owner)
    private static bool IsScaleBone(Node n) =>
        n.VisualChildren.Count() == 0 && System.Text.RegularExpressions.Regex.IsMatch(n.Name ?? "", @"_bone(?:\.\d+)?$");

    // glTF wants an inverse bind's last column exactly (0, 0, 0, 1); products leave rounding there
    private static Matrix4x4 Affine(Matrix4x4 m)
    {
        m.M14 = 0f;
        m.M24 = 0f;
        m.M34 = 0f;
        m.M44 = 1f;
        return m;
    }

    private static void SetLocal(Node n, Matrix4x4 local)
    {
        Matrix4x4.Decompose(local, out Vector3 s, out Quaternion r, out Vector3 t);
        n.LocalTransform = new SharpGLTF.Transforms.AffineTransform(s, Quaternion.Normalize(r), t);
    }

    // translation and rotation keys at every frame; the scale stays the node's static one
    private static Action KeyEveryFrame(Animation anim, Node node, int frames, Func<float, Matrix4x4> local)
    {
        var tk = new SortedDictionary<float, Vector3>();
        var rk = new SortedDictionary<float, Quaternion>();
        Quaternion prev = Quaternion.Identity;
        for (int f = 0; f < frames; f++)
        {
            float t = f / GlbImporter.AnimFps;
            Matrix4x4.Decompose(local(t), out _, out Quaternion r, out Vector3 tr);
            r = Quaternion.Normalize(r);
            // keep consecutive keys on one hemisphere so LINEAR slerps the short way
            if (f > 0 && Quaternion.Dot(r, prev) < 0f)
            {
                r = Quaternion.Negate(r);
            }
            tk[t] = tr;
            rk[t] = r;
            prev = r;
        }
        return () =>
        {
            anim.CreateTranslationChannel(node, tk, true);
            anim.CreateRotationChannel(node, rk, true);
        };
    }

    // a node's local transform in one animation; its rest values where it has no channel
    private sealed class Sampled
    {
        private readonly Node node;
        private readonly SharpGLTF.Animations.ICurveSampler<Vector3>? t;
        private readonly SharpGLTF.Animations.ICurveSampler<Quaternion>? r;
        private readonly SharpGLTF.Animations.ICurveSampler<Vector3>? s;

        public Sampled(Node node, Animation anim)
        {
            this.node = node;
            t = anim.FindTranslationChannel(node)?.GetTranslationSampler()?.CreateCurveSampler();
            r = anim.FindRotationChannel(node)?.GetRotationSampler()?.CreateCurveSampler();
            s = anim.FindScaleChannel(node)?.GetScaleSampler()?.CreateCurveSampler();
        }

        public bool HasT => t != null;
        public bool HasR => r != null;

        public Vector3 T(float time) => t != null ? t.GetPoint(time) : node.LocalTransform.Translation;

        public Matrix4x4 Local(float time)
        {
            Vector3 sc = s != null ? s.GetPoint(time) : node.LocalTransform.Scale;
            Quaternion q = r != null ? r.GetPoint(time) : node.LocalTransform.Rotation;
            return Matrix4x4.CreateScale(sc) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(T(time));
        }
    }
}
