using System.Numerics;
using SharpGLTF.Schema2;
using SkiaSharp;

namespace DotrAssetModder;
// Mostly AI

// glTF source -> Scene, a port of tools/toglb.py's read_glb (DotR-PC). The file is parsed by
// SharpGLTF, as everywhere else in this project; what is kept from the port is the Scene it
// fills and the 30 fps resampling (slerp, gltf_sample).
public static class GltfSourceReader
{
    // A 1x1 PNG handed to SharpGLTF for an image file that is not where the glTF says, so the
    // rest of the model still loads; a material whose image is this has no texture.
    static readonly byte[] MissingImage = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");

    static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tga", ".dds", ".ktx2" };

    // .glb or .gltf with embedded or side-by-side buffers. Rips are often not valid glTF, so
    // nothing is validated; an image file is also looked for beside the model and up to two
    // folders below it (SourceImages), and one that is not found anywhere gets MissingImage.
    static ModelRoot Load(string path, SourceImages images, Action<string> warn)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        ReadContext context = ReadContext.Create(name =>
        {
            string file = Path.Combine(dir, Uri.UnescapeDataString(name));
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

    // System.Numerics keeps the translation in the bottom row; Mat4 in the right column.
    static Mat4 ToMat4(Matrix4x4 n)
    {
        float[] f = {
            n.M11, n.M12, n.M13, n.M14, n.M21, n.M22, n.M23, n.M24,
            n.M31, n.M32, n.M33, n.M34, n.M41, n.M42, n.M43, n.M44,
        };
        Mat4 m = new Mat4();
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                m[r, c] = f[c * 4 + r];
            }
        }
        return m;
    }

    // ---- slerp ----

