using System.Numerics;
using System.Text.Json.Nodes;
using Raylib_cs;
using DotrAssetModder;
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
        // Skinned like the game (lRhModelCpu_CalcSharedPoint): each influence has its own copy
        // of the vertex in its joint's space, moved by that joint and summed by weight. Vertex v's
        // influences are InfStart[v] .. InfStart[v + 1] (none: drawn at LocalPos). null => rigid.
        public int[] InfStart;
        public int[] InfJoint;      // skin joint index
        public float[] InfWeight;
        public Vector3[] InfPos;    // the copy in its joint's space
        public Vector3[]? InfNrm;   // its normal (renderables with LocalNrm only)
        public Matrix4x4[] IbmInv;  // per skin joint: inverse of its inverse bind
        public Matrix4x4[] JointWorld; // per skin joint, this frame (scratch)
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

        // UV transform (KHR_texture_transform offset/scale, animated by "dod_uv_anim" extras) and
        // a second MODULATE mask texture ("dod_modulate" extras, Dawn of Destiny _nuki maps).
        public bool HasUvXform;
        public Vector2 Uv0Scale = Vector2.One, Uv0Offset = Vector2.Zero;
        public UvTrack? Uv0Track;
        public bool HasMask;
        public Texture2D MaskTexture;
        public UvTrack? Uv1Track;
        public bool Multiply;       // DoD blend (DST_COLOR, ZERO): dst * src

        // DoD specular term t1 * v1 ("dod_specular" extras): mask x colour x headlight Phong.
        public bool HasSpec;
        public Texture2D SpecTexture;
        public Vector3 SpecColor;
        public float SpecShininess = 32f;
        public UvTrack? Uv2Track;
        // DoD sphere-mapped reflection t2 * v0 ("dod_reflect" extras, the ref_map textures).
        public bool HasRef;
        public Texture2D RefTexture;
        // DoD vertex lighting v0 = emission + ambient + diffuse * N.L ("dod_light" extras).
        public bool HasLight;
        public Vector3 LightAmbient, LightDiffuse, LightEmission;
        public bool NeedsNormals => HasSpec || HasRef || HasLight;
        public Vector3[]? LocalNrm;         // bind-space normals (renderables with NeedsNormals only)
        public unsafe float* Nrm;           // GPU-side normal buffer
        public unsafe float* Uv2;           // GPU-side TEXCOORD_1 (unit-1 mask / specular UVs)
    }

    // Stepped UV offset/scale keys looping over LoopSeconds (DoD igUvAnimeShader, see dod_export.py).
    sealed class UvTrack
    {
        public float LoopSeconds;
        public float[] Times = Array.Empty<float>();
        public Vector2[] Offset = Array.Empty<Vector2>();
        public Vector2[] Scale = Array.Empty<Vector2>();

        public Vector4 Eval(float time)
        {
            if (Times.Length == 0) return new Vector4(1, 1, 0, 0);
            float t = LoopSeconds > 0f ? time % LoopSeconds : 0f;
            if (t < 0f) t += LoopSeconds;
            int idx = 0;
            for (int i = 0; i < Times.Length; i++)
            {
                if (Times[i] <= t) idx = i; else break;
            }
            return new Vector4(Scale[idx].X, Scale[idx].Y, Offset[idx].X, Offset[idx].Y);
        }

        public static UvTrack? Parse(JsonObject o)
        {
            if (o["times"] is not JsonArray ts || o["offset"] is not JsonArray os || o["scale"] is not JsonArray ss) return null;
            int n = Math.Min(ts.Count, Math.Min(os.Count, ss.Count));
            if (n == 0) return null;
            var tr = new UvTrack
            {
                LoopSeconds = o["loop_seconds"]?.GetValue<float>() ?? 0f,
                Times = new float[n], Offset = new Vector2[n], Scale = new Vector2[n],
            };
            for (int i = 0; i < n; i++)
            {
                tr.Times[i] = ts[i]!.GetValue<float>();
                tr.Offset[i] = new Vector2(os[i]![0]!.GetValue<float>(), os[i]![1]!.GetValue<float>());
                tr.Scale[i] = new Vector2(ss[i]![0]!.GetValue<float>(), ss[i]![1]!.GetValue<float>());
            }
            return tr;
        }
    }

    ModelRoot _gltf;
    SceneInstance _instance;
    Vector3 _restPivot;                                 // node 1 / node 0 in the rest pose
    Vector3 _restNodeZero;
    readonly List<List<Renderable>> _byDrawable = new(); // aligned to DrawableInstances order
    readonly List<Renderable> _all = new();
    // First renderable built for each glTF material. Rigid models split one material
    // across many per-bone primitives; they all share that one set of GPU textures.
    readonly Dictionary<int, Renderable> _materialOwners = new();

    RlMaterial _drawMaterial;
    Raylib_cs.Shader _defaultShader;
    Raylib_cs.Shader _alphaMaskShader;
    Texture2D _whiteTex;
    bool _hasDrawMaterial;
    bool _hasAlphaMaskShader;
    int _alphaCutoffLoc = -1;
    sealed class UvShaderLocs
    {
        public Raylib_cs.Shader Shader;
        public int Xform0 = -1, Xform1 = -1, Xform2 = -1, UseMask = -1, AlphaCutoff = -1, SpecColor = -1, Shininess = -1;
        public int UseSpec = -1, UseRef = -1, UseLight = -1, MatAmbient = -1, MatDiffuse = -1, MatEmission = -1,
            LightDir = -1, LightColor = -1, LightAmbient = -1;

        public static unsafe UvShaderLocs? Load(string? vs, string fs)
        {
            var sh = Raylib.LoadShaderFromMemory(vs, fs);
            if (sh.Id == 0) return null;
            // raylib binds only texture0..2 by name; the reflection map is MaterialMapIndex.Roughness -> "texture3"
            sh.Locs[(int)ShaderLocationIndex.MapRoughness] = Raylib.GetShaderLocation(sh, "texture3");
            return new UvShaderLocs
            {
                Shader = sh,
                Xform0 = Raylib.GetShaderLocation(sh, "uvXform0"),
                Xform1 = Raylib.GetShaderLocation(sh, "uvXform1"),
                Xform2 = Raylib.GetShaderLocation(sh, "uvXform2"),
                UseMask = Raylib.GetShaderLocation(sh, "useMask"),
                AlphaCutoff = Raylib.GetShaderLocation(sh, "alphaCutoff"),
                SpecColor = Raylib.GetShaderLocation(sh, "specColor"),
                Shininess = Raylib.GetShaderLocation(sh, "shininess"),
                UseSpec = Raylib.GetShaderLocation(sh, "useSpec"),
                UseRef = Raylib.GetShaderLocation(sh, "useRef"),
                UseLight = Raylib.GetShaderLocation(sh, "useLight"),
                MatAmbient = Raylib.GetShaderLocation(sh, "matAmbient"),
                MatDiffuse = Raylib.GetShaderLocation(sh, "matDiffuse"),
                MatEmission = Raylib.GetShaderLocation(sh, "matEmission"),
                LightDir = Raylib.GetShaderLocation(sh, "lightDir"),
                LightColor = Raylib.GetShaderLocation(sh, "lightColor"),
                LightAmbient = Raylib.GetShaderLocation(sh, "lightAmbient"),
            };
        }
    }
    UvShaderLocs? _uvShader;    // UV transform + modulate mask
    UvShaderLocs? _specShader;  // ... plus the normal-based DoD terms: specular, reflection, lighting

    // DoD scene light, approximated (the game sets it at runtime; it is not in the model files).
    // Direction is in view space (towards the light), so the light follows the camera like the headlight specular.
    public static Vector3 DodLightDir = Vector3.Normalize(new Vector3(-0.3f, 0.5f, 0.8f));
    public static Vector3 DodLightColor = new(1f, 1f, 1f);
    public static Vector3 DodAmbientColor = new(1f, 1f, 1f);
    Vector3[] _scratchN = Array.Empty<Vector3>();

    // Passes both UV sets (DoD unit-1 textures read TEXCOORD_1); the SPEC variant adds view-space
    // position/normal for a headlight Phong term.
    private const string UvVertexShader = @"
        in vec3 vertexPosition;
        in vec2 vertexTexCoord;
        in vec2 vertexTexCoord2;
        in vec4 vertexColor;
        uniform mat4 mvp;
        out vec2 fragTexCoord;
        out vec2 fragTexCoord2;
        out vec4 fragColor;
    #ifdef SPEC
        in vec3 vertexNormal;
        uniform mat4 matView;
        out vec3 fragViewPos;
        out vec3 fragViewNormal;
    #endif

        void main()
        {
            fragTexCoord = vertexTexCoord;
            fragTexCoord2 = vertexTexCoord2;
            fragColor = vertexColor;
    #ifdef SPEC
            fragViewPos = (matView * vec4(vertexPosition, 1.0)).xyz;
            fragViewNormal = mat3(matView) * vertexNormal;
    #endif
            gl_Position = mvp * vec4(vertexPosition, 1.0);
        }";

    // Used only for renderables with a UV transform or a modulate mask; everything else keeps
    // the default / alpha-mask shaders. alphaCutoff < 0 disables the alpha test.
    private const string UvFragmentShader = @"
        in vec2 fragTexCoord;
        in vec2 fragTexCoord2;
        in vec4 fragColor;
        out vec4 finalColor;

        uniform sampler2D texture0;
        uniform sampler2D texture1;
        uniform vec4 colDiffuse;
        uniform vec4 uvXform0;   // xy = scale, zw = offset (KHR_texture_transform)
        uniform vec4 uvXform1;
        uniform int useMask;
        uniform float alphaCutoff;
    #ifdef SPEC
        in vec3 fragViewPos;
        in vec3 fragViewNormal;
        uniform sampler2D texture2;
        uniform vec4 uvXform2;
        uniform vec3 specColor;
        uniform float shininess;
        uniform sampler2D texture3;
        uniform int useSpec;
        uniform int useRef;
        uniform int useLight;
        uniform vec3 matAmbient;
        uniform vec3 matDiffuse;
        uniform vec3 matEmission;
        uniform vec3 lightDir;
        uniform vec3 lightColor;
        uniform vec3 lightAmbient;
    #endif

        void main()
        {
            vec4 color = texture(texture0, fragTexCoord * uvXform0.xy + uvXform0.zw) * colDiffuse * fragColor;
            if (useMask == 1) color.rgb *= texture(texture1, fragTexCoord2 * uvXform1.xy + uvXform1.zw).r;
    #ifdef SPEC
            vec3 n = normalize(fragViewNormal);
            if (!gl_FrontFacing) n = -n;
            // game pixel programs: t0*v0 (*t1 mask) + t1*v1 (specular) + t2*v0 (sphere-mapped ref_map)
            vec3 v0 = vec3(1.0);
            if (useLight == 1)
                v0 = clamp(matEmission + matAmbient * lightAmbient + matDiffuse * lightColor * max(dot(n, lightDir), 0.0), 0.0, 1.0);
            color.rgb *= v0;
            if (useRef == 1)
            {
                // game vertex program: R = reflect(eye->vertex, N) in view space, uv = 0.5 * R.xy + 0.5
                vec3 r = reflect(normalize(fragViewPos), n);
                color.rgb += texture(texture3, r.xy * 0.5 + 0.5).rgb * v0;
            }
            if (useSpec == 1)
            {
                float s = pow(max(dot(n, normalize(-fragViewPos)), 0.0), shininess);
                color.rgb += texture(texture2, fragTexCoord2 * uvXform2.xy + uvXform2.zw).r * specColor * s;
            }
    #endif
            if (alphaCutoff >= 0.0)
            {
                if (color.a < alphaCutoff) discard;
                color.a = 1.0;
            }
            finalColor = color;
        }";

    private const string AlphaMaskFragmentShader = @"#version 330
        in vec2 fragTexCoord;
        in vec4 fragColor;
        out vec4 finalColor;

        uniform sampler2D texture0;
        uniform vec4 colDiffuse;
        uniform float alphaCutoff;

        void main()
        {
            vec4 color = texture(texture0, fragTexCoord) * colDiffuse * fragColor;
            if (color.a < alphaCutoff) discard;
            finalColor = vec4(color.rgb, 1.0);
        }";

    Vector3[] _scratch = Array.Empty<Vector3>(); // reused world-position buffer
    Vector3 _center;

    public Vector3 Center => _center;

    public int AnimationCount { get; private set; }
    public string[] AnimationNames { get; private set; } = Array.Empty<string>();
    float[] _durations = Array.Empty<float>();
    public float Duration(int i) => (i >= 0 && i < _durations.Length) ? _durations[i] : 0f;

    public static AnimatedGltfModel Load(string path)
    {
        var settings = new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.TryFix };
        return Load(new Parsed(ModelRoot.Load(path, settings)));
    }

    // A glb that is only in memory (a model straight out of an archive).
    public static AnimatedGltfModel Load(byte[] glb)
    {
        return Load(Parse(glb));
    }

    // A glb read and its scene built (SharpGLTF only, no GPU): most of a load's time (Blue-Eyes:
    // ~150 of ~160 ms), so a worker thread can do it and the draw thread finishes with Load.
    public sealed class Parsed
    {
        internal readonly ModelRoot Gltf;
        internal readonly SceneInstance Instance;

        internal Parsed(ModelRoot gltf)
        {
            Gltf = gltf;
            var scene = gltf.DefaultScene ?? (gltf.LogicalScenes.Count > 0 ? gltf.LogicalScenes[0] : null);
            if (scene == null) throw new InvalidOperationException("glTF has no scene to render.");
            // IsolateMemory: the animation keys are copied once. Without it SharpGLTF re-reads the
            // accessors on every sample (3x slower per frame, ~0.2-0.7 MB of garbage per frame).
            Instance = SceneTemplate.Create(scene, new RuntimeOptions { IsolateMemory = true }).CreateInstance();
        }
    }

    public static Parsed Parse(byte[] glb)
    {
        var settings = new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.TryFix };
        return new Parsed(ModelRoot.ReadGLB(new MemoryStream(glb), settings));
    }

    // The GPU half: meshes, textures and shaders (draw thread only).
    public static AnimatedGltfModel Load(Parsed parsed)
    {
        var m = new AnimatedGltfModel();
        m.Build(parsed);
        return m;
    }

    void Build(Parsed parsed)
    {
        _gltf = parsed.Gltf;
        _instance = parsed.Instance;
        _restPivot = RestNodePosition("PivotCamera_", "dotr_pivot");
        _restNodeZero = RestNodePosition("Root_node_0", "dotr_world");

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
            var skin = _gltf.LogicalNodes.FirstOrDefault(n => n.Mesh == mesh && n.Skin != null)?.Skin;
            foreach (var prim in mesh.Primitives)                        // SharpGLTF.Schema2.MeshPrimitive
            {
                var r = MakeRenderable(prim, skin);
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

        // ---- Fill bind-pose positions and upload meshes ----
        _scratchN = new Vector3[maxV];
        EnumerateDrawables((r, xform) =>
        {
            FillUnique(r, xform, _scratch);
            WritePositions(r, _scratch);
            WriteNormals(r, xform);
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
        if (_hasAlphaMaskShader)
            _alphaCutoffLoc = Raylib.GetShaderLocation(_alphaMaskShader, "alphaCutoff");
        if (_all.Any(r => (r.HasUvXform || r.HasMask) && !r.NeedsNormals))
            _uvShader = UvShaderLocs.Load("#version 330\n" + UvVertexShader, "#version 330\n" + UvFragmentShader);
        if (_all.Any(r => r.NeedsNormals))
            _specShader = UvShaderLocs.Load("#version 330\n#define SPEC\n" + UvVertexShader, "#version 330\n#define SPEC\n" + UvFragmentShader);
        _hasDrawMaterial = true;
        unsafe { _whiteTex = _drawMaterial.Maps[(int)MaterialMapIndex.Albedo].Texture; }
    }

    unsafe Renderable MakeRenderable(MeshPrimitive prim, Skin? skin)
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
        // DotR vertices carry up to 8 influences; the extractor writes the second four here
        var joints1 = prim.GetVertexAccessor("JOINTS_1")?.AsVector4Array();
        var weights1 = prim.GetVertexAccessor("WEIGHTS_1")?.AsVector4Array();

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

        // Material tint + base color texture.
        if (r.MaterialIndex >= 0 && _materialOwners.TryGetValue(r.MaterialIndex, out Renderable? owner))
        {
            r.Tint = owner.Tint;
            r.HasTexture = owner.HasTexture;
            r.Texture = owner.Texture;
            r.Additive = owner.Additive;
            r.AlphaBlend = owner.AlphaBlend;
            r.AlphaMask = owner.AlphaMask;
            r.AlphaCutoff = owner.AlphaCutoff;
            r.HasAnim = owner.HasAnim;
            r.AnimFrames = owner.AnimFrames;
            r.AnimTimes = owner.AnimTimes;
            r.AnimDuration = owner.AnimDuration;
            r.HasUvXform = owner.HasUvXform;
            r.Uv0Scale = owner.Uv0Scale;
            r.Uv0Offset = owner.Uv0Offset;
            r.Uv0Track = owner.Uv0Track;
            r.HasMask = owner.HasMask;
            r.MaskTexture = owner.MaskTexture;
            r.Uv1Track = owner.Uv1Track;
            r.Multiply = owner.Multiply;
            r.HasSpec = owner.HasSpec;
            r.SpecTexture = owner.SpecTexture;
            r.SpecColor = owner.SpecColor;
            r.SpecShininess = owner.SpecShininess;
            r.Uv2Track = owner.Uv2Track;
            r.HasRef = owner.HasRef;
            r.RefTexture = owner.RefTexture;
            r.HasLight = owner.HasLight;
            r.LightAmbient = owner.LightAmbient;
            r.LightDiffuse = owner.LightDiffuse;
            r.LightEmission = owner.LightEmission;
        }
        else
        {
            BuildMaterial(prim.Material, r);
            if (r.MaterialIndex >= 0) _materialOwners[r.MaterialIndex] = r;
        }

        // the specular / reflection / lighting terms need normals; a primitive without them draws without
        if (r.NeedsNormals)
        {
            var nrm = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
            if (nrm != null && nrm.Count == V)
            {
                r.LocalNrm = new Vector3[V];
                for (int i = 0; i < V; i++) r.LocalNrm[i] = nrm[i];
            }
            else r.HasSpec = r.HasRef = r.HasLight = false;
        }

        if (skin != null && joints != null && weights != null && joints.Count == V)
        {
            if (joints1 == null || weights1 == null || joints1.Count != V) joints1 = weights1 = null;
            BuildInfluences(r, prim, skin, joints, weights, joints1, weights1);
        }

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
        if (r.NeedsNormals)
        {
            r.Nrm = (float*)Raylib.MemAlloc((uint)(gpuVerts * 3 * sizeof(float)));
            r.Mesh.Normals = r.Nrm;
        }
        if (r.NeedsNormals || r.HasMask || r.HasUvXform)
        {
            // the custom vertex shader reads vertexTexCoord2; without TEXCOORD_1 it gets set 0
            var uvs1 = prim.GetVertexAccessor("TEXCOORD_1")?.AsVector2Array();
            if (uvs1 == null || uvs1.Count != V) uvs1 = uvs;
            float* uv2 = (float*)Raylib.MemAlloc((uint)(gpuVerts * 2 * sizeof(float)));
            for (int k = 0; k < gpuVerts; k++)
            {
                int vi = r.Indexed ? k : r.Tri[k];
                Vector2 t = uvs1 != null ? uvs1[vi] : Vector2.Zero;
                uv2[k * 2 + 0] = t.X; uv2[k * 2 + 1] = t.Y;
            }
            r.Mesh.TexCoords2 = uv2;
            r.Uv2 = uv2;
        }
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
            r.Multiply = string.Equals(dotrBlend, "multiply", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Backward compatibility for older/non-DOTR glTF files. Generic DCC
            // exporters (Blender, Maya, ...) commonly default a material's alphaMode
            // to BLEND regardless of whether its texture actually carries alpha, so
            // this is only a starting guess - it gets vetoed below once the base
            // color texture's real alpha channel has been decoded.
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
        if (IsBlackUntextured(mat))
        {
            r.Tint = new Color((byte)0, (byte)0, (byte)0, r.Tint.A);
        }

        try
        {
            var ch = mat?.FindChannel("BaseColor");
            if (ch.HasValue && ch.Value.Texture?.PrimaryImage != null)
            {
                var img = ch.Value.Texture.PrimaryImage.Content;
                if (img.IsValid)
                {
                    RlImage cpu = GlbImporter.DecodeImage(img);
                    if (cpu.Width > 0 && cpu.Height > 0)
                    {
                        if (!hasDotrBlend && (r.AlphaBlend || r.AlphaMask) && !ImageHasAlpha(cpu))
                        {
                            // The foreign glTF's alphaMode guess doesn't match reality -
                            // the decoded texture has no transparent/semi-transparent
                            // texels, so don't push an opaque mesh into the no-depth-write
                            // blend pass.
                            r.AlphaBlend = false;
                            r.AlphaMask = false;
                        }
                        r.Texture = Raylib.LoadTextureFromImage(cpu);
                        r.HasTexture = r.Texture.Id != 0;
                        if (r.HasTexture) ApplySampler(mat, r.Texture);
                        Raylib.UnloadImage(cpu);
                    }
                }
            }
        }
        catch { }

        LoadUvAnimation(mat, r);
        LoadTextureAnimation(mat, r);
    }

    // KHR_texture_transform on the base color (offset/scale; rotation is not used by the DoD
    // exporter), the "dod_uv_anim" extras tracks and the "dod_modulate" base + mask pair.
    void LoadUvAnimation(GltfMaterial? mat, Renderable r)
    {
        try
        {
            var tt = mat?.FindChannel("BaseColor")?.TextureTransform;
            if (tt != null && (tt.Offset != Vector2.Zero || tt.Scale != Vector2.One))
            {
                r.Uv0Offset = tt.Offset;
                r.Uv0Scale = tt.Scale;
                r.HasUvXform = true;
            }
        }
        catch { }
        try
        {
            if (mat?.Extras is not JsonObject ex) return;
            if (ex["dod_uv_anim"] is JsonArray tracks)
            {
                foreach (var node in tracks)
                {
                    if (node is not JsonObject to) continue;
                    string? target = to["target"]?.GetValue<string>();
                    var tr = UvTrack.Parse(to);
                    if (tr == null) continue;
                    if (target == "baseColorTexture") { r.Uv0Track = tr; r.HasUvXform = true; }
                    else if (target == "dod_modulate") r.Uv1Track = tr;
                    else if (target == "KHR_materials_specular/specularTexture") r.Uv2Track = tr;
                }
            }
            if (ex["dod_modulate"] is JsonObject mod)
            {
                // "base" replaces the base color (which has the mask baked in statically, or is the
                // black/alpha stand-in of a multiply material); "mask" is optional
                int bi = mod["base"]?.GetValue<int>() ?? -1;
                int mi = mod["mask"]?.GetValue<int>() ?? -1;
                Texture2D baseTex = LoadGltfTexture(mat, bi);
                if (baseTex.Id != 0)
                {
                    Texture2D mask = mi >= 0 ? LoadGltfTexture(mat, mi) : default;
                    if (mi < 0 || mask.Id != 0)
                    {
                        if (r.HasTexture && r.Texture.Id != 0) Raylib.UnloadTexture(r.Texture);
                        r.Texture = baseTex;
                        r.HasTexture = true;
                        r.MaskTexture = mask;
                        r.HasMask = mask.Id != 0;
                    }
                    else Raylib.UnloadTexture(baseTex);
                }
            }
            if (ex["dod_specular"] is JsonObject spec && spec["color"] is JsonArray sc && sc.Count >= 3)
            {
                var col = new Vector3(sc[0]!.GetValue<float>(), sc[1]!.GetValue<float>(), sc[2]!.GetValue<float>());
                int si = spec["texture"]?.GetValue<int>() ?? -1;
                if (col != Vector3.Zero && si >= 0)
                {
                    Texture2D st = LoadGltfTexture(mat, si);
                    if (st.Id != 0)
                    {
                        r.SpecTexture = st;
                        r.SpecColor = col;
                        r.SpecShininess = MathF.Max(spec["shininess"]?.GetValue<float>() ?? 32f, 1f);
                        r.HasSpec = true;
                    }
                }
            }
            if (ex["dod_reflect"] is JsonObject refl)
            {
                Texture2D rt = LoadGltfTexture(mat, refl["texture"]?.GetValue<int>() ?? -1);
                if (rt.Id != 0)
                {
                    Raylib.SetTextureWrap(rt, TextureWrap.Clamp);
                    r.RefTexture = rt;
                    r.HasRef = true;
                }
            }
            if (ex["dod_light"] is JsonObject li)
            {
                static Vector3 V3(JsonNode? n) => n is JsonArray a && a.Count >= 3
                    ? new Vector3(a[0]!.GetValue<float>(), a[1]!.GetValue<float>(), a[2]!.GetValue<float>()) : Vector3.Zero;
                r.LightAmbient = V3(li["ambient"]);
                r.LightDiffuse = V3(li["diffuse"]);
                r.LightEmission = V3(li["emission"]);
                r.HasLight = true;
            }
        }
        catch { }
    }

    Texture2D LoadGltfTexture(GltfMaterial? mat, int textureIndex)
    {
        if (textureIndex < 0 || textureIndex >= _gltf.LogicalTextures.Count) return default;
        var img = _gltf.LogicalTextures[textureIndex].PrimaryImage?.Content;
        if (img == null || !img.Value.IsValid) return default;
        RlImage cpu = GlbImporter.DecodeImage(img.Value);
        if (cpu.Width <= 0 || cpu.Height <= 0) return default;
        Texture2D t = Raylib.LoadTextureFromImage(cpu);
        Raylib.UnloadImage(cpu);
        if (t.Id != 0) ApplySampler(mat, t);
        return t;
    }

    // Applies the material's glTF sampler (written by the extractor from the GS
    // CLAMP/TEX1 registers) to a Raylib texture. Raylib's default is REPEAT on both
    // axes, which lets bilinear filtering at a clamped edge pull in the opposite side
    // of an atlas. glbs without a sampler keep Raylib's defaults.
    static void ApplySampler(GltfMaterial? mat, Texture2D tex)
    {
        const int GlTextureWrapS = 0x2802;
        const int GlTextureWrapT = 0x2803;
        const int GlRepeat = 0x2901;
        const int GlClampToEdge = 0x812F;
        const int GlMirroredRepeat = 0x8370;
        try
        {
            SharpGLTF.Schema2.MaterialChannel? ch = mat?.FindChannel("BaseColor");
            if (!ch.HasValue) return;
            SharpGLTF.Schema2.TextureSampler? sampler = ch.Value.TextureSampler;
            if (sampler == null) return;

            int wrapS = GlRepeat;
            int wrapT = GlRepeat;
            if (sampler.WrapS == SharpGLTF.Schema2.TextureWrapMode.CLAMP_TO_EDGE) wrapS = GlClampToEdge;
            if (sampler.WrapS == SharpGLTF.Schema2.TextureWrapMode.MIRRORED_REPEAT) wrapS = GlMirroredRepeat;
            if (sampler.WrapT == SharpGLTF.Schema2.TextureWrapMode.CLAMP_TO_EDGE) wrapT = GlClampToEdge;
            if (sampler.WrapT == SharpGLTF.Schema2.TextureWrapMode.MIRRORED_REPEAT) wrapT = GlMirroredRepeat;
            Rlgl.TextureParameters(tex.Id, GlTextureWrapS, wrapS);
            Rlgl.TextureParameters(tex.Id, GlTextureWrapT, wrapT);

            if (sampler.MagFilter == SharpGLTF.Schema2.TextureInterpolationFilter.LINEAR)
            {
                Raylib.SetTextureFilter(tex, TextureFilter.Bilinear);
            }
            if (sampler.MagFilter == SharpGLTF.Schema2.TextureInterpolationFilter.NEAREST)
            {
                Raylib.SetTextureFilter(tex, TextureFilter.Point);
            }
        }
        catch { }
    }

    static bool ImageHasAlpha(RlImage img)
    {
        RlImage rgba = Raylib.ImageCopy(img);
        try
        {
            Raylib.ImageFormat(ref rgba, PixelFormat.UncompressedR8G8B8A8);
            int count = rgba.Width * rgba.Height;
            if (count <= 0) return false;
            byte[] bytes = new byte[count * 4];
            unsafe { System.Runtime.InteropServices.Marshal.Copy((IntPtr)rgba.Data, bytes, 0, bytes.Length); }
            for (int i = 3; i < bytes.Length; i += 4)
                if (bytes[i] != 255) return true;
            return false;
        }
        finally
        {
            Raylib.UnloadImage(rgba);
        }
    }

    // An untextured page whose colour row is (0,0,0) draws black in the game (the black plane
    // under the map's sea). The extractor writes white for a zero row, which is right for a
    // textured page (booKoo's book) but turned this plane, and the additive sea over it, white.
    static bool IsBlackUntextured(GltfMaterial? mat)
    {
        try
        {
            if (mat?.Extras is not JsonObject extras || extras["DOTR_blend"] is not JsonObject blend)
            {
                return false;
            }
            if (blend["textured"]?.GetValue<bool>() != false)
            {
                return false;
            }
            return blend["materialColorR"]?.GetValue<int>() == 0 &&
                   blend["materialColorG"]?.GetValue<int>() == 0 &&
                   blend["materialColorB"]?.GetValue<int>() == 0;
        }
        catch
        {
            return false;
        }
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
                RlImage cpu = GlbImporter.DecodeImage(content);
                if (cpu.Width > 0 && cpu.Height > 0)
                {
                    Texture2D t2d = Raylib.LoadTextureFromImage(cpu);
                    Raylib.UnloadImage(cpu);
                    if (t2d.Id != 0)
                    {
                        ApplySampler(mat, t2d);
                        texList.Add(t2d);
                        timeList.Add(t);
                    }
                }
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
        if (xform is SharpGLTF.Transforms.SkinnedTransform st && r.InfStart != null)
        {
            SetJointWorld(r, st.SkinMatrices);
            for (int v = 0; v < r.V; v++)
            {
                int s = r.InfStart[v], e = r.InfStart[v + 1];
                if (s == e) { outv[v] = r.LocalPos[v]; continue; }
                Vector3 p = default;
                for (int k = s; k < e; k++)
                    p += r.InfWeight[k] * Vector3.Transform(r.InfPos[k], r.JointWorld[r.InfJoint[k]]);
                outv[v] = p;
            }
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

    // Each skin joint's world matrix this frame: SkinMatrices are inverseBind * jointWorld.
    static void SetJointWorld(Renderable r, IReadOnlyList<Matrix4x4> skin)
    {
        int n = Math.Min(r.JointWorld.Length, skin.Count);
        for (int j = 0; j < n; j++) r.JointWorld[j] = r.IbmInv[j] * skin[j];
    }

    // The influences FillUnique sums. A vertex whose ModelExtractor copies (GlbImporter.GameAccessors)
    // still describe it, by the importer's rule, uses them as stored (weight raw/4096, normal
    // raw/32767, zero weights left out). Any other vertex (a converted import, or one moved or
    // reweighted outside the app) gets one copy per JOINTS/WEIGHTS influence from POSITION and
    // NORMAL, which skins exactly as the glTF blended matrix would; none when its weights sum to
    // ~0 (drawn where it is).
    static void BuildInfluences(Renderable r, MeshPrimitive prim, Skin skin, IList<Vector4> joints,
        IList<Vector4> weights, IList<Vector4>? joints1, IList<Vector4>? weights1)
    {
        int jc = skin.JointsCount;
        var ibm = new Matrix4x4[jc];
        r.IbmInv = new Matrix4x4[jc];
        r.JointWorld = new Matrix4x4[jc];
        for (int j = 0; j < jc; j++)
        {
            ibm[j] = skin.GetJoint(j).InverseBindMatrix;
            if (!Matrix4x4.Invert(ibm[j], out r.IbmInv[j])) r.IbmInv[j] = Matrix4x4.Identity;
        }
        var game = GlbImporter.GameAccessors(prim);
        bool nrm = r.LocalNrm != null;
        var ij = new List<int>(r.V * 2);
        var iw = new List<float>(r.V * 2);
        var ip = new List<Vector3>(r.V * 2);
        var inn = new List<Vector3>(nrm ? r.V * 2 : 0);
        var gameW = new Dictionary<int, float>();
        var glbW = new Dictionary<int, float>();
        r.InfStart = new int[r.V + 1];
        for (int v = 0; v < r.V; v++)
        {
            r.InfStart[v] = ij.Count;
            Vector4 j0 = joints[v], w0 = weights[v];
            Vector4 j1 = joints1?[v] ?? default, w1 = weights1?[v] ?? default;
            if (game != null && game.Jw[0][v].X >= 0)
            {
                gameW.Clear();
                glbW.Clear();
                int wsum = 0;
                bool ok = true;
                for (int k = 0; k < game.Jw.Length && game.Jw[k][v].X >= 0; k++)
                {
                    int j = (int)game.Jw[k][v].X;
                    if (j >= jc) { ok = false; break; }
                    short w = (short)game.Jw[k][v].Y;
                    if (w <= 0) continue;
                    gameW[j] = gameW.GetValueOrDefault(j) + w;
                    wsum += w;
                }
                void Glb(float j, float w)
                {
                    if (w > 0f && j >= 0 && j < jc) glbW[(int)j] = glbW.GetValueOrDefault((int)j) + w;
                }
                Glb(j0.X, w0.X); Glb(j0.Y, w0.Y); Glb(j0.Z, w0.Z); Glb(j0.W, w0.W);
                Glb(j1.X, w1.X); Glb(j1.Y, w1.Y); Glb(j1.Z, w1.Z); Glb(j1.W, w1.W);
                ok = ok && GlbImporter.GameWeightsMatch(gameW, wsum, glbW)
                    && GlbImporter.GameRefPosMatches(Vector3.Transform(r.LocalPos[v], ibm[(int)game.Jw[0][v].X]), game.RefPos[v]);
                if (ok)
                {
                    for (int k = 0; k < game.Jw.Length && game.Jw[k][v].X >= 0; k++)
                    {
                        float w = game.Jw[k][v].Y;
                        if (w == 0f) continue;
                        ij.Add((int)game.Jw[k][v].X);
                        iw.Add(w / 4096f);
                        ip.Add(game.Pos[k][v]);
                        if (nrm) inn.Add(game.Nrm[k][v] / 32767f);
                    }
                    continue;
                }
            }
            if (w0.X + w0.Y + w0.Z + w0.W + w1.X + w1.Y + w1.Z + w1.W < 1e-5f) continue;
            void Derived(float j, float w)
            {
                if (w == 0f || j < 0 || j >= jc) return;
                ij.Add((int)j);
                iw.Add(w);
                ip.Add(Vector3.Transform(r.LocalPos[v], ibm[(int)j]));
                if (nrm) inn.Add(Vector3.TransformNormal(r.LocalNrm![v], ibm[(int)j]));
            }
            Derived(j0.X, w0.X); Derived(j0.Y, w0.Y); Derived(j0.Z, w0.Z); Derived(j0.W, w0.W);
            Derived(j1.X, w1.X); Derived(j1.Y, w1.Y); Derived(j1.Z, w1.Z); Derived(j1.W, w1.W);
        }
        r.InfStart[r.V] = ij.Count;
        r.InfJoint = ij.ToArray();
        r.InfWeight = iw.ToArray();
        r.InfPos = ip.ToArray();
        r.InfNrm = nrm ? inn.ToArray() : null;
    }

    // Skinned normals for the specular term (the same influences as FillUnique). Returns false
    // for renderables that carry no normals.
    unsafe bool WriteNormals(Renderable r, object xform)
    {
        if (r.LocalNrm == null || r.Nrm == null) return false;
        var outn = _scratchN;
        if (xform is SharpGLTF.Transforms.SkinnedTransform st && r.InfStart != null)
        {
            SetJointWorld(r, st.SkinMatrices);
            for (int v = 0; v < r.V; v++)
            {
                int s = r.InfStart[v], e = r.InfStart[v + 1];
                if (s == e) { outn[v] = r.LocalNrm[v]; continue; }
                Vector3 n = default;
                for (int k = s; k < e; k++)
                    n += r.InfWeight[k] * Vector3.TransformNormal(r.InfNrm![k], r.JointWorld[r.InfJoint[k]]);
                outn[v] = n;
            }
        }
        else if (xform is SharpGLTF.Transforms.RigidTransform rt)
        {
            Matrix4x4 m = rt.WorldMatrix;
            for (int v = 0; v < r.V; v++) outn[v] = Vector3.TransformNormal(r.LocalNrm[v], m);
        }
        else
        {
            for (int v = 0; v < r.V; v++) outn[v] = r.LocalNrm[v];
        }
        int count = r.Indexed ? r.V : r.Tri.Length;
        for (int k = 0; k < count; k++)
        {
            Vector3 n = outn[r.Indexed ? k : r.Tri[k]];
            float len = n.Length();
            if (len > 1e-8f) n /= len;
            r.Nrm[k * 3 + 0] = n.X; r.Nrm[k * 3 + 1] = n.Y; r.Nrm[k * 3 + 2] = n.Z;
        }
        return true;
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

    /// <summary>Over up to 16 evenly spaced frames of an animation (animIndex &lt; 0: the bind
    /// pose), the lowest point, the tallest single frame and the X extent over all frames, in the
    /// glb's own units (not recentred), the way the converter measures a model. CPU only; the
    /// armature is left at the last sampled frame, so set the frame again before drawing.</summary>
    public void MeasureStanding(int animIndex, out float lowest, out float height, out float left, out float right,
        out float back, out float front)
    {
        lowest = 0f;
        height = 0f;
        left = 0f;
        right = 0f;
        back = 0f;
        front = 0f;
        if (_instance == null) return;
        int frames = 1;
        float duration = 0f;
        if (animIndex >= 0 && animIndex < AnimationCount)
        {
            duration = _durations[animIndex];
            frames = 16;
        }
        bool first = true;
        for (int f = 0; f < frames; f++)
        {
            if (animIndex >= 0 && animIndex < AnimationCount)
            {
                float t = 0f;
                if (frames > 1)
                {
                    t = duration * f / (frames - 1);
                }
                _instance.Armature.SetAnimationFrame(animIndex, t);
            }
            else
            {
                _instance.Armature.SetPoseTransforms();
            }
            float lo = float.MaxValue;
            float hi = float.MinValue;
            float xlo = float.MaxValue;
            float xhi = float.MinValue;
            float zlo = float.MaxValue;
            float zhi = float.MinValue;
            EnumerateDrawables((r, xform) =>
            {
                FillUnique(r, xform, _scratch);
                for (int v = 0; v < r.V; v++)
                {
                    lo = MathF.Min(lo, _scratch[v].Y);
                    hi = MathF.Max(hi, _scratch[v].Y);
                    xlo = MathF.Min(xlo, _scratch[v].X);
                    xhi = MathF.Max(xhi, _scratch[v].X);
                    zlo = MathF.Min(zlo, _scratch[v].Z);
                    zhi = MathF.Max(zhi, _scratch[v].Z);
                }
            });
            if (lo > hi)
            {
                continue;
            }
            if (first || xlo < left)
            {
                left = xlo;
            }
            if (first || xhi > right)
            {
                right = xhi;
            }
            if (first || zlo < back)
            {
                back = zlo;
            }
            if (first || zhi > front)
            {
                front = zhi;
            }
            if (first || lo < lowest)
            {
                lowest = lo;
            }
            if (first || hi - lo > height)
            {
                height = hi - lo;
            }
            first = false;
        }
    }

    /// <summary>Where node 1 (the game's scale pivot) is in the REST pose: the extractor's
    /// "PivotCamera_" node or the converter's "dotr_pivot"; the origin when there is none. The
    /// game's board deform pivots on node 1's rest offset (SzModel_DrawWithFunc_Pipe reads it
    /// from the block's node tree, which the motion never writes), not on the animated node.
    /// Both kinds of glb have it at the origin; read once at load.</summary>
    public Vector3 RestPivot => _restPivot;

    /// <summary>Where node 0 (the game's standing point) is in the rest pose: the extractor's
    /// "Root_node_0" or the converter's "dotr_world"; the origin when there is none. Not always
    /// the origin: an extracted block's MrgRoot moves node 1 to the origin, so node 0 sits at
    /// -node 1's rest offset (Blue-Eyes: 9.2 up, 33.4 forward).</summary>
    public Vector3 RestNodeZero => _restNodeZero;

    Vector3 RestNodePosition(string extractedPrefix, string convertedName)
    {
        foreach (var n in _gltf.LogicalNodes)
        {
            if (IsNode(n.Name, extractedPrefix, convertedName))
                return n.WorldMatrix.Translation;
        }
        return Vector3.Zero;
    }

    static bool IsNode(string? name, string extractedPrefix, string convertedName)
    {
        name ??= string.Empty;
        return name.StartsWith(extractedPrefix, StringComparison.Ordinal) || name == convertedName;
    }

    /// <summary>Where node 1 is in the pose last set (SetAnimationFrame): the point the game's
    /// board focus and follow cameras aim at (SzModel_GetCenter, node 1's live transform). The
    /// origin when there is none.</summary>
    public Vector3 PivotNow()
    {
        if (_instance == null) return Vector3.Zero;
        foreach (var n in _instance.Armature.LogicalNodes)
        {
            if (IsNode(n.Name, "PivotCamera_", "dotr_pivot"))
                return n.ModelMatrix.Translation;
        }
        return Vector3.Zero;
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
                if (WriteNormals(r, xform))
                    Raylib.UpdateMeshBuffer(r.Mesh, 2, r.Nrm, gpuVerts * 3 * sizeof(float), 0); // VBO 2 = normals
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
            if (r.Additive || r.AlphaBlend || r.AlphaMask || r.Multiply) continue;
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
                if (_alphaCutoffLoc >= 0)
                    Raylib.SetShaderValue(_alphaMaskShader, _alphaCutoffLoc, r.AlphaCutoff, ShaderUniformDataType.Float);
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
            if (!(r.Additive || r.AlphaBlend || r.Multiply)) continue;
            // multiplied = DST_COLOR, ONE_MINUS_SRC_ALPHA: exactly dst * src with the base's alpha 1
            Raylib.BeginBlendMode(r.Additive ? BlendMode.Additive : r.Multiply ? BlendMode.Multiplied : BlendMode.Alpha);
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
        var uvs = r.NeedsNormals ? _specShader : (r.HasUvXform || r.HasMask) ? _uvShader : null;
        if (uvs != null)
        {
            DrawUv(r, uvs);
            return;
        }
        Raylib.DrawMesh(r.Mesh, _drawMaterial, Matrix4x4.Identity);
    }

    // Draws with the UV/modulate shader, keeping the current pass's blend state; inside the
    // alpha-mask pass the shader does the alpha test itself.
    unsafe void DrawUv(Renderable r, UvShaderLocs u)
    {
        var prev = _drawMaterial.Shader;
        bool maskPass = _hasAlphaMaskShader && prev.Id == _alphaMaskShader.Id;
        var sh = u.Shader;
        _drawMaterial.Shader = sh;
        Vector4 x0 = r.Uv0Track?.Eval(TexAnimTime) ?? new Vector4(r.Uv0Scale.X, r.Uv0Scale.Y, r.Uv0Offset.X, r.Uv0Offset.Y);
        Vector4 x1 = r.Uv1Track?.Eval(TexAnimTime) ?? new Vector4(1, 1, 0, 0);
        Raylib.SetShaderValue(sh, u.Xform0, x0, ShaderUniformDataType.Vec4);
        Raylib.SetShaderValue(sh, u.Xform1, x1, ShaderUniformDataType.Vec4);
        Raylib.SetShaderValue(sh, u.UseMask, r.HasMask ? 1 : 0, ShaderUniformDataType.Int);
        Raylib.SetShaderValue(sh, u.AlphaCutoff, maskPass ? r.AlphaCutoff : -1f, ShaderUniformDataType.Float);
        int maskMap = (int)MaterialMapIndex.Metalness; // bound to "texture1"
        int specMap = (int)MaterialMapIndex.Normal;    // bound to "texture2"
        int refMap = (int)MaterialMapIndex.Roughness;  // bound to "texture3"
        var prevMask = _drawMaterial.Maps[maskMap].Texture;
        var prevSpec = _drawMaterial.Maps[specMap].Texture;
        var prevRef = _drawMaterial.Maps[refMap].Texture;
        if (r.NeedsNormals)
        {
            Raylib.SetShaderValue(sh, u.UseSpec, r.HasSpec ? 1 : 0, ShaderUniformDataType.Int);
            Raylib.SetShaderValue(sh, u.UseRef, r.HasRef ? 1 : 0, ShaderUniformDataType.Int);
            Raylib.SetShaderValue(sh, u.UseLight, r.HasLight ? 1 : 0, ShaderUniformDataType.Int);
            Raylib.SetShaderValue(sh, u.MatAmbient, r.LightAmbient, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(sh, u.MatDiffuse, r.LightDiffuse, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(sh, u.MatEmission, r.LightEmission, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(sh, u.LightDir, DodLightDir, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(sh, u.LightColor, DodLightColor, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(sh, u.LightAmbient, DodAmbientColor, ShaderUniformDataType.Vec3);
            if (r.HasRef) _drawMaterial.Maps[refMap].Texture = r.RefTexture;
        }
        if (r.HasMask) _drawMaterial.Maps[maskMap].Texture = r.MaskTexture;
        if (r.HasSpec)
        {
            Vector4 x2 = r.Uv2Track?.Eval(TexAnimTime) ?? new Vector4(1, 1, 0, 0);
            Raylib.SetShaderValue(sh, u.Xform2, x2, ShaderUniformDataType.Vec4);
            Raylib.SetShaderValue(sh, u.SpecColor, r.SpecColor, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(sh, u.Shininess, r.SpecShininess, ShaderUniformDataType.Float);
            _drawMaterial.Maps[specMap].Texture = r.SpecTexture;
        }
        Raylib.DrawMesh(r.Mesh, _drawMaterial, Matrix4x4.Identity);
        _drawMaterial.Maps[maskMap].Texture = prevMask;
        _drawMaterial.Maps[specMap].Texture = prevSpec;
        _drawMaterial.Maps[refMap].Texture = prevRef;
        _drawMaterial.Shader = prev;
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
            // Textures are shared between renderables of the same material, so
            // unload each GPU texture once.
            HashSet<uint> unloaded = new HashSet<uint>();
            foreach (var r in _all)
            {
                // If your Raylib-cs declares UnloadMesh(ref Mesh), use: Raylib.UnloadMesh(ref r.Mesh);
                if (r.Uploaded) Raylib.UnloadMesh(r.Mesh); // frees VBOs + RL_MALLOC'd CPU buffers
                if (r.HasAnim)
                {
                    // r.Texture aliases AnimFrames[0]; unload the frame array only.
                    foreach (var t in r.AnimFrames)
                    {
                        if (unloaded.Add(t.Id)) Raylib.UnloadTexture(t);
                    }
                }
                else if (r.HasTexture)
                {
                    if (unloaded.Add(r.Texture.Id)) Raylib.UnloadTexture(r.Texture);
                }
                if (r.HasMask && unloaded.Add(r.MaskTexture.Id)) Raylib.UnloadTexture(r.MaskTexture);
                if (r.HasSpec && unloaded.Add(r.SpecTexture.Id)) Raylib.UnloadTexture(r.SpecTexture);
                if (r.HasRef && unloaded.Add(r.RefTexture.Id)) Raylib.UnloadTexture(r.RefTexture);
            }
        }
        if (_uvShader != null) Raylib.UnloadShader(_uvShader.Shader);
        if (_specShader != null) Raylib.UnloadShader(_specShader.Shader);
        _uvShader = _specShader = null;
        if (_hasAlphaMaskShader)
        {
            Raylib.UnloadShader(_alphaMaskShader);
            _hasAlphaMaskShader = false;
        }
        _all.Clear();
        _materialOwners.Clear();
        _byDrawable.Clear();
        _instance = null;
        _gltf = null;
    }
}