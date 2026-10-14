using System.Globalization;
using System.Xml;

namespace DotrAssetModder;
// Mostly AI

// COLLADA source -> Scene, a port of tools/toglb.py's read_dae / read_dae_anims (DotR-PC).
public static class DaeReader
{
    public static Scene Read(string path, Action<string> warn)
    {
        DaeRead reader = new DaeRead(path, warn);
        return reader.Run();
    }
}

// One <source>: the float_array, in rows of the accessor's stride.
sealed class DaeSource
{
    public double[] Data = Array.Empty<double>();
    public int Stride = 1;

    public int Rows
    {
        get
        {
            if (Stride <= 0)
            {
                return 0;
            }
            return Data.Length / Stride;
        }
    }
}

// One <matrix>/<translate>/<rotate>/<scale> under a node, in document order.
sealed class DaeXform
{
    public string? Sid;
    public string Tag;
    public double[] Values;

    public DaeXform(string? sid, string tag, double[] values)
    {
        Sid = sid;
        Tag = tag;
        Values = values;
    }
}

// A geometry read into corners, before welding.
sealed class DaeGeometry
{
    public double[] ControlPoints = Array.Empty<double>(); // control points, 3 each
    public int[] CornerControlPoints = Array.Empty<int>();
    public double[]? Normals; // 3 a corner
    public double[]? Uvs; // 2 a corner
    public int[] Triangles = Array.Empty<int>(); // corner indices, 3 a triangle
    public int[] TriangleMaterials = Array.Empty<int>(); // material a triangle
}

// One input of a primitive: source url, offset in the index tuple, and <input set="">.
sealed class DaeSemantic
{
    public string? Source;
    public int Offset;
    public int Set;

    public DaeSemantic(string? source, int offset, int set)
    {
        Source = source;
        Offset = offset;
        Set = set;
    }
}

// One sampled animation channel: the node, which of its transforms, the member (null = whole
// value), the key times and the key outputs.
sealed class DaeSampled
{
    public int Node;
    public int TransformIndex;
    public string? Member;
    public double[] Times = Array.Empty<double>();
    public double[] Outputs = Array.Empty<double>();
    public int OutputColumns;
}

// The reader's state: toglb.py keeps these in read_dae's closures.
sealed class DaeRead
{
    readonly string path;
    readonly Action<string> warn;
    readonly XmlElement root;
    readonly Dictionary<string, XmlElement> byId = new Dictionary<string, XmlElement>();
    readonly Scene scene = new Scene();
    readonly SourceImages images;
    readonly Dictionary<string, DaeSource> sources = new Dictionary<string, DaeSource>();
    readonly Dictionary<string, int> materialIndexByKey = new Dictionary<string, int>();
    readonly Dictionary<string, int> nodeOfId = new Dictionary<string, int>();
    readonly Dictionary<int, List<DaeXform>> nodeTransforms = new Dictionary<int, List<DaeXform>>();
    readonly List<int> instanceNodes = new List<int>();
    readonly List<XmlElement> instanceElements = new List<XmlElement>();

    public DaeRead(string file, Action<string> onWarn)
    {
        path = file;
        warn = onWarn;
        XmlDocument document = new XmlDocument();
        document.XmlResolver = null;
        using (XmlReader xmlReader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
        {
            document.Load(xmlReader);
        }
        if (document.DocumentElement == null)
        {
            throw new InvalidDataException("empty COLLADA file");
        }
        root = document.DocumentElement;
        CollectIds(root);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(file));
        if (directory == null)
        {
            directory = ".";
        }
        images = new SourceImages(directory, onWarn);
    }

    void CollectIds(XmlElement element)
    {
        string? id = Attr(element, "id");
        if (id != null && id.Length > 0 && !byId.ContainsKey(id))
        {
            byId[id] = element;
        }
        foreach (XmlNode childNode in element.ChildNodes)
        {
            XmlElement? childElement = childNode as XmlElement;
            if (childElement != null)
            {
                CollectIds(childElement);
            }
        }
    }

    // ---- small XML helpers (the Python strips the namespace off every tag and uses ET) ----

    static string? Attr(XmlElement element, string name)
    {
        XmlAttribute? attribute = element.Attributes[name];
        if (attribute == null)
        {
            return null;
        }
        return attribute.Value;
    }

    static string Attr(XmlElement element, string name, string defaultValue)
    {
        string? value = Attr(element, name);
        if (value == null)
        {
            return defaultValue;
        }
        return value;
    }

    static XmlElement? Find(XmlElement? element, string tag)
    {
        if (element == null)
        {
            return null;
        }
        foreach (XmlNode childNode in element.ChildNodes)
        {
            XmlElement? childElement = childNode as XmlElement;
            if (childElement != null && childElement.LocalName == tag)
            {
                return childElement;
            }
        }
        return null;
    }

    static XmlElement? FindGrandchild(XmlElement? element, string childTag, string grandchildTag)
    {
        return Find(Find(element, childTag), grandchildTag);
    }

    static List<XmlElement> FindAll(XmlElement? element, string tag)
    {
        List<XmlElement> found = new List<XmlElement>();
        if (element == null)
        {
            return found;
        }
        foreach (XmlNode childNode in element.ChildNodes)
        {
            XmlElement? childElement = childNode as XmlElement;
            if (childElement != null && childElement.LocalName == tag)
            {
                found.Add(childElement);
            }
        }
        return found;
    }

    static List<XmlElement> Children(XmlElement? element)
    {
        List<XmlElement> children = new List<XmlElement>();
        if (element == null)
        {
            return children;
        }
        foreach (XmlNode childNode in element.ChildNodes)
        {
            XmlElement? childElement = childNode as XmlElement;
            if (childElement != null)
            {
                children.Add(childElement);
            }
        }
        return children;
    }

    // ET's el.iter(tag): the element itself and every descendant with that tag.
    static void CollectDescendants(XmlElement element, string tag, List<XmlElement> hits)
    {
        if (element.LocalName == tag)
        {
            hits.Add(element);
        }
        foreach (XmlNode childNode in element.ChildNodes)
        {
            XmlElement? childElement = childNode as XmlElement;
            if (childElement != null)
            {
                CollectDescendants(childElement, tag, hits);
            }
        }
    }

