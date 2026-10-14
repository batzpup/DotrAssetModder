namespace DotrAssetModder;
// Mostly AI

/* The DotR layout step, a port of tools/toglb.py's pose_worlds / skin_blend / posed_points /
   rebind / finish (DotR-PC). Rest pose -> bind pose, nodes that hold nothing folded into their
   children, axes -> +Y up / +Z front, scale. */

// What Finish measured while it fitted the model.
public sealed class SceneFit
{
    public double Scale;
    public double[] Size = new double[3];
    public double Lowest;                              // lowest idle point, relative to node 0
}

public static class SceneFinish
{
    // World matrix of every node at a frame of `anim` (null: the rest pose).
    public static Matrix4[] PoseWorlds(Scene scene, SceneAnim? anim, int frame)
    {
        int nodeCount = scene.Nodes.Count;
        Matrix4[] world = new Matrix4[nodeCount];
        List<int> order = scene.Order();
        foreach (int i in order)
        {
            Matrix4 local = scene.Nodes[i].Local;
            if (anim != null)
            {
                Matrix4[]? track;
                if (anim.Tracks.TryGetValue(i, out track))
                {
                    local = track[frame];
                }
            }
            int parentIndex = scene.Nodes[i].Parent;
            if (parentIndex >= 0)
            {
                world[i] = world[parentIndex] * local;
            }
            else
            {
                world[i] = local;
            }
        }
        return world;
    }

