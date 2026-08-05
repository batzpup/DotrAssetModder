using System.Numerics;
using System.Text.Json.Nodes;
using Raylib_cs;
using SharpGLTF.Schema2;
using SharpGLTF.Runtime;
using SharpGLTF.Materials;
using AlphaMode = SharpGLTF.Schema2.AlphaMode; // MaterialBuilder, KnownChannel, KnownProperty (package: SharpGLTF.Toolkit)
//AI Generated

// ---------------------------------------------------------------------------
// Type origins. Raylib_cs and SharpGLTF.Schema2 BOTH declare `Mesh`/`Material`,
// so those names are aliased below to make every use explicit.
//
//   Raylib_cs (package: Raylib-cs)
//       RlMesh (=Raylib_cs.Mesh), Texture2D, Color, MaterialMap,
//       MaterialMapIndex, Raylib, Rlgl
//   SharpGLTF.Schema2 (package: SharpGLTF.Core)
//       ModelRoot, ReadSettings, GltfMaterial (=SharpGLTF.Schema2.Material),
//       Scene/Mesh/MeshPrimitive/Accessor/Animation/Image (reached via `var`/members)
//   SharpGLTF.Runtime (package: SharpGLTF.Runtime)
//       SceneTemplate, SceneInstance, DrawableInstance
//   SharpGLTF.Transforms (package: SharpGLTF.Core)
//       RigidTransform, SkinnedTransform (matched via `is`, fully qualified below).
//       The base transform interface is never named, so its version-specific
//       name does not matter.
//   SharpGLTF.Materials (package: SharpGLTF.Toolkit)
//       MaterialBuilder, KnownChannel, KnownProperty
//   System.Numerics : Vector2/3/4, Matrix4x4
// ---------------------------------------------------------------------------
using RlMesh       = Raylib_cs.Mesh;
using RlMaterial   = Raylib_cs.Material;
using RlImage      = Raylib_cs.Image;   // SharpGLTF.Schema2 also declares Image
using GltfMaterial = SharpGLTF.Schema2.Material;



/// <summary>
/// Loads a glTF/glb with SharpGLTF and renders it with Raylib.
/// Per frame it reads the runtime joint matrices (SkinnedTransform.SkinMatrices)
/// or the rigid WorldMatrix and does the skinning itself, updating only the
/// position buffer of indexed meshes. No per-frame allocation; rigid, skinned
/// and node-animated geometry are all handled (nothing collapses to the origin).
/// </summary>
public sealed class AnimatedGltfModel : IDisposable
{
    sealed class Renderable
    {
        public RlMesh Mesh;
        public bool Indexed;        // false => non-indexed "soup" fallback (V > 65535)
        public int V;               // unique vertex count
        public int TriCount;
        public unsafe float* Pos;   // GPU-side position buffer (RL_MALLOC, freed by UnloadMesh)

        public Vector3[] LocalPos;  // bind-space positions
        public Vector4[] Joints;    // per-vertex joint indices (null => rigid)
        public Vector4[] Weights;   // per-vertex weights
        public int[] Tri;           // flat triangle indices into LocalPos (len 3*TriCount)

        public int MaterialIndex = -1;
        public unsafe float* Uv;    // GPU-side texcoord buffer

        public Color Tint = Color.White;
        public bool HasTexture;
        public Texture2D Texture;
        public bool Additive;       // DOTR GS (Cs - 0) * As + Cd
        public bool AlphaBlend;     // ordinary source-alpha transparency
        public bool AlphaMask;      // PS2 alpha-test/cutout material
        public float AlphaCutoff = 0.5f;
        public bool Uploaded;

        // Texture animation (DOTR_texAnim extras): cycled base-color frames.
        public bool HasAnim;
        public Texture2D[] AnimFrames = Array.Empty<Texture2D>();
        public float[] AnimTimes = Array.Empty<float>();
        public float AnimDuration;
    }

    ModelRoot _gltf;
    SceneInstance _instance;
    readonly List<List<Renderable>> _byDrawable = new(); // aligned to DrawableInstances order
    readonly List<Renderable> _all = new();

    RlMaterial _drawMaterial;
    Raylib_cs.Shader _defaultShader;
    Raylib_cs.Shader _alphaMaskShader;
    Texture2D _whiteTex;
    bool _hasDrawMaterial;
    bool _hasAlphaMaskShader;

