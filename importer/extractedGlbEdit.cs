using System.Numerics;
using SharpGLTF.Schema2;

namespace DotrAssetModder;

/* Applies the Convert window's options to a glb extracted from MONSTER.MRG
   (ModelExtractor.ExtractGlb), keeping the game's node tree, so Rebuilder.Import puts the
   card back into its own block like any glb. The options mean what they mean on an import:
     Scale / Rotate / Move: the body relative to node 0 (scale and turn about node 1 where the
       Pivot puts it, then move, in node 0's axes); node 1 does not move.
     Pivot: node 1 (its rest offset and every key) relative to node 0; the body does not move.
   The glb is the extractor's: MrgRoot -> node 0 -> node 1 -> ..., each node carrying only
   translation and rotation, its accumulated scale on a `_bone` leaf child. Node 1 turns
   inside the motions, so a child of node 1 takes the edit as a key at every frame the
   importer samples (GlbImporter.AnimFps); a child of node 0 beside node 1 takes it as is.
   Skinned vertices are world positions: they and the inverse binds move together, so the
   bone-local vertices the importer encodes stay the same.
   A card that draws geometry with node 1's own matrix (163: most of the model, 207: four
   vertices) cannot be edited: that geometry turns with node 1 in every motion, so it could
   only follow the body if node 1 does. Radius alone is still editable. */
