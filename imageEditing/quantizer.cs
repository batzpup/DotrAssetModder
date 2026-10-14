namespace DotrAssetModder;

public sealed class Quantizer
{
    const int MaxDepth = 7;

    readonly int maxColors;
    readonly Node[] levels = new Node[MaxDepth + 1];
    readonly Node root = new();
    int leafCount;

    public Quantizer(int maxColors)
    {
        this.maxColors = maxColors;
    }

    public void AddColor(byte red, byte green, byte blue)
    {
        AddColor(root, red, green, blue, 0);
        while (leafCount > maxColors)
        {
            Reduce();
        }
    }

    public List<(byte Red, byte Green, byte Blue)> GetPalette()
    {
        var paletteColours = new List<(byte, byte, byte)>(leafCount);
        CollectLeaves(root, paletteColours);
        return paletteColours;
    }

    sealed class Node
    {
        public long RedTotal, GreenTotal, BlueTotal, Count;
        public bool IsLeaf;
        public Node?[] Children = new Node?[8];
        public Node? NextReducible;
    }

    void AddColor(Node node, byte red, byte green, byte blue, int depth)
    {
        if (node.IsLeaf)
        {
            node.RedTotal += red;
            node.GreenTotal += green;
            node.BlueTotal += blue;
            node.Count++;
            return;
        }
        if (depth == MaxDepth)
        {
            node.IsLeaf = true;
            node.RedTotal += red;
            node.GreenTotal += green;
            node.BlueTotal += blue;
            node.Count++;
            leafCount++;
            return;
        }

        int bitShift = 7 - depth;
        int childIndex = ((red >> bitShift) & 1) << 2 | ((green >> bitShift) & 1) << 1 | ((blue >> bitShift) & 1);
        if (node.Children[childIndex] == null)
        {
            var child = new Node();
            node.Children[childIndex] = child;
            if (depth < MaxDepth - 1)
            {
                child.NextReducible = levels[depth + 1];
                levels[depth + 1] = child;
            }
        }
        AddColor(node.Children[childIndex]!, red, green, blue, depth + 1);
    }

    void Reduce()
    {
        int level = MaxDepth - 1;
        while (level > 0 && levels[level] == null)
        {
            level--;
        }
        var node = levels[level]!;
        levels[level] = node.NextReducible;

        long redTotal = 0, greenTotal = 0, blueTotal = 0, colourCount = 0;
        foreach (var child in node.Children)
        {
            if (child == null)
            {
                continue;
            }
            redTotal += child.RedTotal;
            greenTotal += child.GreenTotal;
            blueTotal += child.BlueTotal;
            colourCount += child.Count;
            if (child.IsLeaf)
            {
                leafCount--;
            }
        }
        node.Children = new Node?[8];
        node.IsLeaf = true;
        node.RedTotal = redTotal;
        node.GreenTotal = greenTotal;
        node.BlueTotal = blueTotal;
        node.Count = colourCount;
        leafCount++;
    }

    static void CollectLeaves(Node node, List<(byte, byte, byte)> paletteColours)
    {
        if (node.IsLeaf)
        {
            long safeCount = Math.Max(node.Count, 1);
            paletteColours.Add(((byte)(node.RedTotal / safeCount), (byte)(node.GreenTotal / safeCount), (byte)(node.BlueTotal / safeCount)));
            return;
        }
        foreach (var child in node.Children)
        {
            if (child != null)
            {
                CollectLeaves(child, paletteColours);
            }
        }
    }
}