    // ET's el.text: the character data before the first child element.
    static string? Text(XmlElement? element)
    {
        if (element == null)
        {
            return null;
        }
        System.Text.StringBuilder textBuilder = new System.Text.StringBuilder();
        foreach (XmlNode childNode in element.ChildNodes)
        {
            if (childNode.NodeType == XmlNodeType.Text || childNode.NodeType == XmlNodeType.CDATA
                || childNode.NodeType == XmlNodeType.Whitespace || childNode.NodeType == XmlNodeType.SignificantWhitespace)
            {
                textBuilder.Append(childNode.Value);
            }
            else
            {
                break;
            }
        }
        if (textBuilder.Length == 0)
        {
            return null;
        }
        return textBuilder.ToString();
    }

    static string? FindText(XmlElement? element, string tag)
    {
        XmlElement? child = Find(element, tag);
        if (child == null)
        {
            return null;
        }
        return Text(child);
    }

    static int CountTokens(string text)
    {
        int count = 0;
        int position = 0;
        while (position < text.Length)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }
            if (position >= text.Length)
            {
                break;
            }
            count++;
            while (position < text.Length && !char.IsWhiteSpace(text[position]))
            {
                position++;
            }
        }
        return count;
    }

    static double[] ReadFloats(XmlElement? element)
    {
        string? text = Text(element);
        if (element == null || text == null || text.Length == 0)
        {
            return Array.Empty<double>();
        }
        double[] values = new double[CountTokens(text)];
        int i = 0;
        int position = 0;
        while (position < text.Length)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }
            if (position >= text.Length)
            {
                break;
            }
            int tokenEnd = position;
            while (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd]))
            {
                tokenEnd++;
            }
            values[i++] = double.Parse(text.AsSpan(position, tokenEnd - position), NumberStyles.Float, CultureInfo.InvariantCulture);
            position = tokenEnd;
        }
        return values;
    }

    static int[] ReadInts(XmlElement? element)
    {
        string? text = Text(element);
        if (element == null || text == null || text.Length == 0)
        {
            return Array.Empty<int>();
        }
        int[] values = new int[CountTokens(text)];
        int i = 0;
        int position = 0;
        while (position < text.Length)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }
            if (position >= text.Length)
            {
                break;
            }
            int tokenEnd = position;
            while (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd]))
            {
                tokenEnd++;
            }
            values[i++] = int.Parse(text.AsSpan(position, tokenEnd - position), NumberStyles.Integer, CultureInfo.InvariantCulture);
            position = tokenEnd;
        }
        return values;
    }

    static string[] SplitTokens(string text)
    {
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    XmlElement? ElementByUrl(string? url)
    {
        if (url == null || url.Length == 0)
        {
            return null;
        }
        string key = url.Trim().TrimStart('#');
        XmlElement? element;
        if (byId.TryGetValue(key, out element))
        {
            return element;
        }
        return null;
    }

    DaeSource Source(string? url)
    {
        string key = "";
        if (url != null)
        {
            key = url;
        }
        DaeSource source;
        if (sources.TryGetValue(key, out source!))
        {
            return source;
        }
        XmlElement? element = ElementByUrl(url);
        XmlElement? accessor = FindGrandchild(element, "technique_common", "accessor");
        int stride = 1;
        if (accessor != null)
        {
            stride = int.Parse(Attr(accessor, "stride", "1"), CultureInfo.InvariantCulture);
        }
        source = new DaeSource();
        source.Data = ReadFloats(Find(element, "float_array"));
        source.Stride = stride;
        sources[key] = source;
        return source;
    }

    List<string> Names(string? url)
    {
        XmlElement? element = ElementByUrl(url);
        XmlElement? nameArray = Find(element, "Name_array");
        if (nameArray == null)
        {
            nameArray = Find(element, "IDREF_array");
        }
        string? text = Text(nameArray);
        List<string> out_ = new List<string>();
        if (nameArray != null && text != null)
        {
            out_.AddRange(SplitTokens(text));
        }
        return out_;
    }

    SkiaSharp.SKBitmap? ImageOf(string? imageId)
    {
        XmlElement? imageElement = null;
        if (imageId != null && imageId.Length > 0)
        {
            byId.TryGetValue(imageId, out imageElement);
        }
        if (imageElement == null)
        {
            return null;
        }
        string imagePath = FindText(imageElement, "init_from") ?? "";
        if (imagePath.Trim().Length == 0)
        {
            imagePath = FindText(Find(imageElement, "init_from"), "ref") ?? "";
        }
        imagePath = Uri.UnescapeDataString(imagePath.Trim());
        if (imagePath.StartsWith("file://", StringComparison.Ordinal))
        {
            imagePath = imagePath.Substring(7);
        }
        if (imagePath.Length >= 3 && imagePath[0] == '/' && char.IsAsciiLetter(imagePath[1]) && imagePath[2] == ':')
        {
            imagePath = imagePath.Substring(1);
        }
        if (imagePath.Length == 0)
        {
            return null;
        }
        return images.Load(new List<string> { imagePath });
    }

    // Python can key a dict on None; "\u0000" stands in for it here.
    static string KeyOf(string? nullableKey)
    {
        if (nullableKey == null)
        {
            return "\u0000";
        }
        return nullableKey;
    }

    int Material(string? target)
    {
        string key = KeyOf(target);
        int existingIndex;
        if (materialIndexByKey.TryGetValue(key, out existingIndex))
        {
            return existingIndex;
        }
        double[] color = new double[] { 1.0, 1.0, 1.0, 1.0 };
        SkiaSharp.SKBitmap? image = null;
        XmlElement? materialElement = ElementByUrl(target);
        XmlElement? effect = null;
        if (materialElement != null)
        {
            XmlElement? instanceEffect = Find(materialElement, "instance_effect");
            if (instanceEffect != null)
            {
                effect = ElementByUrl(Attr(instanceEffect, "url"));
            }
        }
        XmlElement? profile = null;
        if (effect != null)
        {
            profile = Find(effect, "profile_COMMON");
        }
        if (profile != null)
        {
            Dictionary<string, XmlElement> paramsBySid = new Dictionary<string, XmlElement>();
            List<XmlElement> newparams = new List<XmlElement>();
            CollectDescendants(profile, "newparam", newparams);
            foreach (XmlElement newparam in newparams)
            {
                paramsBySid[KeyOf(Attr(newparam, "sid"))] = newparam;
            }
            XmlElement? technique = Find(profile, "technique");
            XmlElement? shader = null;
            if (technique != null)
            {
                foreach (XmlElement child in Children(technique))
                {
                    if (child.LocalName == "phong" || child.LocalName == "lambert" || child.LocalName == "blinn" || child.LocalName == "constant")
                    {
                        shader = child;
                        break;
                    }
                }
            }
            XmlElement? diffuse = null;
            if (shader != null)
            {
                diffuse = Find(shader, "diffuse");
                if (diffuse == null)
                {
                    diffuse = Find(shader, "emission");
                }
            }
            if (diffuse != null)
            {
                XmlElement? colorElement = Find(diffuse, "color");
                if (colorElement != null)
                {
                    double[] rgba = ReadFloats(colorElement);
                    color = new double[] { 1.0, 1.0, 1.0, 1.0 };
                    int take = Math.Min(4, rgba.Length);
                    for (int k = 0; k < take; k++)
                    {
                        color[k] = rgba[k];
                    }
                }
                XmlElement? textureElement = Find(diffuse, "texture");
                if (textureElement != null)
                {
                    string? textureId = Attr(textureElement, "texture");
                    XmlElement? samplerParam;
                    paramsBySid.TryGetValue(KeyOf(textureId), out samplerParam);
                    if (samplerParam != null && Find(samplerParam, "sampler2D") != null)
                    {
                        string? surfaceSid = FindText(Find(samplerParam, "sampler2D"), "source");
                        XmlElement? instanceImage = FindGrandchild(samplerParam, "sampler2D", "instance_image");
                        XmlElement? surfaceParam;
                        if (surfaceSid != null && surfaceSid.Length > 0 && paramsBySid.TryGetValue(surfaceSid.Trim(), out surfaceParam!))
                        {
                            textureId = (FindText(Find(surfaceParam, "surface"), "init_from") ?? "").Trim();
                        }
                        else if (instanceImage != null)
                        {
                            textureId = Attr(instanceImage, "url", "").TrimStart('#');
                        }
                    }
                    image = ImageOf(textureId);
                }
            }
        }
        string name = "default";
        if (target != null && target.Length > 0)
        {
            name = target.TrimStart('#');
        }
        if (materialElement != null && Attr(materialElement, "name") != null)
        {
            name = Attr(materialElement, "name", "");
        }
        materialIndexByKey[key] = scene.AddMaterial(name, color, image);
        return materialIndexByKey[key];
    }

    static void CopyRow(DaeSource source, int row, int take, double[] destination, int destinationOffset)
    {
        int count = Math.Min(take, source.Stride);
        for (int k = 0; k < count; k++)
        {
            destination[destinationOffset + k] = source.Data[row * source.Stride + k];
        }
    }

    DaeGeometry? Geometry(XmlElement? geometryElement, Dictionary<string, string?> symbols)
    {
        XmlElement? mesh = Find(geometryElement, "mesh");
        if (mesh == null)
        {
            return null;
        }
        XmlElement? verts = Find(mesh, "vertices");
        Dictionary<string, string?> vertexInputs = new Dictionary<string, string?>();
        foreach (XmlElement input in FindAll(verts, "input"))
        {
            vertexInputs[KeyOf(Attr(input, "semantic"))] = Attr(input, "source");
        }
        DaeSource controlPointSource = Source(vertexInputs["POSITION"]);
        int controlPointCount = controlPointSource.Rows;
        double[] controlPoints = new double[controlPointCount * 3];
        for (int i = 0; i < controlPointCount; i++)
        {
            CopyRow(controlPointSource, i, 3, controlPoints, i * 3);
        }
        DaeSource? vertexNormals = null;
        if (vertexInputs.ContainsKey("NORMAL"))
        {
            vertexNormals = Source(vertexInputs["NORMAL"]);
        }
        DaeSource? vertexUvs = null;
        if (vertexInputs.ContainsKey("TEXCOORD"))
        {
            vertexUvs = Source(vertexInputs["TEXCOORD"]);
        }
        List<int> cornerControlPoints = new List<int>();
        List<double> cornerNormals = new List<double>();
        List<double> cornerUvs = new List<double>();
        List<int> triangles = new List<int>();
        List<int> triangleMaterials = new List<int>();
        bool hasNormals = true;
        bool hasUv = true;
        int firstCorner = 0;
        foreach (XmlElement primitive in Children(mesh))
        {
            if (primitive.LocalName != "triangles" && primitive.LocalName != "polylist" && primitive.LocalName != "polygons")
            {
                if (primitive.LocalName == "trifans" || primitive.LocalName == "tristrips" || primitive.LocalName == "lines" || primitive.LocalName == "linestrips")
                {
                    warn(Attr(geometryElement!, "id", "") + ": <" + primitive.LocalName + "> skipped");
                }
                continue;
            }
            List<XmlElement> inputs = FindAll(primitive, "input");
            int stride = 1;
            foreach (XmlElement input in inputs)
            {
                int offsetEnd = int.Parse(Attr(input, "offset", "0"), CultureInfo.InvariantCulture) + 1;
                if (offsetEnd > stride)
                {
                    stride = offsetEnd;
                }
            }
            Dictionary<string, DaeSemantic> semantics = new Dictionary<string, DaeSemantic>();
            foreach (XmlElement input in inputs)
            {
                string semantic = KeyOf(Attr(input, "semantic"));
                int set = int.Parse(Attr(input, "set", "0"), CultureInfo.InvariantCulture);
                DaeSemantic existing;
                if (semantic == "TEXCOORD" && semantics.TryGetValue(semantic, out existing!) && set >= existing.Set)
                {
                    continue;
                }
                semantics[semantic] = new DaeSemantic(Attr(input, "source"), int.Parse(Attr(input, "offset", "0"), CultureInfo.InvariantCulture), set);
            }
            int[] indices;
            int[] counts;
            if (primitive.LocalName == "polygons")
            {
                List<int[]> polygons = new List<int[]>();
                foreach (XmlElement polygonElement in FindAll(primitive, "p"))
                {
                    polygons.Add(ReadInts(polygonElement));
                }
                counts = new int[polygons.Count];
                int total = 0;
                for (int k = 0; k < polygons.Count; k++)
                {
                    counts[k] = polygons[k].Length / stride;
                    total += polygons[k].Length;
                }
                indices = new int[total];
                int copyPosition = 0;
                foreach (int[] polygon in polygons)
                {
                    Array.Copy(polygon, 0, indices, copyPosition, polygon.Length);
                    copyPosition += polygon.Length;
                }
            }
            else
            {
                indices = ReadInts(Find(primitive, "p"));
                if (primitive.LocalName == "triangles")
                {
                    counts = new int[indices.Length / (3 * stride)];
                    Array.Fill(counts, 3);
                }
                else
                {
                    counts = ReadInts(Find(primitive, "vcount"));
                }
            }
            int corners = indices.Length / stride;
            int[] cornerVertices = new int[corners];
            int vertexOffset = semantics["VERTEX"].Offset;
            for (int corner = 0; corner < corners; corner++)
            {
                cornerVertices[corner] = indices[corner * stride + vertexOffset];
            }
            cornerControlPoints.AddRange(cornerVertices);
            if (semantics.ContainsKey("NORMAL"))
            {
                DaeSource normalSource = Source(semantics["NORMAL"].Source);
                int normalOffset = semantics["NORMAL"].Offset;
                double[] normal = new double[3];
                for (int corner = 0; corner < corners; corner++)
                {
                    normal[0] = 0;
                    normal[1] = 0;
                    normal[2] = 0;
                    CopyRow(normalSource, indices[corner * stride + normalOffset], 3, normal, 0);
                    cornerNormals.Add(normal[0]);
                    cornerNormals.Add(normal[1]);
                    cornerNormals.Add(normal[2]);
                }
            }
            else if (vertexNormals != null)
            {
                double[] normal = new double[3];
                for (int corner = 0; corner < corners; corner++)
                {
                    normal[0] = 0;
                    normal[1] = 0;
                    normal[2] = 0;
                    CopyRow(vertexNormals, cornerVertices[corner], 3, normal, 0);
                    cornerNormals.Add(normal[0]);
                    cornerNormals.Add(normal[1]);
                    cornerNormals.Add(normal[2]);
                }
            }
            else
            {
                hasNormals = false;
            }
            if (semantics.ContainsKey("TEXCOORD"))
            {
                DaeSource uvSource = Source(semantics["TEXCOORD"].Source);
                int uvOffset = semantics["TEXCOORD"].Offset;
                double[] uv = new double[2];
                for (int corner = 0; corner < corners; corner++)
                {
                    uv[0] = 0;
                    uv[1] = 0;
                    CopyRow(uvSource, indices[corner * stride + uvOffset], 2, uv, 0);
                    cornerUvs.Add(uv[0]);
                    cornerUvs.Add(uv[1]);
                }
            }
            else if (vertexUvs != null)
            {
                double[] uv = new double[2];
                for (int corner = 0; corner < corners; corner++)
                {
                    uv[0] = 0;
                    uv[1] = 0;
                    CopyRow(vertexUvs, cornerVertices[corner], 2, uv, 0);
                    cornerUvs.Add(uv[0]);
                    cornerUvs.Add(uv[1]);
                }
            }
            else
            {
                for (int corner = 0; corner < corners; corner++)
                {
                    cornerUvs.Add(0);
                    cornerUvs.Add(0);
                }
                hasUv = false;
            }
            int[] polygonStarts = new int[counts.Length];
            int cornerCursor = firstCorner;
            for (int k = 0; k < counts.Length; k++)
            {
                polygonStarts[k] = cornerCursor;
                cornerCursor += counts[k];
            }
            int[] fanTriangles = MeshBuild.FanTriangulate(polygonStarts, counts);
            triangles.AddRange(fanTriangles);
            int materialIndex = -1;
            string? primitiveMaterial = Attr(primitive, "material");
            string? target;
            if (symbols.TryGetValue(KeyOf(primitiveMaterial), out target))
            {
                materialIndex = Material(target);
            }
            else
            {
                materialIndex = Material(primitiveMaterial);
            }
            for (int k = 0; k < fanTriangles.Length / 3; k++)
            {
                triangleMaterials.Add(materialIndex);
            }
            firstCorner += corners;
        }
        if (triangles.Count == 0)
        {
            return null;
        }
        DaeGeometry geometry = new DaeGeometry();
        geometry.ControlPoints = controlPoints;
        geometry.CornerControlPoints = cornerControlPoints.ToArray();
        if (hasNormals)
        {
            geometry.Normals = cornerNormals.ToArray();
        }
        if (hasUv)
        {
            geometry.Uvs = cornerUvs.ToArray();
        }
        geometry.Triangles = triangles.ToArray();
        geometry.TriangleMaterials = triangleMaterials.ToArray();
        return geometry;
    }

    static Matrix4 Apply(Matrix4 matrix, string tag, double[] values, int offset)
    {
        if (tag == "matrix")
        {
            Matrix4 elementMatrix = new Matrix4();
            for (int k = 0; k < 16; k++)
            {
                elementMatrix.Elements[k] = values[offset + k];
            }
            return matrix * elementMatrix;
        }
        if (tag == "translate")
        {
            return matrix * Matrix4.Translation(values[offset], values[offset + 1], values[offset + 2]);
        }
        if (tag == "rotate")
        {
            return matrix * Matrix4.Rotation(values[offset], values[offset + 1], values[offset + 2], values[offset + 3]);
        }
        if (tag == "scale")
        {
            return matrix * Matrix4.Scale(values[offset], values[offset + 1], values[offset + 2]);
        }
        return matrix;
    }

    static Matrix4 Compose(List<DaeXform> transforms)
    {
        Matrix4 matrix = Matrix4.Identity();
        foreach (DaeXform transform in transforms)
        {
            matrix = Apply(matrix, transform.Tag, transform.Values, 0);
        }
        return matrix;
    }

    void Walk(XmlElement? parentElement, int parent)
    {
        foreach (XmlElement nodeElement in FindAll(parentElement, "node"))
        {
            string? name = Attr(nodeElement, "name");
            if (name == null || name.Length == 0)
            {
                name = Attr(nodeElement, "id");
            }
            if (name == null || name.Length == 0)
            {
                name = Attr(nodeElement, "sid");
            }
            if (name == null || name.Length == 0)
            {
                name = "node";
            }
            int nodeIndex = scene.AddNode(name, parent);
            string? id = Attr(nodeElement, "id");
            if (id != null && id.Length > 0)
            {
                nodeOfId[id] = nodeIndex;
            }
            List<DaeXform> transforms = new List<DaeXform>();
            foreach (XmlElement child in Children(nodeElement))
            {
                if (child.LocalName == "matrix" || child.LocalName == "translate" || child.LocalName == "rotate" || child.LocalName == "scale")
                {
                    transforms.Add(new DaeXform(Attr(child, "sid"), child.LocalName, ReadFloats(child)));
                }
            }
            nodeTransforms[nodeIndex] = transforms;
            scene.Nodes[nodeIndex].Local = Compose(transforms);
            scene.Nodes[nodeIndex].Sid = Attr(nodeElement, "sid");
            foreach (XmlElement child in Children(nodeElement))
            {
                if (child.LocalName == "instance_geometry" || child.LocalName == "instance_controller")
                {
                    instanceNodes.Add(nodeIndex);
                    instanceElements.Add(child);
                }
            }
            Walk(nodeElement, nodeIndex);
        }
    }

    bool IsUnder(int nodeIndex, int rootNode)
    {
        while (nodeIndex >= 0)
        {
            if (nodeIndex == rootNode)
            {
                return true;
            }
            nodeIndex = scene.Nodes[nodeIndex].Parent;
        }
        return false;
    }

    int ResolveJoint(string name, List<int> roots)
    {
        List<int> bySid = new List<int>();
        for (int k = 0; k < scene.Nodes.Count; k++)
        {
            if (scene.Nodes[k].Sid == name)
            {
                bySid.Add(k);
            }
        }
        foreach (int k in bySid)
        {
            foreach (int rootNode in roots)
            {
                if (IsUnder(k, rootNode))
                {
                    return k;
                }
            }
        }
        if (bySid.Count > 0)
        {
            return bySid[0];
        }
        int nodeById;
        if (nodeOfId.TryGetValue(name, out nodeById))
        {
            return nodeById;
        }
        for (int k = 0; k < scene.Nodes.Count; k++)
        {
            if (scene.Nodes[k].Name == name)
            {
                return k;
            }
        }
        return -1;
    }

    public Scene Run()
    {
        string upAxis = "Y_UP";
        string? upText = FindText(Find(root, "asset"), "up_axis");
        if (upText != null && upText.Trim().Length > 0)
        {
            upAxis = upText.Trim();
        }
        XmlElement? unit = FindGrandchild(root, "asset", "unit");
        if (unit != null && Attr(unit, "meter") != null)
        {
            scene.Unit = double.Parse(Attr(unit, "meter", "1"), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        scene.Axis = Matrix4.Identity();
        if (upAxis == "Z_UP")
        {
            scene.Axis = AxisOf(1, 0, 0, 0, 0, 1, 0, -1, 0);
        }
        else if (upAxis == "X_UP")
        {
            scene.Axis = AxisOf(0, -1, 0, 1, 0, 0, 0, 0, 1);
        }

        XmlElement? instanceVisualScene = FindGrandchild(root, "scene", "instance_visual_scene");
        XmlElement? visualScene = null;
        if (instanceVisualScene != null)
        {
            visualScene = ElementByUrl(Attr(instanceVisualScene, "url"));
        }
        if (visualScene == null)
        {
            visualScene = FindGrandchild(root, "library_visual_scenes", "visual_scene");
        }
        if (visualScene == null)
        {
            warn("no visual scene");
            return scene;
        }
        Walk(visualScene, -1);

        for (int instanceIndex = 0; instanceIndex < instanceNodes.Count; instanceIndex++)
        {
            int nodeIndex = instanceNodes[instanceIndex];
            XmlElement instance = instanceElements[instanceIndex];
            Dictionary<string, string?> symbols = new Dictionary<string, string?>();
            List<XmlElement> instanceMaterials = new List<XmlElement>();
            CollectDescendants(instance, "instance_material", instanceMaterials);
            foreach (XmlElement instanceMaterial in instanceMaterials)
            {
                symbols[KeyOf(Attr(instanceMaterial, "symbol"))] = Attr(instanceMaterial, "target");
            }
            if (instance.LocalName == "instance_geometry")
            {
                XmlElement? geom = ElementByUrl(Attr(instance, "url"));
                DaeGeometry? data = null;
                if (geom != null)
                {
                    data = Geometry(geom, symbols);
                }
                if (data == null)
                {
                    continue;
                }
                SceneMesh mesh = new SceneMesh(MeshName(geom!), nodeIndex);
                MeshBuild.Build(mesh, data.ControlPoints, data.CornerControlPoints, data.Normals, data.Uvs, data.Triangles, data.TriangleMaterials, null);
                scene.Meshes.Add(mesh);
                continue;
            }
            XmlElement? controller = ElementByUrl(Attr(instance, "url"));
            XmlElement? skin = Find(controller, "skin");
            if (skin == null)
            {
                warn("controller " + Attr(instance, "url", "") + " is not a skin; skipped");
                continue;
            }
            XmlElement? skinGeometryElement = ElementByUrl(Attr(skin, "source"));
            DaeGeometry? skinGeometry = null;
            if (skinGeometryElement != null)
            {
                skinGeometry = Geometry(skinGeometryElement, symbols);
            }
            if (skinGeometry == null)
            {
                continue;
            }
            double[] bindShapeValues = ReadFloats(Find(skin, "bind_shape_matrix"));
            Matrix4 bindShapeMatrix = Matrix4.Identity();
            if (bindShapeValues.Length == 16)
            {
                bindShapeMatrix = new Matrix4();
                Array.Copy(bindShapeValues, bindShapeMatrix.Elements, 16);
            }
            Dictionary<string, string?> jointInputs = new Dictionary<string, string?>();
            foreach (XmlElement input in FindAll(Find(skin, "joints"), "input"))
            {
                jointInputs[KeyOf(Attr(input, "semantic"))] = Attr(input, "source");
            }
            List<string> jointNames = Names(jointInputs["JOINT"]);
            DaeSource inverseBindSource = Source(jointInputs["INV_BIND_MATRIX"]);
            List<int> roots = new List<int>();
            foreach (XmlElement skeleton in FindAll(instance, "skeleton"))
            {
                string? skeletonText = Text(skeleton);
                if (skeletonText == null)
                {
                    continue;
                }
                string skeletonId = skeletonText.Trim().TrimStart('#');
                int skeletonRoot;
                if (nodeOfId.TryGetValue(skeletonId, out skeletonRoot))
                {
                    roots.Add(skeletonRoot);
                }
            }
            List<int> jointNodes = new List<int>();
            for (int k = 0; k < jointNames.Count; k++)
            {
                int jointNode = ResolveJoint(jointNames[k], roots);
                if (jointNode < 0)
                {
                    warn("joint " + jointNames[k] + " not found");
                }
                else if (scene.Nodes[jointNode].Bind == null)
                {
                    Matrix4 inverseBind = new Matrix4();
                    Array.Copy(inverseBindSource.Data, k * 16, inverseBind.Elements, 0, 16);
                    scene.Nodes[jointNode].Bind = inverseBind.Inverse();
                }
                jointNodes.Add(jointNode);
            }
            XmlElement? vertexWeights = Find(skin, "vertex_weights");
            Dictionary<string, DaeSemantic> weightInputs = new Dictionary<string, DaeSemantic>();
            int weightStride = 1;
            foreach (XmlElement input in FindAll(vertexWeights, "input"))
            {
                int offset = int.Parse(Attr(input, "offset", "0"), CultureInfo.InvariantCulture);
                weightInputs[KeyOf(Attr(input, "semantic"))] = new DaeSemantic(Attr(input, "source"), offset, 0);
                if (offset + 1 > weightStride)
                {
                    weightStride = offset + 1;
                }
            }
            double[] weights = Source(weightInputs["WEIGHT"].Source).Data;
            int[] influenceCounts = ReadInts(Find(vertexWeights, "vcount"));
            int[] weightIndices = ReadInts(Find(vertexWeights, "v"));
            int controlPointCount = skinGeometry.ControlPoints.Length / 3;
            List<Influence>[] controlPointInfluences = new List<Influence>[controlPointCount];
            Dictionary<int, int>[] influenceSlotByJoint = new Dictionary<int, int>[controlPointCount];
            for (int controlPoint = 0; controlPoint < controlPointCount; controlPoint++)
            {
                controlPointInfluences[controlPoint] = new List<Influence>();
                influenceSlotByJoint[controlPoint] = new Dictionary<int, int>();
            }
            int jointOffset = weightInputs["JOINT"].Offset;
            int weightOffset = weightInputs["WEIGHT"].Offset;
            int weightRow = 0;
            for (int controlPoint = 0; controlPoint < influenceCounts.Length; controlPoint++)
            {
                int influenceCount = influenceCounts[controlPoint];
                for (int k = 0; k < influenceCount; k++)
                {
                    int jointSlot = weightIndices[(weightRow + k) * weightStride + jointOffset];
                    double weight = weights[weightIndices[(weightRow + k) * weightStride + weightOffset]];
                    if (controlPoint < controlPointCount && jointSlot >= 0 && jointSlot < jointNodes.Count && jointNodes[jointSlot] >= 0 && weight > 0)
                    {
                        int influenceJoint = jointNodes[jointSlot];
                        int influenceSlot;
                        if (influenceSlotByJoint[controlPoint].TryGetValue(influenceJoint, out influenceSlot))
                        {
                            controlPointInfluences[controlPoint][influenceSlot].Weight += weight;
                        }
                        else
                        {
                            influenceSlotByJoint[controlPoint][influenceJoint] = controlPointInfluences[controlPoint].Count;
                            controlPointInfluences[controlPoint].Add(new Influence(influenceJoint, weight));
                        }
                    }
                }
                weightRow += influenceCount;
            }
            // the bind shape matrix puts the control points (and normals) into bind-pose space
            double[] bindControlPoints = new double[controlPointCount * 3];
            for (int controlPoint = 0; controlPoint < controlPointCount; controlPoint++)
            {
                double transformedX;
                double transformedY;
                double transformedZ;
                bindShapeMatrix.TransformPoint(skinGeometry.ControlPoints[controlPoint * 3], skinGeometry.ControlPoints[controlPoint * 3 + 1], skinGeometry.ControlPoints[controlPoint * 3 + 2], out transformedX, out transformedY, out transformedZ);
                bindControlPoints[controlPoint * 3] = transformedX;
                bindControlPoints[controlPoint * 3 + 1] = transformedY;
                bindControlPoints[controlPoint * 3 + 2] = transformedZ;
            }
            if (skinGeometry.Normals != null)
            {
                int normalCount = skinGeometry.Normals.Length / 3;
                double[] bindNormals = new double[normalCount * 3];
                for (int normalIndex = 0; normalIndex < normalCount; normalIndex++)
                {
                    double transformedX;
                    double transformedY;
                    double transformedZ;
                    bindShapeMatrix.TransformDir(skinGeometry.Normals[normalIndex * 3], skinGeometry.Normals[normalIndex * 3 + 1], skinGeometry.Normals[normalIndex * 3 + 2], out transformedX, out transformedY, out transformedZ);
                    bindNormals[normalIndex * 3] = transformedX;
                    bindNormals[normalIndex * 3 + 1] = transformedY;
                    bindNormals[normalIndex * 3 + 2] = transformedZ;
                }
                skinGeometry.Normals = bindNormals;
            }
            for (int controlPoint = 0; controlPoint < controlPointCount; controlPoint++)
            {
                if (controlPointInfluences[controlPoint].Count == 0)
                {
                    controlPointInfluences[controlPoint].Add(new Influence(nodeIndex, 1.0));
                }
            }
            SceneMesh skinnedMesh = new SceneMesh(MeshName(skinGeometryElement!), nodeIndex);
            MeshBuild.Build(skinnedMesh, bindControlPoints, skinGeometry.CornerControlPoints, skinGeometry.Normals, skinGeometry.Uvs, skinGeometry.Triangles, skinGeometry.TriangleMaterials, controlPointInfluences);
            scene.Meshes.Add(skinnedMesh);
        }

        ReadAnimations(Path.GetFileNameWithoutExtension(path));
        return scene;
    }

    static string MeshName(XmlElement geom)
    {
        string? name = Attr(geom, "name");
        if (name == null || name.Length == 0)
        {
            name = Attr(geom, "id");
        }
        if (name == null || name.Length == 0)
        {
            name = "mesh";
        }
        return name;
    }

    static Matrix4 AxisOf(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22)
    {
        Matrix4 matrix = Matrix4.Identity();
        matrix[0, 0] = m00;
        matrix[0, 1] = m01;
        matrix[0, 2] = m02;
        matrix[1, 0] = m10;
        matrix[1, 1] = m11;
        matrix[1, 2] = m12;
        matrix[2, 0] = m20;
        matrix[2, 1] = m21;
        matrix[2, 2] = m22;
        return matrix;
    }

    // ---- animations (read_dae_anims) ----

    readonly List<List<string?>> channelAnimationIds = new List<List<string?>>();
    readonly List<XmlElement> channelElements = new List<XmlElement>();

    void Collect(XmlElement animation, List<string?> chain)
    {
        List<string?> next = new List<string?>(chain);
        next.Add(Attr(animation, "id"));
        foreach (XmlElement channel in FindAll(animation, "channel"))
        {
            channelAnimationIds.Add(next);
            channelElements.Add(channel);
        }
        foreach (XmlElement childAnimation in FindAll(animation, "animation"))
        {
            Collect(childAnimation, next);
        }
    }

    // np.interp: linear between the keys, the end values outside them (times increasing)
    static double Interpolate(double time, double[] keyTimes, double[] keyValues, int valueOffset, int stride)
    {
        int keyCount = keyTimes.Length;
        if (time <= keyTimes[0])
        {
            return keyValues[valueOffset];
        }
        if (time >= keyTimes[keyCount - 1])
        {
            return keyValues[valueOffset + (keyCount - 1) * stride];
        }
        int low = 0;
        int high = keyCount - 1;
        while (high - low > 1)
        {
            int middle = (low + high) / 2;
            if (keyTimes[middle] <= time)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }
        double time0 = keyTimes[low];
        double time1 = keyTimes[low + 1];
        if (time1 == time0)
        {
            return keyValues[valueOffset + low * stride];
        }
        double fraction = (time - time0) / (time1 - time0);
        double value0 = keyValues[valueOffset + low * stride];
        double value1 = keyValues[valueOffset + (low + 1) * stride];
        return value0 + (value1 - value0) * fraction;
    }

    void ReadAnimations(string stem)
    {
        XmlElement? library = Find(root, "library_animations");
        if (library == null)
        {
            return;
        }
        foreach (XmlElement animation in FindAll(library, "animation"))
        {
            Collect(animation, new List<string?>());
        }
        if (channelElements.Count == 0)
        {
            return;
        }
        List<string> clipNames = new List<string>();
        List<List<XmlElement>> clipChannels = new List<List<XmlElement>>();
        List<double[]?> clipRanges = new List<double[]?>();
        foreach (XmlElement clip in FindAll(Find(root, "library_animation_clips"), "animation_clip"))
        {
            HashSet<string> animationIds = new HashSet<string>();
            foreach (XmlElement instanceAnimation in FindAll(clip, "instance_animation"))
            {
                animationIds.Add(Attr(instanceAnimation, "url", "").TrimStart('#'));
            }
            List<XmlElement> members = new List<XmlElement>();
            for (int k = 0; k < channelElements.Count; k++)
            {
                bool inClip = false;
                foreach (string? animationId in channelAnimationIds[k])
                {
                    if (animationId != null && animationIds.Contains(animationId))
                    {
                        inClip = true;
                        break;
                    }
                }
                if (inClip)
                {
                    members.Add(channelElements[k]);
                }
            }
            double[]? range = null;
            if (Attr(clip, "end") != null)
            {
                range = new double[]
                {
                    double.Parse(Attr(clip, "start", "0"), NumberStyles.Float, CultureInfo.InvariantCulture),
                    double.Parse(Attr(clip, "end", "0"), NumberStyles.Float, CultureInfo.InvariantCulture),
                };
            }
            string clipName = "clip";
            if (Attr(clip, "name") != null && Attr(clip, "name", "").Length > 0)
            {
                clipName = Attr(clip, "name", "");
            }
            else if (Attr(clip, "id") != null && Attr(clip, "id", "").Length > 0)
            {
                clipName = Attr(clip, "id", "");
            }
            clipNames.Add(clipName);
            clipChannels.Add(members);
            clipRanges.Add(range);
        }
        if (clipNames.Count == 0)
        {
            clipNames.Add(stem);
            clipChannels.Add(new List<XmlElement>(channelElements));
            clipRanges.Add(null);
        }

        for (int clipIndex = 0; clipIndex < clipNames.Count; clipIndex++)
        {
            List<DaeSampled> sampled = new List<DaeSampled>();
            bool anySampled = false;
            double startTime = 0;
            double endTime = 0;
            foreach (XmlElement channel in clipChannels[clipIndex])
            {
                string target = Attr(channel, "target", "");
                int slash = target.IndexOf('/');
                if (slash < 0)
                {
                    continue;
                }
                string nodeId = target.Substring(0, slash);
                string rest = target.Substring(slash + 1);
                int node;
                if (!nodeOfId.TryGetValue(nodeId, out node))
                {
                    continue;
                }
                string transformSid = rest;
                string? member = null;
                int dotIndex = rest.IndexOf('.');
                if (dotIndex >= 0)
                {
                    transformSid = rest.Substring(0, dotIndex);
                    member = rest.Substring(dotIndex + 1);
                }
                else if (rest.Contains('('))
                {
                    warn("animation target " + target + ": indexed members are not supported");
                    continue;
                }
                List<DaeXform> transforms = nodeTransforms[node];
                int transformIndex = -1;
                for (int candidate = 0; candidate < transforms.Count; candidate++)
                {
                    if (transforms[candidate].Sid == transformSid)
                    {
                        transformIndex = candidate;
                        break;
                    }
                }
                if (transformIndex < 0)
                {
                    continue;
                }
                XmlElement? sampler;
                if (!byId.TryGetValue(Attr(channel, "source", "").TrimStart('#'), out sampler))
                {
                    continue;
                }
                Dictionary<string, string?> samplerInputs = new Dictionary<string, string?>();
                foreach (XmlElement x in FindAll(sampler, "input"))
                {
                    samplerInputs[KeyOf(Attr(x, "semantic"))] = Attr(x, "source");
                }
                double[] times = Source(samplerInputs["INPUT"]).Data;
                double[] outputs = Source(samplerInputs["OUTPUT"]).Data;
                if (times.Length == 0)
                {
                    continue;
                }
                DaeSampled sampledChannel = new DaeSampled();
                sampledChannel.Node = node;
                sampledChannel.TransformIndex = transformIndex;
                sampledChannel.Member = member;
                sampledChannel.Times = times;
                sampledChannel.Outputs = outputs;
                sampledChannel.OutputColumns = outputs.Length / times.Length;
                sampled.Add(sampledChannel);
                if (!anySampled)
                {
                    startTime = times[0];
                    endTime = times[times.Length - 1];
                    anySampled = true;
                }
                else
                {
                    startTime = Math.Min(startTime, times[0]);
                    endTime = Math.Max(endTime, times[times.Length - 1]);
                }
            }
            if (sampled.Count == 0)
            {
                continue;
            }
            double[]? clipRange = clipRanges[clipIndex];
            if (clipRange != null && clipRange[1] > clipRange[0])
            {
                startTime = clipRange[0];
                endTime = clipRange[1];
            }
            int frames = (int)Math.Round((endTime - startTime) * Scene.Fps, MidpointRounding.ToEven) + 1;
            double[] sampleTimes = new double[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                sampleTimes[frame] = startTime + (double)frame / Scene.Fps;
            }
            List<int> nodeOrder = new List<int>();
            Dictionary<int, List<DaeSampled>> perNode = new Dictionary<int, List<DaeSampled>>();
            foreach (DaeSampled sampledChannel in sampled)
            {
                List<DaeSampled> items;
                if (!perNode.TryGetValue(sampledChannel.Node, out items!))
                {
                    items = new List<DaeSampled>();
                    perNode[sampledChannel.Node] = items;
                    nodeOrder.Add(sampledChannel.Node);
                }
                items.Add(sampledChannel);
            }
            SceneAnim animation = new SceneAnim(clipNames[clipIndex], frames);
            foreach (int node in nodeOrder)
            {
                List<DaeXform> transforms = nodeTransforms[node];
                int[] widths = new int[transforms.Count];
                double[][] transformValues = new double[transforms.Count][];
                for (int k = 0; k < transforms.Count; k++)
                {
                    int width = transforms[k].Values.Length;
                    widths[k] = width;
                    transformValues[k] = new double[frames * width];
                    for (int frame = 0; frame < frames; frame++)
                    {
                        Array.Copy(transforms[k].Values, 0, transformValues[k], frame * width, width);
                    }
                }
                foreach (DaeSampled sampledChannel in perNode[node])
                {
                    int width = widths[sampledChannel.TransformIndex];
                    if (sampledChannel.Member == null)
                    {
                        int columns = Math.Min(sampledChannel.OutputColumns, width);
                        for (int column = 0; column < columns; column++)
                        {
                            for (int frame = 0; frame < frames; frame++)
                            {
                                transformValues[sampledChannel.TransformIndex][frame * width + column] = Interpolate(sampleTimes[frame], sampledChannel.Times, sampledChannel.Outputs, column, sampledChannel.OutputColumns);
                            }
                        }
                    }
                    else
                    {
                        int column = MemberIndex(sampledChannel.Member);
                        if (column < 0 || column >= width)
                        {
                            continue;
                        }
                        for (int frame = 0; frame < frames; frame++)
                        {
                            transformValues[sampledChannel.TransformIndex][frame * width + column] = Interpolate(sampleTimes[frame], sampledChannel.Times, sampledChannel.Outputs, 0, sampledChannel.OutputColumns);
                        }
                    }
                }
                Matrix4[] frameMatrices = new Matrix4[frames];
                for (int frame = 0; frame < frames; frame++)
                {
                    Matrix4 matrix = Matrix4.Identity();
                    for (int k = 0; k < transforms.Count; k++)
                    {
                        matrix = Apply(matrix, transforms[k].Tag, transformValues[k], frame * widths[k]);
                    }
                    frameMatrices[frame] = matrix;
                }
                animation.Tracks[node] = frameMatrices;
            }
            scene.Anims.Add(animation);
        }
    }

    static int MemberIndex(string member)
    {
        string upperMember = member.ToUpperInvariant();
        if (upperMember == "X")
        {
            return 0;
        }
        if (upperMember == "Y")
        {
            return 1;
        }
        if (upperMember == "Z")
        {
            return 2;
        }
        if (upperMember == "ANGLE")
        {
            return 3;
        }
        return -1;
    }
}
