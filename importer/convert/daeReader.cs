using System.Globalization;
using System.Xml;

namespace DotrAssetModder;
// Mostly AI

// COLLADA source -> Scene, a port of tools/toglb.py's read_dae / read_dae_anims (DotR-PC).
public static class DaeReader
{
    public static Scene Read(string path, Action<string> warn)
    {
        DaeRead r = new DaeRead(path, warn);
        return r.Run();
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
    public double[] V;

    public DaeXform(string? sid, string tag, double[] v)
    {
        Sid = sid;
        Tag = tag;
        V = v;
    }
}

// A geometry read into corners, before welding.
sealed class DaeGeom
{
    public double[] Cp = Array.Empty<double>(); // control points, 3 each
    public int[] CornerCp = Array.Empty<int>();
    public double[]? Nrm; // 3 a corner
    public double[]? Uv; // 2 a corner
    public int[] Tris = Array.Empty<int>(); // corner indices, 3 a triangle
    public int[] TMat = Array.Empty<int>(); // material a triangle
}

// One input of a primitive: source url, offset in the index tuple, and <input set="">.
sealed class DaeSem
{
    public string? Source;
    public int Offset;
    public int Set;

    public DaeSem(string? source, int offset, int set)
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
    public int K;
    public string? Member;
    public double[] Times = Array.Empty<double>();
    public double[] Out = Array.Empty<double>();
    public int Cols;
}

// The reader's state: toglb.py keeps these in read_dae's closures.
sealed class DaeRead
{
    readonly string path;
    readonly Action<string> warn;
    readonly XmlElement root;
    readonly Dictionary<string, XmlElement> byId = new Dictionary<string, XmlElement>();
    readonly Scene sc = new Scene();
    readonly SourceImages images;
    readonly Dictionary<string, DaeSource> sources = new Dictionary<string, DaeSource>();
    readonly Dictionary<string, int> matIndex = new Dictionary<string, int>();
    readonly Dictionary<string, int> nodeOfId = new Dictionary<string, int>();
    readonly Dictionary<int, List<DaeXform>> xforms = new Dictionary<int, List<DaeXform>>();
    readonly List<int> instNode = new List<int>();
    readonly List<XmlElement> instEl = new List<XmlElement>();

