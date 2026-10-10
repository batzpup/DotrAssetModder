namespace DotrAssetModder;
// Mostly AI

// The converter driver, a port of tools/toglb.py's read_any, add_anims_from and convert
// (DotR-PC): read an FBX, COLLADA or glTF source into a Scene, take other files' animations,
// finish it into DotR's shape and write the glb.

public sealed class ConvertResult
{
    public byte[] Glb = new byte[0];
    public int Nodes;
    public int Joints;
    public int Textures;
    public int Flat;
    public int Tris;
    public int Verts;
    public List<GlbAnimInfo> Anims = new List<GlbAnimInfo>();
    public double Scale;
    public double[] Size = new double[3];
    public List<string> Report = new List<string>(); // what toglb.py prints about the output
}

public static class ModelConverter
{
    // Largest texture side written, in pixels (toglb.py's --max-tex default). The importer fits
    // textures to the block itself and never takes more than 512 either.
    const int MaxTex = 512;

    public static Scene ReadSource(string path, Action<string> warn)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".fbx")
        {
            return FbxReader.Read(path, warn);
        }
        if (ext == ".dae")
        {
            return DaeReader.Read(path, warn);
        }
        if (ext == ".glb" || ext == ".gltf")
        {
            return GltfSourceReader.Read(path, warn);
        }
        throw new InvalidDataException($"{path}: expected .fbx, .dae, .glb or .gltf");
    }

    // A 3DS rip writes one motion per file: X.dae beside X_MotionA.dae .. X_MotionD.dae, each
    // with the whole model. Returns the motion files that are there, in A to D order, ready for
    // ConvertOptions.AnimsFrom; an empty list when the file is not part of such a rip.
    public static List<string> FindMotionFiles(string path)
    {
        List<string> found = new List<string>();
        string? dir = Path.GetDirectoryName(path);
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        if (dir == null || stem.Length == 0)
        {
            return found;
        }
        if (stem.IndexOf("_Motion", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return found;
        }
        for (char c = 'A'; c <= 'D'; c++)
        {
            string f = Path.Combine(dir, stem + "_Motion" + c + ext);
            if (File.Exists(f))
            {
                found.Add(f);
            }
        }
        return found;
    }

    // Take the animations of another file of the same model (a 3DS rip writes one motion per
    // file, each with the whole model), matching nodes by name. Tracks are local matrices, so
    // the hierarchy and source axes/units must be the same.
    public static void AddAnimsFrom(Scene sc, Scene es, string label, Action<string> warn)
    {
        for (int k = 0; k < 16; k++)
        {
            if (Math.Abs(es.Axis.M[k] - sc.Axis.M[k]) > 1e-8 + 1e-5 * Math.Abs(sc.Axis.M[k]))
            {
                throw new InvalidDataException($"{label}: other axes or units than the model; not the same export");
            }
        }
        if (Math.Abs(es.Unit - sc.Unit) > 1e-8 + 1e-5 * Math.Abs(sc.Unit))
        {
            throw new InvalidDataException($"{label}: other axes or units than the model; not the same export");
        }
        Dictionary<string, int> count = new Dictionary<string, int>();
        foreach (SceneNode nd in sc.Nodes)
        {
            int c = 0;
            count.TryGetValue(nd.Name, out c);
            count[nd.Name] = c + 1;
        }
        Dictionary<string, int> byName = new Dictionary<string, int>();
        for (int i = 0; i < sc.Nodes.Count; i++)
        {
            if (count[sc.Nodes[i].Name] == 1)
            {
                byName[sc.Nodes[i].Name] = i;
            }
        }
        if (es.Anims.Count == 0)
        {
            warn($"{label}: no animation in it");
        }
        foreach (SceneAnim ea in es.Anims)
        {
            SceneAnim anim = new SceneAnim(ea.Name, ea.Frames);
            List<string> missing = new List<string>();
            foreach (KeyValuePair<int, Mat4[]> kv in ea.Tracks)
            {
                SceneNode en = es.Nodes[kv.Key];
                int i;
                if (!byName.TryGetValue(en.Name, out i))
                {
                    missing.Add(en.Name);
                    continue;
                }
                int p = sc.Nodes[i].Parent;
                int ep = en.Parent;
                if ((p < 0) != (ep < 0) || (p >= 0 && sc.Nodes[p].Name != es.Nodes[ep].Name))
                {
                    warn($"{label}: {en.Name} has another parent than in the model");
                }
                anim.Tracks[i] = kv.Value;
            }
            if (missing.Count > 0)
            {
                List<string> first = new List<string>();
                for (int k = 0; k < missing.Count && k < 4; k++)
                {
                    first.Add(missing[k]);
                }
                warn($"{label}: {missing.Count} animated nodes not in the model, left out ({string.Join(", ", first)}...)");
            }
            if (anim.Tracks.Count == 0)
            {
                throw new InvalidDataException($"{label}: none of its animated nodes are in the model");
            }
            sc.Anims.Add(anim);
        }
    }

    // Convert one source file: read it, take the --anims-from animations, convert.
    public static ConvertResult ConvertFile(string path, ConvertOptions o, Action<string> warn)
    {
        Scene sc = ReadSource(path, warn);
        foreach (string f in o.AnimsFrom)
        {
            string from = f.Trim();
            Scene es = ReadSource(from, warn);
            AddAnimsFrom(sc, es, Path.GetFileName(from), warn);
        }
        return Convert(sc, o, warn);
    }

    public static ConvertResult Convert(Scene sc, ConvertOptions o, Action<string> warn)
    {
        SortByName(sc.Anims);
        if (o.Anims != null && o.Anims.Length > 0)
        {
            // name or name=new name; the importer reads "slot_N" in a name as the motion slot
            List<string> wantOld = new List<string>();
            List<string> wantNew = new List<string>();
            foreach (string w in o.Anims.Split(','))
            {
                int eq = w.IndexOf('=');
                string old = w;
                string renamed = "";
                if (eq >= 0)
                {
                    old = w.Substring(0, eq);
                    renamed = w.Substring(eq + 1);
                }
                old = old.Trim();
                renamed = renamed.Trim();
                if (renamed.Length == 0)
                {
                    renamed = old;
                }
                wantOld.Add(old);
                wantNew.Add(renamed);
            }
            Dictionary<string, SceneAnim> by = new Dictionary<string, SceneAnim>();
            List<string> have = new List<string>();
            foreach (SceneAnim a in sc.Anims)
            {
                by[a.Name] = a;
                have.Add(a.Name);
            }
            List<string> missing = new List<string>();
            foreach (string old in wantOld)
            {
                if (!by.ContainsKey(old))
                {
                    missing.Add(old);
                }
            }
            if (missing.Count > 0)
            {
                throw new InvalidDataException($"no animation [{string.Join(", ", missing)}]; have [{string.Join(", ", have)}]");
            }
            // An animation named twice (one motion in two slots) goes in as a copy: finish
            // changes the tracks in place, so two entries must not share them.
            sc.Anims = new List<SceneAnim>();
            HashSet<string> taken = new HashSet<string>();
            for (int k = 0; k < wantOld.Count; k++)
            {
                SceneAnim a = by[wantOld[k]];
                if (!taken.Add(wantOld[k]))
                {
                    a = CopyAnim(a);
                }
                a.Name = wantNew[k];
                sc.Anims.Add(a);
            }
        }
        double[] rotate = new double[] { o.RotateX, o.RotateY, o.RotateZ };
        double[] translate = new double[] { o.TranslateX, o.TranslateY, o.TranslateZ };
        // Scale and Rotate turn the model about node 1 (the Pivot) rather than node 0: Finish turns
        // about node 0, so add P - S R P to its move.
        Mat4 turn = Mat4.Euler(rotate[0], rotate[1], rotate[2]);
        double[] p = o.PivotPoint;
        for (int r = 0; r < 3; r++)
        {
            translate[r] += p[r] - o.Scale * (turn[r, 0] * p[0] + turn[r, 1] * p[1] + turn[r, 2] * p[2]);
        }
        SceneFit fit = SceneFinish.Finish(sc, o.Scale, rotate, translate, o.RestFromAnim, warn);
        ConvertResult res = new ConvertResult();
        if (sc.UvTopLeft && !o.FlipUv)
        {
            res.Report.Add("UVs are top-left (Unity Studio export), keeping them as --flip-uv does");
        }
        GlbWriteInfo info = SceneGlbWriter.WriteGlb(sc, MaxTex, o.FlipUv || sc.UvTopLeft, o.PivotPoint);

        res.Glb = info.Glb;
        res.Nodes = info.Nodes;
        res.Joints = info.Joints;
        res.Textures = info.Textures;
        res.Flat = info.Flat;
        res.Tris = info.Tris;
        res.Verts = info.Verts;
        res.Anims = info.Anims;
        res.Scale = fit.Scale;
        res.Size = fit.Size;
        double[] sz = fit.Size;
        res.Report.Add($"{info.Nodes} nodes, {info.Joints} joints, {info.Tris} tris, {info.Verts} verts, "
            + $"{info.Textures} textures + {info.Flat} flat colours");
        res.Report.Add($"  size {G(sz[0], 3)} x {G(sz[1], 3)} x {G(sz[2], 3)} (x{G(fit.Scale, 4)} from source), "
            + $"lowest point {G(fit.Lowest, 3)}");
        foreach (GlbAnimInfo a in info.Anims)
        {
            res.Report.Add($"  anim {a.Name}: {a.Frames} frames");
        }
        if (sz[1] < 5 || sz[1] > 310)
        {
            warn($"height {G(sz[1], 3)}: retail monsters stand 6 to 308 tall on their idle animation (median 24); --scale fixes it");
        }
        if (fit.Lowest < -50.5)
        {
            warn($"lowest point {G(fit.Lowest, 3)}: below the battle floor (50 under node 0); Move Y fixes it");
        }
        if (info.Tris > 8000)
        {
            warn($"{info.Tris} triangles: retail monsters stay under ~5000; this may not fit a 1 MiB slot");
        }
        if (info.Textures > 8)
        {
            warn($"{info.Textures} textures: past the template's texture count they get atlas-merged");
        }
        return res;
    }

    // Python's list.sort by name: stable, by code point.
    static SceneAnim CopyAnim(SceneAnim a)
    {
        SceneAnim c = new SceneAnim(a.Name, a.Frames);
        foreach (KeyValuePair<int, Mat4[]> tr in a.Tracks)
        {
            Mat4[] frames = new Mat4[tr.Value.Length];
            for (int f = 0; f < frames.Length; f++)
            {
                frames[f] = tr.Value[f].Copy();
            }
            c.Tracks[tr.Key] = frames;
        }
        return c;
    }

    static void SortByName(List<SceneAnim> anims)
    {
        for (int i = 1; i < anims.Count; i++)
        {
            SceneAnim a = anims[i];
            int k = i - 1;
            while (k >= 0 && string.CompareOrdinal(anims[k].Name, a.Name) > 0)
            {
                anims[k + 1] = anims[k];
                k--;
            }
            anims[k + 1] = a;
        }
    }

    // Python's "%.3g" / "%.4g" for the report lines.
    static string G(double v, int sig)
    {
        if (v == 0)
        {
            return "0";
        }
        int exp = (int)Math.Floor(Math.Log10(Math.Abs(v)));
        if (exp < -4 || exp >= sig)
        {
            return v.ToString("0." + new string('#', sig - 1) + "e+00");
        }
        int dec = sig - 1 - exp;
        if (dec < 0)
        {
            dec = 0;
        }
        return Math.Round(v, dec).ToString("0." + new string('#', dec));
    }
}
