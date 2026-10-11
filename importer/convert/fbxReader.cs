using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace DotrAssetModder;
// Mostly AI

// FBX source -> Scene, a port of tools/toglb.py's FBX reader (DotR-PC; FbxNode, fbx_binary,
// fbx_ascii, fbx_array, fbx_props, fbx_local, Fbx, read_fbx, read_fbx_geometry,
// read_fbx_anims, read_curves, read_fbx6_takes). Binary (6.x and 7.5 wide headers) and ASCII
// are both read; a property is a long, a double, a bool, a string, a byte[] (R) or an
// FbxArray.

// One node of the FBX tree: a name, its properties and its children.
public sealed class FbxNode
{
    public string Name;
    public List<object> Props;
    public List<FbxNode> Children = new List<FbxNode>();

    public FbxNode(string name, List<object> props)
    {
        Name = name;
        Props = props;
    }

    public FbxNode? Find(string name)
    {
        foreach (FbxNode c in Children)
        {
            if (c.Name == name)
            {
                return c;
            }
        }
        return null;
    }

    public List<FbxNode> FindAll(string name)
    {
        List<FbxNode> out_ = new List<FbxNode>();
        foreach (FbxNode c in Children)
        {
            if (c.Name == name)
            {
                out_.Add(c);
            }
        }
        return out_;
    }

    public object? Value(string name)
    {
        FbxNode? c = Find(name);
        if (c == null || c.Props.Count == 0)
        {
            return null;
        }
        return c.Props[0];
    }

    // Value(name, default) for the string lookups the Python makes
    public string ValueStr(string name, string def)
    {
        object? v = Value(name);
        if (v is string s)
        {
            return s;
        }
        if (v == null)
        {
            return def;
        }
        return def;
    }
}

// A binary FBX array property (numpy's ndarray in the Python), widened to doubles.
public sealed class FbxArray
{
    public double[] V;

    public FbxArray(double[] v)
    {
        V = v;
    }
}

// One Connections entry seen from the other end: the object and the property it drives.
public sealed class FbxConn
{
    public object Id;
    public string Prop;

    public FbxConn(object id, string prop)
    {
        Id = id;
        Prop = prop;
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
    public string Ch;
    public FbxCurve?[] Comps = new FbxCurve?[3];

    public FbxChannel(string ch)
    {
        Ch = ch;
    }
}

// The channels of one node, in the order the file gave them (the Python's defaultdict).
public sealed class FbxNodeCurves
{
    public object Node;
    public List<FbxChannel> Chans = new List<FbxChannel>();

    public FbxNodeCurves(object node)
    {
        Node = node;
    }

    public FbxChannel? Find(string ch)
    {
        foreach (FbxChannel c in Chans)
        {
            if (c.Ch == ch)
            {
                return c;
            }
        }
        return null;
    }
}

// One skin cluster: its bone node, Transform, TransformLink and the points it pulls.
public sealed class FbxCluster
{
    public int Joint;
    public Mat4 T = Mat4.Identity();
    public Mat4? Link;
    public long[] Idx = new long[0];
    public double[] W = new double[0];
}

// The parsed file: objects by id, their kind / sub type / name, and the connections.
public sealed class Fbx
{
    public FbxNode Root;
    public Dictionary<object, FbxNode> Objs = new Dictionary<object, FbxNode>();
    public Dictionary<object, string> Kind = new Dictionary<object, string>();
    public Dictionary<object, string> Sub = new Dictionary<object, string>();
    public Dictionary<object, string> Names = new Dictionary<object, string>();
    public List<object> Ids = new List<object>(); // objs in file order
    public Dictionary<object, List<FbxConn>> Kids = new Dictionary<object, List<FbxConn>>();
    public Dictionary<object, List<FbxConn>> Parents = new Dictionary<object, List<FbxConn>>();

    static readonly List<FbxConn> NoConns = new List<FbxConn>();

    public Fbx(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        if (FbxReader.StartsWith(d, "Kaydara FBX Binary"))
        {
            Root = FbxReader.ReadBinary(d);
        }
        else
        {
            Root = FbxReader.ReadAscii(Encoding.UTF8.GetString(d));
        }
        FbxNode? objects = Root.Find("Objects");
        List<FbxNode> list = new List<FbxNode>();
        if (objects != null)
        {
            list = objects.Children;
        }
        foreach (FbxNode o in list)
        {
            if (o.Props.Count == 0)
            {
                continue;
            }
            object oid;
            object name;
            object sub;
            if (o.Props[0] is string)
            {
                oid = o.Props[0];
                name = o.Props[0];
                if (o.Props.Count > 1)
                {
                    sub = o.Props[1];
                }
                else
                {
                    sub = "";
                }
            }
            else
            {
                oid = o.Props[0];
                if (o.Props.Count > 1)
                {
                    name = o.Props[1];
                }
                else
                {
                    name = "";
                }
                if (o.Props.Count > 2)
                {
                    sub = o.Props[2];
                }
                else
                {
                    sub = "";
                }
            }
            if (!Objs.ContainsKey(oid))
            {
                Ids.Add(oid);
            }
            Objs[oid] = o;
            Kind[oid] = o.Name;
            Sub[oid] = FbxReader.Str(sub);
            Names[oid] = FbxReader.FbxName(name);
        }
        FbxNode? conns = Root.Find("Connections");
        List<FbxNode> clist = new List<FbxNode>();
        if (conns != null)
        {
            clist = conns.Children;
        }
        foreach (FbxNode c in clist)
        {
            if (c.Name != "C" && c.Name != "Connect")
            {
                continue;
            }
            if (c.Props.Count < 3)
            {
                continue;
            }
            string prop = "";
            if (c.Props.Count > 3 && c.Props[3] is string ps)
            {
                prop = ps;
            }
            Add(Kids, c.Props[2], new FbxConn(c.Props[1], prop));
            Add(Parents, c.Props[1], new FbxConn(c.Props[2], prop));
        }
    }

    static void Add(Dictionary<object, List<FbxConn>> map, object key, FbxConn conn)
    {
        List<FbxConn>? list;
        if (!map.TryGetValue(key, out list))
        {
            list = new List<FbxConn>();
            map[key] = list;
        }
        list.Add(conn);
    }

    public List<FbxConn> KidsOf(object oid)
    {
        List<FbxConn>? list;
        if (Kids.TryGetValue(oid, out list))
        {
            return list;
        }
        return NoConns;
    }

    public List<FbxConn> ParentsOf(object oid)
    {
        List<FbxConn>? list;
        if (Parents.TryGetValue(oid, out list))
        {
            return list;
        }
        return NoConns;
    }

