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

        public int Compare(int leftRow, int rightRow)
        {
            for (int k = 0; k < Width; k++)
            {
                double x = Rows[leftRow * Width + k];
                double y = Rows[rightRow * Width + k];
                if (x < y)
                {
                    return -1;
                }
                if (x > y)
                {
                    return 1;
                }
            }
            return leftRow.CompareTo(rightRow); // stable: the first corner of a vertex comes first
        }
    }

    public static double RoundHalfToEven(double x, int decimals)
    {
        double factor = Math.Pow(10, decimals);
        return Math.Round(x * factor, MidpointRounding.ToEven) / factor;
    }

    // Polygons (first corner, corner count) -> triangle fans of corner indices, 3 a triangle.
    public static int[] FanTriangulate(int[] starts, int[] sizes)
    {
        List<int> triangles = new List<int>();
        for (int polygon = 0; polygon < starts.Length; polygon++)
        {
            for (int k = 1; k < sizes[polygon] - 1; k++)
            {
                triangles.Add(starts[polygon]);
                triangles.Add(starts[polygon] + k);
                triangles.Add(starts[polygon] + k + 1);
            }
        }
        return triangles.ToArray();
    }

    // Corners -> unique vertices: the first corner of each vertex (in sorted key order, as
    // numpy's unique), and the vertex of each corner.
    public static void Weld(int[] cornerCp, double[]? normals, double[]? uvs, out int[] first, out int[] inverse)
    {
        int cornerCount = cornerCp.Length;
        int width = 1;
        if (normals != null)
        {
            width += 3;
        }
        if (uvs != null)
        {
            width += 2;
        }
        double[] rows = new double[cornerCount * width];
        for (int i = 0; i < cornerCount; i++)
        {
            int rowOffset = i * width;
            rows[rowOffset++] = cornerCp[i];
            if (normals != null)
            {
                rows[rowOffset++] = RoundHalfToEven(normals[i * 3], 3);
                rows[rowOffset++] = RoundHalfToEven(normals[i * 3 + 1], 3);
                rows[rowOffset++] = RoundHalfToEven(normals[i * 3 + 2], 3);
            }
            if (uvs != null)
            {
                rows[rowOffset++] = RoundHalfToEven(uvs[i * 2], 5);
                rows[rowOffset++] = RoundHalfToEven(uvs[i * 2 + 1], 5);
            }
        }
        int[] permutation = new int[cornerCount];
        for (int i = 0; i < cornerCount; i++)
        {
            permutation[i] = i;
        }
        RowComparer comparer = new RowComparer();
        comparer.Rows = rows;
        comparer.Width = width;
        Array.Sort(permutation, comparer);

        List<int> firstList = new List<int>();
        inverse = new int[cornerCount];
        for (int k = 0; k < cornerCount; k++)
        {
            bool startsGroup = k == 0;
            if (!startsGroup)
            {
                int previousCorner = permutation[k - 1];
                int corner = permutation[k];
                for (int column = 0; column < width; column++)
                {
                    if (rows[previousCorner * width + column] != rows[corner * width + column])
                    {
                        startsGroup = true;
                        break;
                    }
                }
            }
            if (startsGroup)
            {
                firstList.Add(permutation[k]);
            }
            inverse[permutation[k]] = firstList.Count - 1;
        }
        first = firstList.ToArray();
    }

    /* controlPoints: 3 each; cornerCp: control point of each corner; cornerNrm/cornerUv: per
       corner or null; triCorners: corner indices, 3 a triangle; triangleMaterials: material per
       triangle; controlPointInfluences: per control point, or null for a rigid mesh. */
    public static void Build(SceneMesh mesh, double[] controlPoints, int[] cornerCp, double[]? cornerNrm, double[]? cornerUv,
        int[] triCorners, int[] triangleMaterials, List<Influence>[]? controlPointInfluences)
    {
        // Some exporters write one control point per corner; merge points at the same place
        // with the same influences, or no two triangles share a vertex (stripification, size).
        int controlPointCount = controlPoints.Length / 3;
        double largest = 0;
        for (int i = 0; i < controlPoints.Length; i++)
        {
            largest = Math.Max(largest, Math.Abs(controlPoints[i]));
        }
        double step = Math.Max(largest, 1e-9) * 1e-6;
        Dictionary<string, int> canonicalByKey = new Dictionary<string, int>();
        int[] canonicalPoint = new int[controlPointCount];
        System.Text.StringBuilder keyBuilder = new System.Text.StringBuilder();
        for (int i = 0; i < controlPointCount; i++)
        {
            keyBuilder.Clear();
            for (int component = 0; component < 3; component++)
            {
                keyBuilder.Append((long)Math.Round(controlPoints[i * 3 + component] / step, MidpointRounding.ToEven));
                keyBuilder.Append(',');
            }
            if (controlPointInfluences != null)
            {
                List<Influence> sorted = new List<Influence>(controlPointInfluences[i]);
                sorted.Sort((left, right) => left.Joint.CompareTo(right.Joint));
                foreach (Influence influence in sorted)
                {
                    keyBuilder.Append(influence.Joint);
                    keyBuilder.Append(':');
                    keyBuilder.Append(Math.Round(influence.Weight, 4, MidpointRounding.ToEven).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    keyBuilder.Append(';');
                }
            }
            string key = keyBuilder.ToString();
            if (canonicalByKey.TryGetValue(key, out int previousPoint))
            {
                canonicalPoint[i] = previousPoint;
            }
            else
            {
                canonicalByKey[key] = i;
                canonicalPoint[i] = i;
            }
        }
        int cornerCount = cornerCp.Length;
        int[] canonicalCornerPoints = new int[cornerCount];
        for (int i = 0; i < cornerCount; i++)
        {
            canonicalCornerPoints[i] = canonicalPoint[cornerCp[i]];
        }
        double[]? weldNormals = null;
        if (cornerNrm != null)
        {
            weldNormals = new double[cornerCount * 3];
            for (int i = 0; i < cornerCount; i++)
            {
                double x = cornerNrm[i * 3];
                double y = cornerNrm[i * 3 + 1];
                double z = cornerNrm[i * 3 + 2];
                double length = Math.Sqrt(x * x + y * y + z * z);
                if (length <= 1e-12)
                {
                    length = 1;
                }
                weldNormals[i * 3] = x / length;
                weldNormals[i * 3 + 1] = y / length;
                weldNormals[i * 3 + 2] = z / length;
            }
        }

        Weld(canonicalCornerPoints, weldNormals, cornerUv, out int[] firstCornerOfVertex, out int[] vertexOfCorner);
        int vertexCount = firstCornerOfVertex.Length;
        int[] vertexPoint = new int[vertexCount];
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            vertexPoint[vertex] = canonicalCornerPoints[firstCornerOfVertex[vertex]];
        }
        mesh.Positions = new double[vertexCount * 3];
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            mesh.Positions[vertex * 3] = controlPoints[vertexPoint[vertex] * 3];
            mesh.Positions[vertex * 3 + 1] = controlPoints[vertexPoint[vertex] * 3 + 1];
            mesh.Positions[vertex * 3 + 2] = controlPoints[vertexPoint[vertex] * 3 + 2];
        }
        mesh.Triangles = new int[triCorners.Length];
        for (int i = 0; i < triCorners.Length; i++)
        {
            mesh.Triangles[i] = vertexOfCorner[triCorners[i]];
        }
        mesh.TriangleMaterials = (int[])triangleMaterials.Clone();
        mesh.Uvs = new double[vertexCount * 2];
        if (cornerUv != null)
        {
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                mesh.Uvs[vertex * 2] = cornerUv[firstCornerOfVertex[vertex] * 2];
                mesh.Uvs[vertex * 2 + 1] = cornerUv[firstCornerOfVertex[vertex] * 2 + 1];
            }
        }
        mesh.Normals = new double[vertexCount * 3];
        if (weldNormals != null)
        {
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                mesh.Normals[vertex * 3] = weldNormals[firstCornerOfVertex[vertex] * 3];
                mesh.Normals[vertex * 3 + 1] = weldNormals[firstCornerOfVertex[vertex] * 3 + 1];
                mesh.Normals[vertex * 3 + 2] = weldNormals[firstCornerOfVertex[vertex] * 3 + 2];
            }
        }
        else
        {
            // smooth normals per control point, area weighted
            double[] accumulatedNormals = new double[controlPoints.Length];
            for (int triangle = 0; triangle < triCorners.Length / 3; triangle++)
            {
                int point0 = canonicalCornerPoints[triCorners[triangle * 3]];
                int point1 = canonicalCornerPoints[triCorners[triangle * 3 + 1]];
                int point2 = canonicalCornerPoints[triCorners[triangle * 3 + 2]];
                double edge1X = controlPoints[point1 * 3] - controlPoints[point0 * 3];
                double edge1Y = controlPoints[point1 * 3 + 1] - controlPoints[point0 * 3 + 1];
                double edge1Z = controlPoints[point1 * 3 + 2] - controlPoints[point0 * 3 + 2];
                double edge2X = controlPoints[point2 * 3] - controlPoints[point0 * 3];
                double edge2Y = controlPoints[point2 * 3 + 1] - controlPoints[point0 * 3 + 1];
                double edge2Z = controlPoints[point2 * 3 + 2] - controlPoints[point0 * 3 + 2];
                double faceNormalX = edge1Y * edge2Z - edge1Z * edge2Y;
                double faceNormalY = edge1Z * edge2X - edge1X * edge2Z;
                double faceNormalZ = edge1X * edge2Y - edge1Y * edge2X;
                foreach (int point in new[] { point0, point1, point2 })
                {
                    accumulatedNormals[point * 3] += faceNormalX;
                    accumulatedNormals[point * 3 + 1] += faceNormalY;
                    accumulatedNormals[point * 3 + 2] += faceNormalZ;
                }
            }
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                mesh.Normals[vertex * 3] = accumulatedNormals[vertexPoint[vertex] * 3];
                mesh.Normals[vertex * 3 + 1] = accumulatedNormals[vertexPoint[vertex] * 3 + 1];
                mesh.Normals[vertex * 3 + 2] = accumulatedNormals[vertexPoint[vertex] * 3 + 2];
            }
        }
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            double x = mesh.Normals[vertex * 3];
            double y = mesh.Normals[vertex * 3 + 1];
            double z = mesh.Normals[vertex * 3 + 2];
            double length = Math.Sqrt(x * x + y * y + z * z);
            if (length > 1e-12)
            {
                mesh.Normals[vertex * 3] = x / length;
                mesh.Normals[vertex * 3 + 1] = y / length;
                mesh.Normals[vertex * 3 + 2] = z / length;
            }
            else
            {
                mesh.Normals[vertex * 3] = 0;
                mesh.Normals[vertex * 3 + 1] = 1;
                mesh.Normals[vertex * 3 + 2] = 0;
            }
        }

        if (controlPointInfluences != null)
        {
            mesh.Skinned = true;
            mesh.Joints = new int[vertexCount * 8];
            mesh.Weights = new double[vertexCount * 8];
            Array.Fill(mesh.Joints, -1);
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                List<Influence> influences = controlPointInfluences[vertexPoint[vertex]];
                if (influences.Count == 0)
                {
                    continue;
                }
                List<Influence> ranked = new List<Influence>(influences);
                // stable, heaviest first (List.Sort is not stable: order by weight, then position)
                List<int> rankOrder = new List<int>();
                for (int k = 0; k < ranked.Count; k++)
                {
                    rankOrder.Add(k);
                }
                rankOrder.Sort((left, right) =>
                {
                    int comparison = ranked[right].Weight.CompareTo(ranked[left].Weight);
                    if (comparison != 0)
                    {
                        return comparison;
                    }
                    return left.CompareTo(right);
                });
                int count = Math.Min(8, rankOrder.Count);
                double totalWeight = 0;
                for (int k = 0; k < count; k++)
                {
                    totalWeight += ranked[rankOrder[k]].Weight;
                }
                for (int k = 0; k < count; k++)
                {
                    mesh.Joints[vertex * 8 + k] = ranked[rankOrder[k]].Joint;
                    mesh.Weights[vertex * 8 + k] = ranked[rankOrder[k]].Weight / totalWeight;
                }
            }
        }
    }
}
