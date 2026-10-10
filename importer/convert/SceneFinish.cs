namespace DotrAssetModder;
// Mostly AI

// The DotR layout step, a port of tools/toglb.py's pose_worlds / skin_blend / posed_points /
// rebind / finish (DotR-PC). Rest pose -> bind pose, nodes that hold nothing folded into their
// children, axes -> +Y up / +Z front, scale.

// What finish() measured while it fitted the model.
public sealed class SceneFit
{
    public double Scale;
    public double[] Size = new double[3];
    public double Lowest;                              // lowest idle point, relative to node 0
}

public static class SceneFinish
{
    // World matrix of every node at a frame of `anim` (null: the rest pose).
    public static Mat4[] PoseWorlds(Scene sc, SceneAnim? anim, int frame)
    {
        int n = sc.Nodes.Count;
        Mat4[] world = new Mat4[n];
        List<int> order = sc.Order();
        foreach (int i in order)
        {
            Mat4 local = sc.Nodes[i].Local;
            if (anim != null)
            {
                Mat4[]? tr;
                if (anim.Tracks.TryGetValue(i, out tr))
                {
                    local = tr[frame];
                }
            }
            int p = sc.Nodes[i].Parent;
            if (p >= 0)
            {
                world[i] = world[p] * local;
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
    public static double[] SkinBlend(SceneMesh m, Mat4[] world, Mat4[] restWorld, int n)
    {
        int verts = m.VertexCount;
        double[] mats = new double[(n + 1) * 16]; // index n: no influence
        bool[] seen = new bool[n];
        int[] jnt = m.Jnt!;
        double[] wgt = m.Wgt!;
        for (int k = 0; k < jnt.Length; k++)
        {
            int j = jnt[k];
            if (j < 0 || seen[j])
            {
                continue;
            }
            seen[j] = true;
            Mat4 b = world[j] * restWorld[j].Inverse();
            for (int t = 0; t < 16; t++)
            {
                mats[j * 16 + t] = b.M[t];
            }
        }
        double[] blend = new double[verts * 16];
        for (int v = 0; v < verts; v++)
        {
            for (int k = 0; k < 8; k++)
            {
                double w = wgt[v * 8 + k];
                int j = jnt[v * 8 + k];
                int idx = n;
                if (j >= 0)
                {
                    idx = j;
                }
                for (int t = 0; t < 16; t++)
                {
                    blend[v * 16 + t] += w * mats[idx * 16 + t];
                }
            }
        }
        return blend;
    }

    // Every mesh vertex in world space (3 doubles a vertex), at a frame of `anim` (null: the
    // rest pose).
    public static double[] PosedPoints(Scene sc, Mat4[] restWorld, SceneAnim? anim, int frame)
    {
        Mat4[] world = PoseWorlds(sc, anim, frame);
        int total = 0;
        foreach (SceneMesh m in sc.Meshes)
        {
            total += m.VertexCount;
        }
        double[] pts = new double[total * 3];
        int at = 0;
        foreach (SceneMesh m in sc.Meshes)
        {
            int verts = m.VertexCount;
            if (m.Skinned)
            {
                double[] blend = SkinBlend(m, world, restWorld, sc.Nodes.Count);
                for (int v = 0; v < verts; v++)
                {
                    double x = m.Pos[v * 3];
                    double y = m.Pos[v * 3 + 1];
                    double z = m.Pos[v * 3 + 2];
                    int b = v * 16;
                    pts[at] = blend[b] * x + blend[b + 1] * y + blend[b + 2] * z + blend[b + 3];
                    pts[at + 1] = blend[b + 4] * x + blend[b + 5] * y + blend[b + 6] * z + blend[b + 7];
                    pts[at + 2] = blend[b + 8] * x + blend[b + 9] * y + blend[b + 10] * z + blend[b + 11];
                    at += 3;
                }
            }
            else
            {
                Mat4 w = world[m.Node];
                for (int v = 0; v < verts; v++)
                {
                    w.TransformPoint(m.Pos[v * 3], m.Pos[v * 3 + 1], m.Pos[v * 3 + 2],
                        out pts[at], out pts[at + 1], out pts[at + 2]);
                    at += 3;
                }
            }
        }
        return pts;
    }

    // Make frame 0 of `anim` the rest pose: skinned vertices are posed into it and every
    // animated node takes its frame-0 local. Returns the new rest world matrices.
    public static Mat4[] Rebind(Scene sc, Mat4[] restWorld, SceneAnim anim, Action<string> warn)
    {
        int n = sc.Nodes.Count;
        Mat4[] world = PoseWorlds(sc, anim, 0);
        foreach (SceneMesh m in sc.Meshes)
        {
            if (!m.Skinned)
            {
                continue;
            }
            foreach (int j in UniqueJoints(m))
            {
                Mat4 a = world[j];
                double lo = double.MaxValue;
                double hi = 0;
                for (int c = 0; c < 3; c++)
                {
                    double norm = Math.Sqrt(a[0, c] * a[0, c] + a[1, c] * a[1, c] + a[2, c] * a[2, c]);
                    if (norm < lo)
                    {
                        lo = norm;
                    }
                    if (norm > hi)
                    {
                        hi = norm;
                    }
                }
                if (!(hi > 0) || lo < hi * 1e-6)
                {
                    warn($"{anim.Name} frame 0 hides joint {sc.Nodes[j].Name} (scale 0); kept the bind pose as rest");
                    return restWorld;
                }
            }
        }
        foreach (SceneMesh m in sc.Meshes)
        {
            if (!m.Skinned)
            {
                continue;
            }
            double[] blend = SkinBlend(m, world, restWorld, n);
            int verts = m.VertexCount;
            double[] pos = new double[verts * 3];
            double[] nrm = new double[verts * 3];
            for (int v = 0; v < verts; v++)
            {
                int b = v * 16;
                double x = m.Pos[v * 3];
                double y = m.Pos[v * 3 + 1];
                double z = m.Pos[v * 3 + 2];
                pos[v * 3] = blend[b] * x + blend[b + 1] * y + blend[b + 2] * z + blend[b + 3];
                pos[v * 3 + 1] = blend[b + 4] * x + blend[b + 5] * y + blend[b + 6] * z + blend[b + 7];
                pos[v * 3 + 2] = blend[b + 8] * x + blend[b + 9] * y + blend[b + 10] * z + blend[b + 11];
                double nx = m.Nrm[v * 3];
                double ny = m.Nrm[v * 3 + 1];
                double nz = m.Nrm[v * 3 + 2];
                double ox = blend[b] * nx + blend[b + 1] * ny + blend[b + 2] * nz;
                double oy = blend[b + 4] * nx + blend[b + 5] * ny + blend[b + 6] * nz;
                double oz = blend[b + 8] * nx + blend[b + 9] * ny + blend[b + 10] * nz;
                double ln = Math.Sqrt(ox * ox + oy * oy + oz * oz);
                if (ln > 1e-12)
                {
                    nrm[v * 3] = ox / ln;
                    nrm[v * 3 + 1] = oy / ln;
                    nrm[v * 3 + 2] = oz / ln;
                }
                else
                {
                    nrm[v * 3] = nx;
                    nrm[v * 3 + 1] = ny;
                    nrm[v * 3 + 2] = nz;
                }
            }
            m.Pos = pos;
            m.Nrm = nrm;
        }
        for (int i = 0; i < n; i++)
        {
            Mat4[]? tr;
            if (anim.Tracks.TryGetValue(i, out tr))
            {
                sc.Nodes[i].Local = tr[0].Copy();
            }
        }
        return world;
    }

    // Rest pose -> bind pose, axes -> +Y up / +Z front, scale, then the model moved by translate
    // (game units, after the turn and scale).
    public static SceneFit Finish(Scene sc, double scale, double[] rotate,
        double[] translate, bool restFromAnim, Action<string> warn)
    {
        List<int> order = sc.Order();
        int n = sc.Nodes.Count;
        Mat4[] world = new Mat4[n];
        Mat4[] local = new Mat4[n];
        for (int i = 0; i < n; i++)
        {
            world[i] = Mat4.Identity();
            local[i] = Mat4.Identity();
        }
        foreach (int i in order)
        {
            int p = sc.Nodes[i].Parent;
            Mat4 baseWorld = Mat4.Identity();
            if (p >= 0)
            {
                baseWorld = world[p];
            }
            if (sc.Nodes[i].Bind != null)
            {
                world[i] = sc.Nodes[i].Bind!;
            }
            else
            {
                world[i] = baseWorld * sc.Nodes[i].Local;
            }
            local[i] = baseWorld.Inverse() * world[i];
        }
        foreach (SceneAnim anim in sc.Anims)
        {
            for (int i = 0; i < n; i++)
            {
                if (!anim.Tracks.ContainsKey(i) && !Mat4.AllClose(local[i], sc.Nodes[i].Local, 1e-6))
                {
                    anim.Tracks[i] = Repeat(sc.Nodes[i].Local, anim.Frames);
                }
            }
        }
        for (int i = 0; i < n; i++)
        {
            sc.Nodes[i].Local = local[i];
        }

        // Fold every node that is neither a joint nor a rigid mesh's node into its children. An
        // exporter's axis turn rides on such nodes (Blender's armature object at -90 X, undone
        // by +90 X on the root bone), so a viewer or importer that treats them differently
        // sees the model lying down; folded, the bones carry the real turn themselves.
        HashSet<int> used = new HashSet<int>();
        foreach (SceneMesh m in sc.Meshes)
        {
            if (m.Skinned)
            {
                foreach (int j in UniqueJoints(m))
                {
                    used.Add(j);
                }
            }
            else
            {
                used.Add(m.Node);
            }
        }
        // Top-down, a node's children are still its original ones when it is reached.
        List<int>[] kids = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            kids[i] = new List<int>();
        }
        foreach (int i in order)
        {
            if (sc.Nodes[i].Parent >= 0)
            {
                kids[sc.Nodes[i].Parent].Add(i);
            }
        }
        foreach (int i in order)
        {
            if (used.Contains(i))
            {
                continue;
            }
            foreach (int kid in kids[i])
            {
                foreach (SceneAnim anim in sc.Anims)
                {
                    if (anim.Tracks.ContainsKey(i) || anim.Tracks.ContainsKey(kid))
                    {
                        Mat4[] pt = TrackOr(anim, i, sc.Nodes[i].Local);
                        Mat4[] ct = TrackOr(anim, kid, sc.Nodes[kid].Local);
                        Mat4[] both = new Mat4[anim.Frames];
                        for (int f = 0; f < anim.Frames; f++)
                        {
                            both[f] = pt[f] * ct[f];
                        }
                        anim.Tracks[kid] = both;
                    }
                }
                sc.Nodes[kid].Local = sc.Nodes[i].Local * sc.Nodes[kid].Local;
                sc.Nodes[kid].Parent = sc.Nodes[i].Parent;
            }
            foreach (SceneAnim anim in sc.Anims)
            {
                anim.Tracks.Remove(i);
            }
        }
        order = sc.Order();

        // The importer binds at the rest pose. On request that is the first animation's frame
        // 0 rather than the bind pose, for a bind pose the animations never show (a rip lying
        // flat, stood up by the root bone's motion).
        if (sc.Anims.Count > 0 && restFromAnim)
        {
            world = Rebind(sc, world, sc.Anims[0], warn);
        }

        // The lowest point and size from the idle animation, the pose the model holds on the
        // field (an attack that reaches out would otherwise set it); the rest pose if there is none.
        Mat4 rot = Mat4.Euler(rotate[0], rotate[1], rotate[2]) * sc.Axis;
        if (sc.Meshes.Count == 0)
        {
            throw new InvalidDataException("no meshes found");
        }
        SceneAnim? idle = IdleAnim(sc.Anims);
        List<SceneAnim> sampled = sc.Anims;
        if (idle != null)
        {
            sampled = new List<SceneAnim> { idle };
        }
        List<double[]> idlePoses = new List<double[]>();
        foreach (SceneAnim anim in sampled)
        {
            foreach (int f in SampleFrames(anim.Frames))
            {
                idlePoses.Add(Rotated(PosedPoints(sc, world, anim, f), rot));
            }
        }
        if (idlePoses.Count == 0)
        {
            idlePoses.Add(Rotated(PosedPoints(sc, world, null, 0), rot));
        }
        double s = sc.Unit * scale;
        // The source's own origin stays node 0's; moving it the other way moves the model by translate.
        double[] c = new double[3];
        if (s != 0)
        {
            for (int k = 0; k < 3; k++)
            {
                c[k] -= translate[k] / s;
            }
        }
        double lowest = 0;
        bool first = true;
        foreach (double[] p in idlePoses)
        {
            double[] lo = new double[3];
            double[] hi = new double[3];
            Bounds(p, lo, hi);
            if (first || lo[1] < lowest)
            {
                lowest = lo[1];
                first = false;
            }
        }

        Mat4 lin = Mat4.Identity();
        for (int r = 0; r < 3; r++)
        {
            for (int cc = 0; cc < 3; cc++)
            {
                lin[r, cc] = s * rot[r, cc];
            }
        }
        Mat4 linInv = lin.Inverse();
        // The game puts node 0's origin at the unit's place (retail monsters have it inside the
        // body, 41-76% of the way up, median 61%, idle frame 0). Roots take the shift; skinned
        // positions are world, so they take it too.
        Mat4 aff = lin.Copy();
        for (int k = 0; k < 3; k++)
        {
            aff[k, 3] = -s * c[k];
        }
        for (int i = 0; i < n; i++)
        {
            if (sc.Nodes[i].Parent < 0)
            {
                sc.Nodes[i].Local = aff * sc.Nodes[i].Local * linInv;
            }
            else
            {
                sc.Nodes[i].Local = lin * sc.Nodes[i].Local * linInv;
            }
        }
        foreach (SceneAnim anim in sc.Anims)
        {
            foreach (int i in TrackNodes(anim))
            {
                Mat4[] tr = anim.Tracks[i];
                Mat4[] outTr = new Mat4[tr.Length];
                for (int f = 0; f < tr.Length; f++)
                {
                    if (sc.Nodes[i].Parent < 0)
                    {
                        outTr[f] = aff * tr[f] * linInv;
                    }
                    else
                    {
                        outTr[f] = lin * tr[f] * linInv;
                    }
                }
                anim.Tracks[i] = outTr;
            }
        }
        foreach (SceneMesh m in sc.Meshes)
        {
            int verts = m.VertexCount;
            for (int v = 0; v < verts; v++)
            {
                double x = m.Pos[v * 3];
                double y = m.Pos[v * 3 + 1];
                double z = m.Pos[v * 3 + 2];
                for (int k = 0; k < 3; k++)
                {
                    double o = s * (rot[k, 0] * x + rot[k, 1] * y + rot[k, 2] * z);
                    if (m.Skinned)
                    {
                        o -= s * c[k];
                    }
                    m.Pos[v * 3 + k] = o;
                }
                double nx = m.Nrm[v * 3];
                double ny = m.Nrm[v * 3 + 1];
                double nz = m.Nrm[v * 3 + 2];
                for (int k = 0; k < 3; k++)
                {
                    m.Nrm[v * 3 + k] = rot[k, 0] * nx + rot[k, 1] * ny + rot[k, 2] * nz;
                }
            }
        }

        // Take each node's uniform rest scale out of its frame (L' = K_parent L K^-1): world
        // positions are unchanged, but the importer encodes vertices bone-local in s16, and a
        // bone at scale 100 (DoDModels) leaves them 1/100 the size, quantised to blocks.
        Mat4[] wk = new Mat4[n];
        double[] k2 = new double[n];
        for (int i = 0; i < n; i++)
        {
            k2[i] = 1.0;
        }
        foreach (int i in order)
        {
            int p = sc.Nodes[i].Parent;
            if (p >= 0)
            {
                wk[i] = wk[p] * sc.Nodes[i].Local;
            }
            else
            {
                wk[i] = sc.Nodes[i].Local;
            }
            double sum = 0;
            for (int cc = 0; cc < 3; cc++)
            {
                sum += Math.Sqrt(wk[i][0, cc] * wk[i][0, cc] + wk[i][1, cc] * wk[i][1, cc] + wk[i][2, cc] * wk[i][2, cc]);
            }
            double ws = sum / 3.0;
            if (ws > 1e-6)
            {
                k2[i] = ws;
            }
        }
        Mat4[] kmat = new Mat4[n];
        Mat4[] kinv = new Mat4[n];
        for (int i = 0; i < n; i++)
        {
            kmat[i] = Mat4.Scale(k2[i], k2[i], k2[i]);
            kinv[i] = Mat4.Scale(1 / k2[i], 1 / k2[i], 1 / k2[i]);
        }
        for (int i = 0; i < n; i++)
        {
            int p = sc.Nodes[i].Parent;
            Mat4 kp = Mat4.Identity();
            if (p >= 0)
            {
                kp = kmat[p];
            }
            sc.Nodes[i].Local = kp * sc.Nodes[i].Local * kinv[i];
            foreach (SceneAnim anim in sc.Anims)
            {
                Mat4[]? tr;
                if (anim.Tracks.TryGetValue(i, out tr))
                {
                    Mat4[] outTr = new Mat4[tr.Length];
                    for (int f = 0; f < tr.Length; f++)
                    {
                        outTr[f] = kp * tr[f] * kinv[i];
                    }
                    anim.Tracks[i] = outTr;
                }
            }
        }
        foreach (SceneMesh m in sc.Meshes)
        {
            if (!m.Skinned)
            {
                double f = k2[m.Node];
                for (int t = 0; t < m.Pos.Length; t++)
                {
                    m.Pos[t] *= f;
                }
            }
        }
        SceneFit fit = new SceneFit();
        fit.Scale = s;
        fit.Lowest = s * (lowest - c[1]);
        for (int i = 0; i < idlePoses.Count; i++)
        {
            double[] lo = new double[3];
            double[] hi = new double[3];
            Bounds(idlePoses[i], lo, hi);
            for (int k = 0; k < 3; k++)
            {
                double d = hi[k] - lo[k];
                if (i == 0 || d > fit.Size[k])
                {
                    fit.Size[k] = d;
                }
            }
        }
        for (int k = 0; k < 3; k++)
        {
            fit.Size[k] *= s;
        }
        return fit;
    }

    // The distinct joints a skinned mesh uses, ascending (numpy unique).
    public static List<int> UniqueJoints(SceneMesh m)
    {
        List<int> js = new List<int>();
        if (m.Jnt == null)
        {
            return js;
        }
        SortedSet<int> set = new SortedSet<int>();
        for (int k = 0; k < m.Jnt.Length; k++)
        {
            if (m.Jnt[k] >= 0)
            {
                set.Add(m.Jnt[k]);
            }
        }
        foreach (int j in set)
        {
            js.Add(j);
        }
        return js;
    }

    static Mat4[] Repeat(Mat4 m, int frames)
    {
        Mat4[] tr = new Mat4[frames];
        for (int f = 0; f < frames; f++)
        {
            tr[f] = m;
        }
        return tr;
    }

    static Mat4[] TrackOr(SceneAnim anim, int node, Mat4 local)
    {
        Mat4[]? tr;
        if (anim.Tracks.TryGetValue(node, out tr))
        {
            return tr;
        }
        return Repeat(local, anim.Frames);
    }

    // A snapshot of a track dictionary's nodes, so the tracks can be replaced while walking it.
    public static List<int> TrackNodes(SceneAnim anim)
    {
        List<int> nodes = new List<int>();
        foreach (KeyValuePair<int, Mat4[]> kv in anim.Tracks)
        {
            nodes.Add(kv.Key);
        }
        return nodes;
    }

    // The animation the importer puts in motion slot 0 (idle), by its rule
    // (RebuildPipeline.AssignSlotIds): "slot_N" in the name, else a role word, else the free
    // slots in the order 0, 2, 4, 5, 6, 1, 3. Null when there is none.
    static SceneAnim? IdleAnim(List<SceneAnim> anims)
    {
        int[] fillOrder = { 0, 2, 4, 5, 6, 1, 3 };
        int[] slots = new int[anims.Count];
        HashSet<int> used = new HashSet<int>();
        for (int i = 0; i < anims.Count; i++)
        {
            int s = NameSlot(anims[i].Name);
            if (s < 0)
            {
                s = RoleSlot(anims[i].Name);
            }
            if (s >= 0 && !used.Add(s))
            {
                s = -1;
            }
            else if (s < 0)
            {
                s = -2;
            }
            slots[i] = s;
        }
        for (int i = 0; i < anims.Count; i++)
        {
            if (slots[i] != -2)
            {
                continue;
            }
            slots[i] = -1;
            foreach (int k in fillOrder)
            {
                if (!used.Contains(k))
                {
                    slots[i] = k;
                    used.Add(k);
                    break;
                }
            }
        }
        for (int i = 0; i < anims.Count; i++)
        {
            if (slots[i] == 0)
            {
                return anims[i];
            }
        }
        return null;
    }

    // "slot_N" in a name, N 0..7; -1 if none.
    static int NameSlot(string name)
    {
        int p = name.IndexOf("slot_", StringComparison.OrdinalIgnoreCase);
        if (p < 0)
        {
            return -1;
        }
        int e = p + 5;
        while (e < name.Length && char.IsDigit(name[e]))
        {
            e++;
        }
        if (e > p + 5 && int.TryParse(name.Substring(p + 5, e - p - 5), out int n) && n >= 0 && n <= 7)
        {
            return n;
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
        int num = Math.Min(frames, 16);
        List<int> outf = new List<int>();
        double stop = frames - 1;
        for (int i = 0; i < num; i++)
        {
            double v;
            if (num == 1)
            {
                v = 0;
            }
            else if (i == num - 1)
            {
                v = stop;
            }
            else
            {
                v = i * (stop / (num - 1));
            }
            int f = (int)v;
            if (outf.Count == 0 || outf[outf.Count - 1] != f)
            {
                outf.Add(f);
            }
        }
        return outf;
    }

    // points @ rot.T
    static double[] Rotated(double[] pts, Mat4 rot)
    {
        double[] outp = new double[pts.Length];
        for (int v = 0; v < pts.Length; v += 3)
        {
            rot.TransformDir(pts[v], pts[v + 1], pts[v + 2], out outp[v], out outp[v + 1], out outp[v + 2]);
        }
        return outp;
    }

    static void Bounds(double[] pts, double[] lo, double[] hi)
    {
        for (int k = 0; k < 3; k++)
        {
            lo[k] = double.MaxValue;
            hi[k] = -double.MaxValue;
        }
        for (int v = 0; v < pts.Length; v += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                if (pts[v + k] < lo[k])
                {
                    lo[k] = pts[v + k];
                }
                if (pts[v + k] > hi[k])
                {
                    hi[k] = pts[v + k];
                }
            }
        }
    }
}