    public DaeRead(string file, Action<string> onWarn)
    {
        path = file;
        warn = onWarn;
        XmlDocument doc = new XmlDocument();
        doc.XmlResolver = null;
        using (XmlReader xr = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
        {
            doc.Load(xr);
        }
        if (doc.DocumentElement == null)
        {
            throw new InvalidDataException("empty COLLADA file");
        }
        root = doc.DocumentElement;
        CollectIds(root);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (dir == null)
        {
            dir = ".";
        }
        images = new SourceImages(dir, onWarn);
    }

    void CollectIds(XmlElement el)
    {
        string? id = Attr(el, "id");
        if (id != null && id.Length > 0 && !byId.ContainsKey(id))
        {
            byId[id] = el;
        }
        foreach (XmlNode n in el.ChildNodes)
        {
            XmlElement? c = n as XmlElement;
            if (c != null)
            {
                CollectIds(c);
            }
        }
    }

    // ---- small XML helpers (the Python strips the namespace off every tag and uses ET) ----

    static string? Attr(XmlElement el, string name)
    {
        XmlAttribute? a = el.Attributes[name];
        if (a == null)
        {
            return null;
        }
        return a.Value;
    }

    static string Attr(XmlElement el, string name, string dflt)
    {
        string? v = Attr(el, name);
        if (v == null)
        {
            return dflt;
        }
        return v;
    }

    static XmlElement? Find(XmlElement? el, string tag)
    {
        if (el == null)
        {
            return null;
        }
        foreach (XmlNode n in el.ChildNodes)
        {
            XmlElement? c = n as XmlElement;
            if (c != null && c.LocalName == tag)
            {
                return c;
            }
        }
        return null;
    }

    static XmlElement? Find2(XmlElement? el, string a, string b)
    {
        return Find(Find(el, a), b);
    }

    static List<XmlElement> FindAll(XmlElement? el, string tag)
    {
        List<XmlElement> out_ = new List<XmlElement>();
        if (el == null)
        {
            return out_;
        }
        foreach (XmlNode n in el.ChildNodes)
        {
            XmlElement? c = n as XmlElement;
            if (c != null && c.LocalName == tag)
            {
                out_.Add(c);
            }
        }
        return out_;
    }

    static List<XmlElement> Kids(XmlElement? el)
    {
        List<XmlElement> out_ = new List<XmlElement>();
        if (el == null)
        {
            return out_;
        }
        foreach (XmlNode n in el.ChildNodes)
        {
            XmlElement? c = n as XmlElement;
            if (c != null)
            {
                out_.Add(c);
            }
        }
        return out_;
    }

    // ET's el.iter(tag): the element itself and every descendant with that tag.
    static void Iter(XmlElement el, string tag, List<XmlElement> hits)
    {
        if (el.LocalName == tag)
        {
            hits.Add(el);
        }
        foreach (XmlNode n in el.ChildNodes)
        {
            XmlElement? c = n as XmlElement;
            if (c != null)
            {
                Iter(c, tag, hits);
            }
        }
    }

    // ET's el.text: the character data before the first child element.
    static string? Text(XmlElement? el)
    {
        if (el == null)
        {
            return null;
        }
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        foreach (XmlNode n in el.ChildNodes)
        {
            if (n.NodeType == XmlNodeType.Text || n.NodeType == XmlNodeType.CDATA
                || n.NodeType == XmlNodeType.Whitespace || n.NodeType == XmlNodeType.SignificantWhitespace)
            {
                sb.Append(n.Value);
            }
            else
            {
                break;
            }
        }
        if (sb.Length == 0)
        {
            return null;
        }
        return sb.ToString();
    }

    static string? FindText(XmlElement? el, string tag)
    {
        XmlElement? c = Find(el, tag);
        if (c == null)
        {
            return null;
        }
        return Text(c);
    }

    static int CountTokens(string s)
    {
        int n = 0;
        int p = 0;
        while (p < s.Length)
        {
            while (p < s.Length && char.IsWhiteSpace(s[p]))
            {
                p++;
            }
            if (p >= s.Length)
            {
                break;
            }
            n++;
            while (p < s.Length && !char.IsWhiteSpace(s[p]))
            {
                p++;
            }
        }
        return n;
    }

    static double[] Floats(XmlElement? el)
    {
        string? s = Text(el);
        if (el == null || s == null || s.Length == 0)
        {
            return Array.Empty<double>();
        }
        double[] o = new double[CountTokens(s)];
        int i = 0;
        int p = 0;
        while (p < s.Length)
        {
            while (p < s.Length && char.IsWhiteSpace(s[p]))
            {
                p++;
            }
            if (p >= s.Length)
            {
                break;
            }
            int q = p;
            while (q < s.Length && !char.IsWhiteSpace(s[q]))
            {
                q++;
            }
            o[i++] = double.Parse(s.AsSpan(p, q - p), NumberStyles.Float, CultureInfo.InvariantCulture);
            p = q;
        }
        return o;
    }

    static int[] Ints(XmlElement? el)
    {
        string? s = Text(el);
        if (el == null || s == null || s.Length == 0)
        {
            return Array.Empty<int>();
        }
        int[] o = new int[CountTokens(s)];
        int i = 0;
        int p = 0;
        while (p < s.Length)
        {
            while (p < s.Length && char.IsWhiteSpace(s[p]))
            {
                p++;
            }
            if (p >= s.Length)
            {
                break;
            }
            int q = p;
            while (q < s.Length && !char.IsWhiteSpace(s[q]))
            {
                q++;
            }
            o[i++] = int.Parse(s.AsSpan(p, q - p), NumberStyles.Integer, CultureInfo.InvariantCulture);
            p = q;
        }
        return o;
    }

    static string[] Tokens(string s)
    {
        return s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    XmlElement? Ref(string? url)
    {
        if (url == null || url.Length == 0)
        {
            return null;
        }
        string key = url.Trim().TrimStart('#');
        XmlElement? el;
        if (byId.TryGetValue(key, out el))
        {
            return el;
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
        DaeSource s;
        if (sources.TryGetValue(key, out s!))
        {
            return s;
        }
        XmlElement? el = Ref(url);
        XmlElement? acc = Find2(el, "technique_common", "accessor");
        int stride = 1;
        if (acc != null)
        {
            stride = int.Parse(Attr(acc, "stride", "1"), CultureInfo.InvariantCulture);
        }
        s = new DaeSource();
        s.Data = Floats(Find(el, "float_array"));
        s.Stride = stride;
        sources[key] = s;
        return s;
    }

    List<string> Names(string? url)
    {
        XmlElement? el = Ref(url);
        XmlElement? na = Find(el, "Name_array");
        if (na == null)
        {
            na = Find(el, "IDREF_array");
        }
        string? t = Text(na);
        List<string> out_ = new List<string>();
        if (na != null && t != null)
        {
            out_.AddRange(Tokens(t));
        }
        return out_;
    }

    SkiaSharp.SKBitmap? ImageOf(string? imgId)
    {
        XmlElement? img = null;
        if (imgId != null && imgId.Length > 0)
        {
            byId.TryGetValue(imgId, out img);
        }
        if (img == null)
        {
            return null;
        }
        string t = FindText(img, "init_from") ?? "";
        if (t.Trim().Length == 0)
        {
            t = FindText(Find(img, "init_from"), "ref") ?? "";
        }
        t = Uri.UnescapeDataString(t.Trim());
        if (t.StartsWith("file://", StringComparison.Ordinal))
        {
            t = t.Substring(7);
        }
        if (t.Length >= 3 && t[0] == '/' && char.IsAsciiLetter(t[1]) && t[2] == ':')
        {
            t = t.Substring(1);
        }
        if (t.Length == 0)
        {
            return null;
        }
        return images.Load(new List<string> { t });
    }

    // Python can key a dict on None; "\u0000" stands in for it here.
    static string MatKey(string? s)
    {
        if (s == null)
        {
            return "\u0000";
        }
        return s;
    }

    int Material(string? target)
    {
        string key = MatKey(target);
        int have;
        if (matIndex.TryGetValue(key, out have))
        {
            return have;
        }
        double[] color = new double[] { 1.0, 1.0, 1.0, 1.0 };
        SkiaSharp.SKBitmap? image = null;
        XmlElement? mat = Ref(target);
        XmlElement? eff = null;
        if (mat != null)
        {
            XmlElement? ie = Find(mat, "instance_effect");
            if (ie != null)
            {
                eff = Ref(Attr(ie, "url"));
            }
        }
        XmlElement? prof = null;
        if (eff != null)
        {
            prof = Find(eff, "profile_COMMON");
        }
        if (prof != null)
        {
            Dictionary<string, XmlElement> pars = new Dictionary<string, XmlElement>();
            List<XmlElement> newparams = new List<XmlElement>();
            Iter(prof, "newparam", newparams);
            foreach (XmlElement p in newparams)
            {
                pars[MatKey(Attr(p, "sid"))] = p;
            }
            XmlElement? tech = Find(prof, "technique");
            XmlElement? shader = null;
            if (tech != null)
            {
                foreach (XmlElement c in Kids(tech))
                {
                    if (c.LocalName == "phong" || c.LocalName == "lambert" || c.LocalName == "blinn" || c.LocalName == "constant")
                    {
                        shader = c;
                        break;
                    }
                }
            }
            XmlElement? d = null;
            if (shader != null)
            {
                d = Find(shader, "diffuse");
                if (d == null)
                {
                    d = Find(shader, "emission");
                }
            }
            if (d != null)
            {
                XmlElement? c = Find(d, "color");
                if (c != null)
                {
                    double[] f = Floats(c);
                    color = new double[] { 1.0, 1.0, 1.0, 1.0 };
                    int take = Math.Min(4, f.Length);
                    for (int k = 0; k < take; k++)
                    {
                        color[k] = f[k];
                    }
                }
                XmlElement? t = Find(d, "texture");
                if (t != null)
                {
                    string? tid = Attr(t, "texture");
                    XmlElement? p;
                    pars.TryGetValue(MatKey(tid), out p);
                    if (p != null && Find(p, "sampler2D") != null)
                    {
                        string? s = FindText(Find(p, "sampler2D"), "source");
                        XmlElement? ii = Find2(p, "sampler2D", "instance_image");
                        XmlElement? surf;
                        if (s != null && s.Length > 0 && pars.TryGetValue(s.Trim(), out surf!))
                        {
                            tid = (FindText(Find(surf, "surface"), "init_from") ?? "").Trim();
                        }
                        else if (ii != null)
                        {
                            tid = Attr(ii, "url", "").TrimStart('#');
                        }
                    }
                    image = ImageOf(tid);
                }
            }
        }
        string name = "default";
        if (target != null && target.Length > 0)
        {
            name = target.TrimStart('#');
        }
        if (mat != null && Attr(mat, "name") != null)
        {
            name = Attr(mat, "name", "");
        }
        matIndex[key] = sc.AddMaterial(name, color, image);
        return matIndex[key];
    }

    static void Row(DaeSource s, int row, int take, double[] dst, int off)
    {
        int n = Math.Min(take, s.Stride);
        for (int k = 0; k < n; k++)
        {
            dst[off + k] = s.Data[row * s.Stride + k];
        }
    }

    DaeGeom? Geometry(XmlElement? geom, Dictionary<string, string?> symbols)
    {
        XmlElement? mesh = Find(geom, "mesh");
        if (mesh == null)
        {
            return null;
        }
        XmlElement? verts = Find(mesh, "vertices");
        Dictionary<string, string?> vin = new Dictionary<string, string?>();
        foreach (XmlElement i in FindAll(verts, "input"))
        {
            vin[MatKey(Attr(i, "semantic"))] = Attr(i, "source");
        }
        DaeSource cpSrc = Source(vin["POSITION"]);
        int ncp = cpSrc.Rows;
        double[] cp = new double[ncp * 3];
        for (int i = 0; i < ncp; i++)
        {
            Row(cpSrc, i, 3, cp, i * 3);
        }
        DaeSource? vnrm = null;
        if (vin.ContainsKey("NORMAL"))
        {
            vnrm = Source(vin["NORMAL"]);
        }
        DaeSource? vuv = null;
        if (vin.ContainsKey("TEXCOORD"))
        {
            vuv = Source(vin["TEXCOORD"]);
        }
        List<int> cornerCp = new List<int>();
        List<double> cornerN = new List<double>();
        List<double> cornerUv = new List<double>();
        List<int> tris = new List<int>();
        List<int> tmat = new List<int>();
        bool hasN = true;
        bool hasUv = true;
        int base_ = 0;
        foreach (XmlElement prim in Kids(mesh))
        {
            if (prim.LocalName != "triangles" && prim.LocalName != "polylist" && prim.LocalName != "polygons")
            {
                if (prim.LocalName == "trifans" || prim.LocalName == "tristrips" || prim.LocalName == "lines" || prim.LocalName == "linestrips")
                {
                    warn(Attr(geom!, "id", "") + ": <" + prim.LocalName + "> skipped");
                }
                continue;
            }
            List<XmlElement> inputs = FindAll(prim, "input");
            int stride = 1;
            foreach (XmlElement i in inputs)
            {
                int o = int.Parse(Attr(i, "offset", "0"), CultureInfo.InvariantCulture) + 1;
                if (o > stride)
                {
                    stride = o;
                }
            }
            Dictionary<string, DaeSem> sem = new Dictionary<string, DaeSem>();
            foreach (XmlElement i in inputs)
            {
                string s = MatKey(Attr(i, "semantic"));
                int set = int.Parse(Attr(i, "set", "0"), CultureInfo.InvariantCulture);
                DaeSem have;
                if (s == "TEXCOORD" && sem.TryGetValue(s, out have!) && set >= have.Set)
                {
                    continue;
                }
                sem[s] = new DaeSem(Attr(i, "source"), int.Parse(Attr(i, "offset", "0"), CultureInfo.InvariantCulture), set);
            }
            int[] idx;
            int[] counts;
            if (prim.LocalName == "polygons")
            {
                List<int[]> ps = new List<int[]>();
                foreach (XmlElement p in FindAll(prim, "p"))
                {
                    ps.Add(Ints(p));
                }
                counts = new int[ps.Count];
                int total = 0;
                for (int k = 0; k < ps.Count; k++)
                {
                    counts[k] = ps[k].Length / stride;
                    total += ps[k].Length;
                }
                idx = new int[total];
                int at = 0;
                foreach (int[] p in ps)
                {
                    Array.Copy(p, 0, idx, at, p.Length);
                    at += p.Length;
                }
            }
            else
            {
                idx = Ints(Find(prim, "p"));
                if (prim.LocalName == "triangles")
                {
                    counts = new int[idx.Length / (3 * stride)];
                    Array.Fill(counts, 3);
                }
                else
                {
                    counts = Ints(Find(prim, "vcount"));
                }
            }
            int corners = idx.Length / stride;
            int[] vi = new int[corners];
            int voff = sem["VERTEX"].Offset;
            for (int c = 0; c < corners; c++)
            {
                vi[c] = idx[c * stride + voff];
            }
            cornerCp.AddRange(vi);
            if (sem.ContainsKey("NORMAL"))
            {
                DaeSource ns = Source(sem["NORMAL"].Source);
                int noff = sem["NORMAL"].Offset;
                double[] tmp = new double[3];
                for (int c = 0; c < corners; c++)
                {
                    tmp[0] = 0;
                    tmp[1] = 0;
                    tmp[2] = 0;
                    Row(ns, idx[c * stride + noff], 3, tmp, 0);
                    cornerN.Add(tmp[0]);
                    cornerN.Add(tmp[1]);
                    cornerN.Add(tmp[2]);
                }
            }
            else if (vnrm != null)
            {
                double[] tmp = new double[3];
                for (int c = 0; c < corners; c++)
                {
                    tmp[0] = 0;
                    tmp[1] = 0;
                    tmp[2] = 0;
                    Row(vnrm, vi[c], 3, tmp, 0);
                    cornerN.Add(tmp[0]);
                    cornerN.Add(tmp[1]);
                    cornerN.Add(tmp[2]);
                }
            }
            else
            {
                hasN = false;
            }
            if (sem.ContainsKey("TEXCOORD"))
            {
                DaeSource us = Source(sem["TEXCOORD"].Source);
                int uoff = sem["TEXCOORD"].Offset;
                double[] tmp = new double[2];
                for (int c = 0; c < corners; c++)
                {
                    tmp[0] = 0;
                    tmp[1] = 0;
                    Row(us, idx[c * stride + uoff], 2, tmp, 0);
                    cornerUv.Add(tmp[0]);
                    cornerUv.Add(tmp[1]);
                }
            }
            else if (vuv != null)
            {
                double[] tmp = new double[2];
                for (int c = 0; c < corners; c++)
                {
                    tmp[0] = 0;
                    tmp[1] = 0;
                    Row(vuv, vi[c], 2, tmp, 0);
                    cornerUv.Add(tmp[0]);
                    cornerUv.Add(tmp[1]);
                }
            }
            else
            {
                for (int c = 0; c < corners; c++)
                {
                    cornerUv.Add(0);
                    cornerUv.Add(0);
                }
                hasUv = false;
            }
            int[] starts = new int[counts.Length];
            int run = base_;
            for (int k = 0; k < counts.Length; k++)
            {
                starts[k] = run;
                run += counts[k];
            }
            int[] t = MeshBuild.Fan(starts, counts);
            tris.AddRange(t);
            int mi = -1;
            string? primMat = Attr(prim, "material");
            string? target;
            if (symbols.TryGetValue(MatKey(primMat), out target))
            {
                mi = Material(target);
            }
            else
            {
                mi = Material(primMat);
            }
            for (int k = 0; k < t.Length / 3; k++)
            {
                tmat.Add(mi);
            }
            base_ += corners;
        }
        if (tris.Count == 0)
        {
            return null;
        }
        DaeGeom g = new DaeGeom();
        g.Cp = cp;
        g.CornerCp = cornerCp.ToArray();
        if (hasN)
        {
            g.Nrm = cornerN.ToArray();
        }
        if (hasUv)
        {
            g.Uv = cornerUv.ToArray();
        }
        g.Tris = tris.ToArray();
        g.TMat = tmat.ToArray();
        return g;
    }

    static Mat4 Apply(Mat4 m, string tag, double[] v, int off)
    {
        if (tag == "matrix")
        {
            Mat4 b = new Mat4();
            for (int k = 0; k < 16; k++)
            {
                b.M[k] = v[off + k];
            }
            return m * b;
        }
        if (tag == "translate")
        {
            return m * Mat4.Translation(v[off], v[off + 1], v[off + 2]);
        }
        if (tag == "rotate")
        {
            return m * Mat4.Rotation(v[off], v[off + 1], v[off + 2], v[off + 3]);
        }
        if (tag == "scale")
        {
            return m * Mat4.Scale(v[off], v[off + 1], v[off + 2]);
        }
        return m;
    }

    static Mat4 Compose(List<DaeXform> xf)
    {
        Mat4 m = Mat4.Identity();
        foreach (DaeXform x in xf)
        {
            m = Apply(m, x.Tag, x.V, 0);
        }
        return m;
    }

    void Walk(XmlElement? el, int parent)
    {
        foreach (XmlElement n in FindAll(el, "node"))
        {
            string? nm = Attr(n, "name");
            if (nm == null || nm.Length == 0)
            {
                nm = Attr(n, "id");
            }
            if (nm == null || nm.Length == 0)
            {
                nm = Attr(n, "sid");
            }
            if (nm == null || nm.Length == 0)
            {
                nm = "node";
            }
            int i = sc.AddNode(nm, parent);
            string? id = Attr(n, "id");
            if (id != null && id.Length > 0)
            {
                nodeOfId[id] = i;
            }
            List<DaeXform> xf = new List<DaeXform>();
            foreach (XmlElement c in Kids(n))
            {
                if (c.LocalName == "matrix" || c.LocalName == "translate" || c.LocalName == "rotate" || c.LocalName == "scale")
                {
                    xf.Add(new DaeXform(Attr(c, "sid"), c.LocalName, Floats(c)));
                }
            }
            xforms[i] = xf;
            sc.Nodes[i].Local = Compose(xf);
            sc.Nodes[i].Sid = Attr(n, "sid");
            foreach (XmlElement c in Kids(n))
            {
                if (c.LocalName == "instance_geometry" || c.LocalName == "instance_controller")
                {
                    instNode.Add(i);
                    instEl.Add(c);
                }
            }
            Walk(n, i);
        }
    }

    bool Under(int k, int rootNode)
    {
        while (k >= 0)
        {
            if (k == rootNode)
            {
                return true;
            }
            k = sc.Nodes[k].Parent;
        }
        return false;
    }

    int ResolveJoint(string name, List<int> roots)
    {
        List<int> bySid = new List<int>();
        for (int k = 0; k < sc.Nodes.Count; k++)
        {
            if (sc.Nodes[k].Sid == name)
            {
                bySid.Add(k);
            }
        }
        foreach (int k in bySid)
        {
            foreach (int r in roots)
            {
                if (Under(k, r))
                {
                    return k;
                }
            }
        }
        if (bySid.Count > 0)
        {
            return bySid[0];
        }
        int hit;
        if (nodeOfId.TryGetValue(name, out hit))
        {
            return hit;
        }
        for (int k = 0; k < sc.Nodes.Count; k++)
        {
            if (sc.Nodes[k].Name == name)
            {
                return k;
            }
        }
        return -1;
    }

    public Scene Run()
    {
        string up = "Y_UP";
        string? upText = FindText(Find(root, "asset"), "up_axis");
        if (upText != null && upText.Trim().Length > 0)
        {
            up = upText.Trim();
        }
        XmlElement? unit = Find2(root, "asset", "unit");
        if (unit != null && Attr(unit, "meter") != null)
        {
            sc.Unit = double.Parse(Attr(unit, "meter", "1"), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        sc.Axis = Mat4.Identity();
        if (up == "Z_UP")
        {
            sc.Axis = AxisOf(1, 0, 0, 0, 0, 1, 0, -1, 0);
        }
        else if (up == "X_UP")
        {
            sc.Axis = AxisOf(0, -1, 0, 1, 0, 0, 0, 0, 1);
        }

        XmlElement? ivs = Find2(root, "scene", "instance_visual_scene");
        XmlElement? vs = null;
        if (ivs != null)
        {
            vs = Ref(Attr(ivs, "url"));
        }
        if (vs == null)
        {
            vs = Find2(root, "library_visual_scenes", "visual_scene");
        }
        if (vs == null)
        {
            warn("no visual scene");
            return sc;
        }
        Walk(vs, -1);

        for (int n = 0; n < instNode.Count; n++)
        {
            int i = instNode[n];
            XmlElement inst = instEl[n];
            Dictionary<string, string?> symbols = new Dictionary<string, string?>();
            List<XmlElement> ims = new List<XmlElement>();
            Iter(inst, "instance_material", ims);
            foreach (XmlElement im in ims)
            {
                symbols[MatKey(Attr(im, "symbol"))] = Attr(im, "target");
            }
            if (inst.LocalName == "instance_geometry")
            {
                XmlElement? geom = Ref(Attr(inst, "url"));
                DaeGeom? data = null;
                if (geom != null)
                {
                    data = Geometry(geom, symbols);
                }
                if (data == null)
                {
                    continue;
                }
                SceneMesh mesh = new SceneMesh(MeshName(geom!), i);
                MeshBuild.Build(mesh, data.Cp, data.CornerCp, data.Nrm, data.Uv, data.Tris, data.TMat, null);
                sc.Meshes.Add(mesh);
                continue;
            }
            XmlElement? ctrl = Ref(Attr(inst, "url"));
            XmlElement? skin = Find(ctrl, "skin");
            if (skin == null)
            {
                warn("controller " + Attr(inst, "url", "") + " is not a skin; skipped");
                continue;
            }
            XmlElement? sgeom = Ref(Attr(skin, "source"));
            DaeGeom? sdata = null;
            if (sgeom != null)
            {
                sdata = Geometry(sgeom, symbols);
            }
            if (sdata == null)
            {
                continue;
            }
            double[] bsmv = Floats(Find(skin, "bind_shape_matrix"));
            Mat4 bsm = Mat4.Identity();
            if (bsmv.Length == 16)
            {
                bsm = new Mat4();
                Array.Copy(bsmv, bsm.M, 16);
            }
            Dictionary<string, string?> jin = new Dictionary<string, string?>();
            foreach (XmlElement x in FindAll(Find(skin, "joints"), "input"))
            {
                jin[MatKey(Attr(x, "semantic"))] = Attr(x, "source");
            }
            List<string> jnames = Names(jin["JOINT"]);
            DaeSource ibmSrc = Source(jin["INV_BIND_MATRIX"]);
            List<int> roots = new List<int>();
            foreach (XmlElement s in FindAll(inst, "skeleton"))
            {
                string? st = Text(s);
                if (st == null)
                {
                    continue;
                }
                string sid = st.Trim().TrimStart('#');
                int rn;
                if (nodeOfId.TryGetValue(sid, out rn))
                {
                    roots.Add(rn);
                }
            }
            List<int> jnode = new List<int>();
            for (int k = 0; k < jnames.Count; k++)
            {
                int j = ResolveJoint(jnames[k], roots);
                if (j < 0)
                {
                    warn("joint " + jnames[k] + " not found");
                }
                else if (sc.Nodes[j].Bind == null)
                {
                    Mat4 ib = new Mat4();
                    Array.Copy(ibmSrc.Data, k * 16, ib.M, 0, 16);
                    sc.Nodes[j].Bind = ib.Inverse();
                }
                jnode.Add(j);
            }
            XmlElement? vw = Find(skin, "vertex_weights");
            Dictionary<string, DaeSem> win = new Dictionary<string, DaeSem>();
            int wstride = 1;
            foreach (XmlElement x in FindAll(vw, "input"))
            {
                int off = int.Parse(Attr(x, "offset", "0"), CultureInfo.InvariantCulture);
                win[MatKey(Attr(x, "semantic"))] = new DaeSem(Attr(x, "source"), off, 0);
                if (off + 1 > wstride)
                {
                    wstride = off + 1;
                }
            }
            double[] weights = Source(win["WEIGHT"].Source).Data;
            int[] vcount = Ints(Find(vw, "vcount"));
            int[] v = Ints(Find(vw, "v"));
            int ncp = sdata.Cp.Length / 3;
            List<Influence>[] cpInfl = new List<Influence>[ncp];
            Dictionary<int, int>[] cpAt = new Dictionary<int, int>[ncp];
            for (int c = 0; c < ncp; c++)
            {
                cpInfl[c] = new List<Influence>();
                cpAt[c] = new Dictionary<int, int>();
            }
            int joff = win["JOINT"].Offset;
            int woff = win["WEIGHT"].Offset;
            int row = 0;
            for (int c = 0; c < vcount.Length; c++)
            {
                int cnt = vcount[c];
                for (int k = 0; k < cnt; k++)
                {
                    int ji = v[(row + k) * wstride + joff];
                    double w = weights[v[(row + k) * wstride + woff]];
                    if (c < ncp && ji >= 0 && ji < jnode.Count && jnode[ji] >= 0 && w > 0)
                    {
                        int jn = jnode[ji];
                        int at;
                        if (cpAt[c].TryGetValue(jn, out at))
                        {
                            cpInfl[c][at].Weight += w;
                        }
                        else
                        {
                            cpAt[c][jn] = cpInfl[c].Count;
                            cpInfl[c].Add(new Influence(jn, w));
                        }
                    }
                }
                row += cnt;
            }
            // the bind shape matrix puts the control points (and normals) into bind-pose space
            double[] cp2 = new double[ncp * 3];
            for (int c = 0; c < ncp; c++)
            {
                double ox;
                double oy;
                double oz;
                bsm.TransformPoint(sdata.Cp[c * 3], sdata.Cp[c * 3 + 1], sdata.Cp[c * 3 + 2], out ox, out oy, out oz);
                cp2[c * 3] = ox;
                cp2[c * 3 + 1] = oy;
                cp2[c * 3 + 2] = oz;
            }
            if (sdata.Nrm != null)
            {
                int nn = sdata.Nrm.Length / 3;
                double[] nrm2 = new double[nn * 3];
                for (int c = 0; c < nn; c++)
                {
                    double ox;
                    double oy;
                    double oz;
                    bsm.TransformDir(sdata.Nrm[c * 3], sdata.Nrm[c * 3 + 1], sdata.Nrm[c * 3 + 2], out ox, out oy, out oz);
                    nrm2[c * 3] = ox;
                    nrm2[c * 3 + 1] = oy;
                    nrm2[c * 3 + 2] = oz;
                }
                sdata.Nrm = nrm2;
            }
            for (int c = 0; c < ncp; c++)
            {
                if (cpInfl[c].Count == 0)
                {
                    cpInfl[c].Add(new Influence(i, 1.0));
                }
            }
            SceneMesh smesh = new SceneMesh(MeshName(sgeom!), i);
            MeshBuild.Build(smesh, cp2, sdata.CornerCp, sdata.Nrm, sdata.Uv, sdata.Tris, sdata.TMat, cpInfl);
            sc.Meshes.Add(smesh);
        }

        ReadAnims(Path.GetFileNameWithoutExtension(path));
        return sc;
    }

    static string MeshName(XmlElement geom)
    {
        string? nm = Attr(geom, "name");
        if (nm == null || nm.Length == 0)
        {
            nm = Attr(geom, "id");
        }
        if (nm == null || nm.Length == 0)
        {
            nm = "mesh";
        }
        return nm;
    }

    static Mat4 AxisOf(double a, double b, double c, double d, double e, double f, double g, double h, double i)
    {
        Mat4 m = Mat4.Identity();
        m[0, 0] = a;
        m[0, 1] = b;
        m[0, 2] = c;
        m[1, 0] = d;
        m[1, 1] = e;
        m[1, 2] = f;
        m[2, 0] = g;
        m[2, 1] = h;
        m[2, 2] = i;
        return m;
    }

    // ---- animations (read_dae_anims) ----

    readonly List<List<string?>> chanChain = new List<List<string?>>();
    readonly List<XmlElement> chanEl = new List<XmlElement>();

    void Collect(XmlElement a, List<string?> chain)
    {
        List<string?> next = new List<string?>(chain);
        next.Add(Attr(a, "id"));
        foreach (XmlElement ch in FindAll(a, "channel"))
        {
            chanChain.Add(next);
            chanEl.Add(ch);
        }
        foreach (XmlElement sub in FindAll(a, "animation"))
        {
            Collect(sub, next);
        }
    }

    // np.interp: linear between the keys, the end values outside them (times increasing)
    static double Interp(double x, double[] xp, double[] fp, int off, int stride)
    {
        int n = xp.Length;
        if (x <= xp[0])
        {
            return fp[off];
        }
        if (x >= xp[n - 1])
        {
            return fp[off + (n - 1) * stride];
        }
        int lo = 0;
        int hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (xp[mid] <= x)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        double t0 = xp[lo];
        double t1 = xp[lo + 1];
        if (t1 == t0)
        {
            return fp[off + lo * stride];
        }
        double u = (x - t0) / (t1 - t0);
        double a = fp[off + lo * stride];
        double b = fp[off + (lo + 1) * stride];
        return a + (b - a) * u;
    }

    void ReadAnims(string stem)
    {
        XmlElement? lib = Find(root, "library_animations");
        if (lib == null)
        {
            return;
        }
        foreach (XmlElement a in FindAll(lib, "animation"))
        {
            Collect(a, new List<string?>());
        }
        if (chanEl.Count == 0)
        {
            return;
        }
        List<string> gname = new List<string>();
        List<List<XmlElement>> gmembers = new List<List<XmlElement>>();
        List<double[]?> grange = new List<double[]?>();
        foreach (XmlElement clip in FindAll(Find(root, "library_animation_clips"), "animation_clip"))
        {
            HashSet<string> ids = new HashSet<string>();
            foreach (XmlElement ia in FindAll(clip, "instance_animation"))
            {
                ids.Add(Attr(ia, "url", "").TrimStart('#'));
            }
            List<XmlElement> members = new List<XmlElement>();
            for (int k = 0; k < chanEl.Count; k++)
            {
                bool hit = false;
                foreach (string? id in chanChain[k])
                {
                    if (id != null && ids.Contains(id))
                    {
                        hit = true;
                        break;
                    }
                }
                if (hit)
                {
                    members.Add(chanEl[k]);
                }
            }
            double[]? rng = null;
            if (Attr(clip, "end") != null)
            {
                rng = new double[]
                {
                    double.Parse(Attr(clip, "start", "0"), NumberStyles.Float, CultureInfo.InvariantCulture),
                    double.Parse(Attr(clip, "end", "0"), NumberStyles.Float, CultureInfo.InvariantCulture),
                };
            }
            string nm = "clip";
            if (Attr(clip, "name") != null && Attr(clip, "name", "").Length > 0)
            {
                nm = Attr(clip, "name", "");
            }
            else if (Attr(clip, "id") != null && Attr(clip, "id", "").Length > 0)
            {
                nm = Attr(clip, "id", "");
            }
            gname.Add(nm);
            gmembers.Add(members);
            grange.Add(rng);
        }
        if (gname.Count == 0)
        {
            gname.Add(stem);
            gmembers.Add(new List<XmlElement>(chanEl));
            grange.Add(null);
        }

        for (int g = 0; g < gname.Count; g++)
        {
            List<DaeSampled> sampled = new List<DaeSampled>();
            bool any = false;
            double lo = 0;
            double hi = 0;
            foreach (XmlElement ch in gmembers[g])
            {
                string target = Attr(ch, "target", "");
                int slash = target.IndexOf('/');
                if (slash < 0)
                {
                    continue;
                }
                string nid = target.Substring(0, slash);
                string rest = target.Substring(slash + 1);
                int node;
                if (!nodeOfId.TryGetValue(nid, out node))
                {
                    continue;
                }
                string sid = rest;
                string? member = null;
                int dot = rest.IndexOf('.');
                if (dot >= 0)
                {
                    sid = rest.Substring(0, dot);
                    member = rest.Substring(dot + 1);
                }
                else if (rest.Contains('('))
                {
                    warn("animation target " + target + ": indexed members are not supported");
                    continue;
                }
                List<DaeXform> xf = xforms[node];
                int k = -1;
                for (int q = 0; q < xf.Count; q++)
                {
                    if (xf[q].Sid == sid)
                    {
                        k = q;
                        break;
                    }
                }
                if (k < 0)
                {
                    continue;
                }
                XmlElement? samp;
                if (!byId.TryGetValue(Attr(ch, "source", "").TrimStart('#'), out samp))
                {
                    continue;
                }
                Dictionary<string, string?> sin = new Dictionary<string, string?>();
                foreach (XmlElement x in FindAll(samp, "input"))
                {
                    sin[MatKey(Attr(x, "semantic"))] = Attr(x, "source");
                }
                double[] times = Source(sin["INPUT"]).Data;
                double[] outv = Source(sin["OUTPUT"]).Data;
                if (times.Length == 0)
                {
                    continue;
                }
                DaeSampled s = new DaeSampled();
                s.Node = node;
                s.K = k;
                s.Member = member;
                s.Times = times;
                s.Out = outv;
                s.Cols = outv.Length / times.Length;
                sampled.Add(s);
                if (!any)
                {
                    lo = times[0];
                    hi = times[times.Length - 1];
                    any = true;
                }
                else
                {
                    lo = Math.Min(lo, times[0]);
                    hi = Math.Max(hi, times[times.Length - 1]);
                }
            }
            if (sampled.Count == 0)
            {
                continue;
            }
            double[]? rng2 = grange[g];
            if (rng2 != null && rng2[1] > rng2[0])
            {
                lo = rng2[0];
                hi = rng2[1];
            }
            int frames = (int)Math.Round((hi - lo) * Scene.Fps, MidpointRounding.ToEven) + 1;
            double[] t = new double[frames];
            for (int f = 0; f < frames; f++)
            {
                t[f] = lo + (double)f / Scene.Fps;
            }
            List<int> nodeOrder = new List<int>();
            Dictionary<int, List<DaeSampled>> perNode = new Dictionary<int, List<DaeSampled>>();
            foreach (DaeSampled s in sampled)
            {
                List<DaeSampled> items;
                if (!perNode.TryGetValue(s.Node, out items!))
                {
                    items = new List<DaeSampled>();
                    perNode[s.Node] = items;
                    nodeOrder.Add(s.Node);
                }
                items.Add(s);
            }
            SceneAnim anim = new SceneAnim(gname[g], frames);
            foreach (int node in nodeOrder)
            {
                List<DaeXform> xf = xforms[node];
                int[] widths = new int[xf.Count];
                double[][] vals = new double[xf.Count][];
                for (int k = 0; k < xf.Count; k++)
                {
                    int w = xf[k].V.Length;
                    widths[k] = w;
                    vals[k] = new double[frames * w];
                    for (int f = 0; f < frames; f++)
                    {
                        Array.Copy(xf[k].V, 0, vals[k], f * w, w);
                    }
                }
                foreach (DaeSampled s in perNode[node])
                {
                    int w = widths[s.K];
                    if (s.Member == null)
                    {
                        int cols = Math.Min(s.Cols, w);
                        for (int c = 0; c < cols; c++)
                        {
                            for (int f = 0; f < frames; f++)
                            {
                                vals[s.K][f * w + c] = Interp(t[f], s.Times, s.Out, c, s.Cols);
                            }
                        }
                    }
                    else
                    {
                        int c = MemberIndex(s.Member);
                        if (c < 0 || c >= w)
                        {
                            continue;
                        }
                        for (int f = 0; f < frames; f++)
                        {
                            vals[s.K][f * w + c] = Interp(t[f], s.Times, s.Out, 0, s.Cols);
                        }
                    }
                }
                Mat4[] mats = new Mat4[frames];
                for (int f = 0; f < frames; f++)
                {
                    Mat4 m = Mat4.Identity();
                    for (int k = 0; k < xf.Count; k++)
                    {
                        m = Apply(m, xf[k].Tag, vals[k], f * widths[k]);
                    }
                    mats[f] = m;
                }
                anim.Tracks[node] = mats;
            }
            sc.Anims.Add(anim);
        }
    }

    static int MemberIndex(string member)
    {
        string m = member.ToUpperInvariant();
        if (m == "X")
        {
            return 0;
        }
        if (m == "Y")
        {
            return 1;
        }
        if (m == "Z")
        {
            return 2;
        }
        if (m == "ANGLE")
        {
            return 3;
        }
        return -1;
    }
}