    // Per-vertex skinning matrix (16 doubles a vertex, row-major): sum of weight * world *
    // inverse(rest world).
    public static double[] SkinBlend(SceneMesh mesh, Matrix4[] world, Matrix4[] restWorld, int nodeCount)
    {
        int vertexCount = mesh.VertexCount;
        double[] jointMatrices = new double[(nodeCount + 1) * 16]; // index n: no influence
        bool[] jointSeen = new bool[nodeCount];
        int[] joints = mesh.Joints!;
        double[] jointWeights = mesh.Weights!;
        for (int k = 0; k < joints.Length; k++)
        {
            int j = joints[k];
            if (j < 0 || jointSeen[j])
            {
                continue;
            }
            jointSeen[j] = true;
            Matrix4 skinMatrix = world[j] * restWorld[j].Inverse();
            for (int element = 0; element < 16; element++)
            {
                jointMatrices[j * 16 + element] = skinMatrix.Elements[element];
            }
        }
        double[] blendMatrices = new double[vertexCount * 16];
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            for (int k = 0; k < 8; k++)
            {
                double w = jointWeights[vertex * 8 + k];
                int j = joints[vertex * 8 + k];
                int matrixSlot = nodeCount;
                if (j >= 0)
                {
                    matrixSlot = j;
                }
                for (int element = 0; element < 16; element++)
                {
                    blendMatrices[vertex * 16 + element] += w * jointMatrices[matrixSlot * 16 + element];
                }
            }
        }
        return blendMatrices;
    }

    // Every mesh vertex in world space (3 doubles a vertex), at a frame of `anim` (null: the
    // rest pose).
    public static double[] PosedPoints(Scene scene, Matrix4[] restWorld, SceneAnim? anim, int frame)
    {
        Matrix4[] world = PoseWorlds(scene, anim, frame);
        int total = 0;
        foreach (SceneMesh mesh in scene.Meshes)
        {
            total += mesh.VertexCount;
        }
        double[] points = new double[total * 3];
        int pointCursor = 0;
        foreach (SceneMesh mesh in scene.Meshes)
        {
            int vertexCount = mesh.VertexCount;
            if (mesh.Skinned)
            {
                double[] blendMatrices = SkinBlend(mesh, world, restWorld, scene.Nodes.Count);
                for (int vertex = 0; vertex < vertexCount; vertex++)
                {
                    double x = mesh.Positions[vertex * 3];
                    double y = mesh.Positions[vertex * 3 + 1];
                    double z = mesh.Positions[vertex * 3 + 2];
                    int blendOffset = vertex * 16;
                    points[pointCursor] = blendMatrices[blendOffset] * x + blendMatrices[blendOffset + 1] * y + blendMatrices[blendOffset + 2] * z + blendMatrices[blendOffset + 3];
                    points[pointCursor + 1] = blendMatrices[blendOffset + 4] * x + blendMatrices[blendOffset + 5] * y + blendMatrices[blendOffset + 6] * z + blendMatrices[blendOffset + 7];
                    points[pointCursor + 2] = blendMatrices[blendOffset + 8] * x + blendMatrices[blendOffset + 9] * y + blendMatrices[blendOffset + 10] * z + blendMatrices[blendOffset + 11];
                    pointCursor += 3;
                }
            }
            else
            {
                Matrix4 w = world[mesh.Node];
                for (int vertex = 0; vertex < vertexCount; vertex++)
                {
                    w.TransformPoint(mesh.Positions[vertex * 3], mesh.Positions[vertex * 3 + 1], mesh.Positions[vertex * 3 + 2],
                        out points[pointCursor], out points[pointCursor + 1], out points[pointCursor + 2]);
                    pointCursor += 3;
                }
            }
        }
        return points;
    }

    // Make frame 0 of `anim` the rest pose: skinned vertices are posed into it and every
    // animated node takes its frame-0 local. Returns the new rest world matrices.
    public static Matrix4[] Rebind(Scene scene, Matrix4[] restWorld, SceneAnim anim, Action<string> warn)
    {
        int nodeCount = scene.Nodes.Count;
        Matrix4[] world = PoseWorlds(scene, anim, 0);
        foreach (SceneMesh mesh in scene.Meshes)
        {
            if (!mesh.Skinned)
            {
                continue;
            }
            foreach (int j in UniqueJoints(mesh))
            {
                Matrix4 jointWorld = world[j];
                double smallestAxisScale = double.MaxValue;
                double largestAxisScale = 0;
                for (int column = 0; column < 3; column++)
                {
                    double columnLength = Math.Sqrt(jointWorld[0, column] * jointWorld[0, column] + jointWorld[1, column] * jointWorld[1, column] + jointWorld[2, column] * jointWorld[2, column]);
                    if (columnLength < smallestAxisScale)
                    {
                        smallestAxisScale = columnLength;
                    }
                    if (columnLength > largestAxisScale)
                    {
                        largestAxisScale = columnLength;
                    }
                }
                if (!(largestAxisScale > 0) || smallestAxisScale < largestAxisScale * 1e-6)
                {
                    warn($"{anim.Name} frame 0 hides joint {scene.Nodes[j].Name} (scale 0); kept the bind pose as rest");
                    return restWorld;
                }
            }
        }
        foreach (SceneMesh mesh in scene.Meshes)
        {
            if (!mesh.Skinned)
            {
                continue;
            }
            double[] blendMatrices = SkinBlend(mesh, world, restWorld, nodeCount);
            int vertexCount = mesh.VertexCount;
            double[] positions = new double[vertexCount * 3];
            double[] normals = new double[vertexCount * 3];
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                int blendOffset = vertex * 16;
                double x = mesh.Positions[vertex * 3];
                double y = mesh.Positions[vertex * 3 + 1];
                double z = mesh.Positions[vertex * 3 + 2];
                positions[vertex * 3] = blendMatrices[blendOffset] * x + blendMatrices[blendOffset + 1] * y + blendMatrices[blendOffset + 2] * z + blendMatrices[blendOffset + 3];
                positions[vertex * 3 + 1] = blendMatrices[blendOffset + 4] * x + blendMatrices[blendOffset + 5] * y + blendMatrices[blendOffset + 6] * z + blendMatrices[blendOffset + 7];
                positions[vertex * 3 + 2] = blendMatrices[blendOffset + 8] * x + blendMatrices[blendOffset + 9] * y + blendMatrices[blendOffset + 10] * z + blendMatrices[blendOffset + 11];
                double normalX = mesh.Normals[vertex * 3];
                double normalY = mesh.Normals[vertex * 3 + 1];
                double normalZ = mesh.Normals[vertex * 3 + 2];
                double resultX = blendMatrices[blendOffset] * normalX + blendMatrices[blendOffset + 1] * normalY + blendMatrices[blendOffset + 2] * normalZ;
                double resultY = blendMatrices[blendOffset + 4] * normalX + blendMatrices[blendOffset + 5] * normalY + blendMatrices[blendOffset + 6] * normalZ;
                double resultZ = blendMatrices[blendOffset + 8] * normalX + blendMatrices[blendOffset + 9] * normalY + blendMatrices[blendOffset + 10] * normalZ;
                double resultLength = Math.Sqrt(resultX * resultX + resultY * resultY + resultZ * resultZ);
                if (resultLength > 1e-12)
                {
                    normals[vertex * 3] = resultX / resultLength;
                    normals[vertex * 3 + 1] = resultY / resultLength;
                    normals[vertex * 3 + 2] = resultZ / resultLength;
                }
                else
                {
                    normals[vertex * 3] = normalX;
                    normals[vertex * 3 + 1] = normalY;
                    normals[vertex * 3 + 2] = normalZ;
                }
            }
            mesh.Positions = positions;
            mesh.Normals = normals;
        }
        for (int i = 0; i < nodeCount; i++)
        {
            Matrix4[]? track;
            if (anim.Tracks.TryGetValue(i, out track))
            {
                scene.Nodes[i].Local = track[0].Copy();
            }
        }
        return world;
    }

    // Rest pose -> bind pose, axes -> +Y up / +Z front, scale, then the model moved by translate
    // (game units, after the turn and scale).
    public static SceneFit Finish(Scene scene, double scale, double[] rotate,
        double[] translate, bool restFromAnim, Action<string> warn)
    {
        List<int> order = scene.Order();
        int nodeCount = scene.Nodes.Count;
        Matrix4[] world = new Matrix4[nodeCount];
        Matrix4[] local = new Matrix4[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            world[i] = Matrix4.Identity();
            local[i] = Matrix4.Identity();
        }
        foreach (int i in order)
        {
            int parentIndex = scene.Nodes[i].Parent;
            Matrix4 baseWorld = Matrix4.Identity();
            if (parentIndex >= 0)
            {
                baseWorld = world[parentIndex];
            }
            if (scene.Nodes[i].Bind != null)
            {
                world[i] = scene.Nodes[i].Bind!;
            }
            else
            {
                world[i] = baseWorld * scene.Nodes[i].Local;
            }
            local[i] = baseWorld.Inverse() * world[i];
        }
        foreach (SceneAnim anim in scene.Anims)
        {
            for (int i = 0; i < nodeCount; i++)
            {
                if (!anim.Tracks.ContainsKey(i) && !Matrix4.AllClose(local[i], scene.Nodes[i].Local, 1e-6))
                {
                    anim.Tracks[i] = Repeat(scene.Nodes[i].Local, anim.Frames);
                }
            }
        }
        for (int i = 0; i < nodeCount; i++)
        {
            scene.Nodes[i].Local = local[i];
        }

        /* Fold every node that is neither a joint nor a rigid mesh's node into its children. An
           exporter's axis turn rides on such nodes (Blender's armature object at -90 X, undone
           by +90 X on the root bone), so a viewer or importer that treats them differently
           sees the model lying down; folded, the bones carry the real turn themselves. */
        HashSet<int> used = new HashSet<int>();
        foreach (SceneMesh mesh in scene.Meshes)
        {
            if (mesh.Skinned)
            {
                foreach (int j in UniqueJoints(mesh))
                {
                    used.Add(j);
                }
            }
            else
            {
                used.Add(mesh.Node);
            }
        }
        // Top-down, a node's children are still its original ones when it is reached.
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
        foreach (int i in order)
        {
            if (used.Contains(i))
            {
                continue;
            }
            foreach (int child in children[i])
            {
                foreach (SceneAnim anim in scene.Anims)
                {
                    if (anim.Tracks.ContainsKey(i) || anim.Tracks.ContainsKey(child))
                    {
                        Matrix4[] parentTrack = TrackOrRest(anim, i, scene.Nodes[i].Local);
                        Matrix4[] childTrack = TrackOrRest(anim, child, scene.Nodes[child].Local);
                        Matrix4[] combinedTrack = new Matrix4[anim.Frames];
                        for (int frame = 0; frame < anim.Frames; frame++)
                        {
                            combinedTrack[frame] = parentTrack[frame] * childTrack[frame];
                        }
                        anim.Tracks[child] = combinedTrack;
                    }
                }
                scene.Nodes[child].Local = scene.Nodes[i].Local * scene.Nodes[child].Local;
                scene.Nodes[child].Parent = scene.Nodes[i].Parent;
            }
            foreach (SceneAnim anim in scene.Anims)
            {
                anim.Tracks.Remove(i);
            }
        }
        order = scene.Order();

        /* The importer binds at the rest pose. On request (restFromAnim) that is the first
           animation's frame 0 rather than the bind pose, for a bind pose the animations never
           show (a rip lying flat, stood up by the root bone's motion). */
        if (scene.Anims.Count > 0 && restFromAnim)
        {
            world = Rebind(scene, world, scene.Anims[0], warn);
        }

        // The lowest point and size from the idle animation, the pose the model holds on the
        // field (an attack that reaches out would otherwise set it); the rest pose if there is none.
        Matrix4 rotation = Matrix4.EulerAngles(rotate[0], rotate[1], rotate[2]) * scene.Axis;
        if (scene.Meshes.Count == 0)
        {
            throw new InvalidDataException("no meshes found");
        }
        SceneAnim? idleAnim = IdleAnim(scene.Anims);
        List<SceneAnim> sampled = scene.Anims;
        if (idleAnim != null)
        {
            sampled = new List<SceneAnim> { idleAnim };
        }
        List<double[]> idlePoses = new List<double[]>();
        foreach (SceneAnim anim in sampled)
        {
            foreach (int frame in SampleFrames(anim.Frames))
            {
                idlePoses.Add(RotatedPoints(PosedPoints(scene, world, anim, frame), rotation));
            }
        }
        if (idlePoses.Count == 0)
        {
            idlePoses.Add(RotatedPoints(PosedPoints(scene, world, null, 0), rotation));
        }
        double totalScale = scene.Unit * scale;
        // The source's own origin stays node 0's; moving it the other way moves the model by translate.
        double[] centre = new double[3];
        if (totalScale != 0)
        {
            for (int k = 0; k < 3; k++)
            {
                centre[k] -= translate[k] / totalScale;
            }
        }
        double lowest = 0;
        bool isFirstPose = true;
        foreach (double[] pose in idlePoses)
        {
            double[] poseMinimum = new double[3];
            double[] poseMaximum = new double[3];
            PointBounds(pose, poseMinimum, poseMaximum);
            if (isFirstPose || poseMinimum[1] < lowest)
            {
                lowest = poseMinimum[1];
                isFirstPose = false;
            }
        }

        Matrix4 linear = Matrix4.Identity();
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                linear[row, column] = totalScale * rotation[row, column];
            }
        }
        Matrix4 linearInverse = linear.Inverse();
        /* The game puts node 0's origin at the unit's place (retail monsters have it inside the
           body, 41-76% of the way up, median 61%, idle frame 0). Roots take the shift; skinned
           positions are world, so they take it too. */
        Matrix4 affine = linear.Copy();
        for (int k = 0; k < 3; k++)
        {
            affine[k, 3] = -totalScale * centre[k];
        }
        for (int i = 0; i < nodeCount; i++)
        {
            if (scene.Nodes[i].Parent < 0)
            {
                scene.Nodes[i].Local = affine * scene.Nodes[i].Local * linearInverse;
            }
            else
            {
                scene.Nodes[i].Local = linear * scene.Nodes[i].Local * linearInverse;
            }
        }
        foreach (SceneAnim anim in scene.Anims)
        {
            foreach (int i in TrackNodes(anim))
            {
                Matrix4[] track = anim.Tracks[i];
                Matrix4[] outputTrack = new Matrix4[track.Length];
                for (int frame = 0; frame < track.Length; frame++)
                {
                    if (scene.Nodes[i].Parent < 0)
                    {
                        outputTrack[frame] = affine * track[frame] * linearInverse;
                    }
                    else
                    {
                        outputTrack[frame] = linear * track[frame] * linearInverse;
                    }
                }
                anim.Tracks[i] = outputTrack;
            }
        }
        foreach (SceneMesh mesh in scene.Meshes)
        {
            int vertexCount = mesh.VertexCount;
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                double x = mesh.Positions[vertex * 3];
                double y = mesh.Positions[vertex * 3 + 1];
                double z = mesh.Positions[vertex * 3 + 2];
                for (int k = 0; k < 3; k++)
                {
                    double component = totalScale * (rotation[k, 0] * x + rotation[k, 1] * y + rotation[k, 2] * z);
                    if (mesh.Skinned)
                    {
                        component -= totalScale * centre[k];
                    }
                    mesh.Positions[vertex * 3 + k] = component;
                }
                double normalX = mesh.Normals[vertex * 3];
                double normalY = mesh.Normals[vertex * 3 + 1];
                double normalZ = mesh.Normals[vertex * 3 + 2];
                for (int k = 0; k < 3; k++)
                {
                    mesh.Normals[vertex * 3 + k] = rotation[k, 0] * normalX + rotation[k, 1] * normalY + rotation[k, 2] * normalZ;
                }
            }
        }

        /* Take each node's uniform rest scale out of its frame (L' = K_parent L K^-1): world
           positions are unchanged, but the importer encodes vertices bone-local in s16, and a
           bone at scale 100 (DoDModels) leaves them 1/100 the size, quantised to blocks. */
        Matrix4[] worldWithScale = new Matrix4[nodeCount];
        double[] restScale = new double[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            restScale[i] = 1.0;
        }
        foreach (int i in order)
        {
            int parentIndex = scene.Nodes[i].Parent;
            if (parentIndex >= 0)
            {
                worldWithScale[i] = worldWithScale[parentIndex] * scene.Nodes[i].Local;
            }
            else
            {
                worldWithScale[i] = scene.Nodes[i].Local;
            }
            double sum = 0;
            for (int column = 0; column < 3; column++)
            {
                sum += Math.Sqrt(worldWithScale[i][0, column] * worldWithScale[i][0, column] + worldWithScale[i][1, column] * worldWithScale[i][1, column] + worldWithScale[i][2, column] * worldWithScale[i][2, column]);
            }
            double averageScale = sum / 3.0;
            if (averageScale > 1e-6)
            {
                restScale[i] = averageScale;
            }
        }
        Matrix4[] scaleMatrices = new Matrix4[nodeCount];
        Matrix4[] inverseScaleMatrices = new Matrix4[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            scaleMatrices[i] = Matrix4.Scale(restScale[i], restScale[i], restScale[i]);
            inverseScaleMatrices[i] = Matrix4.Scale(1 / restScale[i], 1 / restScale[i], 1 / restScale[i]);
        }
        for (int i = 0; i < nodeCount; i++)
        {
            int parentIndex = scene.Nodes[i].Parent;
            Matrix4 parentScale = Matrix4.Identity();
            if (parentIndex >= 0)
            {
                parentScale = scaleMatrices[parentIndex];
            }
            scene.Nodes[i].Local = parentScale * scene.Nodes[i].Local * inverseScaleMatrices[i];
            foreach (SceneAnim anim in scene.Anims)
            {
                Matrix4[]? track;
                if (anim.Tracks.TryGetValue(i, out track))
                {
                    Matrix4[] outputTrack = new Matrix4[track.Length];
                    for (int frame = 0; frame < track.Length; frame++)
                    {
                        outputTrack[frame] = parentScale * track[frame] * inverseScaleMatrices[i];
                    }
                    anim.Tracks[i] = outputTrack;
                }
            }
        }
        foreach (SceneMesh mesh in scene.Meshes)
        {
            if (!mesh.Skinned)
            {
                double meshScale = restScale[mesh.Node];
                for (int component = 0; component < mesh.Positions.Length; component++)
                {
                    mesh.Positions[component] *= meshScale;
                }
            }
        }
        SceneFit fit = new SceneFit();
        fit.Scale = totalScale;
        fit.Lowest = totalScale * (lowest - centre[1]);
        for (int i = 0; i < idlePoses.Count; i++)
        {
            double[] minimumCorner = new double[3];
            double[] maximumCorner = new double[3];
            PointBounds(idlePoses[i], minimumCorner, maximumCorner);
            for (int k = 0; k < 3; k++)
            {
                double extent = maximumCorner[k] - minimumCorner[k];
                if (i == 0 || extent > fit.Size[k])
                {
                    fit.Size[k] = extent;
                }
            }
        }
        for (int k = 0; k < 3; k++)
        {
            fit.Size[k] *= totalScale;
        }
        return fit;
    }

    // The distinct joints a skinned mesh uses, ascending (numpy unique).
    public static List<int> UniqueJoints(SceneMesh mesh)
    {
        List<int> jointList = new List<int>();
        if (mesh.Joints == null)
        {
            return jointList;
        }
        SortedSet<int> jointSet = new SortedSet<int>();
        for (int k = 0; k < mesh.Joints.Length; k++)
        {
            if (mesh.Joints[k] >= 0)
            {
                jointSet.Add(mesh.Joints[k]);
            }
        }
        foreach (int j in jointSet)
        {
            jointList.Add(j);
        }
        return jointList;
    }

    static Matrix4[] Repeat(Matrix4 matrix, int frames)
    {
        Matrix4[] track = new Matrix4[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            track[frame] = matrix;
        }
        return track;
    }

    static Matrix4[] TrackOrRest(SceneAnim anim, int node, Matrix4 local)
    {
        Matrix4[]? track;
        if (anim.Tracks.TryGetValue(node, out track))
        {
            return track;
        }
        return Repeat(local, anim.Frames);
    }

    // A snapshot of a track dictionary's nodes, so the tracks can be replaced while walking it.
    public static List<int> TrackNodes(SceneAnim anim)
    {
        List<int> nodes = new List<int>();
        foreach (KeyValuePair<int, Matrix4[]> track in anim.Tracks)
        {
            nodes.Add(track.Key);
        }
        return nodes;
    }

    /* The animation the importer puts in motion slot 0 (idle), by its rule
       (RebuildPipeline.AssignSlotIds): "slot_N" in the name, else a role word, else the free
       slots in the order 0, 2, 4, 5, 6, 1, 3. Null when there is none. */
    static SceneAnim? IdleAnim(List<SceneAnim> anims)
    {
        int[] fillOrder = { 0, 2, 4, 5, 6, 1, 3 };
        int[] slotNumbers = new int[anims.Count];
        HashSet<int> used = new HashSet<int>();
        for (int i = 0; i < anims.Count; i++)
        {
            int slotNumber = NameSlot(anims[i].Name);
            if (slotNumber < 0)
            {
                slotNumber = RoleSlot(anims[i].Name);
            }
            if (slotNumber >= 0 && !used.Add(slotNumber))
            {
                slotNumber = -1;
            }
            else if (slotNumber < 0)
            {
                slotNumber = -2;
            }
            slotNumbers[i] = slotNumber;
        }
        for (int i = 0; i < anims.Count; i++)
        {
            if (slotNumbers[i] != -2)
            {
                continue;
            }
            slotNumbers[i] = -1;
            foreach (int k in fillOrder)
            {
                if (!used.Contains(k))
                {
                    slotNumbers[i] = k;
                    used.Add(k);
                    break;
                }
            }
        }
        for (int i = 0; i < anims.Count; i++)
        {
            if (slotNumbers[i] == 0)
            {
                return anims[i];
            }
        }
        return null;
    }

    // "slot_N" in a name, N 0..7; -1 if none.
    static int NameSlot(string name)
    {
        int slotPosition = name.IndexOf("slot_", StringComparison.OrdinalIgnoreCase);
        if (slotPosition < 0)
        {
            return -1;
        }
        int digitsEnd = slotPosition + 5;
        while (digitsEnd < name.Length && char.IsDigit(name[digitsEnd]))
        {
            digitsEnd++;
        }
        if (digitsEnd > slotPosition + 5 && int.TryParse(name.Substring(slotPosition + 5, digitsEnd - slotPosition - 5), out int slotNumber) && slotNumber >= 0 && slotNumber <= 7)
        {
            return slotNumber;
        }
        return -1;
    }

    // A role word in a name (whole words), as the importer matches them; -1 if none.
    static int RoleSlot(string name)
    {
        string[] words = System.Text.RegularExpressions.Regex.Split(name.ToLowerInvariant(), "[^a-z]+");
        if (words.Contains("idle") || words.Contains("wait"))
        {
            return 0;
        }
        if (words.Contains("attack"))
        {
            return 2;
        }
        if (words.Contains("death") || words.Contains("die") || words.Contains("dead"))
        {
            return 4;
        }
        if (words.Contains("win") || words.Contains("victory"))
        {
            return 5;
        }
        if (words.Contains("damage") || words.Contains("hurt") || words.Contains("hit"))
        {
            return 6;
        }
        return -1;
    }

    // np.unique(np.linspace(0, frames - 1, min(frames, 16)).astype(int)): up to 16 frames
    // spread over the animation, truncated to integers.
    static List<int> SampleFrames(int frames)
    {
        int sampleCount = Math.Min(frames, 16);
        List<int> sampledFrames = new List<int>();
        double lastFrame = frames - 1;
        for (int i = 0; i < sampleCount; i++)
        {
            double position;
            if (sampleCount == 1)
            {
                position = 0;
            }
            else if (i == sampleCount - 1)
            {
                position = lastFrame;
            }
            else
            {
                position = i * (lastFrame / (sampleCount - 1));
            }
            int frame = (int)position;
            if (sampledFrames.Count == 0 || sampledFrames[sampledFrames.Count - 1] != frame)
            {
                sampledFrames.Add(frame);
            }
        }
        return sampledFrames;
    }

    // points @ rot.T
    static double[] RotatedPoints(double[] points, Matrix4 rotation)
    {
        double[] rotated = new double[points.Length];
        for (int pointOffset = 0; pointOffset < points.Length; pointOffset += 3)
        {
            rotation.TransformDir(points[pointOffset], points[pointOffset + 1], points[pointOffset + 2], out rotated[pointOffset], out rotated[pointOffset + 1], out rotated[pointOffset + 2]);
        }
        return rotated;
    }

    static void PointBounds(double[] points, double[] minimum, double[] maximum)
    {
        for (int k = 0; k < 3; k++)
        {
            minimum[k] = double.MaxValue;
            maximum[k] = -double.MaxValue;
        }
        for (int pointOffset = 0; pointOffset < points.Length; pointOffset += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                if (points[pointOffset + k] < minimum[k])
                {
                    minimum[k] = points[pointOffset + k];
                }
                if (points[pointOffset + k] > maximum[k])
                {
                    maximum[k] = points[pointOffset + k];
                }
            }
        }
    }
}