    static void Slerp(double[] q0, int o0, double[] q1, int o1, double u, double[] outq, int oo)
    {
        double dot = 0;
        for (int k = 0; k < 4; k++)
        {
            dot += q0[o0 + k] * q1[o1 + k];
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
        double a;
        double b;
        if (dot > 0.9995)
        {
            a = 1 - u;
            b = u;
        }
        else
        {
            double st = Math.Sin(theta);
            a = Math.Sin((1 - u) * theta) / st;
            b = Math.Sin(u * theta) / st;
        }
        double len = 0;
        for (int k = 0; k < 4; k++)
        {
            double x = a * q0[o0 + k] + b * sign * q1[o1 + k];
            outq[oo + k] = x;
            len += x * x;
        }
        len = Math.Sqrt(len);
        for (int k = 0; k < 4; k++)
        {
            outq[oo + k] /= len;
        }
    }

    // ---- gltf_sample ----

    // A glTF animation sampler evaluated at times t (STEP, LINEAR, CUBICSPLINE). `v` holds
    // `w` values a key, or for CUBICSPLINE in-tangent, value, out-tangent a key.
    static double[] Sample(double[] times, double[] v, int w, string interp, double[] t, bool rotation)
    {
        int k = times.Length;
        int vstep = w;        // one key's stride in v
        int vbase = 0;        // the value's offset inside a key
        int tinBase = 0;
        int toutBase = 0;
        bool cubic = interp == "CUBICSPLINE";
        if (cubic)
        {
            vstep = 3 * w;
            tinBase = 0;
            vbase = w;
            toutBase = 2 * w;
        }
        double[] outv = new double[t.Length * w];
        if (k == 1)
        {
            for (int f = 0; f < t.Length; f++)
            {
                for (int c = 0; c < w; c++)
                {
                    outv[f * w + c] = v[vbase + c];
                }
            }
            return outv;
        }
        for (int f = 0; f < t.Length; f++)
        {
            int seg = SearchRight(times, t[f]) - 1;
            if (seg < 0)
            {
                seg = 0;
            }
            if (seg > k - 2)
            {
                seg = k - 2;
            }
            double t0 = times[seg];
            double t1 = times[seg + 1];
            double dt = 1.0;
            if (t1 > t0)
            {
                dt = t1 - t0;
            }
            double u = (t[f] - t0) / dt;
            if (u < 0.0)
            {
                u = 0.0;
            }
            if (u > 1.0)
            {
                u = 1.0;
            }
            int a = seg * vstep + vbase;
            int b = (seg + 1) * vstep + vbase;
            if (interp == "STEP")
            {
                int src = a;
                if (u >= 1.0)
                {
                    src = b;
                }
                for (int c = 0; c < w; c++)
                {
                    outv[f * w + c] = v[src + c];
                }
            }
            else if (cubic)
            {
                double u2 = u * u;
                double u3 = u * u * u;
                int ta = seg * vstep + toutBase;
                int tb = (seg + 1) * vstep + tinBase;
                for (int c = 0; c < w; c++)
                {
                    outv[f * w + c] = (2 * u3 - 3 * u2 + 1) * v[a + c] + (u3 - 2 * u2 + u) * v[ta + c] * dt
                        + (-2 * u3 + 3 * u2) * v[b + c] + (u3 - u2) * v[tb + c] * dt;
                }
            }
            else if (rotation)
            {
                Slerp(v, a, v, b, u, outv, f * w);
            }
            else
            {
                for (int c = 0; c < w; c++)
                {
                    outv[f * w + c] = (1 - u) * v[a + c] + u * v[b + c];
                }
            }
        }
        if (rotation)
        {
            for (int f = 0; f < t.Length; f++)
            {
                double len = 0;
                for (int c = 0; c < w; c++)
                {
                    len += outv[f * w + c] * outv[f * w + c];
                }
                len = Math.Sqrt(len);
                for (int c = 0; c < w; c++)
                {
                    outv[f * w + c] /= len;
                }
            }
        }
        return outv;
    }

    // np.searchsorted(times, x, side="right"): how many times are <= x
    static int SearchRight(double[] times, double x)
    {
        int lo = 0;
        int hi = times.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (times[mid] <= x)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    // One channel's keys as gltf_sample takes them: the times, and the values flattened `w` a
    // key (in-tangent, value, out-tangent a key for CUBICSPLINE).
    static void Keys<T>(IAnimationSampler<T> s, Func<T, double[]> row, out double[] times, out double[] vals, out string interp)
    {
        List<double> ts = new List<double>();
        List<double> vs = new List<double>();
        interp = s.InterpolationMode.ToString();
        if (s.InterpolationMode == AnimationInterpolationMode.CUBICSPLINE)
        {
            foreach ((float time, (T tin, T value, T tout)) in s.GetCubicKeys())
            {
                ts.Add(time);
                vs.AddRange(row(tin));
                vs.AddRange(row(value));
                vs.AddRange(row(tout));
            }
        }
        else
        {
            foreach ((float time, T value) in s.GetLinearKeys())
            {
                ts.Add(time);
                vs.AddRange(row(value));
            }
        }
        times = ts.ToArray();
        vals = vs.ToArray();
    }

    static double[] Row(Vector3 v)
    {
        return new double[] { v.X, v.Y, v.Z };
    }

    static double[] Row(Quaternion q)
    {
        return new double[] { q.X, q.Y, q.Z, q.W };
    }

    // ---- read_glb ----

    // A glTF scene as it is: glTF is already +Y up, +Z front and in metres.
    public static Scene Read(string path, Action<string> warn)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        SourceImages images = new SourceImages(dir, warn);
        ModelRoot model = Load(path, images, warn);
        Scene sc = new Scene();
        IReadOnlyList<Node> gnodes = model.LogicalNodes;
        // per glTF node: translation, rotation, scale (a matrix node is decomposed)
        List<double[]> restT = new List<double[]>();
        List<double[]> restQ = new List<double[]>();
        List<double[]> restS = new List<double[]>();
        for (int k = 0; k < gnodes.Count; k++)
        {
            Node nd = gnodes[k];
            string name = nd.Name ?? "";
            if (name.Length == 0)
            {
                name = "node" + k;
            }
            sc.AddNode(name);
            var lt = nd.LocalTransform;
            double[] t;
            double[] q;
            double[] s;
            if (lt.IsMatrix)
            {
                Mat4 m = ToMat4(lt.Matrix);
                sc.Nodes[k].Local = m;
                t = new double[3];
                q = new double[4];
                s = new double[3];
                m.Decompose(t, q, s);
            }
            else
            {
                t = Row(lt.Translation);
                q = Row(lt.Rotation);
                s = Row(lt.Scale);
                sc.Nodes[k].Local = Mat4.FromTrs(t, q, s);
            }
            restT.Add(t);
            restQ.Add(q);
            restS.Add(s);
        }
        for (int k = 0; k < gnodes.Count; k++)
        {
            foreach (Node c in gnodes[k].VisualChildren)
            {
                sc.Nodes[c.LogicalIndex].Parent = k;
            }
        }
        // nodes outside the scene shown are left out by giving them no meshes or joints
        HashSet<int> shown = new HashSet<int>();
        Stack<Node> stack = new Stack<Node>();
        if (model.DefaultScene != null)
        {
            foreach (Node n in model.DefaultScene.VisualChildren)
            {
                stack.Push(n);
            }
        }
        else
        {
            foreach (Node n in gnodes)
            {
                stack.Push(n);
            }
        }
        while (stack.Count > 0)
        {
            Node n = stack.Pop();
            shown.Add(n.LogicalIndex);
            foreach (Node c in n.VisualChildren)
            {
                stack.Push(c);
            }
        }

        Dictionary<int, SKBitmap?> imageCache = new Dictionary<int, SKBitmap?>();
        Dictionary<int, int> matIndex = new Dictionary<int, int>();
        Dictionary<int, int> matSet = new Dictionary<int, int>(); // glTF material -> its base-colour TEXCOORD set

        for (int k = 0; k < gnodes.Count; k++)
        {
            Node nd = gnodes[k];
            if (nd.Mesh == null || !shown.Contains(k))
            {
                continue;
            }
            Mesh gm = nd.Mesh;
            Skin? skin = nd.Skin;
            List<int> joints = new List<int>();
            if (skin != null)
            {
                for (int ji = 0; ji < skin.JointsCount; ji++)
                {
                    (Node joint, Matrix4x4 inverseBind) = skin.GetJoint(ji);
                    joints.Add(joint.LogicalIndex);
                    if (sc.Nodes[joint.LogicalIndex].Bind == null)
                    {
                        sc.Nodes[joint.LogicalIndex].Bind = ToMat4(inverseBind).Inverse();
                    }
                }
            }
            List<double> cps = new List<double>();
            List<double> nrms = new List<double>();
            bool anyNoNormal = false;
            List<double> uvs = new List<double>();
            List<int> tris = new List<int>();
            List<int> tmat = new List<int>();
            List<List<Influence>> infl = new List<List<Influence>>();
            int bse = 0;
            foreach (MeshPrimitive prim in gm.Primitives)
            {
                if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES)
                {
                    string mname = gm.Name ?? "";
                    if (mname.Length == 0)
                    {
                        mname = "mesh";
                    }
                    warn(mname + ": primitive mode " + (int)prim.DrawPrimitiveType + " skipped (triangles only)");
                    continue;
                }
                IList<Vector3> pos = prim.GetVertexAccessor("POSITION").AsVector3Array();
                int np = pos.Count;
                int[] idx;
                if (prim.IndexAccessor != null)
                {
                    IList<uint> ia = prim.IndexAccessor.AsIndicesArray();
                    idx = new int[ia.Count];
                    for (int e = 0; e < idx.Length; e++)
                    {
                        idx[e] = (int)ia[e];
                    }
                }
                else
                {
                    idx = new int[np];
                    for (int e = 0; e < np; e++)
                    {
                        idx[e] = e;
                    }
                }
                Material(prim.Material, sc, imageCache, matIndex, matSet, warn, out int mi, out int tset);
                Accessor? na = prim.GetVertexAccessor("NORMAL");
                if (na != null)
                {
                    IList<Vector3> nv = na.AsVector3Array();
                    for (int e = 0; e < np; e++)
                    {
                        nrms.Add(nv[e].X);
                        nrms.Add(nv[e].Y);
                        nrms.Add(nv[e].Z);
                    }
                }
                else
                {
                    anyNoNormal = true;
                    for (int e = 0; e < np; e++)
                    {
                        nrms.Add(0);
                        nrms.Add(0);
                        nrms.Add(0);
                    }
                }
                Accessor? ua = prim.GetVertexAccessor("TEXCOORD_" + tset);
                if (ua != null)
                {
                    IList<Vector2> uv = ua.AsVector2Array();
                    for (int e = 0; e < np; e++)
                    {
                        uvs.Add(uv[e].X);
                        // glTF is top-left; the scene keeps bottom-left (write_glb flips back)
                        uvs.Add(1.0 - uv[e].Y);
                    }
                }
                else
                {
                    for (int e = 0; e < np; e++)
                    {
                        uvs.Add(0);
                        uvs.Add(0);
                    }
                }
                if (skin != null)
                {
                    List<List<Influence>> d = new List<List<Influence>>();
                    for (int e = 0; e < np; e++)
                    {
                        d.Add(new List<Influence>());
                    }
                    for (int set = 0; set < 4; set++)
                    {
                        Accessor? ja = prim.GetVertexAccessor("JOINTS_" + set);
                        if (ja == null)
                        {
                            break;
                        }
                        IList<Vector4> jn = ja.AsVector4Array();
                        IList<Vector4> wv = prim.GetVertexAccessor("WEIGHTS_" + set).AsVector4Array();
                        for (int c = 0; c < 4; c++)
                        {
                            for (int v = 0; v < np; v++)
                            {
                                double w = Component(wv[v], c);
                                if (!(w > 0))
                                {
                                    continue;
                                }
                                int node = joints[(int)Component(jn[v], c)];
                                List<Influence> list = d[v];
                                bool found = false;
                                for (int q = 0; q < list.Count; q++)
                                {
                                    if (list[q].Joint == node)
                                    {
                                        list[q].Weight += w;
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
                    for (int v = 0; v < np; v++)
                    {
                        if (d[v].Count == 0)
                        {
                            d[v].Add(new Influence(k, 1.0)); // unweighted: rides the mesh's own node
                        }
                    }
                    infl.AddRange(d);
                }
                for (int e = 0; e < np; e++)
                {
                    cps.Add(pos[e].X);
                    cps.Add(pos[e].Y);
                    cps.Add(pos[e].Z);
                }
                for (int e = 0; e < idx.Length; e++)
                {
                    tris.Add(idx[e] + bse);
                }
                for (int e = 0; e < idx.Length / 3; e++)
                {
                    tmat.Add(mi);
                }
                bse += np;
            }
            if (cps.Count == 0)
            {
                continue;
            }
            double[] cp = cps.ToArray();
            int[] corners = tris.ToArray();
            double[]? cornerNrm = null;
            if (!anyNoNormal)
            {
                cornerNrm = new double[corners.Length * 3];
                for (int e = 0; e < corners.Length; e++)
                {
                    cornerNrm[e * 3] = nrms[corners[e] * 3];
                    cornerNrm[e * 3 + 1] = nrms[corners[e] * 3 + 1];
                    cornerNrm[e * 3 + 2] = nrms[corners[e] * 3 + 2];
                }
            }
            double[] cornerUv = new double[corners.Length * 2];
            for (int e = 0; e < corners.Length; e++)
            {
                cornerUv[e * 2] = uvs[corners[e] * 2];
                cornerUv[e * 2 + 1] = uvs[corners[e] * 2 + 1];
            }
            string meshName = gm.Name ?? "";
            if (meshName.Length == 0)
            {
                meshName = nd.Name ?? "";
            }
            if (meshName.Length == 0)
            {
                meshName = "mesh";
            }
            SceneMesh mesh = new SceneMesh(meshName, k);
            List<Influence>[]? cpInfl = null;
            if (skin != null)
            {
                cpInfl = infl.ToArray();
                if (sc.Nodes[k].Bind == null && infl.Any(list => list.Any(f => f.Joint == k)))
                {
                    sc.Nodes[k].Bind = sc.NodeWorld(k);
                }
            }
            int[] triCorners = new int[corners.Length];
            for (int e = 0; e < corners.Length; e++)
            {
                triCorners[e] = e;
            }
            MeshBuild.Build(mesh, cp, corners, cornerNrm, cornerUv, triCorners, tmat.ToArray(), cpInfl);
            sc.Meshes.Add(mesh);
        }

        int ai = 0;
        foreach (Animation ga in model.LogicalAnimations)
        {
            List<int> chNode = new List<int>();
            List<string> chPath = new List<string>();
            List<double[]> chTimes = new List<double[]>();
            List<double[]> chVals = new List<double[]>();
            List<string> chInterp = new List<string>();
            double end = 0.0;
            foreach (AnimationChannel ch in ga.Channels)
            {
                if (ch.TargetNode == null)
                {
                    continue;
                }
                double[] times;
                double[] vals;
                string interp;
                string pathName;
                if (ch.TargetNodePath == PropertyPath.translation)
                {
                    Keys(ch.GetTranslationSampler(), Row, out times, out vals, out interp);
                    pathName = "translation";
                }
                else if (ch.TargetNodePath == PropertyPath.rotation)
                {
                    Keys(ch.GetRotationSampler(), Row, out times, out vals, out interp);
                    pathName = "rotation";
                }
                else if (ch.TargetNodePath == PropertyPath.scale)
                {
                    Keys(ch.GetScaleSampler(), Row, out times, out vals, out interp);
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
                chNode.Add(ch.TargetNode.LogicalIndex);
                chPath.Add(pathName);
                chTimes.Add(times);
                chVals.Add(vals);
                chInterp.Add(interp);
                end = Math.Max(end, times[times.Length - 1]);
            }
            if (chNode.Count == 0)
            {
                ai++;
                continue;
            }
            int frames = (int)Math.Round(end * Scene.Fps, MidpointRounding.ToEven) + 1;
            double[] t = new double[frames];
            for (int f = 0; f < frames; f++)
            {
                t[f] = (double)f / Scene.Fps;
            }
            string aname = ga.Name ?? "";
            if (aname.Length == 0)
            {
                aname = "animation" + ai;
            }
            SceneAnim anim = new SceneAnim(aname, frames);
            Dictionary<int, double[][]> trs = new Dictionary<int, double[][]>();
            List<int> trsOrder = new List<int>();
            for (int c = 0; c < chNode.Count; c++)
            {
                int node = chNode[c];
                if (!trs.ContainsKey(node))
                {
                    double[][] slots = new double[3][];
                    slots[0] = Tile(restT[node], frames);
                    slots[1] = Tile(restQ[node], frames);
                    slots[2] = Tile(restS[node], frames);
                    trs[node] = slots;
                    trsOrder.Add(node);
                }
                int slot = 0;
                int w = 3;
                if (chPath[c] == "rotation")
                {
                    slot = 1;
                    w = 4;
                }
                else if (chPath[c] == "scale")
                {
                    slot = 2;
                }
                trs[node][slot] = Sample(chTimes[c], chVals[c], w, chInterp[c], t, chPath[c] == "rotation");
            }
            foreach (int node in trsOrder)
            {
                double[][] slots = trs[node];
                Mat4[] mats = new Mat4[frames];
                double[] tt = new double[3];
                double[] qq = new double[4];
                double[] ss = new double[3];
                for (int f = 0; f < frames; f++)
                {
                    tt[0] = slots[0][f * 3];
                    tt[1] = slots[0][f * 3 + 1];
                    tt[2] = slots[0][f * 3 + 2];
                    qq[0] = slots[1][f * 4];
                    qq[1] = slots[1][f * 4 + 1];
                    qq[2] = slots[1][f * 4 + 2];
                    qq[3] = slots[1][f * 4 + 3];
                    ss[0] = slots[2][f * 3];
                    ss[1] = slots[2][f * 3 + 1];
                    ss[2] = slots[2][f * 3 + 2];
                    mats[f] = Mat4.FromTrs(tt, qq, ss);
                }
                anim.Tracks[node] = mats;
            }
            sc.Anims.Add(anim);
            ai++;
        }
        return sc;
    }

    static double Component(Vector4 v, int c)
    {
        if (c == 0)
        {
            return v.X;
        }
        if (c == 1)
        {
            return v.Y;
        }
        if (c == 2)
        {
            return v.Z;
        }
        return v.W;
    }

    static double[] Tile(double[] row, int frames)
    {
        double[] o = new double[row.Length * frames];
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < row.Length; c++)
            {
                o[f * row.Length + c] = row[c];
            }
        }
        return o;
    }

    // read_glb's image(): one bitmap per glTF image, shared by the materials that use it
    static SKBitmap? Image(Image im, Dictionary<int, SKBitmap?> cache, Action<string> warn)
    {
        if (!cache.ContainsKey(im.LogicalIndex))
        {
            byte[] data = im.Content.Content.ToArray();
            SKBitmap? bmp = null;
            if (!data.AsSpan().SequenceEqual(MissingImage))
            {
                bmp = SourceImages.Decode(data);
                if (bmp == null)
                {
                    warn("texture " + (im.Name ?? "image" + im.LogicalIndex) + " did not load");
                }
            }
            cache[im.LogicalIndex] = bmp;
        }
        return cache[im.LogicalIndex];
    }

    // read_glb's material(): a primitive's material index in the scene, and its TEXCOORD set
    static void Material(Material? m, Scene sc, Dictionary<int, SKBitmap?> imageCache,
        Dictionary<int, int> matIndex, Dictionary<int, int> matSet, Action<string> warn, out int index, out int tset)
    {
        int mi = m == null ? -1 : m.LogicalIndex; // -1: the primitive has no material (Python's None)
        if (!matIndex.ContainsKey(mi))
        {
            if (m == null)
            {
                matIndex[mi] = sc.AddMaterial("default", new double[] { 1.0, 1.0, 1.0, 1.0 }, null);
                matSet[mi] = 0;
                index = matIndex[mi];
                tset = 0;
                return;
            }
            double[] color = new double[] { 1, 1, 1, 1 };
            SKBitmap? img = null;
            matSet[mi] = 0;
            MaterialChannel? bc = m.FindChannel("BaseColor");
            if (bc.HasValue)
            {
                Vector4 f = bc.Value.Color;
                color = new double[] { f.X, f.Y, f.Z, f.W };
                Texture? tex = bc.Value.Texture;
                if (tex != null)
                {
                    Image? src = tex.FallbackImage ?? tex.PrimaryImage;
                    if (src != null)
                    {
                        img = Image(src, imageCache, warn);
                    }
                    matSet[mi] = bc.Value.TextureCoordinate;
                }
            }
            string name = m.Name ?? "";
            if (name.Length == 0)
            {
                name = "material" + mi;
            }
            matIndex[mi] = sc.AddMaterial(name, color, img);
        }
        index = matIndex[mi];
        tset = matSet[mi];
    }
}