internal static class ExtractedGlbEdit
{
    public static byte[] Apply(byte[] glb, float scale, Vector3 rotateDeg, Vector3 move, Vector3 pivot)
    {
        ModelRoot model = ModelRoot.ParseGLB(glb);
        Node node1 = NodeOne(model, out Node root, out Node node0);
        List<Node> underNode1 = node1.VisualChildren.Where(child => !IsScaleBone(child)).ToList();
        List<Node> besideNode1 = node0.VisualChildren.Where(child => child != node1 && !IsScaleBone(child)).ToList();

        // M in node 0's axes, row-vector order: scale, X, Y, Z turns (Matrix4.EulerAngles "XYZ") about
        // node 1 where the Pivot puts it (where the game turns and shrinks it), then move
        Vector3 turnAt = node1.LocalTransform.Translation + pivot;
        Matrix4x4 bodyMatrix = Matrix4x4.CreateTranslation(-turnAt)
                      * Matrix4x4.CreateScale(scale)
                      * Matrix4x4.CreateRotationX(rotateDeg.X * MathF.PI / 180f)
                      * Matrix4x4.CreateRotationY(rotateDeg.Y * MathF.PI / 180f)
                      * Matrix4x4.CreateRotationZ(rotateDeg.Z * MathF.PI / 180f)
                      * Matrix4x4.CreateTranslation(turnAt + move);
        bool bodyMoves = bodyMatrix != Matrix4x4.Identity;
        bool pivotMoves = pivot != Vector3.Zero;
        if ((bodyMoves || pivotMoves) && NodeOneDraws(model, node1))
        {
            throw new InvalidOperationException("this card draws part of the model with node 1, which an edit cannot move with the body");
        }
        Matrix4x4 pivotShift = Matrix4x4.CreateTranslation(pivot);

        // A child of node 1 keeps its world (in node 0's frame) times M while node 1 takes the pivot:
        // C' N1' = C N1 M, with N1' = N1 T(P).
        Matrix4x4 UnderNode1(Matrix4x4 childLocal, Matrix4x4 node1Local)
        {
            Matrix4x4.Invert(node1Local * pivotShift, out Matrix4x4 inverse);
            return childLocal * node1Local * bodyMatrix * inverse;
        }

        // Every new key is worked out from the glb as extracted, before anything is written.
        var writes = new List<Action>();
        foreach (Animation anim in model.LogicalAnimations)
        {
            int frames = (int)MathF.Round(anim.Duration * GlbImporter.AnimFps) + 1;
            Sampled node1Sampled = new Sampled(node1, anim);
            if (pivotMoves && node1Sampled.HasTranslation)
            {
                var keys = new SortedDictionary<float, Vector3>();
                for (int frame = 0; frame < frames; frame++)
                {
                    keys[frame / GlbImporter.AnimFps] = node1Sampled.TranslationAt(frame / GlbImporter.AnimFps) + pivot;
                }
                writes.Add(() => anim.CreateTranslationChannel(node1, keys, true));
            }
            if (bodyMoves || pivotMoves)
            {
                foreach (Node child in underNode1)
                {
                    Sampled childSampled = new Sampled(child, anim);
                    if (node1Sampled.HasTranslation || node1Sampled.HasRotation || childSampled.HasTranslation || childSampled.HasRotation)
                    {
                        writes.Add(KeyEveryFrame(anim, child, frames, time => UnderNode1(childSampled.Local(time), node1Sampled.Local(time))));
                    }
                }
            }
            if (bodyMoves)
            {
                foreach (Node child in besideNode1)
                {
                    Sampled childSampled = new Sampled(child, anim);
                    if (childSampled.HasTranslation || childSampled.HasRotation)
                    {
                        writes.Add(KeyEveryFrame(anim, child, frames, time => childSampled.Local(time) * bodyMatrix));
                    }
                }
            }
        }

        // the skin: the body's world at rest moves by W (scene space), vertices with it
        Matrix4x4 node0World = node0.LocalMatrix * root.LocalMatrix;
        Matrix4x4.Invert(node0World, out Matrix4x4 node0WorldInverse);
        Matrix4x4 sceneMatrix = node0WorldInverse * bodyMatrix * node0World;
        Matrix4x4.Invert(sceneMatrix, out Matrix4x4 sceneMatrixInverse);
        if (bodyMoves)
        {
            var done = new HashSet<int>();
            foreach (Node meshNode in model.LogicalNodes.Where(candidate => candidate.Skin != null && candidate.Mesh != null))
            {
                foreach (MeshPrimitive primitive in meshNode.Mesh.Primitives)
                {
                    Accessor? positions = primitive.GetVertexAccessor("POSITION");
                    if (positions != null && done.Add(positions.LogicalIndex))
                    {
                        var values = positions.AsVector3Array();
                        for (int i = 0; i < values.Count; i++)
                        {
                            values[i] = Vector3.Transform(values[i], sceneMatrix);
                        }
                        positions.UpdateBounds();
                    }
                    Accessor? normals = primitive.GetVertexAccessor("NORMAL");
                    if (normals != null && done.Add(normals.LogicalIndex))
                    {
                        var values = normals.AsVector3Array();
                        for (int i = 0; i < values.Count; i++)
                        {
                            Vector3 normal = Vector3.TransformNormal(values[i], sceneMatrix);
                            values[i] = normal == Vector3.Zero ? normal : Vector3.Normalize(normal);
                        }
                        normals.UpdateBounds();
                    }
                }
            }
            foreach (Skin skin in model.LogicalSkins)
            {
                var inverseBinds = skin.GetInverseBindMatricesAccessor().AsMatrix4x4Array();
                for (int i = 0; i < skin.JointsCount; i++)
                {
                    inverseBinds[i] = Affine(sceneMatrixInverse * inverseBinds[i]);
                }
            }
        }

        // rest pose
        Matrix4x4 node1Rest = node1.LocalMatrix;
        if (bodyMoves || pivotMoves)
        {
            foreach (Node childNode in underNode1)
            {
                SetLocal(childNode, UnderNode1(childNode.LocalMatrix, node1Rest));
            }
        }
        if (bodyMoves)
        {
            foreach (Node childNode in besideNode1)
            {
                SetLocal(childNode, childNode.LocalMatrix * bodyMatrix);
            }
        }
        if (pivotMoves)
        {
            SetLocal(node1, node1Rest * pivotShift);
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
        root = model.LogicalNodes.First(candidate => candidate.Name == "MrgRoot");
        node0 = root.VisualChildren.Single();
        return node0.VisualChildren.First(candidate => !IsScaleBone(candidate));
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
    private static bool IsScaleBone(Node node) =>
        node.VisualChildren.Count() == 0 && System.Text.RegularExpressions.Regex.IsMatch(node.Name ?? "", @"_bone(?:\.\d+)?$");

    // glTF wants an inverse bind's last column exactly (0, 0, 0, 1); products leave rounding there
    private static Matrix4x4 Affine(Matrix4x4 matrix)
    {
        matrix.M14 = 0f;
        matrix.M24 = 0f;
        matrix.M34 = 0f;
        matrix.M44 = 1f;
        return matrix;
    }

    private static void SetLocal(Node node, Matrix4x4 local)
    {
        Matrix4x4.Decompose(local, out Vector3 scale, out Quaternion rotation, out Vector3 translation);
        node.LocalTransform = new SharpGLTF.Transforms.AffineTransform(scale, Quaternion.Normalize(rotation), translation);
    }

    // translation and rotation keys at every frame; the scale stays the node's static one
    private static Action KeyEveryFrame(Animation anim, Node node, int frames, Func<float, Matrix4x4> local)
    {
        var translationKeys = new SortedDictionary<float, Vector3>();
        var rotationKeys = new SortedDictionary<float, Quaternion>();
        Quaternion previous = Quaternion.Identity;
        for (int frame = 0; frame < frames; frame++)
        {
            float time = frame / GlbImporter.AnimFps;
            Matrix4x4.Decompose(local(time), out _, out Quaternion rotation, out Vector3 translation);
            rotation = Quaternion.Normalize(rotation);
            // keep consecutive keys on one hemisphere so LINEAR slerps the short way
            if (frame > 0 && Quaternion.Dot(rotation, previous) < 0f)
            {
                rotation = Quaternion.Negate(rotation);
            }
            translationKeys[time] = translation;
            rotationKeys[time] = rotation;
            previous = rotation;
        }
        return () =>
        {
            anim.CreateTranslationChannel(node, translationKeys, true);
            anim.CreateRotationChannel(node, rotationKeys, true);
        };
    }

    // a node's local transform in one animation; its rest values where it has no channel
    private sealed class Sampled
    {
        private readonly Node node;
        private readonly SharpGLTF.Animations.ICurveSampler<Vector3>? translationSampler;
        private readonly SharpGLTF.Animations.ICurveSampler<Quaternion>? rotationSampler;
        private readonly SharpGLTF.Animations.ICurveSampler<Vector3>? scaleSampler;

        public Sampled(Node node, Animation anim)
        {
            this.node = node;
            translationSampler = anim.FindTranslationChannel(node)?.GetTranslationSampler()?.CreateCurveSampler();
            rotationSampler = anim.FindRotationChannel(node)?.GetRotationSampler()?.CreateCurveSampler();
            scaleSampler = anim.FindScaleChannel(node)?.GetScaleSampler()?.CreateCurveSampler();
        }

        public bool HasTranslation => translationSampler != null;
        public bool HasRotation => rotationSampler != null;

        public Vector3 TranslationAt(float time) => translationSampler != null ? translationSampler.GetPoint(time) : node.LocalTransform.Translation;

        public Matrix4x4 Local(float time)
        {
            Vector3 scale = scaleSampler != null ? scaleSampler.GetPoint(time) : node.LocalTransform.Scale;
            Quaternion rotation = rotationSampler != null ? rotationSampler.GetPoint(time) : node.LocalTransform.Rotation;
            return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(TranslationAt(time));
        }
    }
}
