using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace DotrAssetModder;
// Mostly AI

/* FBX source -> Scene, a port of tools/toglb.py's FBX reader (DotR-PC; FbxNode, fbx_binary,
   fbx_ascii, fbx_array, fbx_props, fbx_local, Fbx, read_fbx, read_fbx_geometry,
   read_fbx_anims, read_curves, read_fbx6_takes). Binary (6.x and 7.5 wide headers) and ASCII
   are both read; a property is a long, a double, a bool, a string, a byte[] (R) or an
   FbxArray. */

// One node of the FBX tree: a name, its properties and its children.
public sealed class FbxNode
{
    public string Name;
    public List<object> Properties;
    public List<FbxNode> Children = new List<FbxNode>();

    public FbxNode(string name, List<object> props)
    {
        Name = name;
        Properties = props;
    }

    public FbxNode? Find(string name)
    {
        foreach (FbxNode child in Children)
        {
            if (child.Name == name)
            {
                return child;
            }
        }
        return null;
    }

    public List<FbxNode> FindAll(string name)
    {
        List<FbxNode> found = new List<FbxNode>();
        foreach (FbxNode child in Children)
        {
            if (child.Name == name)
            {
                found.Add(child);
            }
        }
        return found;
    }

    public object? Value(string name)
    {
        FbxNode? child = Find(name);
        if (child == null || child.Properties.Count == 0)
        {
            return null;
        }
        return child.Properties[0];
    }

    // Value(name, default) for the string lookups the Python makes
    public string ValueStr(string name, string defaultValue)
    {
        object? value = Value(name);
        if (value is string text)
        {
            return text;
        }
        if (value == null)
        {
            return defaultValue;
        }
        return defaultValue;
    }
}

// A binary FBX array property (numpy's ndarray in the Python), widened to doubles.
public sealed class FbxArray
{
    public double[] Values;

    public FbxArray(double[] values)
    {
        Values = values;
    }
}

// One Connections entry seen from the other end: the object and the property it drives.
public sealed class FbxConnection
{
    public object Id;
    public string Property;

    public FbxConnection(object id, string property)
    {
        Id = id;
        Property = property;
    }
}

// One animation curve's keys (times in seconds).
public sealed class FbxCurve
{
    public double[] Times;
    public double[] Values;

    public FbxCurve(double[] times, double[] values)
    {
        Times = times;
        Values = values;
    }
}

// One channel (T, R or S) of a node: its X, Y and Z curves, any of which may be missing.
public sealed class FbxChannel
{
    public string Channel;
    public FbxCurve?[] Components = new FbxCurve?[3];

    public FbxChannel(string channelName)
    {
        Channel = channelName;
    }
}

// The channels of one node, in the order the file gave them (the Python's defaultdict).
public sealed class FbxNodeCurves
{
    public object Node;
    public List<FbxChannel> Channels = new List<FbxChannel>();

    public FbxNodeCurves(object node)
    {
        Node = node;
    }

    public FbxChannel? Find(string channelName)
    {
        foreach (FbxChannel channel in Channels)
        {
            if (channel.Channel == channelName)
            {
                return channel;
            }
        }
        return null;
    }
}

// One skin cluster: its bone node, Transform, TransformLink and the points it pulls.
public sealed class FbxCluster
{
    public int Joint;
    public Matrix4 Transform = Matrix4.Identity();
    public Matrix4? Link;
    public long[] Indices = new long[0];
    public double[] Weights = new double[0];
}

// The parsed file: objects by id, their kind / sub type / name, and the connections.
public sealed class Fbx
{
    public FbxNode Root;
    public Dictionary<object, FbxNode> Objects = new Dictionary<object, FbxNode>();
    public Dictionary<object, string> Kind = new Dictionary<object, string>();
    public Dictionary<object, string> SubType = new Dictionary<object, string>();
    public Dictionary<object, string> Names = new Dictionary<object, string>();
    public List<object> Ids = new List<object>(); // objs in file order
    public Dictionary<object, List<FbxConnection>> Children = new Dictionary<object, List<FbxConnection>>();
    public Dictionary<object, List<FbxConnection>> Parents = new Dictionary<object, List<FbxConnection>>();

    static readonly List<FbxConnection> NoConnections = new List<FbxConnection>();

    public Fbx(string path)
    {
        byte[] fileBytes = File.ReadAllBytes(path);
        if (FbxReader.StartsWith(fileBytes, "Kaydara FBX Binary"))
        {
            Root = FbxReader.ReadBinary(fileBytes);
        }
        else
        {
            Root = FbxReader.ReadAscii(Encoding.UTF8.GetString(fileBytes));
        }
        FbxNode? objects = Root.Find("Objects");
        List<FbxNode> list = new List<FbxNode>();
        if (objects != null)
        {
            list = objects.Children;
        }
        foreach (FbxNode objectNode in list)
        {
            if (objectNode.Properties.Count == 0)
            {
                continue;
            }
            object objectId;
            object name;
            object subclass;
            if (objectNode.Properties[0] is string)
            {
                objectId = objectNode.Properties[0];
                name = objectNode.Properties[0];
                if (objectNode.Properties.Count > 1)
                {
                    subclass = objectNode.Properties[1];
                }
                else
                {
                    subclass = "";
                }
            }
            else
            {
                objectId = objectNode.Properties[0];
                if (objectNode.Properties.Count > 1)
                {
                    name = objectNode.Properties[1];
                }
                else
                {
                    name = "";
                }
                if (objectNode.Properties.Count > 2)
                {
                    subclass = objectNode.Properties[2];
                }
                else
                {
                    subclass = "";
                }
            }
            if (!Objects.ContainsKey(objectId))
            {
                Ids.Add(objectId);
            }
            Objects[objectId] = objectNode;
            Kind[objectId] = objectNode.Name;
            SubType[objectId] = FbxReader.ReadString(subclass);
            Names[objectId] = FbxReader.FbxName(name);
        }
        FbxNode? connections = Root.Find("Connections");
        List<FbxNode> connectionList = new List<FbxNode>();
        if (connections != null)
        {
            connectionList = connections.Children;
        }
        foreach (FbxNode connection in connectionList)
        {
            if (connection.Name != "C" && connection.Name != "Connect")
            {
                continue;
            }
            if (connection.Properties.Count < 3)
            {
                continue;
            }
            string property = "";
            if (connection.Properties.Count > 3 && connection.Properties[3] is string propertyText)
            {
                property = propertyText;
            }
            AddConnection(Children, connection.Properties[2], new FbxConnection(connection.Properties[1], property));
            AddConnection(Parents, connection.Properties[1], new FbxConnection(connection.Properties[2], property));
        }
    }

    static void AddConnection(Dictionary<object, List<FbxConnection>> map, object key, FbxConnection connection)
    {
        List<FbxConnection>? list;
        if (!map.TryGetValue(key, out list))
        {
            list = new List<FbxConnection>();
            map[key] = list;
        }
        list.Add(connection);
    }

    public List<FbxConnection> ChildrenIdsOf(object objectId)
    {
        List<FbxConnection>? list;
        if (Children.TryGetValue(objectId, out list))
        {
            return list;
        }
        return NoConnections;
    }

    public List<FbxConnection> ParentsOf(object objectId)
    {
        List<FbxConnection>? list;
        if (Parents.TryGetValue(objectId, out list))
        {
            return list;
        }
        return NoConnections;
    }

    public List<FbxConnection> ChildrenOf(object objectId, string kind, string? subclass = null)
    {
        List<FbxConnection> found = new List<FbxConnection>();
        foreach (FbxConnection connection in ChildrenIdsOf(objectId))
        {
            string? k;
            if (!Kind.TryGetValue(connection.Id, out k))
            {
                continue;
            }
            if (k != kind)
            {
                continue;
            }
            if (subclass != null)
            {
                string? subclassName;
                if (!SubType.TryGetValue(connection.Id, out subclassName) || subclassName != subclass)
                {
                    continue;
                }
            }
            found.Add(connection);
        }
        return found;
    }

