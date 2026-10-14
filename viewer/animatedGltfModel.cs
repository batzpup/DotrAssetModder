using System.Numerics;
using System.Text.Json.Nodes;
using Raylib_cs;
using SharpGLTF.Schema2;
using SharpGLTF.Runtime;
using SharpGLTF.Materials;
using AlphaMode = SharpGLTF.Schema2.AlphaMode;
//AI Generated

/* Raylib_cs and SharpGLTF.Schema2 both declare Mesh, Material and Image, so they are aliased
   below (RlMesh, RlMaterial, RlImage, GltfMaterial) to make every use explicit.
   Packages: Raylib-cs; SharpGLTF.Core (Schema2, Transforms: RigidTransform and SkinnedTransform
   are matched via `is`, fully qualified; the base transform interface is never named, so its
   version-specific name does not matter); SharpGLTF.Runtime (SceneTemplate, SceneInstance,
   DrawableInstance); SharpGLTF.Toolkit (Materials: MaterialBuilder, KnownChannel, KnownProperty). */
using RlMesh       = Raylib_cs.Mesh;
using RlMaterial   = Raylib_cs.Material;
using RlImage      = Raylib_cs.Image;   // SharpGLTF.Schema2 also declares Image
using GltfMaterial = SharpGLTF.Schema2.Material;
namespace DotrAssetModder;



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
        public int VertexCount;               // unique vertex count
        public int TriCount;
        public unsafe float* GpuPositions;   // GPU-side position buffer (RL_MALLOC, freed by UnloadMesh)

        public Vector3[] LocalPos;  // bind-space positions
        /* Skinned like the game (lRhModelCpu_CalcSharedPoint): each influence has its own copy
           of the vertex in its joint's space, moved by that joint and summed by weight. Vertex v's
           influences are InfluenceStart[v] .. InfluenceStart[v + 1] (none: drawn at LocalPos).
           null => rigid. */
        public int[] InfluenceStart;
        public int[] InfluenceJoint;      // skin joint index
        public float[] InfluenceWeight;
        public Vector3[] InfluencePositions;    // the copy in its joint's space
        public Vector3[]? InfluenceNormals;   // its normal (renderables with LocalNrm only)
        public Matrix4x4[] InverseOfInverseBind;  // per skin joint: inverse of its inverse bind
        public Matrix4x4[] JointWorld; // per skin joint, this frame (scratch)
        public int[] TriangleIndices;           // flat triangle indices into LocalPos (len 3*TriCount)

        public int MaterialIndex = -1;
        public unsafe float* GpuTexCoords;    // GPU-side texcoord buffer

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
        public bool HasUvTransform;
        public Vector2 Uv0Scale = Vector2.One, Uv0Offset = Vector2.Zero;
        public UvTrack? Uv0Track;
        public bool HasMask;
        public Texture2D MaskTexture;
        public UvTrack? Uv1Track;
        public bool Multiply;       // DoD blend (DST_COLOR, ZERO): dst * src

        // DoD specular term t1 * v1 ("dod_specular" extras): mask x colour x headlight Phong.
        public bool HasSpecular;
        public Texture2D SpecularTexture;
        public Vector3 SpecularColor;
        public float SpecularShininess = 32f;
        public UvTrack? Uv2Track;
        // DoD sphere-mapped reflection t2 * v0 ("dod_reflect" extras, the ref_map textures).
        public bool HasReflection;
        public Texture2D ReflectionTexture;
        // DoD vertex lighting v0 = emission + ambient + diffuse * N.L ("dod_light" extras).
        public bool HasLight;
        public Vector3 LightAmbient, LightDiffuse, LightEmission;
        public bool NeedsNormals => HasSpecular || HasReflection || HasLight;
        public Vector3[]? LocalNrm;         // bind-space normals (renderables with NeedsNormals only)
        public unsafe float* GpuNormals;           // GPU-side normal buffer
        public unsafe float* GpuTexCoords2;           // GPU-side TEXCOORD_1 (unit-1 mask / specular UVs)
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
            float loopedTime = LoopSeconds > 0f ? time % LoopSeconds : 0f;
            if (loopedTime < 0f) loopedTime += LoopSeconds;
            int keyIndex = 0;
            for (int i = 0; i < Times.Length; i++)
            {
                if (Times[i] <= loopedTime) keyIndex = i; else break;
            }
            return new Vector4(Scale[keyIndex].X, Scale[keyIndex].Y, Offset[keyIndex].X, Offset[keyIndex].Y);
        }

        public static UvTrack? Parse(JsonObject json)
        {
            if (json["times"] is not JsonArray timesJson || json["offset"] is not JsonArray offsetsJson || json["scale"] is not JsonArray scalesJson) return null;
            int keyCount = Math.Min(timesJson.Count, Math.Min(offsetsJson.Count, scalesJson.Count));
            if (keyCount == 0) return null;
            var track = new UvTrack
            {
                LoopSeconds = json["loop_seconds"]?.GetValue<float>() ?? 0f,
                Times = new float[keyCount], Offset = new Vector2[keyCount], Scale = new Vector2[keyCount],
            };
            for (int i = 0; i < keyCount; i++)
            {
                track.Times[i] = timesJson[i]!.GetValue<float>();
                track.Offset[i] = new Vector2(offsetsJson[i]![0]!.GetValue<float>(), offsetsJson[i]![1]!.GetValue<float>());
                track.Scale[i] = new Vector2(scalesJson[i]![0]!.GetValue<float>(), scalesJson[i]![1]!.GetValue<float>());
            }
            return track;
        }
    }

    ModelRoot _gltf;
    SceneInstance _sceneInstance;
    Vector3 _restPivot;                                 // node 1 / node 0 in the rest pose
    Vector3 _restNodeZero;
    readonly List<List<Renderable>> _byDrawable = new(); // aligned to DrawableInstances order
    readonly List<Renderable> _allRenderables = new();
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
        public int UvTransform0 = -1, UvTransform1 = -1, UvTransform2 = -1, UseMask = -1, AlphaCutoff = -1, SpecularColor = -1, Shininess = -1;
        public int UseSpecular = -1, UseReflection = -1, UseLight = -1, MatAmbient = -1, MatDiffuse = -1, MatEmission = -1,
            LightDir = -1, LightColor = -1, LightAmbient = -1;

        public static unsafe UvShaderLocs? Load(string? vertexShaderSource, string fragmentShaderSource)
        {
            var shader = Raylib.LoadShaderFromMemory(vertexShaderSource, fragmentShaderSource);
            if (shader.Id == 0) return null;
            // raylib binds only texture0..2 by name; the reflection map is MaterialMapIndex.Roughness -> "texture3"
            shader.Locs[(int)ShaderLocationIndex.MapRoughness] = Raylib.GetShaderLocation(shader, "texture3");
            return new UvShaderLocs
            {
                Shader = shader,
                UvTransform0 = Raylib.GetShaderLocation(shader, "uvXform0"),
                UvTransform1 = Raylib.GetShaderLocation(shader, "uvXform1"),
                UvTransform2 = Raylib.GetShaderLocation(shader, "uvXform2"),
                UseMask = Raylib.GetShaderLocation(shader, "useMask"),
                AlphaCutoff = Raylib.GetShaderLocation(shader, "alphaCutoff"),
                SpecularColor = Raylib.GetShaderLocation(shader, "specColor"),
                Shininess = Raylib.GetShaderLocation(shader, "shininess"),
                UseSpecular = Raylib.GetShaderLocation(shader, "useSpec"),
                UseReflection = Raylib.GetShaderLocation(shader, "useRef"),
                UseLight = Raylib.GetShaderLocation(shader, "useLight"),
                MatAmbient = Raylib.GetShaderLocation(shader, "matAmbient"),
                MatDiffuse = Raylib.GetShaderLocation(shader, "matDiffuse"),
                MatEmission = Raylib.GetShaderLocation(shader, "matEmission"),
                LightDir = Raylib.GetShaderLocation(shader, "lightDir"),
                LightColor = Raylib.GetShaderLocation(shader, "lightColor"),
                LightAmbient = Raylib.GetShaderLocation(shader, "lightAmbient"),
            };
        }
    }
    UvShaderLocs? _uvShader;    // UV transform + modulate mask
    UvShaderLocs? _specularShader;  // ... plus the normal-based DoD terms: specular, reflection, lighting

    // DoD scene light, approximated (the game sets it at runtime; it is not in the model files).
    // Direction is in view space (towards the light), so the light follows the camera like the headlight specular.
    public static Vector3 DodLightDir = Vector3.Normalize(new Vector3(-0.3f, 0.5f, 0.8f));
    public static Vector3 DodLightColor = new(1f, 1f, 1f);
    public static Vector3 DodAmbientColor = new(1f, 1f, 1f);
    Vector3[] _scratchNormals = Array.Empty<Vector3>();

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

    Vector3[] _scratchPositions = Array.Empty<Vector3>(); // reused world-position buffer
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
        var model = new AnimatedGltfModel();
        model.Build(parsed);
        return model;
    }

    void Build(Parsed parsed)
    {
        _gltf = parsed.Gltf;
        _sceneInstance = parsed.Instance;
        _restPivot = RestNodePosition("PivotCamera_", "dotr_pivot");
        _restNodeZero = RestNodePosition("Root_node_0", "dotr_world");

        AnimationCount = _gltf.LogicalAnimations.Count;
        AnimationNames = new string[AnimationCount];
        _durations = new float[AnimationCount];
        for (int i = 0; i < AnimationCount; i++)
        {
            var animation = _gltf.LogicalAnimations[i];
            AnimationNames[i] = string.IsNullOrWhiteSpace(animation.Name) ? $"Animation {i}" : animation.Name;
            _durations[i] = animation.Duration;
        }

        // ---- Build one renderable per (drawable, primitive); read static data once ----
        int maxVertexCount = 1;
        foreach (var drawable in _sceneInstance) // SceneInstance : IEnumerable<DrawableInstance>
        {
            var bucket = new List<Renderable>();
            _byDrawable.Add(bucket);

            var mesh = _gltf.LogicalMeshes[drawable.Template.LogicalMeshIndex]; // SharpGLTF.Schema2.Mesh
            var skin = _gltf.LogicalNodes.FirstOrDefault(node => node.Mesh == mesh && node.Skin != null)?.Skin;
            foreach (var primitive in mesh.Primitives)                        // SharpGLTF.Schema2.MeshPrimitive
            {
                var renderable = MakeRenderable(primitive, skin);
                if (renderable == null) continue;
                bucket.Add(renderable);
                _allRenderables.Add(renderable);
                if (renderable.VertexCount > maxVertexCount) maxVertexCount = renderable.VertexCount;
            }
        }
        _scratchPositions = new Vector3[maxVertexCount];

        // ---- Measure bind-pose bounds ----
        _sceneInstance.Armature.SetPoseTransforms();
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        EnumerateDrawables((renderable, transform) =>
        {
            FillUnique(renderable, transform, _scratchPositions);
            for (int vertexIndex = 0; vertexIndex < renderable.VertexCount; vertexIndex++)
            {
                min = Vector3.Min(min, _scratchPositions[vertexIndex]);
                max = Vector3.Max(max, _scratchPositions[vertexIndex]);
            }
        });
        if (min.X > max.X) { min = Vector3.Zero; max = Vector3.Zero; }
        _center = (min + max) * 0.5f;

        // ---- Fill bind-pose positions and upload meshes ----
        _scratchNormals = new Vector3[maxVertexCount];
        EnumerateDrawables((renderable, transform) =>
        {
            FillUnique(renderable, transform, _scratchPositions);
            WritePositions(renderable, _scratchPositions);
            WriteNormals(renderable, transform);
        });
        unsafe
        {
            foreach (var renderable in _allRenderables)
            {
                Raylib.UploadMesh(ref renderable.Mesh, true); // dynamic = true
                renderable.Uploaded = true;
            }
        }

        _drawMaterial = Raylib.LoadMaterialDefault();
        _defaultShader = _drawMaterial.Shader;
        _alphaMaskShader = Raylib.LoadShaderFromMemory(null, AlphaMaskFragmentShader);
        _hasAlphaMaskShader = _alphaMaskShader.Id != 0;
        if (_hasAlphaMaskShader)
            _alphaCutoffLoc = Raylib.GetShaderLocation(_alphaMaskShader, "alphaCutoff");
        if (_allRenderables.Any(renderable => (renderable.HasUvTransform || renderable.HasMask) && !renderable.NeedsNormals))
            _uvShader = UvShaderLocs.Load("#version 330\n" + UvVertexShader, "#version 330\n" + UvFragmentShader);
        if (_allRenderables.Any(renderable => renderable.NeedsNormals))
            _specularShader = UvShaderLocs.Load("#version 330\n#define SPEC\n" + UvVertexShader, "#version 330\n#define SPEC\n" + UvFragmentShader);
        _hasDrawMaterial = true;
        unsafe { _whiteTex = _drawMaterial.Maps[(int)MaterialMapIndex.Albedo].Texture; }
    }

    unsafe Renderable MakeRenderable(MeshPrimitive primitive, Skin? skin)
    {
        var positionAccessor = primitive.GetVertexAccessor("POSITION");
        if (positionAccessor == null) return null;
        var positions = positionAccessor.AsVector3Array();      // IList<Vector3>
        int vertexCount = positions.Count;
        if (vertexCount == 0) return null;

        var uvAccessor = primitive.GetVertexAccessor("TEXCOORD_0");
        var uvs = uvAccessor?.AsVector2Array();
        var jointsAccessor = primitive.GetVertexAccessor("JOINTS_0");
        var weightsAccessor = primitive.GetVertexAccessor("WEIGHTS_0");
        var joints = jointsAccessor?.AsVector4Array();
        var weights = weightsAccessor?.AsVector4Array();
        // DotR vertices carry up to 8 influences; the extractor writes the second four here
        var joints1 = primitive.GetVertexAccessor("JOINTS_1")?.AsVector4Array();
        var weights1 = primitive.GetVertexAccessor("WEIGHTS_1")?.AsVector4Array();

        // Flatten triangle indices (handles strips/fans too).
        var triList = new List<int>(vertexCount * 3);
        foreach (var (firstIndex, secondIndex, thirdIndex) in primitive.GetTriangleIndices())
        {
            triList.Add(firstIndex); triList.Add(secondIndex); triList.Add(thirdIndex);
        }
        if (triList.Count == 0) return null;

        var renderable = new Renderable
        {
            VertexCount = vertexCount,
            TriCount = triList.Count / 3,
            TriangleIndices = triList.ToArray(),
            Indexed = vertexCount <= 65535,
            MaterialIndex = primitive.Material?.LogicalIndex ?? -1,
        };

        renderable.LocalPos = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++) renderable.LocalPos[i] = positions[i];

        // Material tint + base color texture.
        if (renderable.MaterialIndex >= 0 && _materialOwners.TryGetValue(renderable.MaterialIndex, out Renderable? owner))
        {
            renderable.Tint = owner.Tint;
            renderable.HasTexture = owner.HasTexture;
            renderable.Texture = owner.Texture;
            renderable.Additive = owner.Additive;
            renderable.AlphaBlend = owner.AlphaBlend;
            renderable.AlphaMask = owner.AlphaMask;
            renderable.AlphaCutoff = owner.AlphaCutoff;
            renderable.HasAnim = owner.HasAnim;
            renderable.AnimFrames = owner.AnimFrames;
            renderable.AnimTimes = owner.AnimTimes;
            renderable.AnimDuration = owner.AnimDuration;
            renderable.HasUvTransform = owner.HasUvTransform;
            renderable.Uv0Scale = owner.Uv0Scale;
            renderable.Uv0Offset = owner.Uv0Offset;
            renderable.Uv0Track = owner.Uv0Track;
            renderable.HasMask = owner.HasMask;
            renderable.MaskTexture = owner.MaskTexture;
            renderable.Uv1Track = owner.Uv1Track;
            renderable.Multiply = owner.Multiply;
            renderable.HasSpecular = owner.HasSpecular;
            renderable.SpecularTexture = owner.SpecularTexture;
            renderable.SpecularColor = owner.SpecularColor;
            renderable.SpecularShininess = owner.SpecularShininess;
            renderable.Uv2Track = owner.Uv2Track;
            renderable.HasReflection = owner.HasReflection;
            renderable.ReflectionTexture = owner.ReflectionTexture;
            renderable.HasLight = owner.HasLight;
            renderable.LightAmbient = owner.LightAmbient;
            renderable.LightDiffuse = owner.LightDiffuse;
            renderable.LightEmission = owner.LightEmission;
        }
        else
        {
            BuildMaterial(primitive.Material, renderable);
            if (renderable.MaterialIndex >= 0) _materialOwners[renderable.MaterialIndex] = renderable;
        }

        // the specular / reflection / lighting terms need normals; a primitive without them draws without
        if (renderable.NeedsNormals)
        {
            var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
            if (normals != null && normals.Count == vertexCount)
            {
                renderable.LocalNrm = new Vector3[vertexCount];
                for (int i = 0; i < vertexCount; i++) renderable.LocalNrm[i] = normals[i];
            }
            else renderable.HasSpecular = renderable.HasReflection = renderable.HasLight = false;
        }

        if (skin != null && joints != null && weights != null && joints.Count == vertexCount)
        {
            if (joints1 == null || weights1 == null || joints1.Count != vertexCount) joints1 = weights1 = null;
            BuildInfluences(renderable, primitive, skin, joints, weights, joints1, weights1);
        }

        // Allocate GPU buffers (RL_MALLOC so UnloadMesh frees them) and fill static channels.
        int gpuVerts = renderable.Indexed ? vertexCount : renderable.TriCount * 3;
        renderable.GpuPositions = (float*)Raylib.MemAlloc((uint)(gpuVerts * 3 * sizeof(float)));
        float* gpuTexCoords = (float*)Raylib.MemAlloc((uint)(gpuVerts * 2 * sizeof(float)));

        if (renderable.Indexed)
        {
            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                Vector2 texCoord = uvs != null ? uvs[vertexIndex] : Vector2.Zero;
                gpuTexCoords[vertexIndex * 2 + 0] = texCoord.X; gpuTexCoords[vertexIndex * 2 + 1] = texCoord.Y;
            }
            ushort* gpuIndices = (ushort*)Raylib.MemAlloc((uint)(renderable.TriangleIndices.Length * sizeof(ushort)));
            for (int k = 0; k < renderable.TriangleIndices.Length; k++) gpuIndices[k] = (ushort)renderable.TriangleIndices[k];
            renderable.Mesh.Indices = gpuIndices;
            renderable.Mesh.VertexCount = vertexCount;
        }
        else
        {
            for (int k = 0; k < renderable.TriangleIndices.Length; k++)
            {
                int sourceVertexIndex = renderable.TriangleIndices[k];
                Vector2 texCoord = uvs != null ? uvs[sourceVertexIndex] : Vector2.Zero;
                gpuTexCoords[k * 2 + 0] = texCoord.X; gpuTexCoords[k * 2 + 1] = texCoord.Y;
            }
            renderable.Mesh.VertexCount = gpuVerts;
        }

        renderable.Mesh.TriangleCount = renderable.TriCount;
        renderable.Mesh.Vertices = renderable.GpuPositions;
        renderable.Mesh.TexCoords = gpuTexCoords;
        renderable.GpuTexCoords = gpuTexCoords;
        if (renderable.NeedsNormals)
        {
            renderable.GpuNormals = (float*)Raylib.MemAlloc((uint)(gpuVerts * 3 * sizeof(float)));
            renderable.Mesh.Normals = renderable.GpuNormals;
        }
        if (renderable.NeedsNormals || renderable.HasMask || renderable.HasUvTransform)
        {
            // the custom vertex shader reads vertexTexCoord2; without TEXCOORD_1 it gets set 0
            var secondTexCoords = primitive.GetVertexAccessor("TEXCOORD_1")?.AsVector2Array();
            if (secondTexCoords == null || secondTexCoords.Count != vertexCount) secondTexCoords = uvs;
            float* gpuTexCoords2 = (float*)Raylib.MemAlloc((uint)(gpuVerts * 2 * sizeof(float)));
            for (int k = 0; k < gpuVerts; k++)
            {
                int sourceVertexIndex = renderable.Indexed ? k : renderable.TriangleIndices[k];
                Vector2 texCoord = secondTexCoords != null ? secondTexCoords[sourceVertexIndex] : Vector2.Zero;
                gpuTexCoords2[k * 2 + 0] = texCoord.X; gpuTexCoords2[k * 2 + 1] = texCoord.Y;
            }
            renderable.Mesh.TexCoords2 = gpuTexCoords2;
            renderable.GpuTexCoords2 = gpuTexCoords2;
        }
        return renderable;
    }

    void BuildMaterial(GltfMaterial? material, Renderable renderable)
    {
        /* The extractor writes the per-page PS2 GS state. An explicit "opaque" matters too: every
           embedded image is RGBA, so neither image format nor glTF-library inference may move an
           opaque monster into the transparent no-depth-write pass. */
        bool hasDotrBlend = TryReadDotrBlendInfo(material, out string? dotrBlend, out float alphaCutoff);
        renderable.AlphaCutoff = alphaCutoff;

        if (hasDotrBlend)
        {
            renderable.Additive = string.Equals(dotrBlend, "additive", StringComparison.OrdinalIgnoreCase);
            renderable.AlphaBlend = string.Equals(dotrBlend, "alpha", StringComparison.OrdinalIgnoreCase);
            renderable.AlphaMask = string.Equals(dotrBlend, "mask", StringComparison.OrdinalIgnoreCase);
            renderable.Multiply = string.Equals(dotrBlend, "multiply", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            /* Non-DOTR glTF files. Generic DCC exporters (Blender, Maya, ...) commonly default
               alphaMode to BLEND whether or not the texture has alpha, so this is only a guess,
               vetoed below once the base color texture's real alpha channel is decoded. */
            renderable.Additive = false;
            renderable.AlphaBlend = material != null && material.Alpha == AlphaMode.BLEND;
            renderable.AlphaMask = material != null && material.Alpha == AlphaMode.MASK;
        }

        Vector4 color = Vector4.One;
        if (material != null)
        {
            try
            {
                var materialBuilder = new MaterialBuilder();
                material.CopyTo(materialBuilder);
                var baseColorChannel = materialBuilder.GetChannel(KnownChannel.BaseColor);
                if (baseColorChannel != null) color = (Vector4)baseColorChannel.Parameters[KnownProperty.RGBA];
            }
            catch { }
        }
        // Explicit opaque ignores any default alpha from a generic glTF material conversion.
        if (hasDotrBlend && string.Equals(dotrBlend, "opaque", StringComparison.OrdinalIgnoreCase))
            color.W = 1f;

        renderable.Tint = new Color(
            (byte)(Math.Clamp(color.X, 0f, 1f) * 255f),
            (byte)(Math.Clamp(color.Y, 0f, 1f) * 255f),
            (byte)(Math.Clamp(color.Z, 0f, 1f) * 255f),
            (byte)(Math.Clamp(color.W, 0f, 1f) * 255f));
        if (IsBlackUntextured(material))
        {
            renderable.Tint = new Color((byte)0, (byte)0, (byte)0, renderable.Tint.A);
        }

        try
        {
            var channel = material?.FindChannel("BaseColor");
            if (channel.HasValue && channel.Value.Texture?.PrimaryImage != null)
            {
                var memoryImage = channel.Value.Texture.PrimaryImage.Content;
                if (memoryImage.IsValid)
                {
                    RlImage cpuImage = GlbImporter.DecodeImage(memoryImage);
                    if (cpuImage.Width > 0 && cpuImage.Height > 0)
                    {
                        if (!hasDotrBlend && (renderable.AlphaBlend || renderable.AlphaMask) && !ImageHasAlpha(cpuImage))
                        {
                            // The alphaMode guess is wrong: the texture has no transparent texels,
                            // so an opaque mesh must not go into the no-depth-write blend pass.
                            renderable.AlphaBlend = false;
                            renderable.AlphaMask = false;
                        }
                        renderable.Texture = Raylib.LoadTextureFromImage(cpuImage);
                        renderable.HasTexture = renderable.Texture.Id != 0;
                        if (renderable.HasTexture) ApplySampler(material, renderable.Texture);
                        Raylib.UnloadImage(cpuImage);
                    }
                }
            }
        }
        catch { }

        LoadUvAnimation(material, renderable);
        LoadTextureAnimation(material, renderable);
    }

    // KHR_texture_transform on the base color (offset/scale; rotation is not used by the DoD
    // exporter), the "dod_uv_anim" extras tracks and the "dod_modulate" base + mask pair.
    void LoadUvAnimation(GltfMaterial? material, Renderable renderable)
    {
        try
        {
            var textureTransform = material?.FindChannel("BaseColor")?.TextureTransform;
            if (textureTransform != null && (textureTransform.Offset != Vector2.Zero || textureTransform.Scale != Vector2.One))
            {
                renderable.Uv0Offset = textureTransform.Offset;
                renderable.Uv0Scale = textureTransform.Scale;
                renderable.HasUvTransform = true;
            }
        }
        catch { }
        try
        {
            if (material?.Extras is not JsonObject extras) return;
            if (extras["dod_uv_anim"] is JsonArray tracks)
            {
                foreach (var node in tracks)
                {
                    if (node is not JsonObject trackJson) continue;
                    string? target = trackJson["target"]?.GetValue<string>();
                    var track = UvTrack.Parse(trackJson);
                    if (track == null) continue;
                    if (target == "baseColorTexture") { renderable.Uv0Track = track; renderable.HasUvTransform = true; }
                    else if (target == "dod_modulate") renderable.Uv1Track = track;
                    else if (target == "KHR_materials_specular/specularTexture") renderable.Uv2Track = track;
                }
            }
            if (extras["dod_modulate"] is JsonObject modulate)
            {
                // "base" replaces the base color (which has the mask baked in statically, or is the
                // black/alpha stand-in of a multiply material); "mask" is optional
                int baseTextureIndex = modulate["base"]?.GetValue<int>() ?? -1;
                int maskTextureIndex = modulate["mask"]?.GetValue<int>() ?? -1;
                Texture2D baseTex = LoadGltfTexture(material, baseTextureIndex);
                if (baseTex.Id != 0)
                {
                    Texture2D mask = maskTextureIndex >= 0 ? LoadGltfTexture(material, maskTextureIndex) : default;
                    if (maskTextureIndex < 0 || mask.Id != 0)
                    {
                        if (renderable.HasTexture && renderable.Texture.Id != 0) Raylib.UnloadTexture(renderable.Texture);
                        renderable.Texture = baseTex;
                        renderable.HasTexture = true;
                        renderable.MaskTexture = mask;
                        renderable.HasMask = mask.Id != 0;
                    }
                    else Raylib.UnloadTexture(baseTex);
                }
            }
            if (extras["dod_specular"] is JsonObject specular && specular["color"] is JsonArray colorJson && colorJson.Count >= 3)
            {
                var specularColor = new Vector3(colorJson[0]!.GetValue<float>(), colorJson[1]!.GetValue<float>(), colorJson[2]!.GetValue<float>());
                int specularTextureIndex = specular["texture"]?.GetValue<int>() ?? -1;
                if (specularColor != Vector3.Zero && specularTextureIndex >= 0)
                {
                    Texture2D specularTexture = LoadGltfTexture(material, specularTextureIndex);
                    if (specularTexture.Id != 0)
                    {
                        renderable.SpecularTexture = specularTexture;
                        renderable.SpecularColor = specularColor;
                        renderable.SpecularShininess = MathF.Max(specular["shininess"]?.GetValue<float>() ?? 32f, 1f);
                        renderable.HasSpecular = true;
                    }
                }
            }
            if (extras["dod_reflect"] is JsonObject reflection)
            {
                Texture2D reflectionTexture = LoadGltfTexture(material, reflection["texture"]?.GetValue<int>() ?? -1);
                if (reflectionTexture.Id != 0)
                {
                    Raylib.SetTextureWrap(reflectionTexture, TextureWrap.Clamp);
                    renderable.ReflectionTexture = reflectionTexture;
                    renderable.HasReflection = true;
                }
            }
            if (extras["dod_light"] is JsonObject light)
            {
                static Vector3 ReadVector3(JsonNode? node) => node is JsonArray array && array.Count >= 3
                    ? new Vector3(array[0]!.GetValue<float>(), array[1]!.GetValue<float>(), array[2]!.GetValue<float>()) : Vector3.Zero;
                renderable.LightAmbient = ReadVector3(light["ambient"]);
                renderable.LightDiffuse = ReadVector3(light["diffuse"]);
                renderable.LightEmission = ReadVector3(light["emission"]);
                renderable.HasLight = true;
            }
        }
        catch { }
    }

    Texture2D LoadGltfTexture(GltfMaterial? material, int textureIndex)
    {
        if (textureIndex < 0 || textureIndex >= _gltf.LogicalTextures.Count) return default;
        var memoryImage = _gltf.LogicalTextures[textureIndex].PrimaryImage?.Content;
        if (memoryImage == null || !memoryImage.Value.IsValid) return default;
        RlImage cpuImage = GlbImporter.DecodeImage(memoryImage.Value);
        if (cpuImage.Width <= 0 || cpuImage.Height <= 0) return default;
        Texture2D gpuTexture = Raylib.LoadTextureFromImage(cpuImage);
        Raylib.UnloadImage(cpuImage);
        if (gpuTexture.Id != 0) ApplySampler(material, gpuTexture);
        return gpuTexture;
    }

    /* Applies the material's glTF sampler (the extractor writes it from the GS CLAMP/TEX1
       registers) to a Raylib texture. Raylib defaults to REPEAT on both axes, which lets
       bilinear filtering at a clamped edge pull in the opposite side of an atlas. A glb
       without a sampler keeps Raylib's defaults. */
    static void ApplySampler(GltfMaterial? material, Texture2D tex)
    {
        const int GlTextureWrapS = 0x2802;
        const int GlTextureWrapT = 0x2803;
        const int GlRepeat = 0x2901;
        const int GlClampToEdge = 0x812F;
        const int GlMirroredRepeat = 0x8370;
        try
        {
            SharpGLTF.Schema2.MaterialChannel? channel = material?.FindChannel("BaseColor");
            if (!channel.HasValue) return;
            SharpGLTF.Schema2.TextureSampler? sampler = channel.Value.TextureSampler;
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

    static bool ImageHasAlpha(RlImage image)
    {
        RlImage rgba = Raylib.ImageCopy(image);
        try
        {
            Raylib.ImageFormat(ref rgba, PixelFormat.UncompressedR8G8B8A8);
            int count = rgba.Width * rgba.Height;
            if (count <= 0) return false;
            byte[] bytes = GlbImporter.Pixels(rgba);
            for (int i = 3; i < bytes.Length; i += 4)
                if (bytes[i] != 255) return true;
            return false;
        }
        finally
        {
            Raylib.UnloadImage(rgba);
        }
    }

    /* An untextured page whose colour row is (0,0,0) draws black in the game (the black plane
       under the map's sea). The extractor writes white for a zero row, which is right for a
       textured page (booKoo's book) but would make this plane, and the additive sea over it, white. */
    static bool IsBlackUntextured(GltfMaterial? material)
    {
        try
        {
            if (material?.Extras is not JsonObject extras || extras["DOTR_blend"] is not JsonObject blend)
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

    static bool TryReadDotrBlendInfo(GltfMaterial? material, out string? mode, out float alphaCutoff)
    {
        mode = null;
        alphaCutoff = 0.5f;
        try
        {
            if (material?.Extras is not JsonObject extras) return false;

            if (extras["DOTR_blend"] is JsonObject blend)
            {
                mode = blend["mode"]?.GetValue<string>();
                alphaCutoff = blend["alphaCutoff"]?.GetValue<float>() ?? 0.5f;
                alphaCutoff = Math.Clamp(alphaCutoff, 0f, 1f);
                if (!string.IsNullOrWhiteSpace(mode)) return true;
            }

            // Older extractor output stored an additive hint in DOTR_texAnim instead of DOTR_blend.
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

    // Turns each image the DOTR_texAnim extras reference into a Raylib texture; Draw binds the
    // frame for the current wall-clock time instead of the static base texture.
    void LoadTextureAnimation(GltfMaterial? material, Renderable renderable)
    {
        try
        {
            if (material?.Extras is not JsonObject extras) return;
            if (extras["DOTR_texAnim"] is not JsonObject texAnimJson) return;
            if (texAnimJson["frames"] is not JsonArray frames || frames.Count < 2) return;

            float duration = texAnimJson["duration"]?.GetValue<float>() ?? 0f;
            var texList = new List<Texture2D>(frames.Count);
            var timeList = new List<float>(frames.Count);

            foreach (var frameNode in frames)
            {
                if (frameNode is not JsonObject frameJson) continue;
                int imageIndex = frameJson["image"]?.GetValue<int>() ?? -1;
                float frameTime = frameJson["t"]?.GetValue<float>() ?? 0f;
                if (imageIndex < 0 || imageIndex >= _gltf.LogicalImages.Count) continue;

                var content = _gltf.LogicalImages[imageIndex].Content;
                if (!content.IsValid) continue;
                RlImage cpuImage = GlbImporter.DecodeImage(content);
                if (cpuImage.Width > 0 && cpuImage.Height > 0)
                {
                    Texture2D frameTexture = Raylib.LoadTextureFromImage(cpuImage);
                    Raylib.UnloadImage(cpuImage);
                    if (frameTexture.Id != 0)
                    {
                        ApplySampler(material, frameTexture);
                        texList.Add(frameTexture);
                        timeList.Add(frameTime);
                    }
                }
            }

            if (texList.Count >= 2 && duration > 0f)
            {
                // The frame array replaces the base-color texture; unload it so its GPU copy is not leaked.
                if (renderable.HasTexture && renderable.Texture.Id != 0) Raylib.UnloadTexture(renderable.Texture);
                renderable.AnimFrames = texList.ToArray();
                renderable.AnimTimes = timeList.ToArray();
                renderable.AnimDuration = duration;
                renderable.HasAnim = true;
                renderable.Texture = renderable.AnimFrames[0];
                renderable.HasTexture = true;
            }
            else
            {
                foreach (var texture in texList) Raylib.UnloadTexture(texture);
            }
        }
        catch { }
    }

    // The transform is passed as object so the version-dependent transform interface is never
    // named; FillUnique matches the concrete types.
    void EnumerateDrawables(Action<Renderable, object> action)
    {
        int drawableIndex = 0;
        foreach (var drawable in _sceneInstance)
        {
            if (drawableIndex >= _byDrawable.Count) break;
            var bucket = _byDrawable[drawableIndex++];
            object transform = drawable.Transform;
            foreach (var renderable in bucket) action(renderable, transform);
        }
    }

    // World-space positions of all unique vertices into `outPositions` (not recentred).
    void FillUnique(Renderable renderable, object transform, Vector3[] outPositions)
    {
        if (transform is SharpGLTF.Transforms.SkinnedTransform skinnedTransform && renderable.InfluenceStart != null)
        {
            SetJointWorld(renderable, skinnedTransform.SkinMatrices);
            for (int vertexIndex = 0; vertexIndex < renderable.VertexCount; vertexIndex++)
            {
                int influenceBegin = renderable.InfluenceStart[vertexIndex], influenceEnd = renderable.InfluenceStart[vertexIndex + 1];
                if (influenceBegin == influenceEnd) { outPositions[vertexIndex] = renderable.LocalPos[vertexIndex]; continue; }
                Vector3 position = default;
                for (int k = influenceBegin; k < influenceEnd; k++)
                    position += renderable.InfluenceWeight[k] * Vector3.Transform(renderable.InfluencePositions[k], renderable.JointWorld[renderable.InfluenceJoint[k]]);
                outPositions[vertexIndex] = position;
            }
        }
        else if (transform is SharpGLTF.Transforms.RigidTransform rigidTransform)
        {
            Matrix4x4 worldMatrix = rigidTransform.WorldMatrix;
            for (int vertexIndex = 0; vertexIndex < renderable.VertexCount; vertexIndex++)
                outPositions[vertexIndex] = Vector3.Transform(renderable.LocalPos[vertexIndex], worldMatrix);
        }
        else
        {
            for (int vertexIndex = 0; vertexIndex < renderable.VertexCount; vertexIndex++) outPositions[vertexIndex] = renderable.LocalPos[vertexIndex];
        }
    }

    // Each skin joint's world matrix this frame: SkinMatrices are inverseBind * jointWorld.
    static void SetJointWorld(Renderable renderable, IReadOnlyList<Matrix4x4> skin)
    {
        int jointCount = Math.Min(renderable.JointWorld.Length, skin.Count);
        for (int j = 0; j < jointCount; j++) renderable.JointWorld[j] = renderable.InverseOfInverseBind[j] * skin[j];
    }

    /* The influences FillUnique sums. A vertex whose ModelExtractor copies
       (GlbImporter.GameAccessors) still describe it, by the importer's rule, uses them as stored
       (weight raw/4096, normal raw/32767, zero weights left out). Any other vertex (a converted
       import, or one moved or reweighted outside the app) gets one copy per JOINTS/WEIGHTS
       influence from POSITION and NORMAL, which skins exactly as the glTF blended matrix would;
       none when its weights sum to ~0 (drawn where it is). */
    static void BuildInfluences(Renderable renderable, MeshPrimitive primitive, Skin skin, IList<Vector4> joints,
        IList<Vector4> weights, IList<Vector4>? joints1, IList<Vector4>? weights1)
    {
        int jointCount = skin.JointsCount;
        var inverseBindMatrices = new Matrix4x4[jointCount];
        renderable.InverseOfInverseBind = new Matrix4x4[jointCount];
        renderable.JointWorld = new Matrix4x4[jointCount];
        for (int j = 0; j < jointCount; j++)
        {
            inverseBindMatrices[j] = skin.GetJoint(j).InverseBindMatrix;
            if (!Matrix4x4.Invert(inverseBindMatrices[j], out renderable.InverseOfInverseBind[j])) renderable.InverseOfInverseBind[j] = Matrix4x4.Identity;
        }
        var game = GlbImporter.GameAccessors(primitive);
        bool hasNormals = renderable.LocalNrm != null;
        var influenceJoints = new List<int>(renderable.VertexCount * 2);
        var influenceWeights = new List<float>(renderable.VertexCount * 2);
        var influencePositions = new List<Vector3>(renderable.VertexCount * 2);
        var influenceNormals = new List<Vector3>(hasNormals ? renderable.VertexCount * 2 : 0);
        var gameWeights = new Dictionary<int, float>();
        var glbWeights = new Dictionary<int, float>();
        renderable.InfluenceStart = new int[renderable.VertexCount + 1];
        for (int vertexIndex = 0; vertexIndex < renderable.VertexCount; vertexIndex++)
        {
            renderable.InfluenceStart[vertexIndex] = influenceJoints.Count;
            Vector4 jointsLow = joints[vertexIndex], weightsLow = weights[vertexIndex];
            Vector4 jointsHigh = joints1?[vertexIndex] ?? default, weightsHigh = weights1?[vertexIndex] ?? default;
            if (game != null && game.JointWeights[0][vertexIndex].X >= 0)
            {
                gameWeights.Clear();
                glbWeights.Clear();
                int weightSum = 0;
                bool matchesGame = true;
                for (int k = 0; k < game.JointWeights.Length && game.JointWeights[k][vertexIndex].X >= 0; k++)
                {
                    int gameJoint = (int)game.JointWeights[k][vertexIndex].X;
                    if (gameJoint >= jointCount) { matchesGame = false; break; }
                    short gameWeight = (short)game.JointWeights[k][vertexIndex].Y;
                    if (gameWeight <= 0) continue;
                    gameWeights[gameJoint] = gameWeights.GetValueOrDefault(gameJoint) + gameWeight;
                    weightSum += gameWeight;
                }
                void AddGlbWeight(float glbJoint, float glbWeight)
                {
                    if (glbWeight > 0f && glbJoint >= 0 && glbJoint < jointCount) glbWeights[(int)glbJoint] = glbWeights.GetValueOrDefault((int)glbJoint) + glbWeight;
                }
                AddGlbWeight(jointsLow.X, weightsLow.X); AddGlbWeight(jointsLow.Y, weightsLow.Y); AddGlbWeight(jointsLow.Z, weightsLow.Z); AddGlbWeight(jointsLow.W, weightsLow.W);
                AddGlbWeight(jointsHigh.X, weightsHigh.X); AddGlbWeight(jointsHigh.Y, weightsHigh.Y); AddGlbWeight(jointsHigh.Z, weightsHigh.Z); AddGlbWeight(jointsHigh.W, weightsHigh.W);
                matchesGame = matchesGame && GlbImporter.GameWeightsMatch(gameWeights, weightSum, glbWeights)
                    && GlbImporter.GameRefPosMatches(Vector3.Transform(renderable.LocalPos[vertexIndex], inverseBindMatrices[(int)game.JointWeights[0][vertexIndex].X]), game.ReferencePositions[vertexIndex]);
                if (matchesGame)
                {
                    for (int k = 0; k < game.JointWeights.Length && game.JointWeights[k][vertexIndex].X >= 0; k++)
                    {
                        float rawWeight = game.JointWeights[k][vertexIndex].Y;
                        if (rawWeight == 0f) continue;
                        influenceJoints.Add((int)game.JointWeights[k][vertexIndex].X);
                        influenceWeights.Add(rawWeight / 4096f);
                        influencePositions.Add(game.Positions[k][vertexIndex]);
                        if (hasNormals) influenceNormals.Add(game.Normals[k][vertexIndex] / 32767f);
                    }
                    continue;
                }
            }
            if (weightsLow.X + weightsLow.Y + weightsLow.Z + weightsLow.W + weightsHigh.X + weightsHigh.Y + weightsHigh.Z + weightsHigh.W < 1e-5f) continue;
            void Derived(float derivedJoint, float derivedWeight)
            {
                if (derivedWeight == 0f || derivedJoint < 0 || derivedJoint >= jointCount) return;
                influenceJoints.Add((int)derivedJoint);
                influenceWeights.Add(derivedWeight);
                influencePositions.Add(Vector3.Transform(renderable.LocalPos[vertexIndex], inverseBindMatrices[(int)derivedJoint]));
                if (hasNormals) influenceNormals.Add(Vector3.TransformNormal(renderable.LocalNrm![vertexIndex], inverseBindMatrices[(int)derivedJoint]));
            }
            Derived(jointsLow.X, weightsLow.X); Derived(jointsLow.Y, weightsLow.Y); Derived(jointsLow.Z, weightsLow.Z); Derived(jointsLow.W, weightsLow.W);
            Derived(jointsHigh.X, weightsHigh.X); Derived(jointsHigh.Y, weightsHigh.Y); Derived(jointsHigh.Z, weightsHigh.Z); Derived(jointsHigh.W, weightsHigh.W);
        }
        renderable.InfluenceStart[renderable.VertexCount] = influenceJoints.Count;
        renderable.InfluenceJoint = influenceJoints.ToArray();
        renderable.InfluenceWeight = influenceWeights.ToArray();
        renderable.InfluencePositions = influencePositions.ToArray();
        renderable.InfluenceNormals = hasNormals ? influenceNormals.ToArray() : null;
    }

    // Skinned normals for the specular term (the same influences as FillUnique). Returns false
    // for renderables that carry no normals.
    unsafe bool WriteNormals(Renderable renderable, object transform)
    {
        if (renderable.LocalNrm == null || renderable.GpuNormals == null) return false;
        var outNormals = _scratchNormals;
        if (transform is SharpGLTF.Transforms.SkinnedTransform skinnedTransform && renderable.InfluenceStart != null)
        {
            SetJointWorld(renderable, skinnedTransform.SkinMatrices);
            for (int vertexIndex = 0; vertexIndex < renderable.VertexCount; vertexIndex++)
            {
                int influenceBegin = renderable.InfluenceStart[vertexIndex], influenceEnd = renderable.InfluenceStart[vertexIndex + 1];
                if (influenceBegin == influenceEnd) { outNormals[vertexIndex] = renderable.LocalNrm[vertexIndex]; continue; }
                Vector3 normal = default;
                for (int k = influenceBegin; k < influenceEnd; k++)
                    normal += renderable.InfluenceWeight[k] * Vector3.TransformNormal(renderable.InfluenceNormals![k], renderable.JointWorld[renderable.InfluenceJoint[k]]);
                outNormals[vertexIndex] = normal;
            }
        }
        else if (transform is SharpGLTF.Transforms.RigidTransform rigidTransform)
        {
            Matrix4x4 worldMatrix = rigidTransform.WorldMatrix;
            for (int vertexIndex = 0; vertexIndex < renderable.VertexCount; vertexIndex++) outNormals[vertexIndex] = Vector3.TransformNormal(renderable.LocalNrm[vertexIndex], worldMatrix);
        }
        else
        {
            for (int vertexIndex = 0; vertexIndex < renderable.VertexCount; vertexIndex++) outNormals[vertexIndex] = renderable.LocalNrm[vertexIndex];
        }
        int count = renderable.Indexed ? renderable.VertexCount : renderable.TriangleIndices.Length;
        for (int k = 0; k < count; k++)
        {
            Vector3 normal = outNormals[renderable.Indexed ? k : renderable.TriangleIndices[k]];
            float length = normal.Length();
            if (length > 1e-8f) normal /= length;
            renderable.GpuNormals[k * 3 + 0] = normal.X; renderable.GpuNormals[k * 3 + 1] = normal.Y; renderable.GpuNormals[k * 3 + 2] = normal.Z;
        }
        return true;
    }

    unsafe void WritePositions(Renderable renderable, Vector3[] world)
    {
        if (renderable.Indexed)
        {
            for (int vertexIndex = 0; vertexIndex < renderable.VertexCount; vertexIndex++)
            {
                Vector3 position = world[vertexIndex] - _center;
                renderable.GpuPositions[vertexIndex * 3 + 0] = position.X; renderable.GpuPositions[vertexIndex * 3 + 1] = position.Y; renderable.GpuPositions[vertexIndex * 3 + 2] = position.Z;
            }
        }
        else
        {
            for (int k = 0; k < renderable.TriangleIndices.Length; k++)
            {
                Vector3 position = world[renderable.TriangleIndices[k]] - _center;
                renderable.GpuPositions[k * 3 + 0] = position.X; renderable.GpuPositions[k * 3 + 1] = position.Y; renderable.GpuPositions[k * 3 + 2] = position.Z;
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
        if (_sceneInstance == null) return;
        int frames = 1;
        float duration = 0f;
        if (animIndex >= 0 && animIndex < AnimationCount)
        {
            duration = _durations[animIndex];
            frames = 16;
        }
        bool first = true;
        for (int frameIndex = 0; frameIndex < frames; frameIndex++)
        {
            if (animIndex >= 0 && animIndex < AnimationCount)
            {
                float frameTime = 0f;
                if (frames > 1)
                {
                    frameTime = duration * frameIndex / (frames - 1);
                }
                _sceneInstance.Armature.SetAnimationFrame(animIndex, frameTime);
            }
            else
            {
                _sceneInstance.Armature.SetPoseTransforms();
            }
            float lowestY = float.MaxValue;
            float highestY = float.MinValue;
            float lowestX = float.MaxValue;
            float highestX = float.MinValue;
            float lowestZ = float.MaxValue;
            float highestZ = float.MinValue;
            EnumerateDrawables((renderable, transform) =>
            {
                FillUnique(renderable, transform, _scratchPositions);
                for (int vertexIndex = 0; vertexIndex < renderable.VertexCount; vertexIndex++)
                {
                    lowestY = MathF.Min(lowestY, _scratchPositions[vertexIndex].Y);
                    highestY = MathF.Max(highestY, _scratchPositions[vertexIndex].Y);
                    lowestX = MathF.Min(lowestX, _scratchPositions[vertexIndex].X);
                    highestX = MathF.Max(highestX, _scratchPositions[vertexIndex].X);
                    lowestZ = MathF.Min(lowestZ, _scratchPositions[vertexIndex].Z);
                    highestZ = MathF.Max(highestZ, _scratchPositions[vertexIndex].Z);
                }
            });
            if (lowestY > highestY)
            {
                continue;
            }
            if (first || lowestX < left)
            {
                left = lowestX;
            }
            if (first || highestX > right)
            {
                right = highestX;
            }
            if (first || lowestZ < back)
            {
                back = lowestZ;
            }
            if (first || highestZ > front)
            {
                front = highestZ;
            }
            if (first || lowestY < lowest)
            {
                lowest = lowestY;
            }
            if (first || highestY - lowestY > height)
            {
                height = highestY - lowestY;
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
        foreach (var node in _gltf.LogicalNodes)
        {
            if (IsNode(node.Name, extractedPrefix, convertedName))
                return node.WorldMatrix.Translation;
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
        if (_sceneInstance == null) return Vector3.Zero;
        foreach (var nodeInstance in _sceneInstance.Armature.LogicalNodes)
        {
            if (IsNode(nodeInstance.Name, "PivotCamera_", "dotr_pivot"))
                return nodeInstance.ModelMatrix.Translation;
        }
        return Vector3.Zero;
    }

    /// <summary>Evaluate animation at the given time (seconds) and push deformed
    /// positions to the GPU. animIndex &lt; 0 => static bind pose.</summary>
    public void SetAnimationFrame(int animIndex, float timeSeconds)
    {
        if (_sceneInstance == null) return;

        if (animIndex >= 0 && animIndex < AnimationCount)
            _sceneInstance.Armature.SetAnimationFrame(animIndex, timeSeconds);
        else
            _sceneInstance.Armature.SetPoseTransforms();

        EnumerateDrawables((renderable, transform) =>
        {
            FillUnique(renderable, transform, _scratchPositions);
            WritePositions(renderable, _scratchPositions);
            unsafe
            {
                int gpuVerts = renderable.Indexed ? renderable.VertexCount : renderable.TriCount * 3;
                Raylib.UpdateMeshBuffer(renderable.Mesh, 0, renderable.GpuPositions, gpuVerts * 3 * sizeof(float), 0); // VBO 0 = positions
                if (WriteNormals(renderable, transform))
                    Raylib.UpdateMeshBuffer(renderable.Mesh, 2, renderable.GpuNormals, gpuVerts * 3 * sizeof(float), 0); // VBO 2 = normals
            }
        });
    }

    public void Draw()
    {
        if (!_hasDrawMaterial) return;

        Rlgl.DisableBackfaceCulling();

        // Pass 1: explicitly opaque geometry only (writes depth).
        foreach (var renderable in _allRenderables)
        {
            if (renderable.Additive || renderable.AlphaBlend || renderable.AlphaMask || renderable.Multiply) continue;
            DrawOne(renderable);
        }

        /* Pass 2: alpha-tested cutouts. The shader discards transparent texels; visible texels
           write depth. Flipbook sprites such as Electromagnetic Bagworm need this: geometry
           behind the visible part must not show through, but geometry behind discarded
           background pixels can. */
        if (_hasAlphaMaskShader)
        {
            _drawMaterial.Shader = _alphaMaskShader;
            foreach (var renderable in _allRenderables)
            {
                if (!renderable.AlphaMask) continue;
                if (_alphaCutoffLoc >= 0)
                    Raylib.SetShaderValue(_alphaMaskShader, _alphaCutoffLoc, renderable.AlphaCutoff, ShaderUniformDataType.Float);
                DrawOne(renderable);
            }
            _drawMaterial.Shader = _defaultShader;
        }
        else
        {
            // Fallback for systems where the mask shader failed to load.
            foreach (var renderable in _allRenderables)
            {
                if (!renderable.AlphaMask) continue;
                Raylib.BeginBlendMode(BlendMode.Alpha);
                DrawOne(renderable);
                Raylib.EndBlendMode();
            }
        }

        // Pass 3: true translucent and additive geometry does not write depth.
        Rlgl.DisableDepthMask();
        foreach (var renderable in _allRenderables)
        {
            if (!(renderable.Additive || renderable.AlphaBlend || renderable.Multiply)) continue;
            // multiplied = DST_COLOR, ONE_MINUS_SRC_ALPHA: exactly dst * src with the base's alpha 1
            Raylib.BeginBlendMode(renderable.Additive ? BlendMode.Additive : renderable.Multiply ? BlendMode.Multiplied : BlendMode.Alpha);
            DrawOne(renderable);
            Raylib.EndBlendMode();
        }
        Rlgl.EnableDepthMask();

        Rlgl.EnableBackfaceCulling();
    }

    unsafe void DrawOne(Renderable renderable)
    {
        int albedo = (int)MaterialMapIndex.Albedo;
        _drawMaterial.Maps[albedo].Color = renderable.Tint;
        Texture2D tex = renderable.HasAnim ? CurrentAnimFrame(renderable) : (renderable.HasTexture ? renderable.Texture : _whiteTex);
        _drawMaterial.Maps[albedo].Texture = tex;
        var uvShaderLocs = renderable.NeedsNormals ? _specularShader : (renderable.HasUvTransform || renderable.HasMask) ? _uvShader : null;
        if (uvShaderLocs != null)
        {
            DrawUv(renderable, uvShaderLocs);
            return;
        }
        Raylib.DrawMesh(renderable.Mesh, _drawMaterial, Matrix4x4.Identity);
    }

    // Draws with the UV/modulate shader, keeping the current pass's blend state; inside the
    // alpha-mask pass the shader does the alpha test itself.
    unsafe void DrawUv(Renderable renderable, UvShaderLocs shaderLocs)
    {
        var previousShader = _drawMaterial.Shader;
        bool maskPass = _hasAlphaMaskShader && previousShader.Id == _alphaMaskShader.Id;
        var shader = shaderLocs.Shader;
        _drawMaterial.Shader = shader;
        Vector4 uvTransform0 = renderable.Uv0Track?.Eval(TexAnimTime) ?? new Vector4(renderable.Uv0Scale.X, renderable.Uv0Scale.Y, renderable.Uv0Offset.X, renderable.Uv0Offset.Y);
        Vector4 uvTransform1 = renderable.Uv1Track?.Eval(TexAnimTime) ?? new Vector4(1, 1, 0, 0);
        Raylib.SetShaderValue(shader, shaderLocs.UvTransform0, uvTransform0, ShaderUniformDataType.Vec4);
        Raylib.SetShaderValue(shader, shaderLocs.UvTransform1, uvTransform1, ShaderUniformDataType.Vec4);
        Raylib.SetShaderValue(shader, shaderLocs.UseMask, renderable.HasMask ? 1 : 0, ShaderUniformDataType.Int);
        Raylib.SetShaderValue(shader, shaderLocs.AlphaCutoff, maskPass ? renderable.AlphaCutoff : -1f, ShaderUniformDataType.Float);
        int maskMap = (int)MaterialMapIndex.Metalness; // bound to "texture1"
        int specMap = (int)MaterialMapIndex.Normal;    // bound to "texture2"
        int reflectionMapIndex = (int)MaterialMapIndex.Roughness;  // bound to "texture3"
        var prevMask = _drawMaterial.Maps[maskMap].Texture;
        var prevSpec = _drawMaterial.Maps[specMap].Texture;
        var prevRef = _drawMaterial.Maps[reflectionMapIndex].Texture;
        if (renderable.NeedsNormals)
        {
            Raylib.SetShaderValue(shader, shaderLocs.UseSpecular, renderable.HasSpecular ? 1 : 0, ShaderUniformDataType.Int);
            Raylib.SetShaderValue(shader, shaderLocs.UseReflection, renderable.HasReflection ? 1 : 0, ShaderUniformDataType.Int);
            Raylib.SetShaderValue(shader, shaderLocs.UseLight, renderable.HasLight ? 1 : 0, ShaderUniformDataType.Int);
            Raylib.SetShaderValue(shader, shaderLocs.MatAmbient, renderable.LightAmbient, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(shader, shaderLocs.MatDiffuse, renderable.LightDiffuse, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(shader, shaderLocs.MatEmission, renderable.LightEmission, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(shader, shaderLocs.LightDir, DodLightDir, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(shader, shaderLocs.LightColor, DodLightColor, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(shader, shaderLocs.LightAmbient, DodAmbientColor, ShaderUniformDataType.Vec3);
            if (renderable.HasReflection) _drawMaterial.Maps[reflectionMapIndex].Texture = renderable.ReflectionTexture;
        }
        if (renderable.HasMask) _drawMaterial.Maps[maskMap].Texture = renderable.MaskTexture;
        if (renderable.HasSpecular)
        {
            Vector4 uvTransform2 = renderable.Uv2Track?.Eval(TexAnimTime) ?? new Vector4(1, 1, 0, 0);
            Raylib.SetShaderValue(shader, shaderLocs.UvTransform2, uvTransform2, ShaderUniformDataType.Vec4);
            Raylib.SetShaderValue(shader, shaderLocs.SpecularColor, renderable.SpecularColor, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(shader, shaderLocs.Shininess, renderable.SpecularShininess, ShaderUniformDataType.Float);
            _drawMaterial.Maps[specMap].Texture = renderable.SpecularTexture;
        }
        Raylib.DrawMesh(renderable.Mesh, _drawMaterial, Matrix4x4.Identity);
        _drawMaterial.Maps[maskMap].Texture = prevMask;
        _drawMaterial.Maps[specMap].Texture = prevSpec;
        _drawMaterial.Maps[reflectionMapIndex].Texture = prevRef;
        _drawMaterial.Shader = previousShader;
    }

    // Texture-animation clock (seconds). The viewer advances it at the model's animation speed,
    // so it runs in step with the skeleton and freezes when the model is paused or in static pose.
    public float TexAnimTime;

    public void AdvanceTextureAnim(float dtSeconds) => TexAnimTime += dtSeconds;

    Texture2D CurrentAnimFrame(Renderable renderable)
    {
        if (renderable.AnimFrames.Length == 0) return renderable.Texture;
        if (renderable.AnimDuration <= 0f) return renderable.AnimFrames[0];
        float loopedTime = TexAnimTime % renderable.AnimDuration;
        if (loopedTime < 0f) loopedTime += renderable.AnimDuration;
        int frameIndex = 0;
        for (int i = 0; i < renderable.AnimTimes.Length; i++)
        {
            if (renderable.AnimTimes[i] <= loopedTime) frameIndex = i; else break;
        }
        return renderable.AnimFrames[frameIndex];
    }

    public void Dispose()
    {
        unsafe
        {
            // Renderables of one material share textures: unload each once.
            HashSet<uint> unloaded = new HashSet<uint>();
            foreach (var renderable in _allRenderables)
            {
                if (renderable.Uploaded) Raylib.UnloadMesh(renderable.Mesh); // frees VBOs + RL_MALLOC'd CPU buffers
                if (renderable.HasAnim)
                {
                    // renderable.Texture aliases AnimFrames[0]; unload the frame array only.
                    foreach (var texture in renderable.AnimFrames)
                    {
                        if (unloaded.Add(texture.Id)) Raylib.UnloadTexture(texture);
                    }
                }
                else if (renderable.HasTexture)
                {
                    if (unloaded.Add(renderable.Texture.Id)) Raylib.UnloadTexture(renderable.Texture);
                }
                if (renderable.HasMask && unloaded.Add(renderable.MaskTexture.Id)) Raylib.UnloadTexture(renderable.MaskTexture);
                if (renderable.HasSpecular && unloaded.Add(renderable.SpecularTexture.Id)) Raylib.UnloadTexture(renderable.SpecularTexture);
                if (renderable.HasReflection && unloaded.Add(renderable.ReflectionTexture.Id)) Raylib.UnloadTexture(renderable.ReflectionTexture);
            }
        }
        if (_uvShader != null) Raylib.UnloadShader(_uvShader.Shader);
        if (_specularShader != null) Raylib.UnloadShader(_specularShader.Shader);
        _uvShader = _specularShader = null;
        if (_hasAlphaMaskShader)
        {
            Raylib.UnloadShader(_alphaMaskShader);
            _hasAlphaMaskShader = false;
        }
        _allRenderables.Clear();
        _materialOwners.Clear();
        _byDrawable.Clear();
        _sceneInstance = null;
        _gltf = null;
    }
}