    private const string AlphaMaskFragmentShader = @"#version 330
        in vec2 fragTexCoord;
        in vec4 fragColor;
        out vec4 finalColor;

        uniform sampler2D texture0;
        uniform vec4 colDiffuse;

        void main()
        {
            vec4 color = texture(texture0, fragTexCoord) * colDiffuse * fragColor;
            if (color.a < 0.5) discard;
            finalColor = vec4(color.rgb, 1.0);
        }";

    Vector3[] _scratch = Array.Empty<Vector3>(); // reused world-position buffer
    Vector3 _center;

    public Vector3 Center => _center;
    public float Radius { get; private set; } = 1f;

    public int AnimationCount { get; private set; }
    public string[] AnimationNames { get; private set; } = Array.Empty<string>();
    float[] _durations = Array.Empty<float>();
    public float Duration(int i) => (i >= 0 && i < _durations.Length) ? _durations[i] : 0f;

    public static AnimatedGltfModel Load(string path)
    {
        var m = new AnimatedGltfModel();
        m.Build(path);
        return m;
    }

    void Build(string path)
    {
        var settings = new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.TryFix };
        _gltf = ModelRoot.Load(path, settings);

        var scene = _gltf.DefaultScene ?? (_gltf.LogicalScenes.Count > 0 ? _gltf.LogicalScenes[0] : null);
        if (scene == null) throw new InvalidOperationException("glTF has no scene to render.");

        _instance = SceneTemplate.Create(scene).CreateInstance();

        AnimationCount = _gltf.LogicalAnimations.Count;
        AnimationNames = new string[AnimationCount];
        _durations = new float[AnimationCount];
        for (int i = 0; i < AnimationCount; i++)
        {
            var a = _gltf.LogicalAnimations[i];
            AnimationNames[i] = string.IsNullOrWhiteSpace(a.Name) ? $"Animation {i}" : a.Name;
            _durations[i] = a.Duration;
        }

        // ---- Build one renderable per (drawable, primitive); read static data once ----
        int maxV = 1;
        foreach (var d in _instance) // SceneInstance : IEnumerable<DrawableInstance>
        {
            var bucket = new List<Renderable>();
            _byDrawable.Add(bucket);

            var mesh = _gltf.LogicalMeshes[d.Template.LogicalMeshIndex]; // SharpGLTF.Schema2.Mesh
            foreach (var prim in mesh.Primitives)                        // SharpGLTF.Schema2.MeshPrimitive
            {
                var r = MakeRenderable(prim);
                if (r == null) continue;
                bucket.Add(r);
                _all.Add(r);
                if (r.V > maxV) maxV = r.V;
            }
        }
        _scratch = new Vector3[maxV];

        // ---- Measure bind-pose bounds ----
        _instance.Armature.SetPoseTransforms();
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        EnumerateDrawables((r, xform) =>
        {
            FillUnique(r, xform, _scratch);
            for (int v = 0; v < r.V; v++)
            {
                min = Vector3.Min(min, _scratch[v]);
                max = Vector3.Max(max, _scratch[v]);
            }
        });
        if (min.X > max.X) { min = Vector3.Zero; max = Vector3.Zero; }
        _center = (min + max) * 0.5f;
        Radius = MathF.Max((max - min).Length() * 0.5f, 0.01f);

        // ---- Fill bind-pose positions and upload meshes ----
        EnumerateDrawables((r, xform) =>
        {
            FillUnique(r, xform, _scratch);
            WritePositions(r, _scratch);
        });
        unsafe
        {
            foreach (var r in _all)
            {
                Raylib.UploadMesh(ref r.Mesh, true); // dynamic = true
                r.Uploaded = true;
            }
        }

