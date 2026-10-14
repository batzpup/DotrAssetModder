using SkiaSharp;

namespace DotrAssetModder;
// Mostly AI

/* The in-between model of the FBX / COLLADA / glTF conversion, a port of tools/toglb.py's
   Scene (DotR-PC; NOTES/toglb-handover.md there). Readers fill it in the source's own axes and
   units; SceneFinish puts it into DotR's shape; SceneGlbWriter writes the glb the importer
   loads. */

public sealed class SceneNode
{
    public string Name;
    public int Parent;
    public Matrix4 Local = Matrix4.Identity();
    public Matrix4? Bind; // world matrix at skin bind time, for joints
    public string? Sid; // COLLADA scoped id

    public SceneNode(string name, int parent)
    {
        Name = name;
        Parent = parent;
    }
}

/* Triangles with per-vertex attributes, in flat arrays (Positions and Normals 3 a vertex, Uvs
   2, Triangles 3 a triangle, Joints and Weights 8 a vertex). A rigid mesh's positions are in
   its node's space; a skinned mesh's are bind-pose world positions, with influences
   Joints/Weights (node indices, -1 = none), heaviest first. */
public sealed class SceneMesh
{
    public string Name;
    public int Node;
    public bool Skinned;
    public double[] Positions = new double[0];
    public double[] Normals = new double[0];
    public double[] Uvs = new double[0];
    public int[] Triangles = new int[0];
    public int[] TriangleMaterials = new int[0]; // material index per triangle
    public int[]? Joints;
    public double[]? Weights;

    public SceneMesh(string name, int node)
    {
        Name = name;
        Node = node;
    }

    public int VertexCount
    {
        get { return Positions.Length / 3; }
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
    public Dictionary<int, Matrix4[]> Tracks = new Dictionary<int, Matrix4[]>(); // node -> local matrix per frame

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
    public Matrix4 Axis = Matrix4.Identity(); // source axes -> +Y up, +Z front (upper 3x3)
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
        List<int>[] children = new List<int>[Nodes.Count];
        List<int> roots = new List<int>();
        for (int i = 0; i < Nodes.Count; i++)
        {
            children[i] = new List<int>();
        }
        for (int i = 0; i < Nodes.Count; i++)
        {
            if (Nodes[i].Parent < 0)
            {
                roots.Add(i);
            }
            else
            {
                children[Nodes[i].Parent].Add(i);
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
            for (int k = children[i].Count - 1; k >= 0; k--)
            {
                stack.Push(children[i][k]);
            }
        }
        return order;
    }

    // World matrix of node i from the nodes' local matrices.
    public Matrix4 NodeWorld(int i)
    {
        Matrix4 world = Nodes[i].Local;
        while (Nodes[i].Parent >= 0)
        {
            i = Nodes[i].Parent;
            world = Nodes[i].Local * world;
        }
        return world;
    }
}