    public List<FbxConn> ChildrenOf(object oid, string kind, string? sub = null)
    {
        List<FbxConn> out_ = new List<FbxConn>();
        foreach (FbxConn c in KidsOf(oid))
        {
            string? k;
            if (!Kind.TryGetValue(c.Id, out k))
            {
                continue;
            }
            if (k != kind)
            {
                continue;
            }
            if (sub != null)
            {
                string? s;
                if (!Sub.TryGetValue(c.Id, out s) || s != sub)
                {
                    continue;
                }
            }
            out_.Add(c);
        }
        return out_;
    }

    public Dictionary<string, List<object>> GlobalSettings()
    {
        FbxNode? gs = Root.Find("GlobalSettings");
        if (gs == null)
        {
            FbxNode? objects = Root.Find("Objects");
            if (objects != null)
            {
                gs = objects.Find("GlobalSettings");
            }
        }
        return FbxReader.Props(gs);
    }
}

public static class FbxReader
{
    public const double FbxTicks = 46186158000.0; // FBX time units per second

    static readonly string[] EulerOrders = { "XYZ", "XZY", "YZX", "YXZ", "ZXY", "ZYX" };

    // ---- values -------------------------------------------------------------------

    public static bool StartsWith(byte[] d, string s)
    {
        if (d.Length < s.Length)
        {
            return false;
        }
        for (int i = 0; i < s.Length; i++)
        {
            if (d[i] != (byte)s[i])
            {
                return false;
            }
        }
        return true;
    }

    static bool IsNum(object? v)
    {
        return v is long || v is double || v is bool;
    }

    static double Num(object? v)
    {
        if (v is long l)
        {
            return l;
        }
        if (v is double d)
        {
            return d;
        }
        if (v is bool b)
        {
            if (b)
            {
                return 1;
            }
            return 0;
        }
        throw new InvalidDataException("FBX value is not a number");
    }

    public static string Str(object? v)
    {
        if (v is string s)
        {
            return s;
        }
        if (v is double d)
        {
            return d.ToString("R", CultureInfo.InvariantCulture);
        }
        if (v == null)
        {
            return "";
        }
        return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
    }

    // fbx_name: "Model::name" or "name\x00\x01Model"
    public static string FbxName(object? v)
    {
        string s = Str(v);
        int z = s.IndexOf("\x00\x01", StringComparison.Ordinal);
        if (z >= 0)
        {
            return s.Substring(0, z);
        }
        int c = s.IndexOf("::", StringComparison.Ordinal);
        if (c >= 0)
        {
            return s.Substring(c + 2);
        }
        return s;
    }

    // ---- fbx_binary ---------------------------------------------------------------

    static int ElemSize(char t)
    {
        if (t == 'f' || t == 'i')
        {
            return 4;
        }
        if (t == 'd' || t == 'l')
        {
            return 8;
        }
        return 1; // 'b'
    }

    static double[] DecodeArray(byte[] raw, char t)
    {
        int size = ElemSize(t);
        int n = raw.Length / size;
        double[] v = new double[n];
        for (int i = 0; i < n; i++)
        {
            int o = i * size;
            if (t == 'f')
            {
                v[i] = BitConverter.ToSingle(raw, o);
            }
            else if (t == 'd')
            {
                v[i] = BitConverter.ToDouble(raw, o);
            }
            else if (t == 'l')
            {
                v[i] = BitConverter.ToInt64(raw, o);
            }
            else if (t == 'i')
            {
                v[i] = BitConverter.ToInt32(raw, o);
            }
            else
            {
                v[i] = raw[o];
            }
        }
        return v;
    }

    static byte[] Inflate(byte[] raw, int expect)
    {
        using MemoryStream src = new MemoryStream(raw);
        using ZLibStream z = new ZLibStream(src, CompressionMode.Decompress);
        int cap = expect;
        if (cap <= 0)
        {
            cap = 1024;
        }
        using MemoryStream dst = new MemoryStream(cap);
        z.CopyTo(dst);
        return dst.ToArray();
    }

    static FbxNode? BinRead(byte[] d, bool wide, int off, out int next)
    {
        int start = off;
        long end;
        long count;
        if (wide)
        {
            end = BitConverter.ToInt64(d, off);
            count = BitConverter.ToInt64(d, off + 8);
            off += 24;
        }
        else
        {
            end = BitConverter.ToUInt32(d, off);
            count = BitConverter.ToUInt32(d, off + 4);
            off += 12;
        }
        if (end == 0)
        {
            if (wide)
            {
                next = start + 25;
            }
            else
            {
                next = start + 13;
            }
            return null;
        }
        int ln = d[off];
        string name = Encoding.Latin1.GetString(d, off + 1, ln);
        off += 1 + ln;
        List<object> props = new List<object>();
        for (long k = 0; k < count; k++)
        {
            char t = (char)d[off];
            off++;
            if (t == 'Y')
            {
                props.Add((long)BitConverter.ToInt16(d, off));
                off += 2;
            }
            else if (t == 'C')
            {
                props.Add(d[off] != 0);
                off += 1;
            }
            else if (t == 'I')
            {
                props.Add((long)BitConverter.ToInt32(d, off));
                off += 4;
            }
            else if (t == 'F')
            {
                props.Add((double)BitConverter.ToSingle(d, off));
                off += 4;
            }
            else if (t == 'D')
            {
                props.Add(BitConverter.ToDouble(d, off));
                off += 8;
            }
            else if (t == 'L')
            {
                props.Add(BitConverter.ToInt64(d, off));
                off += 8;
            }
            else if (t == 'f' || t == 'd' || t == 'l' || t == 'i' || t == 'b')
            {
                int n = (int)BitConverter.ToUInt32(d, off);
                int enc = (int)BitConverter.ToUInt32(d, off + 4);
                int clen = (int)BitConverter.ToUInt32(d, off + 8);
                off += 12;
                byte[] raw = new byte[clen];
                Array.Copy(d, off, raw, 0, clen);
                off += clen;
                if (enc != 0)
                {
                    raw = Inflate(raw, n * ElemSize(t));
                }
                props.Add(new FbxArray(DecodeArray(raw, t)));
            }
            else if (t == 'S' || t == 'R')
            {
                int n = (int)BitConverter.ToUInt32(d, off);
                if (t == 'S')
                {
                    props.Add(Encoding.UTF8.GetString(d, off + 4, n));
                }
                else
                {
                    byte[] raw = new byte[n];
                    Array.Copy(d, off + 4, raw, 0, n);
                    props.Add(raw);
                }
                off += 4 + n;
            }
            else
            {
                throw new InvalidDataException("FBX property type '" + t + "' at 0x" + (off - 1).ToString("x"));
            }
        }
        FbxNode node = new FbxNode(name, props);
        while (off < end)
        {
            FbxNode? child = BinRead(d, wide, off, out off);
            if (child == null)
            {
                break;
            }
            node.Children.Add(child);
        }
        next = (int)end;
        return node;
    }