        _drawMaterial = Raylib.LoadMaterialDefault();
        _defaultShader = _drawMaterial.Shader;
        _alphaMaskShader = Raylib.LoadShaderFromMemory(null, AlphaMaskFragmentShader);
        _hasAlphaMaskShader = _alphaMaskShader.Id != 0;
        _hasDrawMaterial = true;
        unsafe { _whiteTex = _drawMaterial.Maps[(int)MaterialMapIndex.Albedo].Texture; }
    }

    unsafe Renderable MakeRenderable(MeshPrimitive prim)
    {
        var posAcc = prim.GetVertexAccessor("POSITION");
        if (posAcc == null) return null;
        var positions = posAcc.AsVector3Array();      // IList<Vector3>
        int V = positions.Count;
        if (V == 0) return null;

        var uvAcc = prim.GetVertexAccessor("TEXCOORD_0");
        var uvs = uvAcc?.AsVector2Array();
        var jAcc = prim.GetVertexAccessor("JOINTS_0");
        var wAcc = prim.GetVertexAccessor("WEIGHTS_0");
        var joints = jAcc?.AsVector4Array();
        var weights = wAcc?.AsVector4Array();

        // Flatten triangle indices (handles strips/fans too).
        var triList = new List<int>(V * 3);
        foreach (var (a, b, c) in prim.GetTriangleIndices())
        {
            triList.Add(a); triList.Add(b); triList.Add(c);
        }
        if (triList.Count == 0) return null;

        var r = new Renderable
        {
            V = V,
            TriCount = triList.Count / 3,
            Tri = triList.ToArray(),
            Indexed = V <= 65535,
            MaterialIndex = prim.Material?.LogicalIndex ?? -1,
        };

        r.LocalPos = new Vector3[V];
        for (int i = 0; i < V; i++) r.LocalPos[i] = positions[i];

        if (joints != null && weights != null && joints.Count == V)
        {
            r.Joints = new Vector4[V];
            r.Weights = new Vector4[V];
            for (int i = 0; i < V; i++) { r.Joints[i] = joints[i]; r.Weights[i] = weights[i]; }
        }

        // Material tint + base color texture.
        BuildMaterial(prim.Material, r);

        // Allocate GPU buffers (RL_MALLOC so UnloadMesh frees them) and fill static channels.
        int gpuVerts = r.Indexed ? V : r.TriCount * 3;
        r.Pos = (float*)Raylib.MemAlloc((uint)(gpuVerts * 3 * sizeof(float)));
        float* uv = (float*)Raylib.MemAlloc((uint)(gpuVerts * 2 * sizeof(float)));

        if (r.Indexed)
        {
            for (int v = 0; v < V; v++)
            {
                Vector2 t = uvs != null ? uvs[v] : Vector2.Zero;
                uv[v * 2 + 0] = t.X; uv[v * 2 + 1] = t.Y;
            }
            ushort* idx = (ushort*)Raylib.MemAlloc((uint)(r.Tri.Length * sizeof(ushort)));
            for (int k = 0; k < r.Tri.Length; k++) idx[k] = (ushort)r.Tri[k];
            r.Mesh.Indices = idx;
            r.Mesh.VertexCount = V;
        }
        else
        {
            for (int k = 0; k < r.Tri.Length; k++)
            {
                int vi = r.Tri[k];
                Vector2 t = uvs != null ? uvs[vi] : Vector2.Zero;
                uv[k * 2 + 0] = t.X; uv[k * 2 + 1] = t.Y;
            }
            r.Mesh.VertexCount = gpuVerts;
        }

        r.Mesh.TriangleCount = r.TriCount;
        r.Mesh.Vertices = r.Pos;
        r.Mesh.TexCoords = uv;
        r.Uv = uv;
        return r;
    }

    void BuildMaterial(GltfMaterial? mat, Renderable r)
    {
        // New extractor output carries the actual per-page PS2 GS state. Explicit
        // "opaque" is important too: every embedded image is RGBA, so neither image
        // format nor glTF-library inference should move an opaque monster into the
        // transparent no-depth-write pass.
        bool hasDotrBlend = TryReadDotrBlendInfo(mat, out string? dotrBlend, out float alphaCutoff);
        r.AlphaCutoff = alphaCutoff;

        if (hasDotrBlend)
        {
            r.Additive = string.Equals(dotrBlend, "additive", StringComparison.OrdinalIgnoreCase);
            r.AlphaBlend = string.Equals(dotrBlend, "alpha", StringComparison.OrdinalIgnoreCase);
            r.AlphaMask = string.Equals(dotrBlend, "mask", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Backward compatibility for older/non-DOTR glTF files.
            r.Additive = false;
            r.AlphaBlend = mat != null && mat.Alpha == AlphaMode.BLEND;
            r.AlphaMask = mat != null && mat.Alpha == AlphaMode.MASK;
        }

        Vector4 color = Vector4.One;
        if (mat != null)
        {
            try
            {
                var mb = new MaterialBuilder();
                mat.CopyTo(mb);
                var bc = mb.GetChannel(KnownChannel.BaseColor);
                if (bc != null) color = (Vector4)bc.Parameters[KnownProperty.RGBA];
            }
            catch { }
        }
        // Explicit opaque state must ignore any accidental/default alpha carried by
        // a generic glTF material conversion. Additive/alpha/mask materials retain it.
        if (hasDotrBlend && string.Equals(dotrBlend, "opaque", StringComparison.OrdinalIgnoreCase))
            color.W = 1f;

        r.Tint = new Color(
            (byte)(Math.Clamp(color.X, 0f, 1f) * 255f),
            (byte)(Math.Clamp(color.Y, 0f, 1f) * 255f),
            (byte)(Math.Clamp(color.Z, 0f, 1f) * 255f),
            (byte)(Math.Clamp(color.W, 0f, 1f) * 255f));

        try
        {
            var ch = mat?.FindChannel("BaseColor");
            if (ch.HasValue && ch.Value.Texture?.PrimaryImage != null)
            {
                var img = ch.Value.Texture.PrimaryImage.Content;
                if (img.IsValid)
                {
                    string ext = string.IsNullOrEmpty(img.FileExtension) ? "png" : img.FileExtension;
                    string tmp = Path.Combine(Path.GetTempPath(), "rlgltf_" + Guid.NewGuid().ToString("N") + "." + ext);
                    img.SaveToFile(tmp);

                    RlImage cpu = Raylib.LoadImage(tmp);
                    if (cpu.Width > 0 && cpu.Height > 0)
                    {
                        r.Texture = Raylib.LoadTextureFromImage(cpu);
                        r.HasTexture = r.Texture.Id != 0;
                        Raylib.UnloadImage(cpu);
                    }
                    try { File.Delete(tmp); } catch { }
                }
            }
        }
        catch { }

        LoadTextureAnimation(mat, r);
    }

    static bool TryReadDotrBlendInfo(GltfMaterial? mat, out string? mode, out float alphaCutoff)
    {
        mode = null;
        alphaCutoff = 0.5f;
        try
        {
            if (mat?.Extras is not JsonObject extras) return false;

            if (extras["DOTR_blend"] is JsonObject blend)
            {
                mode = blend["mode"]?.GetValue<string>();
                alphaCutoff = blend["alphaCutoff"]?.GetValue<float>() ?? 0.5f;
                alphaCutoff = Math.Clamp(alphaCutoff, 0f, 1f);
                if (!string.IsNullOrWhiteSpace(mode)) return true;
            }

            // Compatibility with the previous extractor revision, which stored
            // an additive hint inside DOTR_texAnim instead of full GS metadata.
            if (extras["DOTR_texAnim"] is JsonObject texAnim)
            {
                mode = texAnim["blend"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(mode)) return true;
            }
        }
        catch { }
        mode = null;
        alphaCutoff = 0.5f;
        return false;
    }

    // Reads the DOTR_texAnim extras block written by the extractor and turns each
    // referenced glTF image into a Raylib texture. At draw time the matching frame
    // for the current wall-clock time is bound instead of the static base texture.
    void LoadTextureAnimation(GltfMaterial? mat, Renderable r)
    {
        try
        {
            if (mat?.Extras is not JsonObject ex) return;
            if (ex["DOTR_texAnim"] is not JsonObject ta) return;
            if (ta["frames"] is not JsonArray frames || frames.Count < 2) return;

            float duration = ta["duration"]?.GetValue<float>() ?? 0f;
            var texList = new List<Texture2D>(frames.Count);
            var timeList = new List<float>(frames.Count);

            foreach (var fr in frames)
            {
                if (fr is not JsonObject fo) continue;
                int imgIdx = fo["image"]?.GetValue<int>() ?? -1;
                float t = fo["t"]?.GetValue<float>() ?? 0f;
                if (imgIdx < 0 || imgIdx >= _gltf.LogicalImages.Count) continue;

                var content = _gltf.LogicalImages[imgIdx].Content;
                if (!content.IsValid) continue;
                string ext = string.IsNullOrEmpty(content.FileExtension) ? "png" : content.FileExtension;
                string tmp = Path.Combine(Path.GetTempPath(), "rlgltfanm_" + Guid.NewGuid().ToString("N") + "." + ext);
                content.SaveToFile(tmp);
                RlImage cpu = Raylib.LoadImage(tmp);
                if (cpu.Width > 0 && cpu.Height > 0)
                {
                    Texture2D t2d = Raylib.LoadTextureFromImage(cpu);
                    Raylib.UnloadImage(cpu);
                    if (t2d.Id != 0) { texList.Add(t2d); timeList.Add(t); }
                }
                try { File.Delete(tmp); } catch { }
            }

            if (texList.Count >= 2 && duration > 0f)
            {
                // The static base-color texture is superseded by the frame array;
                // unload it so the GPU copy isn't leaked.
                if (r.HasTexture && r.Texture.Id != 0) Raylib.UnloadTexture(r.Texture);
                r.AnimFrames = texList.ToArray();
                r.AnimTimes = timeList.ToArray();
                r.AnimDuration = duration;
                r.HasAnim = true;
                r.Texture = r.AnimFrames[0];
                r.HasTexture = true;
            }
            else
            {
                foreach (var t in texList) Raylib.UnloadTexture(t);
            }
        }
        catch { }
    }

    // Walks the SceneInstance in order, pairing each drawable with its prebuilt renderables.
    // The transform is passed as object so we never have to name the (version-dependent)
    // transform interface; FillUnique pattern-matches the concrete transform types.
    void EnumerateDrawables(Action<Renderable, object> action)
    {
        int di = 0;
        foreach (var d in _instance)
        {
            if (di >= _byDrawable.Count) break;
            var bucket = _byDrawable[di++];
            object xform = d.Transform;
            foreach (var r in bucket) action(r, xform);
        }
    }

    // Computes world-space positions of all unique vertices into 'outv' (no recenter).
    void FillUnique(Renderable r, object xform, Vector3[] outv)
    {
        if (xform is SharpGLTF.Transforms.SkinnedTransform st && r.Joints != null)
        {
            IReadOnlyList<Matrix4x4> skin = st.SkinMatrices; // already jointWorld * inverseBind
            for (int v = 0; v < r.V; v++)
                outv[v] = Vector3.Transform(r.LocalPos[v], Blend(skin, r.Joints[v], r.Weights[v]));
        }
        else if (xform is SharpGLTF.Transforms.RigidTransform rt)
        {
            Matrix4x4 m = rt.WorldMatrix;
            for (int v = 0; v < r.V; v++)
                outv[v] = Vector3.Transform(r.LocalPos[v], m);
        }
        else
        {
            for (int v = 0; v < r.V; v++) outv[v] = r.LocalPos[v];
        }
    }

    static Matrix4x4 Blend(IReadOnlyList<Matrix4x4> skin, Vector4 j, Vector4 w)
    {
        Matrix4x4 m = default; // all zeros
        m = Add(m, skin, (int)j.X, w.X);
        m = Add(m, skin, (int)j.Y, w.Y);
        m = Add(m, skin, (int)j.Z, w.Z);
        m = Add(m, skin, (int)j.W, w.W);
        float tw = w.X + w.Y + w.Z + w.W;
        if (tw < 1e-5f) return Matrix4x4.Identity; // avoid collapsing unweighted verts
        return m;
    }

    static Matrix4x4 Add(Matrix4x4 m, IReadOnlyList<Matrix4x4> skin, int idx, float wt)
    {
        if (wt == 0f || idx < 0 || idx >= skin.Count) return m;
        return m + Matrix4x4.Multiply(skin[idx], wt);
    }

    unsafe void WritePositions(Renderable r, Vector3[] world)
    {
        if (r.Indexed)
        {
            for (int v = 0; v < r.V; v++)
            {
                Vector3 p = world[v] - _center;
                r.Pos[v * 3 + 0] = p.X; r.Pos[v * 3 + 1] = p.Y; r.Pos[v * 3 + 2] = p.Z;
            }
        }
        else
        {
            for (int k = 0; k < r.Tri.Length; k++)
            {
                Vector3 p = world[r.Tri[k]] - _center;
                r.Pos[k * 3 + 0] = p.X; r.Pos[k * 3 + 1] = p.Y; r.Pos[k * 3 + 2] = p.Z;
            }
        }
    }

    /// <summary>Evaluate animation at the given time (seconds) and push deformed
    /// positions to the GPU. animIndex &lt; 0 => static bind pose.</summary>
    public void SetAnimationFrame(int animIndex, float timeSeconds)
    {
        if (_instance == null) return;

        if (animIndex >= 0 && animIndex < AnimationCount)
            _instance.Armature.SetAnimationFrame(animIndex, timeSeconds);
        else
            _instance.Armature.SetPoseTransforms();

        EnumerateDrawables((r, xform) =>
        {
            FillUnique(r, xform, _scratch);
            WritePositions(r, _scratch);
            unsafe
            {
                int gpuVerts = r.Indexed ? r.V : r.TriCount * 3;
                Raylib.UpdateMeshBuffer(r.Mesh, 0, r.Pos, gpuVerts * 3 * sizeof(float), 0); // VBO 0 = positions
            }
        });
    }

    public void Draw()
    {
        if (!_hasDrawMaterial) return;

        Rlgl.DisableBackfaceCulling();

        // Pass 1: explicitly opaque geometry only (writes depth).
        foreach (var r in _all)
        {
            if (r.Additive || r.AlphaBlend || r.AlphaMask) continue;
            DrawOne(r);
        }

        // Pass 2: alpha-tested cutouts. The shader discards transparent texels,
        // while visible texels write depth normally. This is required for flipbook
        // sprites such as Electromagnetic Bagworm: geometry behind the visible part
        // must not show through, but geometry behind discarded background pixels can.
        if (_hasAlphaMaskShader)
        {
            _drawMaterial.Shader = _alphaMaskShader;
            foreach (var r in _all)
            {
                if (!r.AlphaMask) continue;
                DrawOne(r);
            }
            _drawMaterial.Shader = _defaultShader;
        }
        else
        {
            // Fallback for systems where the mask shader failed to load.
            foreach (var r in _all)
            {
                if (!r.AlphaMask) continue;
                Raylib.BeginBlendMode(BlendMode.Alpha);
                DrawOne(r);
                Raylib.EndBlendMode();
            }
        }

        // Pass 3: true translucent and additive geometry does not write depth.
        Rlgl.DisableDepthMask();
        foreach (var r in _all)
        {
            if (!(r.Additive || r.AlphaBlend)) continue;
            Raylib.BeginBlendMode(r.Additive ? BlendMode.Additive : BlendMode.Alpha);
            DrawOne(r);
            Raylib.EndBlendMode();
        }
        Rlgl.EnableDepthMask();

        Rlgl.EnableBackfaceCulling();
    }

    unsafe void DrawOne(Renderable r)
    {
        int albedo = (int)MaterialMapIndex.Albedo;
        _drawMaterial.Maps[albedo].Color = r.Tint;
        Texture2D tex = r.HasAnim ? CurrentAnimFrame(r) : (r.HasTexture ? r.Texture : _whiteTex);
        _drawMaterial.Maps[albedo].Texture = tex;
        Raylib.DrawMesh(r.Mesh, _drawMaterial, Matrix4x4.Identity);
    }

    // Texture-animation playback clock (seconds). The viewer advances it at the
    // model's animation speed, so texanim runs in lockstep with skeletal speed
    // (and freezes when the model is paused / in static pose).
    public float TexAnimTime;

    public void AdvanceTextureAnim(float dtSeconds) => TexAnimTime += dtSeconds;

    Texture2D CurrentAnimFrame(Renderable r)
    {
        if (r.AnimFrames.Length == 0) return r.Texture;
        if (r.AnimDuration <= 0f) return r.AnimFrames[0];
        float t = TexAnimTime % r.AnimDuration;
        if (t < 0f) t += r.AnimDuration;
        int idx = 0;
        for (int i = 0; i < r.AnimTimes.Length; i++)
        {
            if (r.AnimTimes[i] <= t) idx = i; else break;
        }
        return r.AnimFrames[idx];
    }

    public void Dispose()
    {
        unsafe
        {
            foreach (var r in _all)
            {
                // If your Raylib-cs declares UnloadMesh(ref Mesh), use: Raylib.UnloadMesh(ref r.Mesh);
                if (r.Uploaded) Raylib.UnloadMesh(r.Mesh); // frees VBOs + RL_MALLOC'd CPU buffers
                if (r.HasAnim)
                {
                    // r.Texture aliases AnimFrames[0]; unload the frame array only.
                    foreach (var t in r.AnimFrames) Raylib.UnloadTexture(t);
                }
                else if (r.HasTexture)
                {
                    Raylib.UnloadTexture(r.Texture);
                }
            }
        }
        if (_hasAlphaMaskShader)
        {
            Raylib.UnloadShader(_alphaMaskShader);
            _hasAlphaMaskShader = false;
        }
        _all.Clear();
        _byDrawable.Clear();
        _instance = null;
        _gltf = null;
    }
}