using System.Numerics;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;
using SkiaSharp;

namespace DotrAssetModder;
// Mostly AI

/* A finished Scene -> the .glb DotrAssetModder's custom-model import reads, a port of
   tools/toglb.py's png_bytes and write_glb (DotR-PC), written with SharpGLTF. The glb comes
   back as bytes; nothing is written to disk here. */

public sealed class GlbAnimInfo
{
    public string Name;
    public int Frames;

    public GlbAnimInfo(string name, int frames)
    {
        Name = name;
        Frames = frames;
    }
}

public sealed class GlbWriteInfo
{
    public byte[] Glb = new byte[0];
    public int Nodes;
    public int Joints;
    public int Textures;
    public int FlatColourMaterials;
    public int Triangles;
    public int Vertices;
    public List<GlbAnimInfo> Animations = new List<GlbAnimInfo>();
}

public static class SceneGlbWriter
{
    // A PNG of the image, at most max_tex pixels a side. An image whose alpha is all opaque
    // loses its alpha channel, as Pillow's convert("RGB") does.
    public static byte[] PngBytes(SKBitmap sourceBitmap, int maxTextureSize)
    {
        SKBitmap bitmap = sourceBitmap;
        bool opaque = true;
        for (int y = 0; y < bitmap.Height && opaque; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha != 255)
                {
                    opaque = false;
                    break;
                }
            }
        }
        int width = bitmap.Width;
        int height = bitmap.Height;
        if (Math.Max(width, height) > maxTextureSize)
        {
            // Pillow's thumbnail: the largest side becomes max_tex, the other keeps the aspect
            // ratio (floored or ceiled, whichever is nearer), and never drops below 1.
            double aspect = (double)width / height;
            width = maxTextureSize;
            height = maxTextureSize;
            if ((double)width / height >= aspect)
            {
                width = RoundAspect(height * aspect, height, aspect, true);
            }
            else
            {
                height = RoundAspect(width / aspect, width, aspect, false);
            }
        }
        if (width != bitmap.Width || height != bitmap.Height)
        {
            SKBitmap resized = bitmap.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                SKFilterQuality.High);
            bitmap = resized;
        }
        if (opaque)
        {
            SKBitmap opaqueCopy = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            bitmap.CopyTo(opaqueCopy, SKColorType.Rgba8888);
            bitmap = opaqueCopy;
        }
        SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // Pillow's round_aspect: floor or ceil, whichever comes nearer the aspect ratio, at least 1.
    static int RoundAspect(double number, int other, double aspect, bool widthSide)
    {
        int lower = (int)Math.Floor(number);
        int upper = (int)Math.Ceiling(number);
        double lowerError = AspectError(lower, other, aspect, widthSide);
        double upperError = AspectError(upper, other, aspect, widthSide);
        int best = lower;
        if (upperError < lowerError)
        {
            best = upper;
        }
        if (best < 1)
        {
            best = 1;
        }
        return best;
    }

    static double AspectError(int size, int other, double aspect, bool widthSide)
    {
        if (widthSide)
        {
            return Math.Abs(aspect - (double)size / other);
        }
        if (size == 0)
        {
            return 0;
        }
        return Math.Abs(aspect - (double)other / size);
    }

    // A rigid mesh's node and the meshes on it, in the order the meshes were read.
    sealed class RigidGroup
    {
        public int Node;
        public List<SceneMesh> Meshes = new List<SceneMesh>();
    }

    public static GlbWriteInfo WriteGlb(Scene scene, int maxTextureSize, bool flipUv, double[]? pivotAt = null)
    {
        int nodeCount = scene.Nodes.Count;
        List<int> order = scene.Order();
        List<int>[] children = new List<int>[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            children[i] = new List<int>();
        }
        foreach (int i in order)
        {
            if (scene.Nodes[i].Parent >= 0)
            {
                children[scene.Nodes[i].Parent].Add(i);
            }
        }
        HashSet<int> joints = new HashSet<int>();
        List<RigidGroup> rigidGroups = new List<RigidGroup>();
        List<SceneMesh> skinnedMeshes = new List<SceneMesh>();
        foreach (SceneMesh mesh in scene.Meshes)
        {
            if (mesh.Skinned)
            {
                skinnedMeshes.Add(mesh);
                foreach (int jointNode in SceneFinish.UniqueJoints(mesh))
                {
                    joints.Add(jointNode);
                }
            }
            else
            {
                RigidGroup? nodeGroup = null;
                foreach (RigidGroup candidate in rigidGroups)
                {
                    if (candidate.Node == mesh.Node)
                    {
                        nodeGroup = candidate;
                    }
                }
                if (nodeGroup == null)
                {
                    nodeGroup = new RigidGroup();
                    nodeGroup.Node = mesh.Node;
                    rigidGroups.Add(nodeGroup);
                }
                nodeGroup.Meshes.Add(mesh);
            }
        }
        bool[] isRigidNode = new bool[nodeCount];
        foreach (RigidGroup rigidGroup in rigidGroups)
        {
            isRigidNode[rigidGroup.Node] = true;
        }
        bool[] keepNode = new bool[nodeCount];
        for (int k = order.Count - 1; k >= 0; k--)
        {
            int i = order[k];
            bool anyKeptChild = false;
            foreach (int child in children[i])
            {
                if (keepNode[child])
                {
                    anyKeptChild = true;
                }
            }
            keepNode[i] = joints.Contains(i) || isRigidNode[i] || anyKeptChild;
        }

        ModelRoot model = ModelRoot.CreateModel();
        model.Asset.Generator = "DotrAssetModder";
        /* dotr_root: dropped by the importer. dotr_world: node 0, the world placement.
           dotr_pivot: node 1, the pivot. Both at the source's own origin, unless `pivotAt`
           moves the pivot; dotr_model then takes the opposite move so the body stays where it
           is around node 0.
           dotr_model: the holder of everything else; it needs two children or more, or the
           importer's wrapper peel walks past it and takes the wrong pair as node 0 and node 1.
           Nodes are made parents first, in Scene.Order, so their indices and child order are
           the scene's. */
        Node root = model.UseScene(0).CreateNode("dotr_root");
        Node world = root.CreateNode("dotr_world");
        Node pivot = world.CreateNode("dotr_pivot");
        Node holder = pivot.CreateNode("dotr_model");
        if (pivotAt != null && (pivotAt[0] != 0 || pivotAt[1] != 0 || pivotAt[2] != 0))
        {
            Vector3 pivotPosition = ToVector3(pivotAt, 0);
            pivot.LocalTransform = new SharpGLTF.Transforms.AffineTransform(null, null, pivotPosition);
            holder.LocalTransform = new SharpGLTF.Transforms.AffineTransform(null, null, -pivotPosition);
        }
        Node?[] glbNodes = new Node?[nodeCount];
        double[] translation = new double[3];
        double[] rotation = new double[4];
        double[] scale = new double[3];
        foreach (int i in order)
        {
            if (!keepNode[i])
            {
                continue;
            }
            Node parent = holder;
            if (scene.Nodes[i].Parent >= 0)
            {
                parent = glbNodes[scene.Nodes[i].Parent]!;
            }
            Node glbNode = parent.CreateNode(scene.Nodes[i].Name);
            scene.Nodes[i].Local.Decompose(translation, rotation, scale);
            glbNode.LocalTransform = new SharpGLTF.Transforms.AffineTransform(ToVector3(scale, 0), ToQuaternion(rotation, 0), ToVector3(translation, 0));
            glbNodes[i] = glbNode;
        }

        Material?[] glbMaterials = new Material?[scene.Materials.Count];
        List<byte[]> texturePngs = new List<byte[]>();
        List<Material> textureMaterials = new List<Material>();
        List<double[]> colourKeys = new List<double[]>();
        List<Material> colourMaterials = new List<Material>();
        Dictionary<SKBitmap, byte[]> pngCache = new Dictionary<SKBitmap, byte[]>();
        List<int> usedMaterials = new List<int>();
        foreach (SceneMesh mesh in scene.Meshes)
        {
            HashSet<int> seenMaterials = new HashSet<int>();
            foreach (int materialIndex in mesh.TriangleMaterials)
            {
                if (!seenMaterials.Add(materialIndex))
                {
                    continue;
                }
                if (!usedMaterials.Contains(materialIndex))
                {
                    usedMaterials.Add(materialIndex);
                }
            }
        }
        foreach (int materialIndex in usedMaterials)
        {
            SceneMaterial sceneMaterial = scene.Materials[materialIndex];
            if (sceneMaterial.Image != null)
            {
                byte[]? png;
                if (!pngCache.TryGetValue(sceneMaterial.Image, out png))
                {
                    png = PngBytes(sceneMaterial.Image, maxTextureSize);
                    pngCache[sceneMaterial.Image] = png;
                }
                Material? existingMaterial = null;
                for (int k = 0; k < texturePngs.Count; k++)
                {
                    if (SameBytes(texturePngs[k], png!))
                    {
                        existingMaterial = textureMaterials[k];
                    }
                }
                if (existingMaterial != null)
                {
                    glbMaterials[materialIndex] = existingMaterial;
                    continue;
                }
                int imageNumber = texturePngs.Count;
                Image glbImage = model.CreateImage($"tex{imageNumber}");
                glbImage.Content = new MemoryImage(png!);
                // no sampler: the importer and viewer take glTF's default, repeat
                Texture glbTexture = model.UseTexture(glbImage, (TextureSampler)null!);
                Material glbMaterial = NewMaterial(model, $"mat_tex{imageNumber}");
                glbMaterial.FindChannel("BaseColor")!.Value.SetTexture(0, glbTexture);
                texturePngs.Add(png!);
                textureMaterials.Add(glbMaterial);
                glbMaterials[materialIndex] = glbMaterial;
            }
            else
            {
                double[] key = new double[4];
                for (int k = 0; k < 4; k++)
                {
                    key[k] = MeshBuild.RoundHalfToEven(sceneMaterial.Color[k], 3);
                }
                Material? existingMaterial = null;
                for (int k = 0; k < colourKeys.Count; k++)
                {
                    if (SameColour(colourKeys[k], key))
                    {
                        existingMaterial = colourMaterials[k];
                    }
                }
                if (existingMaterial != null)
                {
                    glbMaterials[materialIndex] = existingMaterial;
                    continue;
                }
                Material glbMaterial = NewMaterial(model, $"mat_color{colourKeys.Count}");
                MaterialChannel baseColor = glbMaterial.FindChannel("BaseColor")!.Value;
                baseColor.Color = new Vector4((float)key[0], (float)key[1], (float)key[2], (float)key[3]);
                colourKeys.Add(key);
                colourMaterials.Add(glbMaterial);
                glbMaterials[materialIndex] = glbMaterial;
            }
        }

        List<int> jointList = new List<int>();
        foreach (int i in order)
        {
            if (joints.Contains(i))
            {
                jointList.Add(i);
            }
        }
        int[] jointSlot = new int[nodeCount + 1]; // node -> skin joint index; index n: no influence
        for (int k = 0; k < jointList.Count; k++)
        {
            jointSlot[jointList[k]] = k;
        }

        foreach (RigidGroup rigidGroup in rigidGroups)
        {
            Mesh glbMesh = model.CreateMesh(rigidGroup.Meshes[0].Name);
            Primitives(model, glbMesh, rigidGroup.Meshes, false, jointSlot, glbMaterials, nodeCount, flipUv);
            glbNodes[rigidGroup.Node]!.Mesh = glbMesh;
        }
        if (skinnedMeshes.Count > 0)
        {
            Matrix4[] nodeWorld = new Matrix4[nodeCount];
            foreach (int i in order)
            {
                int parentIndex = scene.Nodes[i].Parent;
                if (parentIndex >= 0)
                {
                    nodeWorld[i] = nodeWorld[parentIndex] * scene.Nodes[i].Local;
                }
                else
                {
                    nodeWorld[i] = scene.Nodes[i].Local;
                }
            }
            List<(Node, Matrix4x4)> jointBinds = new List<(Node, Matrix4x4)>();
            foreach (int jointNode in jointList)
            {
                jointBinds.Add((glbNodes[jointNode]!, ToNumerics(nodeWorld[jointNode].Inverse())));
            }
            Skin skin = model.CreateSkin();
            skin.BindJoints(jointBinds);
            Node skinnedNode = holder.CreateNode("dotr_skinned");
            Mesh glbMesh = model.CreateMesh("skinned");
            Primitives(model, glbMesh, skinnedMeshes, true, jointSlot, glbMaterials, nodeCount, flipUv);
            skinnedNode.Mesh = glbMesh;
            skinnedNode.Skin = skin;
        }

        if (holder.VisualChildren.Count() < 2)
        {
            holder.CreateNode("dotr_anchor");
        }

        List<GlbAnimInfo> animInfo = new List<GlbAnimInfo>();
        foreach (SceneAnim anim in scene.Anims)
        {
            List<int> tracks = new List<int>();
            foreach (int i in SceneFinish.TrackNodes(anim))
            {
                if (keepNode[i])
                {
                    tracks.Add(i);
                }
            }
            if (tracks.Count == 0)
            {
                continue;
            }
            float[] times = new float[anim.Frames];
            for (int frame = 0; frame < anim.Frames; frame++)
            {
                times[frame] = (float)((double)frame / Scene.Fps);
            }
            Animation glbAnimation = model.CreateAnimation(anim.Name);
            bool anyChannel = false;
            foreach (int i in tracks)
            {
                Matrix4[] track = anim.Tracks[i];
                int frames = track.Length;
                double[] translations = new double[frames * 3];
                double[] rotations = new double[frames * 4];
                double[] scales = new double[frames * 3];
                for (int frame = 0; frame < frames; frame++)
                {
                    track[frame].Decompose(translation, rotation, scale);
                    for (int k = 0; k < 3; k++)
                    {
                        translations[frame * 3 + k] = translation[k];
                        scales[frame * 3 + k] = scale[k];
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        rotations[frame * 4 + k] = rotation[k];
                    }
                }
                for (int frame = 1; frame < frames; frame++)
                {
                    double dotProduct = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        dotProduct += rotations[frame * 4 + k] * rotations[(frame - 1) * 4 + k];
                    }
                    if (dotProduct < 0)
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            rotations[frame * 4 + k] = -rotations[frame * 4 + k];
                        }
                    }
                }
                double[] restTranslation = new double[3];
                double[] restRotation = new double[4];
                double[] restScale = new double[3];
                scene.Nodes[i].Local.Decompose(restTranslation, restRotation, restScale);
                /* A channel that never leaves the rest value says nothing (the importer reads the
                   rest value where there is no channel). The animation's first channel is kept
                   either way, so the animation keeps its length even when nothing moves. */
                bool movesTranslation = !CloseTo(translations, restTranslation, 3, 1e-5);
                bool movesScale = !CloseTo(scales, restScale, 3, 1e-6);
                bool movesRotation = false;
                for (int frame = 0; frame < frames; frame++)
                {
                    double dotProduct = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        dotProduct += rotations[frame * 4 + k] * restRotation[k];
                    }
                    if (Math.Abs(Math.Abs(dotProduct) - 1.0) > 1e-10)
                    {
                        movesRotation = true;
                    }
                }
                Node target = glbNodes[i]!;
                if (movesTranslation || !anyChannel)
                {
                    Dictionary<float, Vector3> translationKeys = new Dictionary<float, Vector3>();
                    for (int frame = 0; frame < frames; frame++)
                    {
                        translationKeys[times[frame]] = ToVector3(translations, frame * 3);
                    }
                    glbAnimation.CreateTranslationChannel(target, translationKeys, true);
                    anyChannel = true;
                }
                if (movesRotation)
                {
                    Dictionary<float, Quaternion> rotationKeys = new Dictionary<float, Quaternion>();
                    for (int frame = 0; frame < frames; frame++)
                    {
                        rotationKeys[times[frame]] = ToQuaternion(rotations, frame * 4);
                    }
                    glbAnimation.CreateRotationChannel(target, rotationKeys, true);
                }
                if (movesScale)
                {
                    Dictionary<float, Vector3> scaleKeys = new Dictionary<float, Vector3>();
                    for (int frame = 0; frame < frames; frame++)
                    {
                        scaleKeys[times[frame]] = ToVector3(scales, frame * 3);
                    }
                    glbAnimation.CreateScaleChannel(target, scaleKeys, true);
                }
            }
            animInfo.Add(new GlbAnimInfo(anim.Name, anim.Frames));
        }

        GlbWriteInfo info = new GlbWriteInfo();
        info.Nodes = model.LogicalNodes.Count;
        info.Joints = jointList.Count;
        info.Textures = texturePngs.Count;
        info.FlatColourMaterials = colourKeys.Count;
        foreach (SceneMesh mesh in scene.Meshes)
        {
            info.Triangles += mesh.Triangles.Length / 3;
            info.Vertices += mesh.VertexCount;
        }
        info.Animations = animInfo;
        info.Glb = model.WriteGLB().ToArray();
        return info;
    }

    // A double-sided material, not metallic and fully rough, as toglb.py writes them.
    static Material NewMaterial(ModelRoot model, string name)
    {
        Material glbMaterial = model.CreateMaterial(name);
        glbMaterial.InitializePBRMetallicRoughness();
        glbMaterial.DoubleSided = true;
        MaterialChannel metallicRoughness = glbMaterial.FindChannel("MetallicRoughness")!.Value;
        metallicRoughness.SetFactor("MetallicFactor", 0f);
        metallicRoughness.SetFactor("RoughnessFactor", 1f);
        return glbMaterial;
    }

    static void Primitives(ModelRoot model, Mesh mesh, List<SceneMesh> meshes, bool skin, int[] jointSlot,
        Material?[] glbMaterials, int noInfluenceSlot, bool flipUv)
    {
        foreach (SceneMesh sceneMesh in meshes)
        {
            SortedSet<int> materialSet = new SortedSet<int>();
            foreach (int materialIndex in sceneMesh.TriangleMaterials)
            {
                materialSet.Add(materialIndex);
            }
            foreach (int materialIndex in materialSet)
            {
                List<int> triangleCorners = new List<int>();
                for (int triangle = 0; triangle < sceneMesh.TriangleMaterials.Length; triangle++)
                {
                    if (sceneMesh.TriangleMaterials[triangle] == materialIndex)
                    {
                        triangleCorners.Add(sceneMesh.Triangles[triangle * 3]);
                        triangleCorners.Add(sceneMesh.Triangles[triangle * 3 + 1]);
                        triangleCorners.Add(sceneMesh.Triangles[triangle * 3 + 2]);
                    }
                }
                SortedSet<int> uniqueVertices = new SortedSet<int>(triangleCorners);
                int[] usedVertices = new int[uniqueVertices.Count];
                Dictionary<int, int> newIndexOf = new Dictionary<int, int>();
                int vertexCursor = 0;
                foreach (int vertex in uniqueVertices)
                {
                    usedVertices[vertexCursor] = vertex;
                    newIndexOf[vertex] = vertexCursor;
                    vertexCursor++;
                }
                int[] remappedCorners = new int[triangleCorners.Count];
                for (int k = 0; k < triangleCorners.Count; k++)
                {
                    remappedCorners[k] = newIndexOf[triangleCorners[k]];
                }
                double[] positions = new double[usedVertices.Length * 3];
                double[] normals = new double[usedVertices.Length * 3];
                double[] uvs = new double[usedVertices.Length * 2];
                for (int k = 0; k < usedVertices.Length; k++)
                {
                    for (int component = 0; component < 3; component++)
                    {
                        positions[k * 3 + component] = sceneMesh.Positions[usedVertices[k] * 3 + component];
                        normals[k * 3 + component] = sceneMesh.Normals[usedVertices[k] * 3 + component];
                    }
                    uvs[k * 2] = sceneMesh.Uvs[usedVertices[k] * 2];
                    uvs[k * 2 + 1] = sceneMesh.Uvs[usedVertices[k] * 2 + 1];
                    // FBX and COLLADA put the UV origin bottom-left, glTF top-left; --flip-uv is
                    // for sources already written top-left (Unity rips, e.g. Duel Links FBX)
                    if (!flipUv)
                    {
                        uvs[k * 2 + 1] = 1.0 - uvs[k * 2 + 1];
                    }
                }
                MeshPrimitive primitive = mesh.CreatePrimitive();
                primitive.SetVertexAccessor("POSITION", FloatAccessor(model, positions, DimensionType.VEC3, 3));
                primitive.SetVertexAccessor("NORMAL", FloatAccessor(model, normals, DimensionType.VEC3, 3));
                primitive.SetVertexAccessor("TEXCOORD_0", FloatAccessor(model, uvs, DimensionType.VEC2, 2));
                if (skin)
                {
                    int[] joints0 = new int[usedVertices.Length * 4];
                    int[] joints1 = new int[usedVertices.Length * 4];
                    double[] weights0 = new double[usedVertices.Length * 4];
                    double[] weights1 = new double[usedVertices.Length * 4];
                    bool needsSecondSet = false;
                    for (int k = 0; k < usedVertices.Length; k++)
                    {
                        for (int influence = 0; influence < 8; influence++)
                        {
                            int jointNode = sceneMesh.Joints![usedVertices[k] * 8 + influence];
                            double w = sceneMesh.Weights![usedVertices[k] * 8 + influence];
                            int skinSlot = jointSlot[noInfluenceSlot];
                            if (jointNode >= 0)
                            {
                                skinSlot = jointSlot[jointNode];
                            }
                            else
                            {
                                w = 0.0;
                            }
                            if (influence < 4)
                            {
                                joints0[k * 4 + influence] = skinSlot;
                                weights0[k * 4 + influence] = w;
                            }
                            else
                            {
                                joints1[k * 4 + influence - 4] = skinSlot;
                                weights1[k * 4 + influence - 4] = w;
                                if (w > 0)
                                {
                                    needsSecondSet = true;
                                }
                            }
                        }
                    }
                    primitive.SetVertexAccessor("JOINTS_0", JointAccessor(model, joints0));
                    primitive.SetVertexAccessor("WEIGHTS_0", FloatAccessor(model, weights0, DimensionType.VEC4, 4));
                    if (needsSecondSet)
                    {
                        primitive.SetVertexAccessor("JOINTS_1", JointAccessor(model, joints1));
                        primitive.SetVertexAccessor("WEIGHTS_1", FloatAccessor(model, weights1, DimensionType.VEC4, 4));
                    }
                }
                primitive.Material = glbMaterials[materialIndex];
                primitive.SetIndexAccessor(IndexAccessor(model, remappedCorners));
            }
        }
    }

    static Accessor FloatAccessor(ModelRoot model, double[] data, DimensionType dimension, int components)
    {
        byte[] bytes = new byte[data.Length * 4];
        for (int k = 0; k < data.Length; k++)
        {
            BitConverter.TryWriteBytes(new Span<byte>(bytes, k * 4, 4), (float)data[k]);
        }
        Accessor accessor = model.CreateAccessor();
        accessor.SetVertexData(model.UseBufferView(bytes, 0, null, 0, BufferMode.ARRAY_BUFFER), 0,
            data.Length / components, dimension, EncodingType.FLOAT, false);
        return accessor;
    }

    // JOINTS_n: four skin joint indices a vertex, as unsigned shorts.
    static Accessor JointAccessor(ModelRoot model, int[] data)
    {
        byte[] bytes = new byte[data.Length * 2];
        for (int k = 0; k < data.Length; k++)
        {
            BitConverter.TryWriteBytes(new Span<byte>(bytes, k * 2, 2), (ushort)data[k]);
        }
        Accessor accessor = model.CreateAccessor();
        accessor.SetVertexData(model.UseBufferView(bytes, 0, null, 0, BufferMode.ARRAY_BUFFER), 0,
            data.Length / 4, DimensionType.VEC4, EncodingType.UNSIGNED_SHORT, false);
        return accessor;
    }

    static Accessor IndexAccessor(ModelRoot model, int[] data)
    {
        byte[] bytes = new byte[data.Length * 4];
        for (int k = 0; k < data.Length; k++)
        {
            BitConverter.TryWriteBytes(new Span<byte>(bytes, k * 4, 4), (uint)data[k]);
        }
        Accessor accessor = model.CreateAccessor();
        accessor.SetIndexData(model.UseBufferView(bytes, 0, null, 0, BufferMode.ELEMENT_ARRAY_BUFFER), 0,
            data.Length, IndexEncodingType.UNSIGNED_INT);
        return accessor;
    }

    static Vector3 ToVector3(double[] values, int offset)
    {
        return new Vector3((float)values[offset], (float)values[offset + 1], (float)values[offset + 2]);
    }

    static Quaternion ToQuaternion(double[] values, int offset)
    {
        return new Quaternion((float)values[offset], (float)values[offset + 1], (float)values[offset + 2], (float)values[offset + 3]);
    }

    // Matrix4 keeps the translation in the right column; System.Numerics in the bottom row.
    static Matrix4x4 ToNumerics(Matrix4 matrix)
    {
        return new Matrix4x4(
            (float)matrix[0, 0], (float)matrix[1, 0], (float)matrix[2, 0], (float)matrix[3, 0],
            (float)matrix[0, 1], (float)matrix[1, 1], (float)matrix[2, 1], (float)matrix[3, 1],
            (float)matrix[0, 2], (float)matrix[1, 2], (float)matrix[2, 2], (float)matrix[3, 2],
            (float)matrix[0, 3], (float)matrix[1, 3], (float)matrix[2, 3], (float)matrix[3, 3]);
    }

    // numpy allclose with rtol 0: every frame's components against the rest value.
    static bool CloseTo(double[] data, double[] restValues, int components, double absoluteTolerance)
    {
        for (int k = 0; k < data.Length; k++)
        {
            if (Math.Abs(data[k] - restValues[k % components]) > absoluteTolerance)
            {
                return false;
            }
        }
        return true;
    }

    static bool SameBytes(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }
        for (int k = 0; k < left.Length; k++)
        {
            if (left[k] != right[k])
            {
                return false;
            }
        }
        return true;
    }

    static bool SameColour(double[] left, double[] right)
    {
        for (int k = 0; k < left.Length; k++)
        {
            if (left[k] != right[k])
            {
                return false;
            }
        }
        return true;
    }
}