    public static FbxNode ReadBinary(byte[] d)
    {
        uint ver = BitConverter.ToUInt32(d, 23);
        bool wide = ver >= 7500;
        FbxNode root = new FbxNode("", new List<object>());
        int off = 27;
        while (off < d.Length)
        {
            FbxNode? child = BinRead(d, wide, off, out off);
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
        FbxNode? cur = null;
        foreach (Match m in FbxToken.Matches(text))
        {
            if (m.Groups["ws"].Success || m.Groups["cm"].Success || m.Groups["other"].Success)
            {
                continue;
            }
            if (m.Groups["key"].Success)
            {
                cur = new FbxNode(m.Groups["key"].Value, new List<object>());
                stack[stack.Count - 1].Children.Add(cur);
                continue;
            }
            if (m.Groups["punct"].Success)
            {
                string p = m.Groups["punct"].Value;
                if (p == "{")
                {
                    if (cur != null)
                    {
                        stack.Add(cur);
                    }
                    cur = null;
                }
                else if (p == "}")
                {
                    if (stack.Count > 1)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }
                    cur = null;
                }
                continue;
            }
            // A value outside any key belongs to the block it sits in: some exporters write
            // arrays as "Materials: *N { 0,0,1 }" without the usual "a:" key.
            FbxNode? owner = cur;
            if (owner == null && stack.Count > 1)
            {
                owner = stack[stack.Count - 1];
            }
            if (owner == null)
            {
                continue;
            }
            if (m.Groups["num"].Success)
            {
                string s = m.Groups["num"].Value;
                if (s.Contains('.') || s.Contains('e') || s.Contains('E'))
                {
                    owner.Props.Add(double.Parse(s, CultureInfo.InvariantCulture));
                }
                else
                {
                    owner.Props.Add(long.Parse(s, CultureInfo.InvariantCulture));
                }
            }
            else if (m.Groups["str"].Success)
            {
                owner.Props.Add(m.Groups["str"].Value);
            }
            else if (m.Groups["star"].Success)
            {
                owner.Props.Add(m.Groups["star"].Value);
            }
            else
            {
                owner.Props.Add(m.Groups["word"].Value);
            }
        }
        return root;
    }

    // ---- fbx_array / fbx_props ----------------------------------------------------

    static readonly double[] NoDoubles = new double[0];

    public static double[] Array_(FbxNode? node)
    {
        if (node == null)
        {
            return NoDoubles;
        }
        if (node.Props.Count > 0 && node.Props[0] is FbxArray fa)
        {
            return fa.V;
        }
        List<object> vals;
        FbxNode? a = node.Find("a");
        if (a != null)
        {
            vals = a.Props;
        }
        else
        {
            vals = new List<object>();
            foreach (object p in node.Props)
            {
                if (p is string s && s.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }
                vals.Add(p);
            }
        }
        double[] v = new double[vals.Count];
        for (int i = 0; i < vals.Count; i++)
        {
            v[i] = Num(vals[i]);
        }
        return v;
    }

    // fbx_array(node, np.int64): numpy's astype truncates towards zero
    public static long[] ArrayInt(FbxNode? node)
    {
        double[] v = Array_(node);
        long[] o = new long[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            o[i] = (long)v[i];
        }
        return o;
    }

    public static Dictionary<string, List<object>> Props(FbxNode? node)
    {
        Dictionary<string, List<object>> out_ = new Dictionary<string, List<object>>();
        if (node == null)
        {
            return out_;
        }
        FbxNode? p70 = node.Find("Properties70");
        if (p70 != null)
        {
            foreach (FbxNode p in p70.FindAll("P"))
            {
                if (p.Props.Count == 0)
                {
                    continue;
                }
                out_[Str(p.Props[0])] = Slice(p.Props, 4);
            }
        }
        FbxNode? p60 = node.Find("Properties60");
        if (p60 != null)
        {
            foreach (FbxNode p in p60.FindAll("Property"))
            {
                if (p.Props.Count == 0)
                {
                    continue;
                }
                out_[Str(p.Props[0])] = Slice(p.Props, 3);
            }
        }
        return out_;
    }

    static List<object> Slice(List<object> props, int from)
    {
        List<object> out_ = new List<object>();
        for (int i = from; i < props.Count; i++)
        {
            out_.Add(props[i]);
        }
        return out_;
    }

    static List<object>? Get(Dictionary<string, List<object>> p, string name)
    {
        List<object>? v;
        if (p.TryGetValue(name, out v) && v.Count > 0)
        {
            return v;
        }
        return null;
    }

    // vec3: the property's first three numbers, or the default
    static double[] Vec3(Dictionary<string, List<object>> p, string name, double dx, double dy, double dz)
    {
        List<object>? v = Get(p, name);
        if (v != null && v.Count >= 3)
        {
            return new double[] { Num(v[0]), Num(v[1]), Num(v[2]) };
        }
        return new double[] { dx, dy, dz };
    }

    // ---- fbx_local / fbx_matrix ---------------------------------------------------

