namespace DotrAssetModder;
// Mostly AI

/* The converter driver, a port of tools/toglb.py's read_any, add_anims_from and convert
   (DotR-PC): read an FBX, COLLADA or glTF source into a Scene, take other files' animations,
   finish it into DotR's shape and write the glb. */

public sealed class ConvertResult
{
    public byte[] Glb = new byte[0];
    public int Nodes;
    public int Joints;
    public int Textures;
    public int FlatColourMaterials;
    public int Triangles;
    public int Vertices;
    public List<GlbAnimInfo> Animations = new List<GlbAnimInfo>();
    public double Scale;
    public double[] Size = new double[3];
    public List<string> Report = new List<string>(); // what toglb.py prints about the output
}

public static class ModelConverter
{
    // Largest texture side written, in pixels (toglb.py's --max-tex default). The importer fits
    // textures to the block itself and never takes more than 512 either.
    const int MaxTextureSize = 512;

    public static Scene ReadSource(string path, Action<string> warn)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".fbx")
        {
            return FbxReader.Read(path, warn);
        }
        if (extension == ".dae")
        {
            return DaeReader.Read(path, warn);
        }
        if (extension == ".glb" || extension == ".gltf")
        {
            return GltfSourceReader.Read(path, warn);
        }
        throw new InvalidDataException($"{path}: expected .fbx, .dae, .glb or .gltf");
    }

    /* A 3DS rip writes one motion per file: X.dae beside X_MotionA.dae .. X_MotionD.dae, each
       with the whole model. Returns the motion files that are there, in A to D order, ready for
       ConvertOptions.AnimsFrom; an empty list when the file is not part of such a rip. */
    public static List<string> FindMotionFiles(string path)
    {
        List<string> found = new List<string>();
        string? directory = Path.GetDirectoryName(path);
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        if (directory == null || stem.Length == 0)
        {
            return found;
        }
        if (stem.IndexOf("_Motion", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return found;
        }
        for (char motionLetter = 'A'; motionLetter <= 'D'; motionLetter++)
        {
            string motionFile = Path.Combine(directory, stem + "_Motion" + motionLetter + extension);
            if (File.Exists(motionFile))
            {
                found.Add(motionFile);
            }
        }
        return found;
    }

    /* Takes the animations of another file of the same model, matching nodes by name. Tracks
       are local matrices, so the hierarchy and source axes/units must be the same. */
    public static void AddAnimsFrom(Scene scene, Scene extraScene, string label, Action<string> warn)
    {
        for (int k = 0; k < 16; k++)
        {
            if (Math.Abs(extraScene.Axis.Elements[k] - scene.Axis.Elements[k]) > 1e-8 + 1e-5 * Math.Abs(scene.Axis.Elements[k]))
            {
                throw new InvalidDataException($"{label}: other axes or units than the model; not the same export");
            }
        }
        if (Math.Abs(extraScene.Unit - scene.Unit) > 1e-8 + 1e-5 * Math.Abs(scene.Unit))
        {
            throw new InvalidDataException($"{label}: other axes or units than the model; not the same export");
        }
        Dictionary<string, int> count = new Dictionary<string, int>();
        foreach (SceneNode sceneNode in scene.Nodes)
        {
            int nameCount = 0;
            count.TryGetValue(sceneNode.Name, out nameCount);
            count[sceneNode.Name] = nameCount + 1;
        }
        Dictionary<string, int> byName = new Dictionary<string, int>();
        for (int i = 0; i < scene.Nodes.Count; i++)
        {
            if (count[scene.Nodes[i].Name] == 1)
            {
                byName[scene.Nodes[i].Name] = i;
            }
        }
        if (extraScene.Anims.Count == 0)
        {
            warn($"{label}: no animation in it");
        }
        foreach (SceneAnim extraAnim in extraScene.Anims)
        {
            SceneAnim animation = new SceneAnim(extraAnim.Name, extraAnim.Frames);
            List<string> missing = new List<string>();
            foreach (KeyValuePair<int, Matrix4[]> track in extraAnim.Tracks)
            {
                SceneNode extraNode = extraScene.Nodes[track.Key];
                int i;
                if (!byName.TryGetValue(extraNode.Name, out i))
                {
                    missing.Add(extraNode.Name);
                    continue;
                }
                int parentIndex = scene.Nodes[i].Parent;
                int extraParent = extraNode.Parent;
                if ((parentIndex < 0) != (extraParent < 0) || (parentIndex >= 0 && scene.Nodes[parentIndex].Name != extraScene.Nodes[extraParent].Name))
                {
                    warn($"{label}: {extraNode.Name} has another parent than in the model");
                }
                animation.Tracks[i] = track.Value;
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
            if (animation.Tracks.Count == 0)
            {
                throw new InvalidDataException($"{label}: none of its animated nodes are in the model");
            }
            scene.Anims.Add(animation);
        }
    }

    public static ConvertResult ConvertFile(string path, ConvertOptions options, Action<string> warn)
    {
        Scene scene = ReadSource(path, warn);
        foreach (string animsFile in options.AnimsFrom)
        {
            string animsPath = animsFile.Trim();
            Scene extraScene = ReadSource(animsPath, warn);
            AddAnimsFrom(scene, extraScene, Path.GetFileName(animsPath), warn);
        }
        return Convert(scene, options, warn);
    }

    public static ConvertResult Convert(Scene scene, ConvertOptions options, Action<string> warn)
    {
        SortByName(scene.Anims);
        if (options.Animations != null && options.Animations.Length > 0)
        {
            // name or name=new name; the importer reads "slot_N" in a name as the motion slot
            List<string> wantedOldNames = new List<string>();
            List<string> wantedNewNames = new List<string>();
            foreach (string w in options.Animations.Split(','))
            {
                int equalsIndex = w.IndexOf('=');
                string oldName = w;
                string renamed = "";
                if (equalsIndex >= 0)
                {
                    oldName = w.Substring(0, equalsIndex);
                    renamed = w.Substring(equalsIndex + 1);
                }
                oldName = oldName.Trim();
                renamed = renamed.Trim();
                if (renamed.Length == 0)
                {
                    renamed = oldName;
                }
                wantedOldNames.Add(oldName);
                wantedNewNames.Add(renamed);
            }
            Dictionary<string, SceneAnim> animByName = new Dictionary<string, SceneAnim>();
            List<string> availableNames = new List<string>();
            foreach (SceneAnim animation in scene.Anims)
            {
                animByName[animation.Name] = animation;
                availableNames.Add(animation.Name);
            }
            List<string> missing = new List<string>();
            foreach (string oldName in wantedOldNames)
            {
                if (!animByName.ContainsKey(oldName))
                {
                    missing.Add(oldName);
                }
            }
            if (missing.Count > 0)
            {
                throw new InvalidDataException($"no animation [{string.Join(", ", missing)}]; have [{string.Join(", ", availableNames)}]");
            }
            // An animation named twice (one motion in two slots) goes in as a copy: Finish
            // changes the tracks in place, so two entries must not share them.
            scene.Anims = new List<SceneAnim>();
            HashSet<string> taken = new HashSet<string>();
            for (int k = 0; k < wantedOldNames.Count; k++)
            {
                SceneAnim animation = animByName[wantedOldNames[k]];
                if (!taken.Add(wantedOldNames[k]))
                {
                    animation = CopyAnim(animation);
                }
                animation.Name = wantedNewNames[k];
                scene.Anims.Add(animation);
            }
        }
        double[] rotate = new double[] { options.RotateX, options.RotateY, options.RotateZ };
        double[] translate = new double[] { options.TranslateX, options.TranslateY, options.TranslateZ };
        // Scale and Rotate act about node 1 (PivotPoint), but Finish acts about node 0, so add
        // P - S R P to the translation.
        Matrix4 turn = Matrix4.EulerAngles(rotate[0], rotate[1], rotate[2]);
        double[] pivot = options.PivotPoint;
        for (int axis = 0; axis < 3; axis++)
        {
            translate[axis] += pivot[axis] - options.Scale * (turn[axis, 0] * pivot[0] + turn[axis, 1] * pivot[1] + turn[axis, 2] * pivot[2]);
        }
        SceneFit fit = SceneFinish.Finish(scene, options.Scale, rotate, translate, options.RestFromAnim, warn);
        ConvertResult result = new ConvertResult();
        if (scene.UvTopLeft && !options.FlipUv)
        {
            result.Report.Add("UVs are top-left (Unity Studio export), keeping them as --flip-uv does");
        }
        GlbWriteInfo info = SceneGlbWriter.WriteGlb(scene, MaxTextureSize, options.FlipUv || scene.UvTopLeft, options.PivotPoint);

        result.Glb = info.Glb;
        result.Nodes = info.Nodes;
        result.Joints = info.Joints;
        result.Textures = info.Textures;
        result.FlatColourMaterials = info.FlatColourMaterials;
        result.Triangles = info.Triangles;
        result.Vertices = info.Vertices;
        result.Animations = info.Animations;
        result.Scale = fit.Scale;
        result.Size = fit.Size;
        double[] size = fit.Size;
        result.Report.Add($"{info.Nodes} nodes, {info.Joints} joints, {info.Triangles} tris, {info.Vertices} verts, "
            + $"{info.Textures} textures + {info.FlatColourMaterials} flat colours");
        result.Report.Add($"  size {FormatSignificant(size[0], 3)} x {FormatSignificant(size[1], 3)} x {FormatSignificant(size[2], 3)} (x{FormatSignificant(fit.Scale, 4)} from source), "
            + $"lowest point {FormatSignificant(fit.Lowest, 3)}");
        foreach (GlbAnimInfo animInfo in info.Animations)
        {
            result.Report.Add($"  anim {animInfo.Name}: {animInfo.Frames} frames");
        }
        if (size[1] < 5 || size[1] > 310)
        {
            warn($"height {FormatSignificant(size[1], 3)}: retail monsters stand 6 to 308 tall on their idle animation (median 24); --scale fixes it");
        }
        if (fit.Lowest < -50.5)
        {
            warn($"lowest point {FormatSignificant(fit.Lowest, 3)}: below the battle floor (50 under node 0); Move Y fixes it");
        }
        if (info.Triangles > 8000)
        {
            warn($"{info.Triangles} triangles: retail monsters stay under ~5000; this may not fit a 1 MiB slot");
        }
        if (info.Textures > 8)
        {
            warn($"{info.Textures} textures: past the template's texture count they get atlas-merged");
        }
        return result;
    }

    // Python's list.sort by name: stable, by code point.
    static SceneAnim CopyAnim(SceneAnim source)
    {
        SceneAnim copy = new SceneAnim(source.Name, source.Frames);
        foreach (KeyValuePair<int, Matrix4[]> track in source.Tracks)
        {
            Matrix4[] frames = new Matrix4[track.Value.Length];
            for (int frame = 0; frame < frames.Length; frame++)
            {
                frames[frame] = track.Value[frame].Copy();
            }
            copy.Tracks[track.Key] = frames;
        }
        return copy;
    }

    static void SortByName(List<SceneAnim> anims)
    {
        for (int i = 1; i < anims.Count; i++)
        {
            SceneAnim animation = anims[i];
            int k = i - 1;
            while (k >= 0 && string.CompareOrdinal(anims[k].Name, animation.Name) > 0)
            {
                anims[k + 1] = anims[k];
                k--;
            }
            anims[k + 1] = animation;
        }
    }

    // Python's "%.3g" / "%.4g" for the report lines.
    static string FormatSignificant(double value, int significantDigits)
    {
        if (value == 0)
        {
            return "0";
        }
        int exponent = (int)Math.Floor(Math.Log10(Math.Abs(value)));
        if (exponent < -4 || exponent >= significantDigits)
        {
            return value.ToString("0." + new string('#', significantDigits - 1) + "e+00");
        }
        int decimals = significantDigits - 1 - exponent;
        if (decimals < 0)
        {
            decimals = 0;
        }
        return Math.Round(value, decimals).ToString("0." + new string('#', decimals));
    }
}