    public Dictionary<string, List<object>> GlobalSettings()
    {
        FbxNode? globalSettings = Root.Find("GlobalSettings");
        if (globalSettings == null)
        {
            FbxNode? objects = Root.Find("Objects");
            if (objects != null)
            {
                globalSettings = objects.Find("GlobalSettings");
            }
        }
        return FbxReader.ReadProperties(globalSettings);
    }
}

public static class FbxReader
{
    public const double FbxTicks = 46186158000.0; // FBX time units per second

    static readonly string[] EulerOrders = { "XYZ", "XZY", "YZX", "YXZ", "ZXY", "ZYX" };

    // ---- values -------------------------------------------------------------------

    public static bool StartsWith(byte[] bytes, string prefix)
    {
        if (bytes.Length < prefix.Length)
        {
            return false;
        }
        for (int i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != (byte)prefix[i])
            {
                return false;
            }
        }
        return true;
    }

    static bool IsNumber(object? value)
    {
        return value is long || value is double || value is bool;
    }

    static double ToNumber(object? value)
    {
        if (value is long longValue)
        {
            return longValue;
        }
        if (value is double doubleValue)
        {
            return doubleValue;
        }
        if (value is bool boolValue)
        {
            if (boolValue)
            {
                return 1;
            }
            return 0;
        }
        throw new InvalidDataException("FBX value is not a number");
    }

    public static string ReadString(object? value)
    {
        if (value is string text)
        {
            return text;
        }
        if (value is double doubleValue)
        {
            return doubleValue.ToString("R", CultureInfo.InvariantCulture);
        }
        if (value == null)
        {
            return "";
        }
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    // fbx_name: "Model::name" or "name\x00\x01Model"
    public static string FbxName(object? value)
    {
        string text = ReadString(value);
        int z = text.IndexOf("\x00\x01", StringComparison.Ordinal);
        if (z >= 0)
        {
            return text.Substring(0, z);
        }
        int separatorIndex = text.IndexOf("::", StringComparison.Ordinal);
        if (separatorIndex >= 0)
        {
            return text.Substring(separatorIndex + 2);
        }
        return text;
    }

    // ---- fbx_binary ---------------------------------------------------------------

    static int ElemSize(char typeCode)
    {
        if (typeCode == 'f' || typeCode == 'i')
        {
            return 4;
        }
        if (typeCode == 'd' || typeCode == 'l')
        {
            return 8;
        }
        return 1; // 'b'
    }

    static double[] DecodeArray(byte[] raw, char typeCode)
    {
        int size = ElemSize(typeCode);
        int elementCount = raw.Length / size;
        double[] values = new double[elementCount];
        for (int i = 0; i < elementCount; i++)
        {
            int elementOffset = i * size;
            if (typeCode == 'f')
            {
                values[i] = BitConverter.ToSingle(raw, elementOffset);
            }
            else if (typeCode == 'd')
            {
                values[i] = BitConverter.ToDouble(raw, elementOffset);
            }
            else if (typeCode == 'l')
            {
                values[i] = BitConverter.ToInt64(raw, elementOffset);
            }
            else if (typeCode == 'i')
            {
                values[i] = BitConverter.ToInt32(raw, elementOffset);
            }
            else
            {
                values[i] = raw[elementOffset];
            }
        }
        return values;
    }

    static byte[] Inflate(byte[] raw, int expectedLength)
    {
        using MemoryStream compressed = new MemoryStream(raw);
        using ZLibStream z = new ZLibStream(compressed, CompressionMode.Decompress);
        int capacity = expectedLength;
        if (capacity <= 0)
        {
            capacity = 1024;
        }
        using MemoryStream decompressed = new MemoryStream(capacity);
        z.CopyTo(decompressed);
        return decompressed.ToArray();
    }

    static FbxNode? ReadBinaryNode(byte[] bytes, bool wideOffsets, int offset, out int next)
    {
        int start = offset;
        long end;
        long count;
        if (wideOffsets)
        {
            end = BitConverter.ToInt64(bytes, offset);
            count = BitConverter.ToInt64(bytes, offset + 8);
            offset += 24;
        }
        else
        {
            end = BitConverter.ToUInt32(bytes, offset);
            count = BitConverter.ToUInt32(bytes, offset + 4);
            offset += 12;
        }
        if (end == 0)
        {
            if (wideOffsets)
            {
                next = start + 25;
            }
            else
            {
                next = start + 13;
            }
            return null;
        }
        int nameLength = bytes[offset];
        string name = Encoding.Latin1.GetString(bytes, offset + 1, nameLength);
        offset += 1 + nameLength;
        List<object> props = new List<object>();
        for (long k = 0; k < count; k++)
        {
            char typeCode = (char)bytes[offset];
            offset++;
            if (typeCode == 'Y')
            {
                props.Add((long)BitConverter.ToInt16(bytes, offset));
                offset += 2;
            }
            else if (typeCode == 'C')
            {
                props.Add(bytes[offset] != 0);
                offset += 1;
            }
            else if (typeCode == 'I')
            {
                props.Add((long)BitConverter.ToInt32(bytes, offset));
                offset += 4;
            }
            else if (typeCode == 'F')
            {
                props.Add((double)BitConverter.ToSingle(bytes, offset));
                offset += 4;
            }
            else if (typeCode == 'D')
            {
                props.Add(BitConverter.ToDouble(bytes, offset));
                offset += 8;
            }
            else if (typeCode == 'L')
            {
                props.Add(BitConverter.ToInt64(bytes, offset));
                offset += 8;
            }
            else if (typeCode == 'f' || typeCode == 'd' || typeCode == 'l' || typeCode == 'i' || typeCode == 'b')
            {
                int elementCount = (int)BitConverter.ToUInt32(bytes, offset);
                int encoding = (int)BitConverter.ToUInt32(bytes, offset + 4);
                int storedLength = (int)BitConverter.ToUInt32(bytes, offset + 8);
                offset += 12;
                byte[] stored = new byte[storedLength];
                Array.Copy(bytes, offset, stored, 0, storedLength);
                offset += storedLength;
                if (encoding != 0)
                {
                    stored = Inflate(stored, elementCount * ElemSize(typeCode));
                }
                props.Add(new FbxArray(DecodeArray(stored, typeCode)));
            }
            else if (typeCode == 'S' || typeCode == 'R')
            {
                int byteCount = (int)BitConverter.ToUInt32(bytes, offset);
                if (typeCode == 'S')
                {
                    props.Add(Encoding.UTF8.GetString(bytes, offset + 4, byteCount));
                }
                else
                {
                    byte[] rawBytes = new byte[byteCount];
                    Array.Copy(bytes, offset + 4, rawBytes, 0, byteCount);
                    props.Add(rawBytes);
                }
                offset += 4 + byteCount;
            }
            else
            {
                throw new InvalidDataException("FBX property type '" + typeCode + "' at 0x" + (offset - 1).ToString("x"));
            }
        }
        FbxNode node = new FbxNode(name, props);
        while (offset < end)
        {
            FbxNode? child = ReadBinaryNode(bytes, wideOffsets, offset, out offset);
            if (child == null)
            {
                break;
            }
            node.Children.Add(child);
        }
        next = (int)end;
        return node;
    }

    public static FbxNode ReadBinary(byte[] bytes)
    {
        uint version = BitConverter.ToUInt32(bytes, 23);
        bool wideOffsets = version >= 7500;
        FbxNode root = new FbxNode("", new List<object>());
        int offset = 27;
        while (offset < bytes.Length)
        {
            FbxNode? child = ReadBinaryNode(bytes, wideOffsets, offset, out offset);
            if (child == null)
            {
                break;
            }
            root.Children.Add(child);
        }
        return root;
    }

    // ---- fbx_ascii ----------------------------------------------------------------

    static readonly Regex FbxToken = new Regex(
        "(?<ws>\\s+)|(?<cm>;[^\n]*)|\"(?<str>[^\"]*)\"|(?<key>[A-Za-z_][\\w|.\\-]*)\\s*:(?!:)"
        + "|(?<star>\\*\\d+)|(?<num>[-+]?(?:\\d+\\.?\\d*|\\.\\d+)(?:[eE][-+]?\\d+)?)"
        + "|(?<punct>[{},])|(?<word>[A-Za-z_][\\w|.\\-#]*)|(?<other>.)",
        RegexOptions.Compiled);

    public static FbxNode ReadAscii(string text)
    {
        FbxNode root = new FbxNode("", new List<object>());
        List<FbxNode> stack = new List<FbxNode>();
        stack.Add(root);
        FbxNode? current = null;
        foreach (Match match in FbxToken.Matches(text))
        {
            if (match.Groups["ws"].Success || match.Groups["cm"].Success || match.Groups["other"].Success)
            {
                continue;
            }
            if (match.Groups["key"].Success)
            {
                current = new FbxNode(match.Groups["key"].Value, new List<object>());
                stack[stack.Count - 1].Children.Add(current);
                continue;
            }
            if (match.Groups["punct"].Success)
            {
                string punctuation = match.Groups["punct"].Value;
                if (punctuation == "{")
                {
                    if (current != null)
                    {
                        stack.Add(current);
                    }
                    current = null;
                }
                else if (punctuation == "}")
                {
                    if (stack.Count > 1)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }
                    current = null;
                }
                continue;
            }
            // A value outside any key belongs to the block it sits in: some exporters write
            // arrays as "Materials: *N { 0,0,1 }" without the usual "a:" key.
            FbxNode? owner = current;
            if (owner == null && stack.Count > 1)
            {
                owner = stack[stack.Count - 1];
            }
            if (owner == null)
            {
                continue;
            }
            if (match.Groups["num"].Success)
            {
                string numberText = match.Groups["num"].Value;
                if (numberText.Contains('.') || numberText.Contains('e') || numberText.Contains('E'))
                {
                    owner.Properties.Add(double.Parse(numberText, CultureInfo.InvariantCulture));
                }
                else
                {
                    owner.Properties.Add(long.Parse(numberText, CultureInfo.InvariantCulture));
                }
            }
            else if (match.Groups["str"].Success)
            {
                owner.Properties.Add(match.Groups["str"].Value);
            }
            else if (match.Groups["star"].Success)
            {
                owner.Properties.Add(match.Groups["star"].Value);
            }
            else
            {
                owner.Properties.Add(match.Groups["word"].Value);
            }
        }
        return root;
    }

    // ---- fbx_array / fbx_props ----------------------------------------------------

    static readonly double[] NoDoubles = new double[0];

    public static double[] ReadArray(FbxNode? node)
    {
        if (node == null)
        {
            return NoDoubles;
        }
        if (node.Properties.Count > 0 && node.Properties[0] is FbxArray fbxArray)
        {
            return fbxArray.Values;
        }
        List<object> propertyValues;
        FbxNode? arrayNode = node.Find("a");
        if (arrayNode != null)
        {
            propertyValues = arrayNode.Properties;
        }
        else
        {
            propertyValues = new List<object>();
            foreach (object property in node.Properties)
            {
                if (property is string text && text.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }
                propertyValues.Add(property);
            }
        }
        double[] values = new double[propertyValues.Count];
        for (int i = 0; i < propertyValues.Count; i++)
        {
            values[i] = ToNumber(propertyValues[i]);
        }
        return values;
    }

    // fbx_array(node, np.int64): numpy's astype truncates towards zero
    public static long[] ReadIntArray(FbxNode? node)
    {
        double[] values = ReadArray(node);
        long[] integers = new long[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            integers[i] = (long)values[i];
        }
        return integers;
    }

    public static Dictionary<string, List<object>> ReadProperties(FbxNode? node)
    {
        Dictionary<string, List<object>> properties = new Dictionary<string, List<object>>();
        if (node == null)
        {
            return properties;
        }
        FbxNode? properties70 = node.Find("Properties70");
        if (properties70 != null)
        {
            foreach (FbxNode property in properties70.FindAll("P"))
            {
                if (property.Properties.Count == 0)
                {
                    continue;
                }
                properties[ReadString(property.Properties[0])] = Slice(property.Properties, 4);
            }
        }
        FbxNode? properties60 = node.Find("Properties60");
        if (properties60 != null)
        {
            foreach (FbxNode property in properties60.FindAll("Property"))
            {
                if (property.Properties.Count == 0)
                {
                    continue;
                }
                properties[ReadString(property.Properties[0])] = Slice(property.Properties, 3);
            }
        }
        return properties;
    }

    static List<object> Slice(List<object> props, int firstIndex)
    {
        List<object> slice = new List<object>();
        for (int i = firstIndex; i < props.Count; i++)
        {
            slice.Add(props[i]);
        }
        return slice;
    }

    static List<object>? GetProperty(Dictionary<string, List<object>> properties, string name)
    {
        List<object>? value;
        if (properties.TryGetValue(name, out value) && value.Count > 0)
        {
            return value;
        }
        return null;
    }

    // vec3: the property's first three numbers, or the default
    static double[] Vector3Property(Dictionary<string, List<object>> properties, string name, double defaultX, double defaultY, double defaultZ)
    {
        List<object>? value = GetProperty(properties, name);
        if (value != null && value.Count >= 3)
        {
            return new double[] { ToNumber(value[0]), ToNumber(value[1]), ToNumber(value[2]) };
        }
        return new double[] { defaultX, defaultY, defaultZ };
    }

    // ---- fbx_local / fbx_matrix ---------------------------------------------------

    // FBX node transform T Roff Rp Rpre R Rpost^-1 Rp^-1 Soff Sp S Sp^-1, for frames of
    // translations, Euler rotations and scales (3 numbers each) -> one matrix a frame.
    static Matrix4[] LocalMatrices(Dictionary<string, List<object>> properties, double[] translations, double[] rotations, double[] scales, int frames)
    {
        int rotationOrder = 0;
        List<object>? rotationOrderProperty = GetProperty(properties, "RotationOrder");
        if (rotationOrderProperty != null)
        {
            rotationOrder = (int)ToNumber(rotationOrderProperty[0]);
        }
        rotationOrder = rotationOrder % 6;
        if (rotationOrder < 0)
        {
            rotationOrder += 6;
        }
        string order = EulerOrders[rotationOrder];
        double[] rotationPivot = Vector3Property(properties, "RotationPivot", 0, 0, 0);
        double[] scalingPivot = Vector3Property(properties, "ScalingPivot", 0, 0, 0);
        double[] preRotation = Vector3Property(properties, "PreRotation", 0, 0, 0);
        double[] postRotation = Vector3Property(properties, "PostRotation", 0, 0, 0);
        Matrix4 preRotationMatrix = Matrix4.EulerAngles(preRotation[0], preRotation[1], preRotation[2]);
        Matrix4 postRotationMatrix = Matrix4.EulerAngles(postRotation[0], postRotation[1], postRotation[2]);
        double[] rotationOffset = Vector3Property(properties, "RotationOffset", 0, 0, 0);
        double[] scalingOffset = Vector3Property(properties, "ScalingOffset", 0, 0, 0);
        Matrix4 beforeRotation = Matrix4.Translation(rotationOffset[0], rotationOffset[1], rotationOffset[2]) * Matrix4.Translation(rotationPivot[0], rotationPivot[1], rotationPivot[2]) * preRotationMatrix;
        Matrix4 afterRotation = postRotationMatrix.Inverse() * Matrix4.Translation(-rotationPivot[0], -rotationPivot[1], -rotationPivot[2])
            * Matrix4.Translation(scalingOffset[0], scalingOffset[1], scalingOffset[2]) * Matrix4.Translation(scalingPivot[0], scalingPivot[1], scalingPivot[2]);
        Matrix4 negativeScalingPivot = Matrix4.Translation(-scalingPivot[0], -scalingPivot[1], -scalingPivot[2]);
        Matrix4[] matrices = new Matrix4[frames];
        for (int i = 0; i < frames; i++)
        {
            Matrix4 translationMatrix = Matrix4.Translation(translations[i * 3], translations[i * 3 + 1], translations[i * 3 + 2]);
            Matrix4 scaleMatrix = Matrix4.Scale(scales[i * 3], scales[i * 3 + 1], scales[i * 3 + 2]);
            Matrix4 rotationMatrix = Matrix4.EulerAngles(rotations[i * 3], rotations[i * 3 + 1], rotations[i * 3 + 2], order);
            matrices[i] = translationMatrix * beforeRotation * rotationMatrix * afterRotation * scaleMatrix * negativeScalingPivot;
        }
        return matrices;
    }

    // The node's default (Lcl) transform.
    static Matrix4 LocalMatrix(Dictionary<string, List<object>> properties)
    {
        return LocalMatrices(properties, Vector3Property(properties, "Lcl Translation", 0, 0, 0), Vector3Property(properties, "Lcl Rotation", 0, 0, 0),
            Vector3Property(properties, "Lcl Scaling", 1, 1, 1), 1)[0];
    }

    static Matrix4? Matrix(FbxNode? node)
    {
        double[] values = ReadArray(node);
        if (values.Length != 16)
        {
            return null;
        }
        Matrix4 matrix = new Matrix4();
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                matrix[row, column] = values[column * 4 + row]; // reshape(4, 4).T
            }
        }
        return matrix;
    }

    static int Clamp(long value, int low, int high)
    {
        if (value < low)
        {
            return low;
        }
        if (value > high)
        {
            return high;
        }
        return (int)value;
    }

    // np.interp: linear between the keys, flat outside them
    static double Interpolate(double time, double[] keyTimes, double[] keyValues)
    {
        int keyCount = keyTimes.Length;
        if (time < keyTimes[0])
        {
            return keyValues[0];
        }
        if (time >= keyTimes[keyCount - 1])
        {
            return keyValues[keyCount - 1];
        }
        int j = 0;
        int low = 0;
        int high = keyCount - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (keyTimes[middle] <= time)
            {
                j = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        if (j > keyCount - 2)
        {
            j = keyCount - 2;
        }
        double slope = (keyValues[j + 1] - keyValues[j]) / (keyTimes[j + 1] - keyTimes[j]);
        return slope * (time - keyTimes[j]) + keyValues[j];
    }

    static void Fbx6Keys(List<object> keyValues, List<double> times, List<double> values)
    {
        /* (time, value) pairs from an FBX 6 Key list: time, value, then an interpolation
           letter and its tangent data; a key starts at a number following a letter run's data
           once a large integer time turns up. */
        int i = 0;
        int valueCount = keyValues.Count;
        while (i + 1 < valueCount)
        {
            object keyTime = keyValues[i];
            object keyValue = keyValues[i + 1];
            if (!IsNumber(keyTime) || !IsNumber(keyValue))
            {
                i += 1;
                continue;
            }
            times.Add(ToNumber(keyTime));
            values.Add(ToNumber(keyValue));
            i += 2;
            if (i < valueCount && keyValues[i] is string kind)
            {
                i += 1;
                if (kind == "C")
                {
                    // constant: one letter of its own
                    if (i < valueCount && keyValues[i] is string)
                    {
                        i += 1;
                    }
                }
                else if (kind == "U")
                {
                    // cubic: a letter, then 2 or 4 numbers
                    string mode = "";
                    if (i < valueCount && keyValues[i] is string modeText)
                    {
                        mode = modeText;
                    }
                    if (mode.Length > 0)
                    {
                        i += 1;
                    }
                    i += CubicSkip(mode);
                }
                else if (kind == "T")
                {
                    // TCB
                    i += 4;
                }
            }
        }
    }

    static int CubicSkip(string mode)
    {
        if (mode == "a")
        {
            return 4;
        }
        if (mode == "n")
        {
            return 0;
        }
        return 2; // "s", "r" and anything else
    }

    static string ChannelOf(string property)
    {
        if (property == "Lcl Translation")
        {
            return "T";
        }
        if (property == "Lcl Rotation")
        {
            return "R";
        }
        if (property == "Lcl Scaling")
        {
            return "S";
        }
        return "";
    }

    // ---- read_fbx -----------------------------------------------------------------

    public static Scene Read(string path, Action<string> warn)
    {
        FbxLoader loader = new FbxLoader();
        loader.Warn = warn;
        loader.Document = new Fbx(path);
        loader.OutputScene = new Scene();
        loader.Images = new SourceImages(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", warn);
        return loader.Run();
    }

    // read_fbx and its closures: the file, the scene being filled and the node of each Model.
    sealed class FbxLoader
    {
        public Fbx Document = null!;
        public Scene OutputScene = null!;
        public SourceImages Images = null!;
        public Action<string> Warn = null!;
        public List<object> Models = new List<object>();
        public Dictionary<object, int> NodeIndexById = new Dictionary<object, int>();
        public Dictionary<object, Dictionary<string, List<object>>> NodeProps = new Dictionary<object, Dictionary<string, List<object>>>();
        public Dictionary<object, int> MatIndex = new Dictionary<object, int>();
        public Dictionary<object, int> TexIndex = new Dictionary<object, int>();

        static readonly string[] FileKeys = { "RelativeFilename", "FileName", "Filename" };
        static readonly string[] PathKeys = { "RelativeFilename", "FileName", "Path" };

        int AxisSetting(Dictionary<string, List<object>> globalProperties, string name, int defaultValue)
        {
            List<object>? value = GetProperty(globalProperties, name);
            if (value != null)
            {
                return (int)ToNumber(value[0]);
            }
            return defaultValue;
        }

        public Scene Run()
        {
            Dictionary<string, List<object>> globalProperties = Document.GlobalSettings();
            double[] axisMatrix = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            int coordAxis = AxisSetting(globalProperties, "CoordAxis", 0);
            int coordAxisSign = AxisSetting(globalProperties, "CoordAxisSign", 1);
            int upAxis = AxisSetting(globalProperties, "UpAxis", 1);
            int upAxisSign = AxisSetting(globalProperties, "UpAxisSign", 1);
            int frontAxis = AxisSetting(globalProperties, "FrontAxis", 2);
            int frontAxisSign = AxisSetting(globalProperties, "FrontAxisSign", 1);
            Matrix4 axis = Matrix4.Identity();
            for (int column = 0; column < 3; column++)
            {
                axis[0, column] = axisMatrix[coordAxis * 3 + column] * coordAxisSign;
                axis[1, column] = axisMatrix[upAxis * 3 + column] * upAxisSign;
                axis[2, column] = axisMatrix[frontAxis * 3 + column] * frontAxisSign;
            }
            if (axis.Determinant3x3() < 0)
            {
                for (int column = 0; column < 3; column++)
                {
                    axis[0, column] = -axis[0, column];
                }
            }
            OutputScene.Axis = axis;
            double unit = 1.0;
            List<object>? unitScale = GetProperty(globalProperties, "UnitScaleFactor");
            if (unitScale != null)
            {
                unit = ToNumber(unitScale[0]);
            }
            OutputScene.Unit = unit / 100.0;
            // The exporter is named at the top level (binary) or in FBXHeaderExtension (ASCII);
            // Unity Studio writes UVs top-left, Blender and the SDK bottom-left
            object? creator = Document.Root.Value("Creator");
            FbxNode? headerExtension = Document.Root.Find("FBXHeaderExtension");
            if (!(creator is string) && headerExtension != null)
            {
                creator = headerExtension.Value("Creator");
            }
            if (creator is string creatorText && creatorText.Contains("Unity Studio"))
            {
                OutputScene.UvTopLeft = true;
            }

            foreach (object objectId in Document.Ids)
            {
                if (Document.Kind[objectId] == "Model")
                {
                    Models.Add(objectId);
                }
            }
            foreach (object objectId in Models)
            {
                NodeIndexById[objectId] = OutputScene.AddNode(Document.Names[objectId]);
                NodeProps[objectId] = ReadProperties(Document.Objects[objectId]);
                OutputScene.Nodes[NodeIndexById[objectId]].Local = LocalMatrix(NodeProps[objectId]);
            }
            foreach (object objectId in Models)
            {
                foreach (FbxConnection connection in Document.ParentsOf(objectId))
                {
                    if (NodeIndexById.ContainsKey(connection.Id))
                    {
                        OutputScene.Nodes[NodeIndexById[objectId]].Parent = NodeIndexById[connection.Id];
                        break;
                    }
                }
            }

            foreach (object objectId in Models)
            {
                List<object> geometries = new List<object>();
                foreach (FbxConnection connection in Document.ChildrenOf(objectId, "Geometry"))
                {
                    geometries.Add(connection.Id);
                }
                if (geometries.Count == 0 && Document.Objects[objectId].Find("Vertices") != null)
                {
                    geometries.Add(objectId); // FBX 6: the geometry lives on the model
                }
                if (geometries.Count == 0)
                {
                    continue;
                }
                Dictionary<string, List<object>> properties = NodeProps[objectId];
                double[] geometricTranslation = Vector3Property(properties, "GeometricTranslation", 0, 0, 0);
                double[] geometricRotation = Vector3Property(properties, "GeometricRotation", 0, 0, 0);
                double[] geometricScaling = Vector3Property(properties, "GeometricScaling", 1, 1, 1);
                Matrix4 geometricMatrix = Matrix4.Translation(geometricTranslation[0], geometricTranslation[1], geometricTranslation[2]) * Matrix4.EulerAngles(geometricRotation[0], geometricRotation[1], geometricRotation[2])
                    * Matrix4.Scale(geometricScaling[0], geometricScaling[1], geometricScaling[2]);
                List<object> modelTexs = new List<object>();
                foreach (FbxConnection connection in Document.ChildrenOf(objectId, "Texture"))
                {
                    modelTexs.Add(connection.Id);
                }
                List<int> materials = new List<int>();
                foreach (FbxConnection connection in Document.ChildrenOf(objectId, "Material"))
                {
                    materials.Add(Material(connection.Id, modelTexs));
                }
                foreach (object geometryId in geometries)
                {
                    SceneMesh? mesh = Geometry(geometryId, objectId, geometricMatrix, materials, modelTexs);
                    if (mesh != null)
                    {
                        OutputScene.Meshes.Add(mesh);
                    }
                }
            }

            ReadAnimStacks();
            return OutputScene;
        }

        SKBitmap? TextureImage(object textureId)
        {
            FbxNode textureNode = Document.Objects[textureId];
            List<string> names = new List<string>();
            byte[]? data = null;
            foreach (string k in FileKeys)
            {
                if (textureNode.Value(k) is string fileName)
                {
                    names.Add(fileName);
                }
            }
            Dictionary<string, List<object>> textureProperties = ReadProperties(textureNode);
            foreach (string k in PathKeys)
            {
                List<object>? value = GetProperty(textureProperties, k);
                if (value != null && value[0] is string fileName)
                {
                    names.Add(fileName);
                }
            }
            foreach (FbxConnection videoConnection in Document.ChildrenOf(textureId, "Video"))
            {
                FbxNode videoNode = Document.Objects[videoConnection.Id];
                foreach (string k in FileKeys)
                {
                    if (videoNode.Value(k) is string fileName)
                    {
                        names.Add(fileName);
                    }
                }
                FbxNode? content = videoNode.Find("Content");
                if (content != null && content.Properties.Count > 0)
                {
                    if (content.Properties[0] is byte[] contentBytes)
                    {
                        data = contentBytes;
                    }
                    else
                    {
                        StringBuilder base64Text = new StringBuilder();
                        foreach (object property in content.Properties)
                        {
                            if (property is string text && !text.StartsWith("*", StringComparison.Ordinal))
                            {
                                base64Text.Append(text);
                            }
                        }
                        FbxNode? arrayNode = content.Find("a");
                        if (arrayNode != null)
                        {
                            foreach (object property in arrayNode.Properties)
                            {
                                if (property is string text)
                                {
                                    base64Text.Append(text);
                                }
                            }
                        }
                        try
                        {
                            data = Convert.FromBase64String(base64Text.ToString());
                        }
                        catch (Exception)
                        {
                            data = null;
                        }
                    }
                }
            }
            return Images.Load(names, data);
        }

        int Material(object materialId, List<object> texturesFallback)
        {
            int existingIndex;
            if (MatIndex.TryGetValue(materialId, out existingIndex))
            {
                return existingIndex;
            }
            Dictionary<string, List<object>> properties = ReadProperties(Document.Objects[materialId]);
            List<object>? diffuseColor = GetProperty(properties, "DiffuseColor");
            if (diffuseColor == null)
            {
                diffuseColor = GetProperty(properties, "Diffuse");
            }
            double[] color = { 1.0, 1.0, 1.0, 1.0 };
            if (diffuseColor != null)
            {
                for (int k = 0; k < 3 && k < diffuseColor.Count; k++)
                {
                    color[k] = ToNumber(diffuseColor[k]);
                }
            }
            List<FbxConnection> textures = Document.ChildrenOf(materialId, "Texture");
            object? textureId = null;
            foreach (FbxConnection connection in textures)
            {
                if (connection.Property == "DiffuseColor" || connection.Property == "Diffuse")
                {
                    textureId = connection.Id;
                    break;
                }
            }
            if (textureId == null && textures.Count > 0)
            {
                textureId = textures[0].Id;
            }
            if (textureId == null && texturesFallback.Count > 0)
            {
                textureId = texturesFallback[0];
            }
            SKBitmap? image = null;
            if (textureId != null)
            {
                image = TextureImage(textureId);
            }
            MatIndex[materialId] = OutputScene.AddMaterial(Document.Names[materialId], color, image);
            return MatIndex[materialId];
        }

        int TextureMaterial(object textureId)
        {
            int existingIndex;
            if (!TexIndex.TryGetValue(textureId, out existingIndex))
            {
                existingIndex = OutputScene.AddMaterial(Document.Names[textureId], new double[] { 1.0, 1.0, 1.0, 1.0 }, TextureImage(textureId));
                TexIndex[textureId] = existingIndex;
            }
            return existingIndex;
        }

        // read_fbx_geometry
        SceneMesh? Geometry(object geometryId, object model, Matrix4 geometricMatrix, List<int> materials, List<object> modelTextures)
        {
            FbxNode geometryNode = Document.Objects[geometryId];
            double[] controlPoints = ReadArray(geometryNode.Find("Vertices"));
            long[] polygonVertexIndices = ReadIntArray(geometryNode.Find("PolygonVertexIndex"));
            int controlPointCount = controlPoints.Length / 3;
            if (controlPointCount == 0 || polygonVertexIndices.Length == 0)
            {
                return null;
            }
            int cornerCount = polygonVertexIndices.Length;
            int[] cornerCp = new int[cornerCount];
            List<int> starts = new List<int>();
            List<int> sizes = new List<int>();
            int start = 0;
            for (int i = 0; i < cornerCount; i++)
            {
                if (polygonVertexIndices[i] < 0)
                {
                    cornerCp[i] = (int)(~polygonVertexIndices[i]);
                    starts.Add(start);
                    sizes.Add(i - start + 1);
                    start = i + 1;
                }
                else
                {
                    cornerCp[i] = (int)polygonVertexIndices[i];
                }
            }
            int[] polyOfCorner = new int[cornerCount];
            int cornerCursor = 0;
            for (int polygon = 0; polygon < sizes.Count; polygon++)
            {
                for (int k = 0; k < sizes[polygon]; k++)
                {
                    polyOfCorner[cornerCursor] = polygon;
                    cornerCursor++;
                }
            }
            int[] triCorners = MeshBuild.FanTriangulate(starts.ToArray(), sizes.ToArray());
            int triangleCount = triCorners.Length / 3;
            int[] trianglePolygon = new int[triangleCount];
            for (int triangle = 0; triangle < triangleCount; triangle++)
            {
                trianglePolygon[triangle] = polyOfCorner[triCorners[triangle * 3]];
            }

            double[] worldControlPoints = new double[controlPointCount * 3];
            for (int i = 0; i < controlPointCount; i++)
            {
                double transformedX;
                double transformedY;
                double transformedZ;
                geometricMatrix.TransformPoint(controlPoints[i * 3], controlPoints[i * 3 + 1], controlPoints[i * 3 + 2], out transformedX, out transformedY, out transformedZ);
                worldControlPoints[i * 3] = transformedX;
                worldControlPoints[i * 3 + 1] = transformedY;
                worldControlPoints[i * 3 + 2] = transformedZ;
            }
            controlPoints = worldControlPoints;
            double[]? normals = ReadLayer(geometryNode, cornerCp, polyOfCorner, "LayerElementNormal", "Normals", "NormalsIndex", 3);
            if (normals != null)
            {
                TransformDirs(normals, geometricMatrix);
            }
            double[]? uvs = ReadLayer(geometryNode, cornerCp, polyOfCorner, "LayerElementUV", "UV", "UVIndex", 2);

            // per-triangle material: LayerElementMaterial, or FBX 6's per-polygon TextureId
            long[] triangleMaterialSlot = new long[triangleCount];
            FbxNode? materialLayer = geometryNode.Find("LayerElementMaterial");
            if (materialLayer != null)
            {
                long[] materialSlots = ReadIntArray(materialLayer.Find("Materials"));
                if (materialSlots.Length > 0 && materialLayer.ValueStr("MappingInformationType", "AllSame") == "ByPolygon")
                {
                    for (int triangle = 0; triangle < triangleCount; triangle++)
                    {
                        triangleMaterialSlot[triangle] = materialSlots[Clamp(trianglePolygon[triangle], 0, materialSlots.Length - 1)];
                    }
                }
                else if (materialSlots.Length > 0)
                {
                    for (int triangle = 0; triangle < triangleCount; triangle++)
                    {
                        triangleMaterialSlot[triangle] = materialSlots[0];
                    }
                }
            }
            List<int> usedMaterials = materials;
            if (usedMaterials.Count == 0)
            {
                usedMaterials = new List<int>();
                usedMaterials.Add(OutputScene.AddMaterial("default", new double[] { 1.0, 1.0, 1.0, 1.0 }, null));
            }
            int[] triangleMaterials = new int[triangleCount];
            for (int triangle = 0; triangle < triangleCount; triangle++)
            {
                triangleMaterials[triangle] = usedMaterials[Clamp(triangleMaterialSlot[triangle], 0, usedMaterials.Count - 1)];
            }
            FbxNode? textureLayer = geometryNode.Find("LayerElementTexture");
            if (textureLayer != null && modelTextures.Count > 1 && textureLayer.ValueStr("MappingInformationType", "") == "ByPolygon")
            {
                long[] textureIds = ReadIntArray(textureLayer.Find("TextureId"));
                if (textureIds.Length > 0)
                {
                    // FBX 6 picks the texture per polygon: one material per texture
                    List<int> materialPerTexture = new List<int>();
                    foreach (object modelTexture in modelTextures)
                    {
                        materialPerTexture.Add(TextureMaterial(modelTexture));
                    }
                    for (int triangle = 0; triangle < triangleCount; triangle++)
                    {
                        int k = Clamp(textureIds[Clamp(trianglePolygon[triangle], 0, textureIds.Length - 1)], 0, materialPerTexture.Count - 1);
                        triangleMaterials[triangle] = materialPerTexture[k];
                    }
                }
            }

            SceneMesh mesh = new SceneMesh(Document.Names[model], NodeIndexById[model]);
            List<FbxConnection> skins = Document.ChildrenOf(geometryId, "Deformer", "Skin");
            List<Influence>[]? controlPointInfluences = null;
            if (skins.Count > 0)
            {
                List<FbxCluster> clusters = new List<FbxCluster>();
                foreach (FbxConnection clusterConnection in Document.ChildrenOf(skins[0].Id, "Deformer", "Cluster"))
                {
                    FbxNode clusterNode = Document.Objects[clusterConnection.Id];
                    List<FbxConnection> bones = Document.ChildrenOf(clusterConnection.Id, "Model");
                    if (bones.Count == 0)
                    {
                        continue;
                    }
                    Matrix4? transform = Matrix(clusterNode.Find("Transform"));
                    if (transform == null)
                    {
                        transform = Matrix4.Identity();
                    }
                    FbxCluster cluster = new FbxCluster();
                    cluster.Joint = NodeIndexById[bones[0].Id];
                    cluster.Transform = transform;
                    cluster.Link = Matrix(clusterNode.Find("TransformLink"));
                    cluster.Indices = ReadIntArray(clusterNode.Find("Indexes"));
                    cluster.Weights = ReadArray(clusterNode.Find("Weights"));
                    clusters.Add(cluster);
                }
                /* Two conventions turn up. The FBX SDK's: every cluster of a mesh holds the mesh's
                   bind matrix in Transform and its bone's bind matrix in TransformLink. Ripping
                   tools' (DoDModels, MMD and HSD converters): Transform is the bone's inverse bind
                   matrix in mesh space, and TransformLink is missing, its inverse, or in some other
                   space. */
                bool sdkConvention = true;
                foreach (FbxCluster cluster in clusters)
                {
                    if (cluster.Link == null)
                    {
                        sdkConvention = false;
                    }
                }
                if (sdkConvention)
                {
                    foreach (FbxCluster cluster in clusters)
                    {
                        if (!Matrix4.AllClose(cluster.Transform, clusters[0].Transform, 1e-4))
                        {
                            sdkConvention = false;
                        }
                    }
                }
                foreach (FbxCluster cluster in clusters)
                {
                    if (cluster.Link != null && Matrix4.AllClose(cluster.Transform * cluster.Link, Matrix4.Identity(), 1e-3)
                        && !Matrix4.AllClose(cluster.Transform, Matrix4.Identity(), 1e-4))
                    {
                        sdkConvention = false;
                    }
                }
                Matrix4 meshBind;
                if (sdkConvention && clusters.Count > 0)
                {
                    meshBind = clusters[0].Transform;
                }
                else
                {
                    meshBind = OutputScene.NodeWorld(NodeIndexById[model]);
                }
                controlPointInfluences = new List<Influence>[controlPointCount];
                for (int i = 0; i < controlPointCount; i++)
                {
                    controlPointInfluences[i] = new List<Influence>();
                }
                foreach (FbxCluster cluster in clusters)
                {
                    if (OutputScene.Nodes[cluster.Joint].Bind == null)
                    {
                        if (sdkConvention)
                        {
                            OutputScene.Nodes[cluster.Joint].Bind = cluster.Link;
                        }
                        else
                        {
                            OutputScene.Nodes[cluster.Joint].Bind = meshBind * cluster.Transform.Inverse();
                        }
                    }
                    int pairCount = Math.Min(cluster.Indices.Length, cluster.Weights.Length);
                    for (int k = 0; k < pairCount; k++)
                    {
                        long i = cluster.Indices[k];
                        double weight = cluster.Weights[k];
                        if (weight > 0 && i >= 0 && i < controlPointCount)
                        {
                            Accumulate(controlPointInfluences[(int)i], cluster.Joint, weight);
                        }
                    }
                }
                double[] bindControlPoints = new double[controlPointCount * 3];
                for (int i = 0; i < controlPointCount; i++)
                {
                    double transformedX;
                    double transformedY;
                    double transformedZ;
                    meshBind.TransformPoint(controlPoints[i * 3], controlPoints[i * 3 + 1], controlPoints[i * 3 + 2], out transformedX, out transformedY, out transformedZ);
                    bindControlPoints[i * 3] = transformedX;
                    bindControlPoints[i * 3 + 1] = transformedY;
                    bindControlPoints[i * 3 + 2] = transformedZ;
                }
                controlPoints = bindControlPoints;
                if (normals != null)
                {
                    TransformDirs(normals, meshBind);
                }
                bool hasUnweighted = false;
                for (int i = 0; i < controlPointCount; i++)
                {
                    if (controlPointInfluences[i].Count == 0)
                    {
                        hasUnweighted = true;
                        break;
                    }
                }
                if (hasUnweighted)
                {
                    // unweighted vertices ride the mesh's own node
                    int meshNode = NodeIndexById[model];
                    if (OutputScene.Nodes[meshNode].Bind == null)
                    {
                        OutputScene.Nodes[meshNode].Bind = meshBind;
                    }
                    for (int i = 0; i < controlPointCount; i++)
                    {
                        if (controlPointInfluences[i].Count == 0)
                        {
                            controlPointInfluences[i].Add(new Influence(meshNode, 1.0));
                        }
                    }
                }
            }
            MeshBuild.Build(mesh, controlPoints, cornerCp, normals, uvs, triCorners, triangleMaterials, controlPointInfluences);
            return mesh;
        }

        static void Accumulate(List<Influence> influences, int joint, double weight)
        {
            foreach (Influence influence in influences)
            {
                if (influence.Joint == joint)
                {
                    influence.Weight += weight;
                    return;
                }
            }
            influences.Add(new Influence(joint, weight));
        }

        static void TransformDirs(double[] directions, Matrix4 matrix)
        {
            int directionCount = directions.Length / 3;
            for (int i = 0; i < directionCount; i++)
            {
                double transformedX;
                double transformedY;
                double transformedZ;
                matrix.TransformDir(directions[i * 3], directions[i * 3 + 1], directions[i * 3 + 2], out transformedX, out transformedY, out transformedZ);
                directions[i * 3] = transformedX;
                directions[i * 3 + 1] = transformedY;
                directions[i * 3 + 2] = transformedZ;
            }
        }

        // One LayerElement* read out per corner, or null.
        double[]? ReadLayer(FbxNode geometryNode, int[] cornerCp, int[] polyOfCorner, string elementName, string dataName,
            string indexName, int componentCount)
        {
            FbxNode? layerElement = geometryNode.Find(elementName);
            if (layerElement == null)
            {
                return null;
            }
            int cornerCount = cornerCp.Length;
            string mapping = layerElement.ValueStr("MappingInformationType", "ByPolygonVertex");
            string referenceType = layerElement.ValueStr("ReferenceInformationType", "Direct");
            double[] layerValues = ReadArray(layerElement.Find(dataName));
            int valueCount = layerValues.Length / componentCount;
            int[] selected = new int[cornerCount];
            if (mapping == "ByVertice" || mapping == "ByVertex" || mapping == "ByControlPoint")
            {
                Array.Copy(cornerCp, selected, cornerCount);
            }
            else if (mapping == "ByPolygon")
            {
                Array.Copy(polyOfCorner, selected, cornerCount);
            }
            else if (mapping == "AllSame")
            {
                // zeros
            }
            else
            {
                for (int i = 0; i < cornerCount; i++)
                {
                    selected[i] = i;
                }
            }
            if (valueCount == 0)
            {
                // (the Python checks this after the index lookup; the result is the same)
                return null;
            }
            if (referenceType != "Direct" && layerElement.Find(indexName) != null)
            {
                long[] layerIndices = ReadIntArray(layerElement.Find(indexName));
                for (int i = 0; i < cornerCount; i++)
                {
                    selected[i] = Clamp(layerIndices[selected[i]], 0, valueCount - 1);
                }
            }
            double[] cornerValues = new double[cornerCount * componentCount];
            for (int i = 0; i < cornerCount; i++)
            {
                int valueIndex = Clamp(selected[i], 0, valueCount - 1);
                for (int k = 0; k < componentCount; k++)
                {
                    cornerValues[i * componentCount + k] = layerValues[valueIndex * componentCount + k];
                }
            }
            return cornerValues;
        }

        static FbxNodeCurves CurvesFor(List<FbxNodeCurves> curves, object node)
        {
            foreach (FbxNodeCurves nodeCurves in curves)
            {
                if (nodeCurves.Node.Equals(node))
                {
                    return nodeCurves;
                }
            }
            FbxNodeCurves created = new FbxNodeCurves(node);
            curves.Add(created);
            return created;
        }

        // read_fbx_anims
        void ReadAnimStacks()
        {
            List<object> animStacks = new List<object>();
            foreach (object objectId in Document.Ids)
            {
                if (Document.Kind[objectId] == "AnimationStack")
                {
                    animStacks.Add(objectId);
                }
            }
            foreach (object animStack in animStacks)
            {
                List<FbxNodeCurves> curves = new List<FbxNodeCurves>(); // node -> channel -> 3 curves
                bool anyKeys = false;
                double startTime = 0;
                double endTime = 0;
                foreach (FbxConnection layer in Document.ChildrenOf(animStack, "AnimationLayer"))
                {
                    foreach (FbxConnection curveNode in Document.ChildrenOf(layer.Id, "AnimationCurveNode"))
                    {
                        FbxConnection? target = null;
                        foreach (FbxConnection parentConnection in Document.ParentsOf(curveNode.Id))
                        {
                            if (NodeIndexById.ContainsKey(parentConnection.Id))
                            {
                                target = parentConnection;
                                break;
                            }
                        }
                        if (target == null)
                        {
                            continue;
                        }
                        string channelName = ChannelOf(target.Property);
                        if (channelName.Length == 0)
                        {
                            continue;
                        }
                        FbxNodeCurves nodeCurves = CurvesFor(curves, target.Id);
                        if (nodeCurves.Find(channelName) != null)
                        {
                            continue;
                        }
                        FbxChannel channel = new FbxChannel(channelName);
                        for (int axis = 0; axis < 3; axis++)
                        {
                            string wantedProperty = "d|" + "XYZ"[axis];
                            object? curveId = null;
                            foreach (FbxConnection connection in Document.ChildrenOf(curveNode.Id, "AnimationCurve"))
                            {
                                if (connection.Property == wantedProperty)
                                {
                                    curveId = connection.Id;
                                    break;
                                }
                            }
                            if (curveId == null)
                            {
                                continue;
                            }
                            long[] ticks = ReadIntArray(Document.Objects[curveId].Find("KeyTime"));
                            double[] keyTimes = new double[ticks.Length];
                            for (int k = 0; k < ticks.Length; k++)
                            {
                                keyTimes[k] = ticks[k] / FbxTicks;
                            }
                            double[] keyValues = ReadArray(Document.Objects[curveId].Find("KeyValueFloat"));
                            if (keyTimes.Length == 0 || keyTimes.Length != keyValues.Length)
                            {
                                continue;
                            }
                            channel.Components[axis] = new FbxCurve(keyTimes, keyValues);
                            if (!anyKeys)
                            {
                                startTime = keyTimes[0];
                                endTime = keyTimes[keyTimes.Length - 1];
                                anyKeys = true;
                            }
                            else
                            {
                                startTime = Math.Min(startTime, keyTimes[0]);
                                endTime = Math.Max(endTime, keyTimes[keyTimes.Length - 1]);
                            }
                        }
                        nodeCurves.Channels.Add(channel);
                    }
                }
                if (curves.Count == 0 || !anyKeys)
                {
                    continue;
                }
                Dictionary<string, List<object>> stackProperties = ReadProperties(Document.Objects[animStack]);
                List<object>? localStart = GetProperty(stackProperties, "LocalStart");
                List<object>? localStop = GetProperty(stackProperties, "LocalStop");
                if (localStart != null && localStop != null && ToNumber(localStop[0]) > ToNumber(localStart[0]))
                {
                    startTime = ToNumber(localStart[0]) / FbxTicks;
                    endTime = ToNumber(localStop[0]) / FbxTicks;
                }
                OutputScene.Anims.Add(SampleCurves(Document.Names[animStack], curves, startTime, endTime));
            }
            if (animStacks.Count == 0)
            {
                Fbx6Takes();
            }
        }

        // read_curves
        SceneAnim SampleCurves(string name, List<FbxNodeCurves> curves, double startTime, double endTime)
        {
            int frames = (int)Math.Round((endTime - startTime) * Scene.Fps, MidpointRounding.ToEven) + 1;
            double[] sampleTimes = new double[frames];
            for (int i = 0; i < frames; i++)
            {
                sampleTimes[i] = startTime + (double)i / Scene.Fps;
            }
            SceneAnim anim = new SceneAnim(name, frames);
            foreach (FbxNodeCurves nodeCurves in curves)
            {
                Dictionary<string, List<object>> properties = NodeProps[nodeCurves.Node];
                double[] translations = Repeat(Vector3Property(properties, "Lcl Translation", 0, 0, 0), frames);
                double[] rotations = Repeat(Vector3Property(properties, "Lcl Rotation", 0, 0, 0), frames);
                double[] scales = Repeat(Vector3Property(properties, "Lcl Scaling", 1, 1, 1), frames);
                foreach (FbxChannel channel in nodeCurves.Channels)
                {
                    double[] sampled;
                    if (channel.Channel == "T")
                    {
                        sampled = translations;
                    }
                    else if (channel.Channel == "R")
                    {
                        sampled = rotations;
                    }
                    else
                    {
                        sampled = scales;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        FbxCurve? curve = channel.Components[k];
                        if (curve == null)
                        {
                            continue;
                        }
                        for (int i = 0; i < frames; i++)
                        {
                            sampled[i * 3 + k] = Interpolate(sampleTimes[i], curve.Times, curve.Values);
                        }
                    }
                }
                anim.Tracks[NodeIndexById[nodeCurves.Node]] = LocalMatrices(properties, translations, rotations, scales, frames);
            }
            return anim;
        }

        static double[] Repeat(double[] vector, int frames)
        {
            double[] repeated = new double[frames * 3];
            for (int i = 0; i < frames; i++)
            {
                repeated[i * 3] = vector[0];
                repeated[i * 3 + 1] = vector[1];
                repeated[i * 3 + 2] = vector[2];
            }
            return repeated;
        }

        // read_fbx6_takes: FBX 6 keeps animation in Takes/Take/Model/Channel trees with Key
        // lists of time, value, interpolation[, tangent data] - only times and values are read.
        void Fbx6Takes()
        {
            FbxNode? takes = Document.Root.Find("Takes");
            if (takes == null)
            {
                return;
            }
            foreach (FbxNode takeNode in takes.FindAll("Take"))
            {
                List<FbxNodeCurves> curves = new List<FbxNodeCurves>();
                bool anyKeys = false;
                double startTime = 0;
                double endTime = 0;
                foreach (FbxNode modelNode in takeNode.FindAll("Model"))
                {
                    if (modelNode.Properties.Count == 0)
                    {
                        continue;
                    }
                    object objectId = modelNode.Properties[0];
                    if (!NodeIndexById.ContainsKey(objectId))
                    {
                        continue;
                    }
                    FbxNode? transformChannel = null;
                    foreach (FbxNode channelNode in modelNode.FindAll("Channel"))
                    {
                        if (channelNode.Properties.Count > 0 && ReadString(channelNode.Properties[0]) == "Transform")
                        {
                            transformChannel = channelNode;
                        }
                    }
                    if (transformChannel == null)
                    {
                        continue;
                    }
                    foreach (FbxNode channelNode in transformChannel.FindAll("Channel"))
                    {
                        string channelName = "";
                        if (channelNode.Properties.Count > 0)
                        {
                            channelName = ReadString(channelNode.Properties[0]);
                        }
                        if (channelName != "T" && channelName != "R" && channelName != "S")
                        {
                            continue;
                        }
                        FbxChannel channel = new FbxChannel(channelName);
                        for (int axisIndex = 0; axisIndex < 3; axisIndex++)
                        {
                            string axis = "XYZ".Substring(axisIndex, 1);
                            FbxNode? axisChannel = null;
                            foreach (FbxNode x in channelNode.FindAll("Channel"))
                            {
                                if (x.Properties.Count > 0 && ReadString(x.Properties[0]) == axis)
                                {
                                    axisChannel = x;
                                    break;
                                }
                            }
                            FbxNode? keys = null;
                            if (axisChannel != null)
                            {
                                keys = axisChannel.Find("Key");
                            }
                            if (keys == null)
                            {
                                continue;
                            }
                            List<double> keyTimeList = new List<double>();
                            List<double> keyValueList = new List<double>();
                            Fbx6Keys(keys.Properties, keyTimeList, keyValueList);
                            if (keyTimeList.Count == 0)
                            {
                                continue;
                            }
                            double[] keyTimes = new double[keyTimeList.Count];
                            double[] keyValues = new double[keyTimeList.Count];
                            for (int k = 0; k < keyTimeList.Count; k++)
                            {
                                keyTimes[k] = keyTimeList[k] / FbxTicks;
                                keyValues[k] = keyValueList[k];
                            }
                            channel.Components[axisIndex] = new FbxCurve(keyTimes, keyValues);
                            if (!anyKeys)
                            {
                                startTime = keyTimes[0];
                                endTime = keyTimes[keyTimes.Length - 1];
                                anyKeys = true;
                            }
                            else
                            {
                                startTime = Math.Min(startTime, keyTimes[0]);
                                endTime = Math.Max(endTime, keyTimes[keyTimes.Length - 1]);
                            }
                        }
                        FbxNodeCurves nodeCurves = CurvesFor(curves, objectId);
                        FbxChannel? existing = nodeCurves.Find(channelName);
                        if (existing != null)
                        {
                            nodeCurves.Channels[nodeCurves.Channels.IndexOf(existing)] = channel;
                        }
                        else
                        {
                            nodeCurves.Channels.Add(channel);
                        }
                    }
                }
                if (curves.Count == 0 || !anyKeys)
                {
                    continue;
                }
                string name = "take";
                if (takeNode.Properties.Count > 0)
                {
                    name = FbxName(takeNode.Properties[0]);
                }
                OutputScene.Anims.Add(SampleCurves(name, curves, startTime, endTime));
            }
        }
    }
}