    // FBX node transform T Roff Rp Rpre R Rpost^-1 Rp^-1 Soff Sp S Sp^-1, for frames of
    // translations, Euler rotations and scales (3 numbers each) -> one matrix a frame.
    static Mat4[] LocalMany(Dictionary<string, List<object>> p, double[] t, double[] r, double[] s, int frames)
    {
        int ord = 0;
        List<object>? ro = Get(p, "RotationOrder");
        if (ro != null)
        {
            ord = (int)Num(ro[0]);
        }
        ord = ord % 6;
        if (ord < 0)
        {
            ord += 6;
        }
        string order = EulerOrders[ord];
        double[] rp = Vec3(p, "RotationPivot", 0, 0, 0);
        double[] sp = Vec3(p, "ScalingPivot", 0, 0, 0);
        double[] pr = Vec3(p, "PreRotation", 0, 0, 0);
        double[] po = Vec3(p, "PostRotation", 0, 0, 0);
        Mat4 pre = Mat4.Euler(pr[0], pr[1], pr[2]);
        Mat4 post = Mat4.Euler(po[0], po[1], po[2]);
        double[] roff = Vec3(p, "RotationOffset", 0, 0, 0);
        double[] soff = Vec3(p, "ScalingOffset", 0, 0, 0);
        Mat4 head = Mat4.Translation(roff[0], roff[1], roff[2]) * Mat4.Translation(rp[0], rp[1], rp[2]) * pre;
        Mat4 mid = post.Inverse() * Mat4.Translation(-rp[0], -rp[1], -rp[2])
            * Mat4.Translation(soff[0], soff[1], soff[2]) * Mat4.Translation(sp[0], sp[1], sp[2]);
        Mat4 negSp = Mat4.Translation(-sp[0], -sp[1], -sp[2]);
        Mat4[] out_ = new Mat4[frames];
        for (int i = 0; i < frames; i++)
        {
            Mat4 tm = Mat4.Translation(t[i * 3], t[i * 3 + 1], t[i * 3 + 2]);
            Mat4 sm = Mat4.Scale(s[i * 3], s[i * 3 + 1], s[i * 3 + 2]);
            Mat4 rot = Mat4.Euler(r[i * 3], r[i * 3 + 1], r[i * 3 + 2], order);
            out_[i] = tm * head * rot * mid * sm * negSp;
        }
        return out_;
    }

    // The node's default (Lcl) transform.
    static Mat4 Local(Dictionary<string, List<object>> p)
    {
        return LocalMany(p, Vec3(p, "Lcl Translation", 0, 0, 0), Vec3(p, "Lcl Rotation", 0, 0, 0),
            Vec3(p, "Lcl Scaling", 1, 1, 1), 1)[0];
    }

