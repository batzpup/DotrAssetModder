using SkiaSharp;

namespace DotrAssetModder;
// Mostly AI

// The in-between model of the FBX / COLLADA / glTF conversion, a port of tools/toglb.py's
// Scene (DotR-PC; NOTES/toglb-handover.md there). Readers fill it in the source's own axes and
// units; SceneFinish puts it into DotR's shape; SceneGlbWriter writes the glb the importer
// loads.

public sealed class SceneNode
{
    public string Name;
    public int Parent;
    public Mat4 Local = Mat4.Identity();
    public Mat4? Bind; // world matrix at skin bind time, for joints
    public string? Sid; // COLLADA scoped id

    public SceneNode(string name, int parent)
    {
        Name = name;
        Parent = parent;
    }
}

// Triangles with per-vertex attributes, in flat arrays (Pos and Nrm 3 a vertex, Uv 2, Tris 3
// a triangle, Jnt and Wgt 8 a vertex). A rigid mesh's positions are in its node's space; a
// skinned mesh's are bind-pose world positions, with influences Jnt/Wgt (node indices, -1 =
// none), heaviest first.
public sealed class SceneMesh
{
    public string Name;
    public int Node;
    public bool Skinned;
    public double[] Pos = new double[0];
    public double[] Nrm = new double[0];
    public double[] Uv = new double[0];
    public int[] Tris = new int[0];
    public int[] TMat = new int[0]; // material index per triangle
    public int[]? Jnt;
    public double[]? Wgt;

    public SceneMesh(string name, int node)
    {
        Name = name;
        Node = node;
    }

    public int VertexCount
    {
        get { return Pos.Length / 3; }
    }
}

public sealed class SceneMaterial
{
    public string Name;
    public double[] Color; // RGBA 0..1
    public SKBitmap? Image; // shared between materials that use the same file

    public SceneMaterial(string name, double[] color, SKBitmap? image)
    {
        Name = name;
        Color = color;
        Image = image;
    }
}

public sealed class SceneAnim
{
    public string Name;
    public int Frames; // at 30 fps
    public Dictionary<int, Mat4[]> Tracks = new Dictionary<int, Mat4[]>(); // node -> local matrix per frame

    public SceneAnim(string name, int frames)
    {
        Name = name;
        Frames = frames;
    }
}

public sealed class Scene
{
    public const int Fps = 30;

    public List<SceneNode> Nodes = new List<SceneNode>();
    public List<SceneMesh> Meshes = new List<SceneMesh>();
    public List<SceneMaterial> Materials = new List<SceneMaterial>();
    public List<SceneAnim> Anims = new List<SceneAnim>();
    public Mat4 Axis = Mat4.Identity(); // source axes -> +Y up, +Z front (upper 3x3)
    public double Unit = 1.0; // metres per source unit
    public bool UvTopLeft; // source UVs already top-left (Unity Studio rips)

    public int AddNode(string name, int parent = -1)
    {
        Nodes.Add(new SceneNode(name, parent));
        return Nodes.Count - 1;
    }

    public int AddMaterial(string name, double[] color, SKBitmap? image)
    {
        Materials.Add(new SceneMaterial(name, color, image));
        return Materials.Count - 1;
    }

    // Node indices, parents before children (depth first, in node order).
    public List<int> Order()
    {
        List<int>[] kids = new List<int>[Nodes.Count];
        List<int> roots = new List<int>();
        for (int i = 0; i < Nodes.Count; i++)
        {
            kids[i] = new List<int>();
        }
        for (int i = 0; i < Nodes.Count; i++)
        {
            if (Nodes[i].Parent < 0)
            {
                roots.Add(i);
            }
            else
            {
                kids[Nodes[i].Parent].Add(i);
            }
        }
        List<int> order = new List<int>();
        Stack<int> stack = new Stack<int>();
        for (int k = roots.Count - 1; k >= 0; k--)
        {
            stack.Push(roots[k]);
        }
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            order.Add(i);
            for (int k = kids[i].Count - 1; k >= 0; k--)
            {
                stack.Push(kids[i][k]);
            }
        }
        return order;
    }

    // World matrix of node i from the nodes' local matrices.
    public Mat4 NodeWorld(int i)
    {
        Mat4 m = Nodes[i].Local;
        while (Nodes[i].Parent >= 0)
        {
            i = Nodes[i].Parent;
            m = Nodes[i].Local * m;
        }
        return m;
    }
}
