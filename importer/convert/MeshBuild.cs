namespace DotrAssetModder;
// Mostly AI

// One joint's pull on a control point; a point's list holds each joint once, in the order
// the source gave them (ties in weight keep that order, as toglb's stable sort does).
public sealed class Influence
{
    public int Joint;
    public double Weight;

    public Influence(int joint, double weight)
    {
        Joint = joint;
        Weight = weight;
    }
}

// Corners -> welded vertices, a port of toglb.py's weld / fan / build_mesh. Rounding is
// numpy's (scale, round half to even, unscale) so the vertex order matches the Python's.
public static class MeshBuild
{
    sealed class RowComparer : IComparer<int>
    {
        public double[] Rows = new double[0];
        public int Width;

        public int Compare(int a, int b)
        {
            for (int k = 0; k < Width; k++)
            {
                double x = Rows[a * Width + k];
                double y = Rows[b * Width + k];
                if (x < y)
                {
                    return -1;
                }
                if (x > y)
                {
                    return 1;
                }
            }
            return a.CompareTo(b); // stable: the first corner of a vertex comes first
        }
    }

    public static double NpRound(double x, int decimals)
    {
        double f = Math.Pow(10, decimals);
        return Math.Round(x * f, MidpointRounding.ToEven) / f;
    }

    // Polygons (first corner, corner count) -> triangle fans of corner indices, 3 a triangle.
    public static int[] Fan(int[] starts, int[] sizes)
    {
        List<int> tris = new List<int>();
        for (int p = 0; p < starts.Length; p++)
        {
            for (int k = 1; k < sizes[p] - 1; k++)
            {
                tris.Add(starts[p]);
                tris.Add(starts[p] + k);
                tris.Add(starts[p] + k + 1);
            }
        }
        return tris.ToArray();
    }

    // Corners -> unique vertices: the first corner of each vertex (in sorted key order, as
    // numpy's unique), and the vertex of each corner.
    public static void Weld(int[] cornerCp, double[]? nrm, double[]? uv, out int[] first, out int[] inverse)
    {
        int n = cornerCp.Length;
        int width = 1;
        if (nrm != null)
        {
            width += 3;
        }
        if (uv != null)
        {
            width += 2;
        }
        double[] rows = new double[n * width];
        for (int i = 0; i < n; i++)
        {
            int o = i * width;
            rows[o++] = cornerCp[i];
            if (nrm != null)
            {
                rows[o++] = NpRound(nrm[i * 3], 3);
                rows[o++] = NpRound(nrm[i * 3 + 1], 3);
                rows[o++] = NpRound(nrm[i * 3 + 2], 3);
            }
            if (uv != null)
            {
                rows[o++] = NpRound(uv[i * 2], 5);
                rows[o++] = NpRound(uv[i * 2 + 1], 5);
            }
        }
        int[] perm = new int[n];
        for (int i = 0; i < n; i++)
        {
            perm[i] = i;
        }
        RowComparer cmp = new RowComparer();
        cmp.Rows = rows;
        cmp.Width = width;
        Array.Sort(perm, cmp);

        List<int> firsts = new List<int>();
        inverse = new int[n];
        for (int k = 0; k < n; k++)
        {
            bool fresh = k == 0;
            if (!fresh)
            {
                int a = perm[k - 1];
                int b = perm[k];
                for (int c = 0; c < width; c++)
                {
                    if (rows[a * width + c] != rows[b * width + c])
                    {
                        fresh = true;
                        break;
                    }
                }
            }
            if (fresh)
            {
                firsts.Add(perm[k]);
            }
            inverse[perm[k]] = firsts.Count - 1;
        }
        first = firsts.ToArray();
    }