    static Mat4? Matrix(FbxNode? node)
    {
        double[] a = Array_(node);
        if (a.Length != 16)
        {
            return null;
        }
        Mat4 m = new Mat4();
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                m[r, c] = a[c * 4 + r]; // reshape(4, 4).T
            }
        }
        return m;
    }

    static int Clip(long v, int lo, int hi)
    {
        if (v < lo)
        {
            return lo;
        }
        if (v > hi)
        {
            return hi;
        }
        return (int)v;
    }

    // np.interp: linear between the keys, flat outside them
    static double Interp(double x, double[] xp, double[] fp)
    {
        int n = xp.Length;
        if (x < xp[0])
        {
            return fp[0];
        }
        if (x >= xp[n - 1])
        {
            return fp[n - 1];
        }
        int j = 0;
        int lo = 0;
        int hi = n - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (xp[mid] <= x)
            {
                j = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        if (j > n - 2)
        {
            j = n - 2;
        }
        double slope = (fp[j + 1] - fp[j]) / (xp[j + 1] - xp[j]);
        return slope * (x - xp[j]) + fp[j];
    }

    static void Fbx6Keys(List<object> vals, List<double> ts, List<double> vs)
    {
        // (time, value) pairs from an FBX 6 Key list: time, value, then an interpolation
        // letter and its tangent data; a key starts at a number following a letter run's data
        // once a large integer time turns up.
        int i = 0;
        int n = vals.Count;
        while (i + 1 < n)
        {
            object t = vals[i];
            object v = vals[i + 1];
            if (!IsNum(t) || !IsNum(v))
            {
                i += 1;
                continue;
            }
            ts.Add(Num(t));
            vs.Add(Num(v));
            i += 2;
            if (i < n && vals[i] is string kind)
            {
                i += 1;
                if (kind == "C")
                {
                    // constant: one letter of its own
                    if (i < n && vals[i] is string)
                    {
                        i += 1;
                    }
                }
                else if (kind == "U")
                {
                    // cubic: a letter, then 2 or 4 numbers
                    string mode = "";
                    if (i < n && vals[i] is string ms)
                    {
                        mode = ms;
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

    static string ChannelOf(string prop)
    {
        if (prop == "Lcl Translation")
        {
            return "T";
        }
        if (prop == "Lcl Rotation")
        {
            return "R";
        }
        if (prop == "Lcl Scaling")
        {
            return "S";
        }
        return "";
    }

    // ---- read_fbx -----------------------------------------------------------------

    public static Scene Read(string path, Action<string> warn)
    {
        FbxLoad load = new FbxLoad();
        load.Warn = warn;
        load.F = new Fbx(path);
        load.Sc = new Scene();
        load.Images = new SourceImages(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", warn);
        return load.Run();
    }

    // read_fbx and its closures: the file, the scene being filled and the node of each Model.
    sealed class FbxLoad
    {
        public Fbx F = null!;
        public Scene Sc = null!;
        public SourceImages Images = null!;
        public Action<string> Warn = null!;
        public List<object> Models = new List<object>();
        public Dictionary<object, int> Index = new Dictionary<object, int>();
        public Dictionary<object, Dictionary<string, List<object>>> NodeProps = new Dictionary<object, Dictionary<string, List<object>>>();
        public Dictionary<object, int> MatIndex = new Dictionary<object, int>();
        public Dictionary<object, int> TexIndex = new Dictionary<object, int>();

        static readonly string[] FileKeys = { "RelativeFilename", "FileName", "Filename" };
        static readonly string[] PathKeys = { "RelativeFilename", "FileName", "Path" };

        int Ax(Dictionary<string, List<object>> gp, string name, int def)
        {
            List<object>? v = Get(gp, name);
            if (v != null)
            {
                return (int)Num(v[0]);
            }
            return def;
        }

        public Scene Run()
        {
            Dictionary<string, List<object>> gp = F.GlobalSettings();
            double[] e = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            int ca = Ax(gp, "CoordAxis", 0);
            int cas = Ax(gp, "CoordAxisSign", 1);
            int ua = Ax(gp, "UpAxis", 1);
            int uas = Ax(gp, "UpAxisSign", 1);
            int fa = Ax(gp, "FrontAxis", 2);
            int fas = Ax(gp, "FrontAxisSign", 1);
            Mat4 axis = Mat4.Identity();
            for (int c = 0; c < 3; c++)
            {
                axis[0, c] = e[ca * 3 + c] * cas;
                axis[1, c] = e[ua * 3 + c] * uas;
                axis[2, c] = e[fa * 3 + c] * fas;
            }
            if (axis.Det3() < 0)
            {
                for (int c = 0; c < 3; c++)
                {
                    axis[0, c] = -axis[0, c];
                }
            }
            Sc.Axis = axis;
            double unit = 1.0;
            List<object>? us = Get(gp, "UnitScaleFactor");
            if (us != null)
            {
                unit = Num(us[0]);
            }
            Sc.Unit = unit / 100.0;
            // The exporter is named at the top level (binary) or in FBXHeaderExtension (ASCII);
            // Unity Studio writes UVs top-left, Blender and the SDK bottom-left
            object? creator = F.Root.Value("Creator");
            FbxNode? head = F.Root.Find("FBXHeaderExtension");
            if (!(creator is string) && head != null)
            {
                creator = head.Value("Creator");
            }
            if (creator is string cs && cs.Contains("Unity Studio"))
            {
                Sc.UvTopLeft = true;
            }

            foreach (object o in F.Ids)
            {
                if (F.Kind[o] == "Model")
                {
                    Models.Add(o);
                }
            }
            foreach (object o in Models)
            {
                Index[o] = Sc.AddNode(F.Names[o]);
                NodeProps[o] = Props(F.Objs[o]);
                Sc.Nodes[Index[o]].Local = Local(NodeProps[o]);
            }
            foreach (object o in Models)
            {
                foreach (FbxConn c in F.ParentsOf(o))
                {
                    if (Index.ContainsKey(c.Id))
                    {
                        Sc.Nodes[Index[o]].Parent = Index[c.Id];
                        break;
                    }
                }
            }

            foreach (object o in Models)
            {
                List<object> geoms = new List<object>();
                foreach (FbxConn c in F.ChildrenOf(o, "Geometry"))
                {
                    geoms.Add(c.Id);
                }
                if (geoms.Count == 0 && F.Objs[o].Find("Vertices") != null)
                {
                    geoms.Add(o); // FBX 6: the geometry lives on the model
                }
                if (geoms.Count == 0)
                {
                    continue;
                }
                Dictionary<string, List<object>> p = NodeProps[o];
                double[] gt = Vec3(p, "GeometricTranslation", 0, 0, 0);
                double[] gr = Vec3(p, "GeometricRotation", 0, 0, 0);
                double[] gs = Vec3(p, "GeometricScaling", 1, 1, 1);
                Mat4 geo = Mat4.Translation(gt[0], gt[1], gt[2]) * Mat4.Euler(gr[0], gr[1], gr[2])
                    * Mat4.Scale(gs[0], gs[1], gs[2]);
                List<object> modelTexs = new List<object>();
                foreach (FbxConn c in F.ChildrenOf(o, "Texture"))
                {
                    modelTexs.Add(c.Id);
                }
                List<int> mats = new List<int>();
                foreach (FbxConn c in F.ChildrenOf(o, "Material"))
                {
                    mats.Add(Material(c.Id, modelTexs));
                }
                foreach (object g in geoms)
                {
                    SceneMesh? mesh = Geometry(g, o, geo, mats, modelTexs);
                    if (mesh != null)
                    {
                        Sc.Meshes.Add(mesh);
                    }
                }
            }

            Anims();
            return Sc;
        }

        SKBitmap? TextureImage(object tid)
        {
            FbxNode t = F.Objs[tid];
            List<string> names = new List<string>();
            byte[]? data = null;
            foreach (string k in FileKeys)
            {
                if (t.Value(k) is string s)
                {
                    names.Add(s);
                }
            }
            Dictionary<string, List<object>> tp = Props(t);
            foreach (string k in PathKeys)
            {
                List<object>? v = Get(tp, k);
                if (v != null && v[0] is string s)
                {
                    names.Add(s);
                }
            }
            foreach (FbxConn vc in F.ChildrenOf(tid, "Video"))
            {
                FbxNode vn = F.Objs[vc.Id];
                foreach (string k in FileKeys)
                {
                    if (vn.Value(k) is string s)
                    {
                        names.Add(s);
                    }
                }
                FbxNode? c = vn.Find("Content");
                if (c != null && c.Props.Count > 0)
                {
                    if (c.Props[0] is byte[] raw)
                    {
                        data = raw;
                    }
                    else
                    {
                        StringBuilder sb = new StringBuilder();
                        foreach (object p in c.Props)
                        {
                            if (p is string s && !s.StartsWith("*", StringComparison.Ordinal))
                            {
                                sb.Append(s);
                            }
                        }
                        FbxNode? a = c.Find("a");
                        if (a != null)
                        {
                            foreach (object p in a.Props)
                            {
                                if (p is string s)
                                {
                                    sb.Append(s);
                                }
                            }
                        }
                        try
                        {
                            data = Convert.FromBase64String(sb.ToString());
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

        int Material(object mid, List<object> texturesFallback)
        {
            int have;
            if (MatIndex.TryGetValue(mid, out have))
            {
                return have;
            }
            Dictionary<string, List<object>> p = Props(F.Objs[mid]);
            List<object>? col = Get(p, "DiffuseColor");
            if (col == null)
            {
                col = Get(p, "Diffuse");
            }
            double[] color = { 1.0, 1.0, 1.0, 1.0 };
            if (col != null)
            {
                for (int k = 0; k < 3 && k < col.Count; k++)
                {
                    color[k] = Num(col[k]);
                }
            }
            List<FbxConn> texs = F.ChildrenOf(mid, "Texture");
            object? tid = null;
            foreach (FbxConn c in texs)
            {
                if (c.Prop == "DiffuseColor" || c.Prop == "Diffuse")
                {
                    tid = c.Id;
                    break;
                }
            }
            if (tid == null && texs.Count > 0)
            {
                tid = texs[0].Id;
            }
            if (tid == null && texturesFallback.Count > 0)
            {
                tid = texturesFallback[0];
            }
            SKBitmap? image = null;
            if (tid != null)
            {
                image = TextureImage(tid);
            }
            MatIndex[mid] = Sc.AddMaterial(F.Names[mid], color, image);
            return MatIndex[mid];
        }

        int TextureMaterial(object tid)
        {
            int have;
            if (!TexIndex.TryGetValue(tid, out have))
            {
                have = Sc.AddMaterial(F.Names[tid], new double[] { 1.0, 1.0, 1.0, 1.0 }, TextureImage(tid));
                TexIndex[tid] = have;
            }
            return have;
        }

        // read_fbx_geometry
        SceneMesh? Geometry(object g, object model, Mat4 geo, List<int> mats, List<object> modelTexs)
        {
            FbxNode gn = F.Objs[g];
            double[] cp = Array_(gn.Find("Vertices"));
            long[] pvi = ArrayInt(gn.Find("PolygonVertexIndex"));
            int ncp = cp.Length / 3;
            if (ncp == 0 || pvi.Length == 0)
            {
                return null;
            }
            int nc = pvi.Length;
            int[] cornerCp = new int[nc];
            List<int> starts = new List<int>();
            List<int> sizes = new List<int>();
            int start = 0;
            for (int i = 0; i < nc; i++)
            {
                if (pvi[i] < 0)
                {
                    cornerCp[i] = (int)(~pvi[i]);
                    starts.Add(start);
                    sizes.Add(i - start + 1);
                    start = i + 1;
                }
                else
                {
                    cornerCp[i] = (int)pvi[i];
                }
            }
            int[] polyOfCorner = new int[nc];
            int at = 0;
            for (int p = 0; p < sizes.Count; p++)
            {
                for (int k = 0; k < sizes[p]; k++)
                {
                    polyOfCorner[at] = p;
                    at++;
                }
            }
            int[] triCorners = MeshBuild.Fan(starts.ToArray(), sizes.ToArray());
            int ntri = triCorners.Length / 3;
            int[] triPoly = new int[ntri];
            for (int t = 0; t < ntri; t++)
            {
                triPoly[t] = polyOfCorner[triCorners[t * 3]];
            }

            double[] cpw = new double[ncp * 3];
            for (int i = 0; i < ncp; i++)
            {
                double ox;
                double oy;
                double oz;
                geo.TransformPoint(cp[i * 3], cp[i * 3 + 1], cp[i * 3 + 2], out ox, out oy, out oz);
                cpw[i * 3] = ox;
                cpw[i * 3 + 1] = oy;
                cpw[i * 3 + 2] = oz;
            }
            cp = cpw;
            double[]? nrm = Layer(gn, cornerCp, polyOfCorner, "LayerElementNormal", "Normals", "NormalsIndex", 3);
            if (nrm != null)
            {
                TransformDirs(nrm, geo);
            }
            double[]? uv = Layer(gn, cornerCp, polyOfCorner, "LayerElementUV", "UV", "UVIndex", 2);

            // per-triangle material: LayerElementMaterial, or FBX 6's per-polygon TextureId
            long[] pick = new long[ntri];
            FbxNode? lm = gn.Find("LayerElementMaterial");
            if (lm != null)
            {
                long[] m = ArrayInt(lm.Find("Materials"));
                if (m.Length > 0 && lm.ValueStr("MappingInformationType", "AllSame") == "ByPolygon")
                {
                    for (int t = 0; t < ntri; t++)
                    {
                        pick[t] = m[Clip(triPoly[t], 0, m.Length - 1)];
                    }
                }
                else if (m.Length > 0)
                {
                    for (int t = 0; t < ntri; t++)
                    {
                        pick[t] = m[0];
                    }
                }
            }
            List<int> use = mats;
            if (use.Count == 0)
            {
                use = new List<int>();
                use.Add(Sc.AddMaterial("default", new double[] { 1.0, 1.0, 1.0, 1.0 }, null));
            }
            int[] triMat = new int[ntri];
            for (int t = 0; t < ntri; t++)
            {
                triMat[t] = use[Clip(pick[t], 0, use.Count - 1)];
            }
            FbxNode? lt = gn.Find("LayerElementTexture");
            if (lt != null && modelTexs.Count > 1 && lt.ValueStr("MappingInformationType", "") == "ByPolygon")
            {
                long[] tid = ArrayInt(lt.Find("TextureId"));
                if (tid.Length > 0)
                {
                    // FBX 6 picks the texture per polygon: one material per texture
                    List<int> per = new List<int>();
                    foreach (object t0 in modelTexs)
                    {
                        per.Add(TextureMaterial(t0));
                    }
                    for (int t = 0; t < ntri; t++)
                    {
                        int k = Clip(tid[Clip(triPoly[t], 0, tid.Length - 1)], 0, per.Count - 1);
                        triMat[t] = per[k];
                    }
                }
            }

            SceneMesh mesh = new SceneMesh(F.Names[model], Index[model]);
            List<FbxConn> skins = F.ChildrenOf(g, "Deformer", "Skin");
            List<Influence>[]? cpInfl = null;
            if (skins.Count > 0)
            {
                List<FbxCluster> clusters = new List<FbxCluster>();
                foreach (FbxConn cl in F.ChildrenOf(skins[0].Id, "Deformer", "Cluster"))
                {
                    FbxNode cn = F.Objs[cl.Id];
                    List<FbxConn> bones = F.ChildrenOf(cl.Id, "Model");
                    if (bones.Count == 0)
                    {
                        continue;
                    }
                    Mat4? t = Matrix(cn.Find("Transform"));
                    if (t == null)
                    {
                        t = Mat4.Identity();
                    }
                    FbxCluster c = new FbxCluster();
                    c.Joint = Index[bones[0].Id];
                    c.T = t;
                    c.Link = Matrix(cn.Find("TransformLink"));
                    c.Idx = ArrayInt(cn.Find("Indexes"));
                    c.W = Array_(cn.Find("Weights"));
                    clusters.Add(c);
                }
                // Two conventions turn up. The FBX SDK's: every cluster of a mesh holds the mesh's
                // bind matrix in Transform and its bone's bind matrix in TransformLink. Ripping
                // tools' (DoDModels, MMD and HSD converters): Transform is the bone's inverse bind
                // matrix in mesh space, and TransformLink is missing, its inverse, or in some other
                // space.
                bool sdk = true;
                foreach (FbxCluster c in clusters)
                {
                    if (c.Link == null)
                    {
                        sdk = false;
                    }
                }
                if (sdk)
                {
                    foreach (FbxCluster c in clusters)
                    {
                        if (!Mat4.AllClose(c.T, clusters[0].T, 1e-4))
                        {
                            sdk = false;
                        }
                    }
                }
                foreach (FbxCluster c in clusters)
                {
                    if (c.Link != null && Mat4.AllClose(c.T * c.Link, Mat4.Identity(), 1e-3)
                        && !Mat4.AllClose(c.T, Mat4.Identity(), 1e-4))
                    {
                        sdk = false;
                    }
                }
                Mat4 meshBind;
                if (sdk && clusters.Count > 0)
                {
                    meshBind = clusters[0].T;
                }
                else
                {
                    meshBind = Sc.NodeWorld(Index[model]);
                }
                cpInfl = new List<Influence>[ncp];
                for (int i = 0; i < ncp; i++)
                {
                    cpInfl[i] = new List<Influence>();
                }
                foreach (FbxCluster c in clusters)
                {
                    if (Sc.Nodes[c.Joint].Bind == null)
                    {
                        if (sdk)
                        {
                            Sc.Nodes[c.Joint].Bind = c.Link;
                        }
                        else
                        {
                            Sc.Nodes[c.Joint].Bind = meshBind * c.T.Inverse();
                        }
                    }
                    int pairs = Math.Min(c.Idx.Length, c.W.Length);
                    for (int k = 0; k < pairs; k++)
                    {
                        long i = c.Idx[k];
                        double wt = c.W[k];
                        if (wt > 0 && i >= 0 && i < ncp)
                        {
                            Accumulate(cpInfl[(int)i], c.Joint, wt);
                        }
                    }
                }
                double[] cpb = new double[ncp * 3];
                for (int i = 0; i < ncp; i++)
                {
                    double ox;
                    double oy;
                    double oz;
                    meshBind.TransformPoint(cp[i * 3], cp[i * 3 + 1], cp[i * 3 + 2], out ox, out oy, out oz);
                    cpb[i * 3] = ox;
                    cpb[i * 3 + 1] = oy;
                    cpb[i * 3 + 2] = oz;
                }
                cp = cpb;
                if (nrm != null)
                {
                    TransformDirs(nrm, meshBind);
                }
                bool loose = false;
                for (int i = 0; i < ncp; i++)
                {
                    if (cpInfl[i].Count == 0)
                    {
                        loose = true;
                        break;
                    }
                }
                if (loose)
                {
                    // unweighted vertices ride the mesh's own node
                    int me = Index[model];
                    if (Sc.Nodes[me].Bind == null)
                    {
                        Sc.Nodes[me].Bind = meshBind;
                    }
                    for (int i = 0; i < ncp; i++)
                    {
                        if (cpInfl[i].Count == 0)
                        {
                            cpInfl[i].Add(new Influence(me, 1.0));
                        }
                    }
                }
            }
            MeshBuild.Build(mesh, cp, cornerCp, nrm, uv, triCorners, triMat, cpInfl);
            return mesh;
        }

        static void Accumulate(List<Influence> list, int joint, double wt)
        {
            foreach (Influence f in list)
            {
                if (f.Joint == joint)
                {
                    f.Weight += wt;
                    return;
                }
            }
            list.Add(new Influence(joint, wt));
        }

        static void TransformDirs(double[] v, Mat4 m)
        {
            int n = v.Length / 3;
            for (int i = 0; i < n; i++)
            {
                double ox;
                double oy;
                double oz;
                m.TransformDir(v[i * 3], v[i * 3 + 1], v[i * 3 + 2], out ox, out oy, out oz);
                v[i * 3] = ox;
                v[i * 3 + 1] = oy;
                v[i * 3 + 2] = oz;
            }
        }

        // One LayerElement* read out per corner, or null.
        double[]? Layer(FbxNode gn, int[] cornerCp, int[] polyOfCorner, string elemName, string dataName,
            string indexName, int ncomp)
        {
            FbxNode? el = gn.Find(elemName);
            if (el == null)
            {
                return null;
            }
            int nc = cornerCp.Length;
            string mapping = el.ValueStr("MappingInformationType", "ByPolygonVertex");
            string refr = el.ValueStr("ReferenceInformationType", "Direct");
            double[] vals = Array_(el.Find(dataName));
            int nvals = vals.Length / ncomp;
            int[] sel = new int[nc];
            if (mapping == "ByVertice" || mapping == "ByVertex" || mapping == "ByControlPoint")
            {
                Array.Copy(cornerCp, sel, nc);
            }
            else if (mapping == "ByPolygon")
            {
                Array.Copy(polyOfCorner, sel, nc);
            }
            else if (mapping == "AllSame")
            {
                // zeros
            }
            else
            {
                for (int i = 0; i < nc; i++)
                {
                    sel[i] = i;
                }
            }
            if (nvals == 0)
            {
                // (the Python checks this after the index lookup; the result is the same)
                return null;
            }
            if (refr != "Direct" && el.Find(indexName) != null)
            {
                long[] ix = ArrayInt(el.Find(indexName));
                for (int i = 0; i < nc; i++)
                {
                    sel[i] = Clip(ix[sel[i]], 0, nvals - 1);
                }
            }
            double[] out_ = new double[nc * ncomp];
            for (int i = 0; i < nc; i++)
            {
                int s = Clip(sel[i], 0, nvals - 1);
                for (int k = 0; k < ncomp; k++)
                {
                    out_[i * ncomp + k] = vals[s * ncomp + k];
                }
            }
            return out_;
        }

        static FbxNodeCurves CurvesFor(List<FbxNodeCurves> curves, object node)
        {
            foreach (FbxNodeCurves c in curves)
            {
                if (c.Node.Equals(node))
                {
                    return c;
                }
            }
            FbxNodeCurves fresh = new FbxNodeCurves(node);
            curves.Add(fresh);
            return fresh;
        }

        // read_fbx_anims
        void Anims()
        {
            List<object> stacks = new List<object>();
            foreach (object o in F.Ids)
            {
                if (F.Kind[o] == "AnimationStack")
                {
                    stacks.Add(o);
                }
            }
            foreach (object st in stacks)
            {
                List<FbxNodeCurves> curves = new List<FbxNodeCurves>(); // node -> channel -> 3 curves
                bool have = false;
                double lo = 0;
                double hi = 0;
                foreach (FbxConn layer in F.ChildrenOf(st, "AnimationLayer"))
                {
                    foreach (FbxConn cn in F.ChildrenOf(layer.Id, "AnimationCurveNode"))
                    {
                        FbxConn? target = null;
                        foreach (FbxConn pc in F.ParentsOf(cn.Id))
                        {
                            if (Index.ContainsKey(pc.Id))
                            {
                                target = pc;
                                break;
                            }
                        }
                        if (target == null)
                        {
                            continue;
                        }
                        string ch = ChannelOf(target.Prop);
                        if (ch.Length == 0)
                        {
                            continue;
                        }
                        FbxNodeCurves nc = CurvesFor(curves, target.Id);
                        if (nc.Find(ch) != null)
                        {
                            continue;
                        }
                        FbxChannel chan = new FbxChannel(ch);
                        for (int a = 0; a < 3; a++)
                        {
                            string want = "d|" + "XYZ"[a];
                            object? cv = null;
                            foreach (FbxConn c in F.ChildrenOf(cn.Id, "AnimationCurve"))
                            {
                                if (c.Prop == want)
                                {
                                    cv = c.Id;
                                    break;
                                }
                            }
                            if (cv == null)
                            {
                                continue;
                            }
                            long[] ticks = ArrayInt(F.Objs[cv].Find("KeyTime"));
                            double[] kt = new double[ticks.Length];
                            for (int k = 0; k < ticks.Length; k++)
                            {
                                kt[k] = ticks[k] / FbxTicks;
                            }
                            double[] kv = Array_(F.Objs[cv].Find("KeyValueFloat"));
                            if (kt.Length == 0 || kt.Length != kv.Length)
                            {
                                continue;
                            }
                            chan.Comps[a] = new FbxCurve(kt, kv);
                            if (!have)
                            {
                                lo = kt[0];
                                hi = kt[kt.Length - 1];
                                have = true;
                            }
                            else
                            {
                                lo = Math.Min(lo, kt[0]);
                                hi = Math.Max(hi, kt[kt.Length - 1]);
                            }
                        }
                        nc.Chans.Add(chan);
                    }
                }
                if (curves.Count == 0 || !have)
                {
                    continue;
                }
                Dictionary<string, List<object>> sp = Props(F.Objs[st]);
                List<object>? ls = Get(sp, "LocalStart");
                List<object>? lp = Get(sp, "LocalStop");
                if (ls != null && lp != null && Num(lp[0]) > Num(ls[0]))
                {
                    lo = Num(ls[0]) / FbxTicks;
                    hi = Num(lp[0]) / FbxTicks;
                }
                Sc.Anims.Add(Curves(F.Names[st], curves, lo, hi));
            }
            if (stacks.Count == 0)
            {
                Fbx6Takes();
            }
        }

        // read_curves
        SceneAnim Curves(string name, List<FbxNodeCurves> curves, double lo, double hi)
        {
            int frames = (int)Math.Round((hi - lo) * Scene.Fps, MidpointRounding.ToEven) + 1;
            double[] t = new double[frames];
            for (int i = 0; i < frames; i++)
            {
                t[i] = lo + (double)i / Scene.Fps;
            }
            SceneAnim anim = new SceneAnim(name, frames);
            foreach (FbxNodeCurves nc in curves)
            {
                Dictionary<string, List<object>> p = NodeProps[nc.Node];
                double[] vt = Tile(Vec3(p, "Lcl Translation", 0, 0, 0), frames);
                double[] vr = Tile(Vec3(p, "Lcl Rotation", 0, 0, 0), frames);
                double[] vs = Tile(Vec3(p, "Lcl Scaling", 1, 1, 1), frames);
                foreach (FbxChannel chan in nc.Chans)
                {
                    double[] v;
                    if (chan.Ch == "T")
                    {
                        v = vt;
                    }
                    else if (chan.Ch == "R")
                    {
                        v = vr;
                    }
                    else
                    {
                        v = vs;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        FbxCurve? c = chan.Comps[k];
                        if (c == null)
                        {
                            continue;
                        }
                        for (int i = 0; i < frames; i++)
                        {
                            v[i * 3 + k] = Interp(t[i], c.Times, c.Values);
                        }
                    }
                }
                anim.Tracks[Index[nc.Node]] = LocalMany(p, vt, vr, vs, frames);
            }
            return anim;
        }

        static double[] Tile(double[] v3, int frames)
        {
            double[] o = new double[frames * 3];
            for (int i = 0; i < frames; i++)
            {
                o[i * 3] = v3[0];
                o[i * 3 + 1] = v3[1];
                o[i * 3 + 2] = v3[2];
            }
            return o;
        }

        // read_fbx6_takes: FBX 6 keeps animation in Takes/Take/Model/Channel trees with Key
        // lists of time, value, interpolation[, tangent data] - only times and values are read.
        void Fbx6Takes()
        {
            FbxNode? takes = F.Root.Find("Takes");
            if (takes == null)
            {
                return;
            }
            foreach (FbxNode take in takes.FindAll("Take"))
            {
                List<FbxNodeCurves> curves = new List<FbxNodeCurves>();
                bool have = false;
                double lo = 0;
                double hi = 0;
                foreach (FbxNode m in take.FindAll("Model"))
                {
                    if (m.Props.Count == 0)
                    {
                        continue;
                    }
                    object oid = m.Props[0];
                    if (!Index.ContainsKey(oid))
                    {
                        continue;
                    }
                    FbxNode? tr = null;
                    foreach (FbxNode c in m.FindAll("Channel"))
                    {
                        if (c.Props.Count > 0 && Str(c.Props[0]) == "Transform")
                        {
                            tr = c;
                        }
                    }
                    if (tr == null)
                    {
                        continue;
                    }
                    foreach (FbxNode c in tr.FindAll("Channel"))
                    {
                        string ch = "";
                        if (c.Props.Count > 0)
                        {
                            ch = Str(c.Props[0]);
                        }
                        if (ch != "T" && ch != "R" && ch != "S")
                        {
                            continue;
                        }
                        FbxChannel chan = new FbxChannel(ch);
                        for (int a = 0; a < 3; a++)
                        {
                            string axis = "XYZ".Substring(a, 1);
                            FbxNode? ac = null;
                            foreach (FbxNode x in c.FindAll("Channel"))
                            {
                                if (x.Props.Count > 0 && Str(x.Props[0]) == axis)
                                {
                                    ac = x;
                                    break;
                                }
                            }
                            FbxNode? keys = null;
                            if (ac != null)
                            {
                                keys = ac.Find("Key");
                            }
                            if (keys == null)
                            {
                                continue;
                            }
                            List<double> ts = new List<double>();
                            List<double> vs = new List<double>();
                            Fbx6Keys(keys.Props, ts, vs);
                            if (ts.Count == 0)
                            {
                                continue;
                            }
                            double[] kt = new double[ts.Count];
                            double[] kv = new double[ts.Count];
                            for (int k = 0; k < ts.Count; k++)
                            {
                                kt[k] = ts[k] / FbxTicks;
                                kv[k] = vs[k];
                            }
                            chan.Comps[a] = new FbxCurve(kt, kv);
                            if (!have)
                            {
                                lo = kt[0];
                                hi = kt[kt.Length - 1];
                                have = true;
                            }
                            else
                            {
                                lo = Math.Min(lo, kt[0]);
                                hi = Math.Max(hi, kt[kt.Length - 1]);
                            }
                        }
                        FbxNodeCurves nc = CurvesFor(curves, oid);
                        FbxChannel? old = nc.Find(ch);
                        if (old != null)
                        {
                            nc.Chans[nc.Chans.IndexOf(old)] = chan;
                        }
                        else
                        {
                            nc.Chans.Add(chan);
                        }
                    }
                }
                if (curves.Count == 0 || !have)
                {
                    continue;
                }
                string name = "take";
                if (take.Props.Count > 0)
                {
                    name = FbxName(take.Props[0]);
                }
                Sc.Anims.Add(Curves(name, curves, lo, hi));
            }
        }
    }
}
