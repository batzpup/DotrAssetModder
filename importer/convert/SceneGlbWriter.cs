using System.Numerics;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;
using SkiaSharp;

namespace DotrAssetModder;
// Mostly AI

// A finished Scene -> the .glb DotrAssetModder's custom-model import reads, a port of
// tools/toglb.py's png_bytes and write_glb (DotR-PC), written with SharpGLTF like every other
// glb here. The glb comes back as bytes; nothing is written to disk here.

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
    public int Flat;
    public int Tris;
    public int Verts;
    public List<GlbAnimInfo> Anims = new List<GlbAnimInfo>();
}

public static class SceneGlbWriter
{
    // A PNG of the image, at most max_tex pixels a side. An image whose alpha is all opaque
    // loses its alpha channel, as Pillow's convert("RGB") does.
    public static byte[] PngBytes(SKBitmap im, int maxTex)
    {
        SKBitmap bmp = im;
        bool opaque = true;
        for (int y = 0; y < bmp.Height && opaque; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y).Alpha != 255)
                {
                    opaque = false;
                    break;
                }
            }
        }
        int w = bmp.Width;
        int h = bmp.Height;
        if (Math.Max(w, h) > maxTex)
        {
            // Pillow's thumbnail: the largest side becomes max_tex, the other keeps the aspect
            // ratio (floored or ceiled, whichever is nearer), and never drops below 1.
            double aspect = (double)w / h;
            w = maxTex;
            h = maxTex;
            if ((double)w / h >= aspect)
            {
                w = RoundAspect(h * aspect, h, aspect, true);
            }
            else
            {
                h = RoundAspect(w / aspect, w, aspect, false);
            }
        }
        if (w != bmp.Width || h != bmp.Height)
        {
            SKBitmap small = bmp.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                SKFilterQuality.High);
            bmp = small;
        }
        if (opaque)
        {
            SKBitmap rgb = new SKBitmap(new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            bmp.CopyTo(rgb, SKColorType.Rgba8888);
            bmp = rgb;
        }
        SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // Pillow's round_aspect: floor or ceil, whichever comes nearer the aspect ratio, at least 1.
    static int RoundAspect(double number, int other, double aspect, bool widthSide)
    {
        int lo = (int)Math.Floor(number);
        int hi = (int)Math.Ceiling(number);
        double dlo = Key(lo, other, aspect, widthSide);
        double dhi = Key(hi, other, aspect, widthSide);
        int best = lo;
        if (dhi < dlo)
        {
            best = hi;
        }
        if (best < 1)
        {
            best = 1;
        }
        return best;
    }

    static double Key(int n, int other, double aspect, bool widthSide)
    {
        if (widthSide)
        {
            return Math.Abs(aspect - (double)n / other);
        }
        if (n == 0)
        {
            return 0;
        }
        return Math.Abs(aspect - (double)other / n);
    }

    // A rigid mesh's node and the meshes on it, in the order the meshes were read.
    sealed class RigidGroup
    {
        public int Node;
        public List<SceneMesh> Meshes = new List<SceneMesh>();
    }

    public static GlbWriteInfo WriteGlb(Scene sc, int maxTex, bool flipUv, double[]? pivotAt = null)
    {
        int n = sc.Nodes.Count;
        List<int> order = sc.Order();
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
        HashSet<int> joints = new HashSet<int>();
        List<RigidGroup> rigid = new List<RigidGroup>();
        List<SceneMesh> skinned = new List<SceneMesh>();
        foreach (SceneMesh m in sc.Meshes)
        {
            if (m.Skinned)
            {
                skinned.Add(m);
                foreach (int jn in SceneFinish.UniqueJoints(m))
                {
                    joints.Add(jn);
                }
            }
            else
            {
                RigidGroup? g = null;
                foreach (RigidGroup r in rigid)
                {
                    if (r.Node == m.Node)
                    {
                        g = r;
                    }
                }
                if (g == null)
                {
                    g = new RigidGroup();
                    g.Node = m.Node;
                    rigid.Add(g);
                }
                g.Meshes.Add(m);
            }
        }
        bool[] isRigidNode = new bool[n];
        foreach (RigidGroup r in rigid)
        {
            isRigidNode[r.Node] = true;
        }
        bool[] keep = new bool[n];
        for (int k = order.Count - 1; k >= 0; k--)
        {
            int i = order[k];
            bool any = false;
            foreach (int c in kids[i])
            {
                if (keep[c])
                {
                    any = true;
                }
            }
            keep[i] = joints.Contains(i) || isRigidNode[i] || any;
        }

        ModelRoot model = ModelRoot.CreateModel();
        model.Asset.Generator = "DotrAssetModder";
        // dotr_root: dropped by the importer. dotr_world: node 0, the world placement. dotr_pivot:
        // node 1, the pivot. Both at the source's own origin, unless `pivotAt` moves the
        // pivot; dotr_model then takes the opposite move so the body stays where it is
        // around node 0.
        // dotr_model: the holder of everything else; it needs two children or more, or the
        // importer's wrapper peel walks past it and takes the wrong pair as node 0 and node 1.
        // Nodes are made parents first, in Scene.Order, so their indices and child order are
        // the scene's.
        Node root = model.UseScene(0).CreateNode("dotr_root");
        Node world = root.CreateNode("dotr_world");
        Node pivot = world.CreateNode("dotr_pivot");
        Node holder = pivot.CreateNode("dotr_model");
        if (pivotAt != null && (pivotAt[0] != 0 || pivotAt[1] != 0 || pivotAt[2] != 0))
        {
            Vector3 at = V3(pivotAt, 0);
            pivot.LocalTransform = new SharpGLTF.Transforms.AffineTransform(null, null, at);
            holder.LocalTransform = new SharpGLTF.Transforms.AffineTransform(null, null, -at);
        }
        Node?[] outNode = new Node?[n];
        double[] t3 = new double[3];
        double[] q4 = new double[4];
        double[] s3 = new double[3];
        foreach (int i in order)
        {
            if (!keep[i])
            {
                continue;
            }
            Node parent = holder;
            if (sc.Nodes[i].Parent >= 0)
            {
                parent = outNode[sc.Nodes[i].Parent]!;
            }
            Node nd = parent.CreateNode(sc.Nodes[i].Name);
            sc.Nodes[i].Local.Decompose(t3, q4, s3);
            nd.LocalTransform = new SharpGLTF.Transforms.AffineTransform(V3(s3, 0), Quat(q4, 0), V3(t3, 0));
            outNode[i] = nd;
        }

        // materials
        Material?[] matOut = new Material?[sc.Materials.Count];
        List<byte[]> texKeys = new List<byte[]>();
        List<Material> texMats = new List<Material>();
        List<double[]> colKeys = new List<double[]>();
        List<Material> colMats = new List<Material>();
        Dictionary<SKBitmap, byte[]> pngCache = new Dictionary<SKBitmap, byte[]>();
        List<int> used = new List<int>();
        foreach (SceneMesh m in sc.Meshes)
        {
            HashSet<int> seen = new HashSet<int>();
            foreach (int mi in m.TMat)
            {
                if (!seen.Add(mi))
                {
                    continue;
                }
                if (!used.Contains(mi))
                {
                    used.Add(mi);
                }
            }
        }
        foreach (int mi in used)
        {
            SceneMaterial mat = sc.Materials[mi];
            if (mat.Image != null)
            {
                byte[]? png;
                if (!pngCache.TryGetValue(mat.Image, out png))
                {
                    png = PngBytes(mat.Image, maxTex);
                    pngCache[mat.Image] = png;
                }
                Material? same = null;
                for (int k = 0; k < texKeys.Count; k++)
                {
                    if (SameBytes(texKeys[k], png!))
                    {
                        same = texMats[k];
                    }
                }
                if (same != null)
                {
                    matOut[mi] = same;
                    continue;
                }
                int img = texKeys.Count;
                Image im = model.CreateImage($"tex{img}");
                im.Content = new MemoryImage(png!);
                // no sampler: the importer and viewer take glTF's default, repeat
                Texture tex = model.UseTexture(im, (TextureSampler)null!);
                Material gm = NewMaterial(model, $"mat_tex{img}");
                gm.FindChannel("BaseColor")!.Value.SetTexture(0, tex);
                texKeys.Add(png!);
                texMats.Add(gm);
                matOut[mi] = gm;
            }
            else
            {
                double[] key = new double[4];
                for (int k = 0; k < 4; k++)
                {
                    key[k] = MeshBuild.NpRound(mat.Color[k], 3);
                }
                Material? same = null;
                for (int k = 0; k < colKeys.Count; k++)
                {
                    if (SameColour(colKeys[k], key))
                    {
                        same = colMats[k];
                    }
                }
                if (same != null)
                {
                    matOut[mi] = same;
                    continue;
                }
                Material gm = NewMaterial(model, $"mat_color{colKeys.Count}");
                MaterialChannel bc = gm.FindChannel("BaseColor")!.Value;
                bc.Color = new Vector4((float)key[0], (float)key[1], (float)key[2], (float)key[3]);
                colKeys.Add(key);
                colMats.Add(gm);
                matOut[mi] = gm;
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
        int[] slot = new int[n + 1]; // node -> skin joint index; index n: no influence
        for (int k = 0; k < jointList.Count; k++)
        {
            slot[jointList[k]] = k;
        }

        foreach (RigidGroup r in rigid)
        {
            Mesh me = model.CreateMesh(r.Meshes[0].Name);
            Primitives(model, me, r.Meshes, false, slot, matOut, n, flipUv);
            outNode[r.Node]!.Mesh = me;
        }
        if (skinned.Count > 0)
        {
            Mat4[] worldOf = new Mat4[n];
            foreach (int i in order)
            {
                int p = sc.Nodes[i].Parent;
                if (p >= 0)
                {
                    worldOf[i] = worldOf[p] * sc.Nodes[i].Local;
                }
                else
                {
                    worldOf[i] = sc.Nodes[i].Local;
                }
            }
            List<(Node, Matrix4x4)> binds = new List<(Node, Matrix4x4)>();
            foreach (int jn in jointList)
            {
                binds.Add((outNode[jn]!, ToNumerics(worldOf[jn].Inverse())));
            }
            Skin skin = model.CreateSkin();
            skin.BindJoints(binds);
            Node nd = holder.CreateNode("dotr_skinned");
            Mesh me = model.CreateMesh("skinned");
            Primitives(model, me, skinned, true, slot, matOut, n, flipUv);
            nd.Mesh = me;
            nd.Skin = skin;
        }

        if (holder.VisualChildren.Count() < 2)
        {
            holder.CreateNode("dotr_anchor");
        }

        List<GlbAnimInfo> animInfo = new List<GlbAnimInfo>();
        foreach (SceneAnim anim in sc.Anims)
        {
            List<int> tracks = new List<int>();
            foreach (int i in SceneFinish.TrackNodes(anim))
            {
                if (keep[i])
                {
                    tracks.Add(i);
                }
            }
            if (tracks.Count == 0)
            {
                continue;
            }
            float[] times = new float[anim.Frames];
            for (int f = 0; f < anim.Frames; f++)
            {
                times[f] = (float)((double)f / Scene.Fps);
            }
            Animation an = model.CreateAnimation(anim.Name);
            bool anyChannel = false;
            foreach (int i in tracks)
            {
                Mat4[] tr = anim.Tracks[i];
                int frames = tr.Length;
                double[] T = new double[frames * 3];
                double[] Q = new double[frames * 4];
                double[] S = new double[frames * 3];
                for (int f = 0; f < frames; f++)
                {
                    tr[f].Decompose(t3, q4, s3);
                    for (int k = 0; k < 3; k++)
                    {
                        T[f * 3 + k] = t3[k];
                        S[f * 3 + k] = s3[k];
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        Q[f * 4 + k] = q4[k];
                    }
                }
                for (int f = 1; f < frames; f++)
                {
                    double dot = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        dot += Q[f * 4 + k] * Q[(f - 1) * 4 + k];
                    }
                    if (dot < 0)
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            Q[f * 4 + k] = -Q[f * 4 + k];
                        }
                    }
                }
                double[] rt = new double[3];
                double[] rq = new double[4];
                double[] rs = new double[3];
                sc.Nodes[i].Local.Decompose(rt, rq, rs);
                // A channel that never leaves the rest value says nothing (the importer reads the
                // rest value where there is no channel). The animation's first channel is kept
                // either way, so the animation keeps its length even when nothing moves.
                bool movesT = !CloseTo(T, rt, 3, 1e-5);
                bool movesS = !CloseTo(S, rs, 3, 1e-6);
                bool movesQ = false;
                for (int f = 0; f < frames; f++)
                {
                    double dot = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        dot += Q[f * 4 + k] * rq[k];
                    }
                    if (Math.Abs(Math.Abs(dot) - 1.0) > 1e-10)
                    {
                        movesQ = true;
                    }
                }
                Node target = outNode[i]!;
                if (movesT || !anyChannel)
                {
                    Dictionary<float, Vector3> keys = new Dictionary<float, Vector3>();
                    for (int f = 0; f < frames; f++)
                    {
                        keys[times[f]] = V3(T, f * 3);
                    }
                    an.CreateTranslationChannel(target, keys, true);
                    anyChannel = true;
                }
                if (movesQ)
                {
                    Dictionary<float, Quaternion> keys = new Dictionary<float, Quaternion>();
                    for (int f = 0; f < frames; f++)
                    {
                        keys[times[f]] = Quat(Q, f * 4);
                    }
                    an.CreateRotationChannel(target, keys, true);
                }
                if (movesS)
                {
                    Dictionary<float, Vector3> keys = new Dictionary<float, Vector3>();
                    for (int f = 0; f < frames; f++)
                    {
                        keys[times[f]] = V3(S, f * 3);
                    }
                    an.CreateScaleChannel(target, keys, true);
                }
            }
            animInfo.Add(new GlbAnimInfo(anim.Name, anim.Frames));
        }

        GlbWriteInfo info = new GlbWriteInfo();
        info.Nodes = model.LogicalNodes.Count;
        info.Joints = jointList.Count;
        info.Textures = texKeys.Count;
        info.Flat = colKeys.Count;
        foreach (SceneMesh m in sc.Meshes)
        {
            info.Tris += m.Tris.Length / 3;
            info.Verts += m.VertexCount;
        }
        info.Anims = animInfo;
        info.Glb = model.WriteGLB().ToArray();
        return info;
    }

    // A double-sided material, not metallic and fully rough, as toglb.py writes them.
    static Material NewMaterial(ModelRoot model, string name)
    {
        Material gm = model.CreateMaterial(name);
        gm.InitializePBRMetallicRoughness();
        gm.DoubleSided = true;
        MaterialChannel mr = gm.FindChannel("MetallicRoughness")!.Value;
        mr.SetFactor("MetallicFactor", 0f);
        mr.SetFactor("RoughnessFactor", 1f);
        return gm;
    }

    static void Primitives(ModelRoot model, Mesh mesh, List<SceneMesh> meshes, bool skin, int[] slot,
        Material?[] matOut, int n, bool flipUv)
    {
        foreach (SceneMesh m in meshes)
        {
            SortedSet<int> mats = new SortedSet<int>();
            foreach (int mi in m.TMat)
            {
                mats.Add(mi);
            }
            foreach (int mi in mats)
            {
                List<int> tris = new List<int>();
                for (int t = 0; t < m.TMat.Length; t++)
                {
                    if (m.TMat[t] == mi)
                    {
                        tris.Add(m.Tris[t * 3]);
                        tris.Add(m.Tris[t * 3 + 1]);
                        tris.Add(m.Tris[t * 3 + 2]);
                    }
                }
                SortedSet<int> uniq = new SortedSet<int>(tris);
                int[] usedV = new int[uniq.Count];
                Dictionary<int, int> back = new Dictionary<int, int>();
                int at = 0;
                foreach (int v in uniq)
                {
                    usedV[at] = v;
                    back[v] = at;
                    at++;
                }
                int[] remap = new int[tris.Count];
                for (int k = 0; k < tris.Count; k++)
                {
                    remap[k] = back[tris[k]];
                }
                double[] pos = new double[usedV.Length * 3];
                double[] nrm = new double[usedV.Length * 3];
                double[] uv = new double[usedV.Length * 2];
                for (int k = 0; k < usedV.Length; k++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        pos[k * 3 + c] = m.Pos[usedV[k] * 3 + c];
                        nrm[k * 3 + c] = m.Nrm[usedV[k] * 3 + c];
                    }
                    uv[k * 2] = m.Uv[usedV[k] * 2];
                    uv[k * 2 + 1] = m.Uv[usedV[k] * 2 + 1];
                    // FBX and COLLADA put the UV origin bottom-left, glTF top-left; --flip-uv is
                    // for sources already written top-left (Unity rips, e.g. Duel Links FBX)
                    if (!flipUv)
                    {
                        uv[k * 2 + 1] = 1.0 - uv[k * 2 + 1];
                    }
                }
                MeshPrimitive prim = mesh.CreatePrimitive();
                prim.SetVertexAccessor("POSITION", FloatAccessor(model, pos, DimensionType.VEC3, 3));
                prim.SetVertexAccessor("NORMAL", FloatAccessor(model, nrm, DimensionType.VEC3, 3));
                prim.SetVertexAccessor("TEXCOORD_0", FloatAccessor(model, uv, DimensionType.VEC2, 2));
                if (skin)
                {
                    int[] js0 = new int[usedV.Length * 4];
                    int[] js1 = new int[usedV.Length * 4];
                    double[] w0 = new double[usedV.Length * 4];
                    double[] w1 = new double[usedV.Length * 4];
                    bool second = false;
                    for (int k = 0; k < usedV.Length; k++)
                    {
                        for (int c = 0; c < 8; c++)
                        {
                            int jn = m.Jnt![usedV[k] * 8 + c];
                            double w = m.Wgt![usedV[k] * 8 + c];
                            int sl = slot[n];
                            if (jn >= 0)
                            {
                                sl = slot[jn];
                            }
                            else
                            {
                                w = 0.0;
                            }
                            if (c < 4)
                            {
                                js0[k * 4 + c] = sl;
                                w0[k * 4 + c] = w;
                            }
                            else
                            {
                                js1[k * 4 + c - 4] = sl;
                                w1[k * 4 + c - 4] = w;
                                if (w > 0)
                                {
                                    second = true;
                                }
                            }
                        }
                    }
                    prim.SetVertexAccessor("JOINTS_0", JointAccessor(model, js0));
                    prim.SetVertexAccessor("WEIGHTS_0", FloatAccessor(model, w0, DimensionType.VEC4, 4));
                    if (second)
                    {
                        prim.SetVertexAccessor("JOINTS_1", JointAccessor(model, js1));
                        prim.SetVertexAccessor("WEIGHTS_1", FloatAccessor(model, w1, DimensionType.VEC4, 4));
                    }
                }
                prim.Material = matOut[mi];
                prim.SetIndexAccessor(IndexAccessor(model, remap));
            }
        }
    }

    static Accessor FloatAccessor(ModelRoot model, double[] data, DimensionType dim, int comps)
    {
        byte[] bytes = new byte[data.Length * 4];
        for (int k = 0; k < data.Length; k++)
        {
            BitConverter.TryWriteBytes(new Span<byte>(bytes, k * 4, 4), (float)data[k]);
        }
        Accessor acc = model.CreateAccessor();
        acc.SetVertexData(model.UseBufferView(bytes, 0, null, 0, BufferMode.ARRAY_BUFFER), 0,
            data.Length / comps, dim, EncodingType.FLOAT, false);
        return acc;
    }

    // JOINTS_n: four skin joint indices a vertex, as unsigned shorts.
    static Accessor JointAccessor(ModelRoot model, int[] data)
    {
        byte[] bytes = new byte[data.Length * 2];
        for (int k = 0; k < data.Length; k++)
        {
            BitConverter.TryWriteBytes(new Span<byte>(bytes, k * 2, 2), (ushort)data[k]);
        }
        Accessor acc = model.CreateAccessor();
        acc.SetVertexData(model.UseBufferView(bytes, 0, null, 0, BufferMode.ARRAY_BUFFER), 0,
            data.Length / 4, DimensionType.VEC4, EncodingType.UNSIGNED_SHORT, false);
        return acc;
    }

    static Accessor IndexAccessor(ModelRoot model, int[] data)
    {
        byte[] bytes = new byte[data.Length * 4];
        for (int k = 0; k < data.Length; k++)
        {
            BitConverter.TryWriteBytes(new Span<byte>(bytes, k * 4, 4), (uint)data[k]);
        }
        Accessor acc = model.CreateAccessor();
        acc.SetIndexData(model.UseBufferView(bytes, 0, null, 0, BufferMode.ELEMENT_ARRAY_BUFFER), 0,
            data.Length, IndexEncodingType.UNSIGNED_INT);
        return acc;
    }

    static Vector3 V3(double[] v, int o)
    {
        return new Vector3((float)v[o], (float)v[o + 1], (float)v[o + 2]);
    }

    static Quaternion Quat(double[] q, int o)
    {
        return new Quaternion((float)q[o], (float)q[o + 1], (float)q[o + 2], (float)q[o + 3]);
    }

    // Mat4 keeps the translation in the right column; System.Numerics in the bottom row.
    static Matrix4x4 ToNumerics(Mat4 m)
    {
        return new Matrix4x4(
            (float)m[0, 0], (float)m[1, 0], (float)m[2, 0], (float)m[3, 0],
            (float)m[0, 1], (float)m[1, 1], (float)m[2, 1], (float)m[3, 1],
            (float)m[0, 2], (float)m[1, 2], (float)m[2, 2], (float)m[3, 2],
            (float)m[0, 3], (float)m[1, 3], (float)m[2, 3], (float)m[3, 3]);
    }

    // numpy allclose with rtol 0: every frame's components against the rest value.
    static bool CloseTo(double[] data, double[] rest, int comps, double atol)
    {
        for (int k = 0; k < data.Length; k++)
        {
            if (Math.Abs(data[k] - rest[k % comps]) > atol)
            {
                return false;
            }
        }
        return true;
    }

    static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int k = 0; k < a.Length; k++)
        {
            if (a[k] != b[k])
            {
                return false;
            }
        }
        return true;
    }

    static bool SameColour(double[] a, double[] b)
    {
        for (int k = 0; k < a.Length; k++)
        {
            if (a[k] != b[k])
            {
                return false;
            }
        }
        return true;
    }
}