    // cp: control points (3 each); cornerCp: control point of each corner; cornerNrm/cornerUv:
    // per corner or null; triCorners: corner indices, 3 a triangle; triMat: material per
    // triangle; cpInfl: influences per control point, or null for a rigid mesh.
    public static void Build(SceneMesh mesh, double[] cp, int[] cornerCp, double[]? cornerNrm, double[]? cornerUv,
        int[] triCorners, int[] triMat, List<Influence>[]? cpInfl)
    {
        // Some exporters write one control point per corner; merge points at the same place
        // with the same influences, or no two triangles share a vertex (stripification, size).
        int ncp = cp.Length / 3;
        double big = 0;
        for (int i = 0; i < cp.Length; i++)
        {
            big = Math.Max(big, Math.Abs(cp[i]));
        }
        double step = Math.Max(big, 1e-9) * 1e-6;
        Dictionary<string, int> canon = new Dictionary<string, int>();
        int[] same = new int[ncp];
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < ncp; i++)
        {
            sb.Clear();
            for (int c = 0; c < 3; c++)
            {
                sb.Append((long)Math.Round(cp[i * 3 + c] / step, MidpointRounding.ToEven));
                sb.Append(',');
            }
            if (cpInfl != null)
            {
                List<Influence> sorted = new List<Influence>(cpInfl[i]);
                sorted.Sort((a, b) => a.Joint.CompareTo(b.Joint));
                foreach (Influence f in sorted)
                {
                    sb.Append(f.Joint);
                    sb.Append(':');
                    sb.Append(Math.Round(f.Weight, 4, MidpointRounding.ToEven).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(';');
                }
            }
            string key = sb.ToString();
            if (canon.TryGetValue(key, out int prev))
            {
                same[i] = prev;
            }
            else
            {
                canon[key] = i;
                same[i] = i;
            }
        }
        int nc = cornerCp.Length;
        int[] ccp = new int[nc];
        for (int i = 0; i < nc; i++)
        {
            ccp[i] = same[cornerCp[i]];
        }
        double[]? cn = null;
        if (cornerNrm != null)
        {
            cn = new double[nc * 3];
            for (int i = 0; i < nc; i++)
            {
                double x = cornerNrm[i * 3];
                double y = cornerNrm[i * 3 + 1];
                double z = cornerNrm[i * 3 + 2];
                double len = Math.Sqrt(x * x + y * y + z * z);
                if (len <= 1e-12)
                {
                    len = 1;
                }
                cn[i * 3] = x / len;
                cn[i * 3 + 1] = y / len;
                cn[i * 3 + 2] = z / len;
            }
        }

        Weld(ccp, cn, cornerUv, out int[] first, out int[] inv);
        int nv = first.Length;
        int[] vcp = new int[nv];
        for (int v = 0; v < nv; v++)
        {
            vcp[v] = ccp[first[v]];
        }
        mesh.Pos = new double[nv * 3];
        for (int v = 0; v < nv; v++)
        {
            mesh.Pos[v * 3] = cp[vcp[v] * 3];
            mesh.Pos[v * 3 + 1] = cp[vcp[v] * 3 + 1];
            mesh.Pos[v * 3 + 2] = cp[vcp[v] * 3 + 2];
        }
        mesh.Tris = new int[triCorners.Length];
        for (int i = 0; i < triCorners.Length; i++)
        {
            mesh.Tris[i] = inv[triCorners[i]];
        }
        mesh.TMat = (int[])triMat.Clone();
        mesh.Uv = new double[nv * 2];
        if (cornerUv != null)
        {
            for (int v = 0; v < nv; v++)
            {
                mesh.Uv[v * 2] = cornerUv[first[v] * 2];
                mesh.Uv[v * 2 + 1] = cornerUv[first[v] * 2 + 1];
            }
        }
        mesh.Nrm = new double[nv * 3];
        if (cn != null)
        {
            for (int v = 0; v < nv; v++)
            {
                mesh.Nrm[v * 3] = cn[first[v] * 3];
                mesh.Nrm[v * 3 + 1] = cn[first[v] * 3 + 1];
                mesh.Nrm[v * 3 + 2] = cn[first[v] * 3 + 2];
            }
        }
        else
        {
            // smooth normals per control point, area weighted
            double[] acc = new double[cp.Length];
            for (int t = 0; t < triCorners.Length / 3; t++)
            {
                int a = ccp[triCorners[t * 3]];
                int b = ccp[triCorners[t * 3 + 1]];
                int c = ccp[triCorners[t * 3 + 2]];
                double ux = cp[b * 3] - cp[a * 3];
                double uy = cp[b * 3 + 1] - cp[a * 3 + 1];
                double uz = cp[b * 3 + 2] - cp[a * 3 + 2];
                double wx = cp[c * 3] - cp[a * 3];
                double wy = cp[c * 3 + 1] - cp[a * 3 + 1];
                double wz = cp[c * 3 + 2] - cp[a * 3 + 2];
                double fx = uy * wz - uz * wy;
                double fy = uz * wx - ux * wz;
                double fz = ux * wy - uy * wx;
                foreach (int p in new[] { a, b, c })
                {
                    acc[p * 3] += fx;
                    acc[p * 3 + 1] += fy;
                    acc[p * 3 + 2] += fz;
                }
            }
            for (int v = 0; v < nv; v++)
            {
                mesh.Nrm[v * 3] = acc[vcp[v] * 3];
                mesh.Nrm[v * 3 + 1] = acc[vcp[v] * 3 + 1];
                mesh.Nrm[v * 3 + 2] = acc[vcp[v] * 3 + 2];
            }
        }
        for (int v = 0; v < nv; v++)
        {
            double x = mesh.Nrm[v * 3];
            double y = mesh.Nrm[v * 3 + 1];
            double z = mesh.Nrm[v * 3 + 2];
            double len = Math.Sqrt(x * x + y * y + z * z);
            if (len > 1e-12)
            {
                mesh.Nrm[v * 3] = x / len;
                mesh.Nrm[v * 3 + 1] = y / len;
                mesh.Nrm[v * 3 + 2] = z / len;
            }
            else
            {
                mesh.Nrm[v * 3] = 0;
                mesh.Nrm[v * 3 + 1] = 1;
                mesh.Nrm[v * 3 + 2] = 0;
            }
        }

        if (cpInfl != null)
        {
            mesh.Skinned = true;
            mesh.Jnt = new int[nv * 8];
            mesh.Wgt = new double[nv * 8];
            Array.Fill(mesh.Jnt, -1);
            for (int v = 0; v < nv; v++)
            {
                List<Influence> infl = cpInfl[vcp[v]];
                if (infl.Count == 0)
                {
                    continue;
                }
                List<Influence> ranked = new List<Influence>(infl);
                // stable, heaviest first (List.Sort is not stable: order by weight, then position)
                List<int> idx = new List<int>();
                for (int k = 0; k < ranked.Count; k++)
                {
                    idx.Add(k);
                }
                idx.Sort((a, b) =>
                {
                    int c = ranked[b].Weight.CompareTo(ranked[a].Weight);
                    if (c != 0)
                    {
                        return c;
                    }
                    return a.CompareTo(b);
                });
                int count = Math.Min(8, idx.Count);
                double tot = 0;
                for (int k = 0; k < count; k++)
                {
                    tot += ranked[idx[k]].Weight;
                }
                for (int k = 0; k < count; k++)
                {
                    mesh.Jnt[v * 8 + k] = ranked[idx[k]].Joint;
                    mesh.Wgt[v * 8 + k] = ranked[idx[k]].Weight / tot;
                }
            }
        }
    }
}
