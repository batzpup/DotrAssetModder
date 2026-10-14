using System.Numerics;
using SharpGLTF.Schema2;
using SkiaSharp;

namespace DotrAssetModder;
// Mostly AI

/* glTF source -> Scene, a port of tools/toglb.py's read_glb (DotR-PC). The file is parsed by
   SharpGLTF; what is kept from the port is the Scene it fills and the 30 fps resampling
   (slerp, gltf_sample). */
public static class GltfSourceReader
{
    // A 1x1 PNG handed to SharpGLTF for an image file that is not where the glTF says, so the
    // rest of the model still loads; a material whose image is this has no texture.
    static readonly byte[] MissingImage = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");

    static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tga", ".dds", ".ktx2" };

    /* .glb or .gltf with embedded or side-by-side buffers. Rips are often not valid glTF, so
       nothing is validated; an image file is also looked for beside the model and up to two
       folders below it (SourceImages), and one that is not found anywhere gets MissingImage. */
    static ModelRoot Load(string path, SourceImages images, Action<string> warn)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        ReadContext context = ReadContext.Create(name =>
        {
            string file = Path.Combine(directory, Uri.UnescapeDataString(name));
            if (File.Exists(file))
            {
                return new ArraySegment<byte>(File.ReadAllBytes(file));
            }
            if (!ImageExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
            {
                throw new FileNotFoundException("glTF file not found: " + name, file);
            }
            string? found = images.Locate(Uri.UnescapeDataString(name));
            if (found != null)
            {
                return new ArraySegment<byte>(File.ReadAllBytes(found));
            }
            warn("texture not found: " + Uri.UnescapeDataString(name));
            return new ArraySegment<byte>(MissingImage);
        });
        context = context.WithSettingsFrom(new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip });
        return context.ReadSchema2(Path.GetFileName(path));
    }

    // System.Numerics keeps the translation in the bottom row; Matrix4 in the right column.
    static Matrix4 ToMat4(Matrix4x4 matrix)
    {
        float[] elements = {
            matrix.M11, matrix.M12, matrix.M13, matrix.M14, matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34, matrix.M41, matrix.M42, matrix.M43, matrix.M44,
        };
        Matrix4 result = new Matrix4();
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                result[row, column] = elements[column * 4 + row];
            }
        }
        return result;
    }

    // ---- slerp ----

    static void Slerp(double[] quaternions0, int offset0, double[] quaternions1, int offset1, double fraction, double[] output, int outputOffset)
    {
        double dot = 0;
        for (int k = 0; k < 4; k++)
        {
            dot += quaternions0[offset0 + k] * quaternions1[offset1 + k];
        }
        double sign = 1.0;
        if (dot < 0)
        {
            sign = -1.0;
        }
        dot = Math.Abs(dot);
        double clamped = dot;
        if (clamped > 1.0)
        {
            clamped = 1.0;
        }
        if (clamped < -1.0)
        {
            clamped = -1.0;
        }
        double theta = Math.Acos(clamped);
        double weight0;
        double weight1;
        if (dot > 0.9995)
        {
            weight0 = 1 - fraction;
            weight1 = fraction;
        }
        else
        {
            double sinTheta = Math.Sin(theta);
            weight0 = Math.Sin((1 - fraction) * theta) / sinTheta;
            weight1 = Math.Sin(fraction * theta) / sinTheta;
        }
        double length = 0;
        for (int k = 0; k < 4; k++)
        {
            double x = weight0 * quaternions0[offset0 + k] + weight1 * sign * quaternions1[offset1 + k];
            output[outputOffset + k] = x;
            length += x * x;
        }
        length = Math.Sqrt(length);
        for (int k = 0; k < 4; k++)
        {
            output[outputOffset + k] /= length;
        }
    }

    // ---- gltf_sample ----

    // A glTF animation sampler evaluated at sampleTimes (STEP, LINEAR, CUBICSPLINE). `values` holds
    // `width` values a key, or for CUBICSPLINE in-tangent, value, out-tangent a key.
    static double[] SampleKeys(double[] times, double[] values, int width, string interpolation, double[] sampleTimes, bool rotation)
    {
        int k = times.Length;
        int keyStride = width;        // one key's stride in values
        int valueBase = 0;        // the value's offset inside a key
        int inTangentBase = 0;
        int outTangentBase = 0;
        bool cubic = interpolation == "CUBICSPLINE";
        if (cubic)
        {
            keyStride = 3 * width;
            inTangentBase = 0;
            valueBase = width;
            outTangentBase = 2 * width;
        }
        double[] output = new double[sampleTimes.Length * width];
        if (k == 1)
        {
            for (int frame = 0; frame < sampleTimes.Length; frame++)
            {
                for (int component = 0; component < width; component++)
                {
                    output[frame * width + component] = values[valueBase + component];
                }
            }
            return output;
        }
        for (int frame = 0; frame < sampleTimes.Length; frame++)
        {
            int segment = SearchRight(times, sampleTimes[frame]) - 1;
            if (segment < 0)
            {
                segment = 0;
            }
            if (segment > k - 2)
            {
                segment = k - 2;
            }
            double time0 = times[segment];
            double time1 = times[segment + 1];
            double duration = 1.0;
            if (time1 > time0)
            {
                duration = time1 - time0;
            }
            double fraction = (sampleTimes[frame] - time0) / duration;
            if (fraction < 0.0)
            {
                fraction = 0.0;
            }
            if (fraction > 1.0)
            {
                fraction = 1.0;
            }
            int value0Offset = segment * keyStride + valueBase;
            int value1Offset = (segment + 1) * keyStride + valueBase;
            if (interpolation == "STEP")
            {
                int sourceOffset = value0Offset;
                if (fraction >= 1.0)
                {
                    sourceOffset = value1Offset;
                }
                for (int component = 0; component < width; component++)
                {
                    output[frame * width + component] = values[sourceOffset + component];
                }
            }
            else if (cubic)
            {
                double fractionSquared = fraction * fraction;
                double fractionCubed = fraction * fraction * fraction;
                int outTangentOffset = segment * keyStride + outTangentBase;
                int inTangentOffset = (segment + 1) * keyStride + inTangentBase;
                for (int component = 0; component < width; component++)
                {
                    output[frame * width + component] = (2 * fractionCubed - 3 * fractionSquared + 1) * values[value0Offset + component] + (fractionCubed - 2 * fractionSquared + fraction) * values[outTangentOffset + component] * duration
                        + (-2 * fractionCubed + 3 * fractionSquared) * values[value1Offset + component] + (fractionCubed - fractionSquared) * values[inTangentOffset + component] * duration;
                }
            }
            else if (rotation)
            {
                Slerp(values, value0Offset, values, value1Offset, fraction, output, frame * width);
            }
            else
            {
                for (int component = 0; component < width; component++)
                {
                    output[frame * width + component] = (1 - fraction) * values[value0Offset + component] + fraction * values[value1Offset + component];
                }
            }
        }
        if (rotation)
        {
            for (int frame = 0; frame < sampleTimes.Length; frame++)
            {
                double length = 0;
                for (int component = 0; component < width; component++)
                {
                    length += output[frame * width + component] * output[frame * width + component];
                }
                length = Math.Sqrt(length);
                for (int component = 0; component < width; component++)
                {
                    output[frame * width + component] /= length;
                }
            }
        }
        return output;
    }

    // np.searchsorted(times, x, side="right"): how many times are <= x
    static int SearchRight(double[] times, double x)
    {
        int low = 0;
        int high = times.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (times[middle] <= x)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    // One channel's keys as gltf_sample takes them: the times, and the values flattened `w` a
    // key (in-tangent, value, out-tangent a key for CUBICSPLINE).
    static void ReadKeys<T>(IAnimationSampler<T> sampler, Func<T, double[]> toRow, out double[] times, out double[] values, out string interpolation)
    {
        List<double> timeList = new List<double>();
        List<double> valueList = new List<double>();
        interpolation = sampler.InterpolationMode.ToString();
        if (sampler.InterpolationMode == AnimationInterpolationMode.CUBICSPLINE)
        {
            foreach ((float time, (T inTangent, T value, T outTangent)) in sampler.GetCubicKeys())
            {
                timeList.Add(time);
                valueList.AddRange(toRow(inTangent));
                valueList.AddRange(toRow(value));
                valueList.AddRange(toRow(outTangent));
            }
        }
        else
        {
            foreach ((float time, T value) in sampler.GetLinearKeys())
            {
                timeList.Add(time);
                valueList.AddRange(toRow(value));
            }
        }
        times = timeList.ToArray();
        values = valueList.ToArray();
    }

    static double[] Vector3Row(Vector3 vector)
    {
        return new double[] { vector.X, vector.Y, vector.Z };
    }

    static double[] QuaternionRow(Quaternion rotation)
    {
        return new double[] { rotation.X, rotation.Y, rotation.Z, rotation.W };
    }

    // ---- read_glb ----

    // A glTF scene as it is: glTF is already +Y up, +Z front and in metres.
    public static Scene Read(string path, Action<string> warn)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        SourceImages images = new SourceImages(directory, warn);
        ModelRoot model = Load(path, images, warn);
        Scene scene = new Scene();
        IReadOnlyList<Node> glbNodes = model.LogicalNodes;
        // per glTF node: translation, rotation, scale (a matrix node is decomposed)
        List<double[]> restTranslations = new List<double[]>();
        List<double[]> restRotations = new List<double[]>();
        List<double[]> restScales = new List<double[]>();
        for (int k = 0; k < glbNodes.Count; k++)
        {
            Node glbNode = glbNodes[k];
            string name = glbNode.Name ?? "";
            if (name.Length == 0)
            {
                name = "node" + k;
            }
            scene.AddNode(name);
            var localTransform = glbNode.LocalTransform;
            double[] translation;
            double[] rotation;
            double[] scaling;
            if (localTransform.IsMatrix)
            {
                Matrix4 localMatrix = ToMat4(localTransform.Matrix);
                scene.Nodes[k].Local = localMatrix;
                translation = new double[3];
                rotation = new double[4];
                scaling = new double[3];
                localMatrix.Decompose(translation, rotation, scaling);
            }
            else
            {
                translation = Vector3Row(localTransform.Translation);
                rotation = QuaternionRow(localTransform.Rotation);
                scaling = Vector3Row(localTransform.Scale);
                scene.Nodes[k].Local = Matrix4.FromTrs(translation, rotation, scaling);
            }
            restTranslations.Add(translation);
            restRotations.Add(rotation);
            restScales.Add(scaling);
        }
        for (int k = 0; k < glbNodes.Count; k++)
        {
            foreach (Node child in glbNodes[k].VisualChildren)
            {
                scene.Nodes[child.LogicalIndex].Parent = k;
            }
        }
        // nodes outside the scene shown are left out by giving them no meshes or joints
        HashSet<int> shown = new HashSet<int>();
        Stack<Node> stack = new Stack<Node>();
        if (model.DefaultScene != null)
        {
            foreach (Node sceneRoot in model.DefaultScene.VisualChildren)
            {
                stack.Push(sceneRoot);
            }
        }
        else
        {
            foreach (Node candidate in glbNodes)
            {
                stack.Push(candidate);
            }
        }
        while (stack.Count > 0)
        {
            Node visited = stack.Pop();
            shown.Add(visited.LogicalIndex);
            foreach (Node child in visited.VisualChildren)
            {
                stack.Push(child);
            }
        }

        Dictionary<int, SKBitmap?> imageCache = new Dictionary<int, SKBitmap?>();
        Dictionary<int, int> materialIndexByGlb = new Dictionary<int, int>();
        Dictionary<int, int> texCoordSetByMaterial = new Dictionary<int, int>(); // glTF material -> its base-colour TEXCOORD set

        for (int k = 0; k < glbNodes.Count; k++)
        {
            Node glbNode = glbNodes[k];
            if (glbNode.Mesh == null || !shown.Contains(k))
            {
                continue;
            }
            Mesh glbMesh = glbNode.Mesh;
            Skin? skin = glbNode.Skin;
            List<int> joints = new List<int>();
            if (skin != null)
            {
                for (int jointSlot = 0; jointSlot < skin.JointsCount; jointSlot++)
                {
                    (Node joint, Matrix4x4 inverseBind) = skin.GetJoint(jointSlot);
                    joints.Add(joint.LogicalIndex);
                    if (scene.Nodes[joint.LogicalIndex].Bind == null)
                    {
                        scene.Nodes[joint.LogicalIndex].Bind = ToMat4(inverseBind).Inverse();
                    }
                }
            }
            List<double> controlPointList = new List<double>();
            List<double> normalList = new List<double>();
            bool anyWithoutNormals = false;
            List<double> uvs = new List<double>();
            List<int> triangleCorners = new List<int>();
            List<int> triangleMaterials = new List<int>();
            List<List<Influence>> influences = new List<List<Influence>>();
            int vertexBase = 0;
            foreach (MeshPrimitive primitive in glbMesh.Primitives)
            {
                if (primitive.DrawPrimitiveType != PrimitiveType.TRIANGLES)
                {
                    string primitiveMeshName = glbMesh.Name ?? "";
                    if (primitiveMeshName.Length == 0)
                    {
                        primitiveMeshName = "mesh";
                    }
                    warn(primitiveMeshName + ": primitive mode " + (int)primitive.DrawPrimitiveType + " skipped (triangles only)");
                    continue;
                }
                IList<Vector3> positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
                int positionCount = positions.Count;
                int[] indexList;
                if (primitive.IndexAccessor != null)
                {
                    IList<uint> glbIndices = primitive.IndexAccessor.AsIndicesArray();
                    indexList = new int[glbIndices.Count];
                    for (int indexNumber = 0; indexNumber < indexList.Length; indexNumber++)
                    {
                        indexList[indexNumber] = (int)glbIndices[indexNumber];
                    }
                }
                else
                {
                    indexList = new int[positionCount];
                    for (int vertex = 0; vertex < positionCount; vertex++)
                    {
                        indexList[vertex] = vertex;
                    }
                }
                Material(primitive.Material, scene, imageCache, materialIndexByGlb, texCoordSetByMaterial, warn, out int materialIndex, out int texCoordSet);
                Accessor? normalAccessor = primitive.GetVertexAccessor("NORMAL");
                if (normalAccessor != null)
                {
                    IList<Vector3> normalValues = normalAccessor.AsVector3Array();
                    for (int vertex = 0; vertex < positionCount; vertex++)
                    {
                        normalList.Add(normalValues[vertex].X);
                        normalList.Add(normalValues[vertex].Y);
                        normalList.Add(normalValues[vertex].Z);
                    }
                }
                else
                {
                    anyWithoutNormals = true;
                    for (int vertex = 0; vertex < positionCount; vertex++)
                    {
                        normalList.Add(0);
                        normalList.Add(0);
                        normalList.Add(0);
                    }
                }
                Accessor? uvAccessor = primitive.GetVertexAccessor("TEXCOORD_" + texCoordSet);
                if (uvAccessor != null)
                {
                    IList<Vector2> uvValues = uvAccessor.AsVector2Array();
                    for (int vertex = 0; vertex < positionCount; vertex++)
                    {
                        uvs.Add(uvValues[vertex].X);
                        // glTF is top-left; the scene keeps bottom-left (write_glb flips back)
                        uvs.Add(1.0 - uvValues[vertex].Y);
                    }
                }
                else
                {
                    for (int vertex = 0; vertex < positionCount; vertex++)
                    {
                        uvs.Add(0);
                        uvs.Add(0);
                    }
                }
                if (skin != null)
                {
                    List<List<Influence>> vertexInfluences = new List<List<Influence>>();
                    for (int vertex = 0; vertex < positionCount; vertex++)
                    {
                        vertexInfluences.Add(new List<Influence>());
                    }
                    for (int set = 0; set < 4; set++)
                    {
                        Accessor? jointAccessor = primitive.GetVertexAccessor("JOINTS_" + set);
                        if (jointAccessor == null)
                        {
                            break;
                        }
                        IList<Vector4> jointValues = jointAccessor.AsVector4Array();
                        IList<Vector4> weightValues = primitive.GetVertexAccessor("WEIGHTS_" + set).AsVector4Array();
                        for (int influenceSlot = 0; influenceSlot < 4; influenceSlot++)
                        {
                            for (int vertex = 0; vertex < positionCount; vertex++)
                            {
                                double w = Component(weightValues[vertex], influenceSlot);
                                if (!(w > 0))
                                {
                                    continue;
                                }
                                int node = joints[(int)Component(jointValues[vertex], influenceSlot)];
                                List<Influence> list = vertexInfluences[vertex];
                                bool found = false;
                                for (int influenceIndex = 0; influenceIndex < list.Count; influenceIndex++)
                                {
                                    if (list[influenceIndex].Joint == node)
                                    {
                                        list[influenceIndex].Weight += w;
                                        found = true;
                                        break;
                                    }
                                }
                                if (!found)
                                {
                                    list.Add(new Influence(node, w));
                                }
                            }
                        }
                    }
                    for (int vertex = 0; vertex < positionCount; vertex++)
                    {
                        if (vertexInfluences[vertex].Count == 0)
                        {
                            vertexInfluences[vertex].Add(new Influence(k, 1.0)); // unweighted: rides the mesh's own node
                        }
                    }
                    influences.AddRange(vertexInfluences);
                }
                for (int vertex = 0; vertex < positionCount; vertex++)
                {
                    controlPointList.Add(positions[vertex].X);
                    controlPointList.Add(positions[vertex].Y);
                    controlPointList.Add(positions[vertex].Z);
                }
                for (int indexNumber = 0; indexNumber < indexList.Length; indexNumber++)
                {
                    triangleCorners.Add(indexList[indexNumber] + vertexBase);
                }
                for (int triangle = 0; triangle < indexList.Length / 3; triangle++)
                {
                    triangleMaterials.Add(materialIndex);
                }
                vertexBase += positionCount;
            }
            if (controlPointList.Count == 0)
            {
                continue;
            }
            double[] controlPoints = controlPointList.ToArray();
            int[] corners = triangleCorners.ToArray();
            double[]? cornerNormals = null;
            if (!anyWithoutNormals)
            {
                cornerNormals = new double[corners.Length * 3];
                for (int corner = 0; corner < corners.Length; corner++)
                {
                    cornerNormals[corner * 3] = normalList[corners[corner] * 3];
                    cornerNormals[corner * 3 + 1] = normalList[corners[corner] * 3 + 1];
                    cornerNormals[corner * 3 + 2] = normalList[corners[corner] * 3 + 2];
                }
            }
            double[] cornerUv = new double[corners.Length * 2];
            for (int corner = 0; corner < corners.Length; corner++)
            {
                cornerUv[corner * 2] = uvs[corners[corner] * 2];
                cornerUv[corner * 2 + 1] = uvs[corners[corner] * 2 + 1];
            }
            string meshName = glbMesh.Name ?? "";
            if (meshName.Length == 0)
            {
                meshName = glbNode.Name ?? "";
            }
            if (meshName.Length == 0)
            {
                meshName = "mesh";
            }
            SceneMesh mesh = new SceneMesh(meshName, k);
            List<Influence>[]? controlPointInfluences = null;
            if (skin != null)
            {
                controlPointInfluences = influences.ToArray();
                if (scene.Nodes[k].Bind == null && influences.Any(list => list.Any(influence => influence.Joint == k)))
                {
                    scene.Nodes[k].Bind = scene.NodeWorld(k);
                }
            }
            int[] triCorners = new int[corners.Length];
            for (int corner = 0; corner < corners.Length; corner++)
            {
                triCorners[corner] = corner;
            }
            MeshBuild.Build(mesh, controlPoints, corners, cornerNormals, cornerUv, triCorners, triangleMaterials.ToArray(), controlPointInfluences);
            scene.Meshes.Add(mesh);
        }

        int animationIndex = 0;
        foreach (Animation glbAnimation in model.LogicalAnimations)
        {
            List<int> channelNodes = new List<int>();
            List<string> channelPaths = new List<string>();
            List<double[]> channelTimes = new List<double[]>();
            List<double[]> channelValues = new List<double[]>();
            List<string> chInterp = new List<string>();
            double end = 0.0;
            foreach (AnimationChannel glbChannel in glbAnimation.Channels)
            {
                if (glbChannel.TargetNode == null)
                {
                    continue;
                }
                double[] times;
                double[] keyValues;
                string interpolation;
                string pathName;
                if (glbChannel.TargetNodePath == PropertyPath.translation)
                {
                    ReadKeys(glbChannel.GetTranslationSampler(), Vector3Row, out times, out keyValues, out interpolation);
                    pathName = "translation";
                }
                else if (glbChannel.TargetNodePath == PropertyPath.rotation)
                {
                    ReadKeys(glbChannel.GetRotationSampler(), QuaternionRow, out times, out keyValues, out interpolation);
                    pathName = "rotation";
                }
                else if (glbChannel.TargetNodePath == PropertyPath.scale)
                {
                    ReadKeys(glbChannel.GetScaleSampler(), Vector3Row, out times, out keyValues, out interpolation);
                    pathName = "scale";
                }
                else
                {
                    continue;
                }
                if (times.Length == 0)
                {
                    continue;
                }
                channelNodes.Add(glbChannel.TargetNode.LogicalIndex);
                channelPaths.Add(pathName);
                channelTimes.Add(times);
                channelValues.Add(keyValues);
                chInterp.Add(interpolation);
                end = Math.Max(end, times[times.Length - 1]);
            }
            if (channelNodes.Count == 0)
            {
                animationIndex++;
                continue;
            }
            int frames = (int)Math.Round(end * Scene.Fps, MidpointRounding.ToEven) + 1;
            double[] sampleTimes = new double[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                sampleTimes[frame] = (double)frame / Scene.Fps;
            }
            string animationName = glbAnimation.Name ?? "";
            if (animationName.Length == 0)
            {
                animationName = "animation" + animationIndex;
            }
            SceneAnim anim = new SceneAnim(animationName, frames);
            Dictionary<int, double[][]> trsByNode = new Dictionary<int, double[][]>();
            List<int> trsOrder = new List<int>();
            for (int channelIndex = 0; channelIndex < channelNodes.Count; channelIndex++)
            {
                int node = channelNodes[channelIndex];
                if (!trsByNode.ContainsKey(node))
                {
                    double[][] trsSlots = new double[3][];
                    trsSlots[0] = Repeat(restTranslations[node], frames);
                    trsSlots[1] = Repeat(restRotations[node], frames);
                    trsSlots[2] = Repeat(restScales[node], frames);
                    trsByNode[node] = trsSlots;
                    trsOrder.Add(node);
                }
                int slot = 0;
                int w = 3;
                if (channelPaths[channelIndex] == "rotation")
                {
                    slot = 1;
                    w = 4;
                }
                else if (channelPaths[channelIndex] == "scale")
                {
                    slot = 2;
                }
                trsByNode[node][slot] = SampleKeys(channelTimes[channelIndex], channelValues[channelIndex], w, chInterp[channelIndex], sampleTimes, channelPaths[channelIndex] == "rotation");
            }
            foreach (int node in trsOrder)
            {
                double[][] trsSlots = trsByNode[node];
                Matrix4[] frameMatrices = new Matrix4[frames];
                double[] translation = new double[3];
                double[] rotation = new double[4];
                double[] scaling = new double[3];
                for (int frame = 0; frame < frames; frame++)
                {
                    translation[0] = trsSlots[0][frame * 3];
                    translation[1] = trsSlots[0][frame * 3 + 1];
                    translation[2] = trsSlots[0][frame * 3 + 2];
                    rotation[0] = trsSlots[1][frame * 4];
                    rotation[1] = trsSlots[1][frame * 4 + 1];
                    rotation[2] = trsSlots[1][frame * 4 + 2];
                    rotation[3] = trsSlots[1][frame * 4 + 3];
                    scaling[0] = trsSlots[2][frame * 3];
                    scaling[1] = trsSlots[2][frame * 3 + 1];
                    scaling[2] = trsSlots[2][frame * 3 + 2];
                    frameMatrices[frame] = Matrix4.FromTrs(translation, rotation, scaling);
                }
                anim.Tracks[node] = frameMatrices;
            }
            scene.Anims.Add(anim);
            animationIndex++;
        }
        return scene;
    }

    static double Component(Vector4 vector, int componentIndex)
    {
        if (componentIndex == 0)
        {
            return vector.X;
        }
        if (componentIndex == 1)
        {
            return vector.Y;
        }
        if (componentIndex == 2)
        {
            return vector.Z;
        }
        return vector.W;
    }

    static double[] Repeat(double[] row, int frames)
    {
        double[] repeated = new double[row.Length * frames];
        for (int frame = 0; frame < frames; frame++)
        {
            for (int component = 0; component < row.Length; component++)
            {
                repeated[frame * row.Length + component] = row[component];
            }
        }
        return repeated;
    }

    // read_glb's image(): one bitmap per glTF image, shared by the materials that use it
    static SKBitmap? LoadImage(Image glbImage, Dictionary<int, SKBitmap?> cache, Action<string> warn)
    {
        if (!cache.ContainsKey(glbImage.LogicalIndex))
        {
            byte[] data = glbImage.Content.Content.ToArray();
            SKBitmap? bitmap = null;
            if (!data.AsSpan().SequenceEqual(MissingImage))
            {
                bitmap = SourceImages.Decode(data);
                if (bitmap == null)
                {
                    warn("texture " + (glbImage.Name ?? "image" + glbImage.LogicalIndex) + " did not load");
                }
            }
            cache[glbImage.LogicalIndex] = bitmap;
        }
        return cache[glbImage.LogicalIndex];
    }

    // read_glb's material(): a primitive's material index in the scene, and its TEXCOORD set
    static void Material(Material? glbMaterial, Scene scene, Dictionary<int, SKBitmap?> imageCache,
        Dictionary<int, int> materialIndexByGlb, Dictionary<int, int> texCoordSetByMaterial, Action<string> warn, out int index, out int texCoordSet)
    {
        int glbMaterialIndex = glbMaterial == null ? -1 : glbMaterial.LogicalIndex; // -1: the primitive has no material (Python's None)
        if (!materialIndexByGlb.ContainsKey(glbMaterialIndex))
        {
            if (glbMaterial == null)
            {
                materialIndexByGlb[glbMaterialIndex] = scene.AddMaterial("default", new double[] { 1.0, 1.0, 1.0, 1.0 }, null);
                texCoordSetByMaterial[glbMaterialIndex] = 0;
                index = materialIndexByGlb[glbMaterialIndex];
                texCoordSet = 0;
                return;
            }
            double[] color = new double[] { 1, 1, 1, 1 };
            SKBitmap? bitmap = null;
            texCoordSetByMaterial[glbMaterialIndex] = 0;
            MaterialChannel? baseColor = glbMaterial.FindChannel("BaseColor");
            if (baseColor.HasValue)
            {
                Vector4 colorFactor = baseColor.Value.Color;
                color = new double[] { colorFactor.X, colorFactor.Y, colorFactor.Z, colorFactor.W };
                Texture? texture = baseColor.Value.Texture;
                if (texture != null)
                {
                    Image? sourceImage = texture.FallbackImage ?? texture.PrimaryImage;
                    if (sourceImage != null)
                    {
                        bitmap = LoadImage(sourceImage, imageCache, warn);
                    }
                    texCoordSetByMaterial[glbMaterialIndex] = baseColor.Value.TextureCoordinate;
                }
            }
            string name = glbMaterial.Name ?? "";
            if (name.Length == 0)
            {
                name = "material" + glbMaterialIndex;
            }
            materialIndexByGlb[glbMaterialIndex] = scene.AddMaterial(name, color, bitmap);
        }
        index = materialIndexByGlb[glbMaterialIndex];
        texCoordSet = texCoordSetByMaterial[glbMaterialIndex];
    }
}
