namespace DotrAssetModder;

public sealed class Quantizer
{
    const int MaxDepth = 7;

    readonly int    maxColors;
    readonly Node[] levels = new Node[MaxDepth + 1];
    readonly Node   root   = new();
    int             leafCount;

    public Quantizer(int maxColors)
    {
        this.maxColors = maxColors;
    }

    public void AddColor(byte r, byte g, byte b)
    {
        AddColor(root, r, g, b, 0);
        while (leafCount > maxColors)
        {
            Reduce();
        }
    }

    public List<(byte R, byte G, byte B)> GetPalette()
    {
        var result = new List<(byte, byte, byte)>(leafCount);
        CollectLeaves(root, result);
        return result;
    }

    sealed class Node
    {
        public long    R, G, B, Count;
        public bool    IsLeaf;
        public Node?[] Children      = new Node?[8];
        public Node?   NextReducible;
    }

    void AddColor(Node node, byte r, byte g, byte b, int depth)
    {
        if (node.IsLeaf)
        {
            node.R += r;
            node.G += g;
            node.B += b;
            node.Count++;
            return;
        }
        if (depth == MaxDepth)
        {
            node.IsLeaf = true;
            node.R     += r;
            node.G     += g;
            node.B     += b;
            node.Count++;
            leafCount++;
            return;
        }

        int shift = 7 - depth;
        int idx   = ((r >> shift) & 1) << 2 | ((g >> shift) & 1) << 1 | ((b >> shift) & 1);
        if (node.Children[idx] == null)
        {
            var child = new Node();
            node.Children[idx] = child;
            if (depth < MaxDepth - 1)
            {
                child.NextReducible = levels[depth + 1];
                levels[depth + 1]   = child;
            }
        }
        AddColor(node.Children[idx]!, r, g, b, depth + 1);
    }

    void Reduce()
    {
        int lv = MaxDepth - 1;
        while (lv > 0 && levels[lv] == null)
        {
            lv--;
        }
        var node = levels[lv]!;
        levels[lv] = node.NextReducible;

        long r = 0, g = 0, b = 0, count = 0;
        foreach (var child in node.Children)
        {
            if (child == null)
            {
                continue;
            }
            r     += child.R;
            g     += child.G;
            b     += child.B;
            count += child.Count;
            if (child.IsLeaf)
            {
                leafCount--;
            }
        }
        node.Children = new Node?[8];
        node.IsLeaf   = true;
        node.R        = r;
        node.G        = g;
        node.B        = b;
        node.Count    = count;
        leafCount++;
    }

    static void CollectLeaves(Node node, List<(byte, byte, byte)> result)
    {
        if (node.IsLeaf)
        {
            long c = Math.Max(node.Count, 1);
            result.Add(((byte)(node.R / c), (byte)(node.G / c), (byte)(node.B / c)));
            return;
        }
        foreach (var child in node.Children)
        {
            if (child != null)
            {
                CollectLeaves(child, result);
            }
        }
    }
}