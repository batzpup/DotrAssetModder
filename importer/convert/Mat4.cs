namespace DotrAssetModder;
// Mostly AI

// A 4x4 double matrix, row-major, column vectors (p' = M p), as numpy in tools/toglb.py
// (DotR-PC): M[r * 4 + c]. Doubles, not System.Numerics' float Matrix4x4, so the conversion
// matches the Python reference to float precision.
public sealed class Mat4
{
    public double[] M = new double[16];

    // numpy.allclose: every element within atol + 1e-5 * |b|.
    public static bool AllClose(Mat4 a, Mat4 b, double atol)
    {
        for (int k = 0; k < 16; k++)
        {
            if (Math.Abs(a.M[k] - b.M[k]) > atol + 1e-5 * Math.Abs(b.M[k]))
            {
                return false;
            }
        }
        return true;
    }

    public static Mat4 Identity()
    {
        Mat4 m = new Mat4();
        m.M[0] = 1;
        m.M[5] = 1;
        m.M[10] = 1;
        m.M[15] = 1;
        return m;
    }

    public double this[int r, int c]
    {
        get { return M[r * 4 + c]; }
        set { M[r * 4 + c] = value; }
    }

    public Mat4 Copy()
    {
        Mat4 m = new Mat4();
        Array.Copy(M, m.M, 16);
        return m;
    }

    public static Mat4 operator *(Mat4 a, Mat4 b)
    {
        Mat4 o = new Mat4();
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                double s = 0;
                for (int k = 0; k < 4; k++)
                {
                    s += a.M[r * 4 + k] * b.M[k * 4 + c];
                }
                o.M[r * 4 + c] = s;
            }
        }
        return o;
    }

    public static Mat4 Translation(double x, double y, double z)
    {
        Mat4 m = Identity();
        m.M[3] = x;
        m.M[7] = y;
        m.M[11] = z;
        return m;
    }

    public static Mat4 Scale(double x, double y, double z)
    {
        Mat4 m = Identity();
        m.M[0] = x;
        m.M[5] = y;
        m.M[10] = z;
        return m;
    }

    // toglb mrot: turn about an axis, in degrees
    public static Mat4 Rotation(double ax, double ay, double az, double deg)
    {
        double len = Math.Sqrt(ax * ax + ay * ay + az * az);
        double x = ax / len;
        double y = ay / len;
        double z = az / len;
        double a = deg * Math.PI / 180.0;
        double c = Math.Cos(a);
        double s = Math.Sin(a);
        double t = 1 - c;
        Mat4 m = Identity();
        m.M[0] = t * x * x + c;
        m.M[1] = t * x * y - s * z;
        m.M[2] = t * x * z + s * y;
        m.M[4] = t * x * y + s * z;
        m.M[5] = t * y * y + c;
        m.M[6] = t * y * z - s * x;
        m.M[8] = t * x * z - s * y;
        m.M[9] = t * y * z + s * x;
        m.M[10] = t * z * z + c;
        return m;
    }

    // toglb euler: Euler angles in degrees, the first letter of order applied first
    public static Mat4 Euler(double dx, double dy, double dz, string order = "XYZ")
    {
        Mat4 m = Identity();
        foreach (char ch in order)
        {
            double deg;
            int i;
            int j;
            if (ch == 'X')
            {
                deg = dx;
                i = 1;
                j = 2;
            }
            else if (ch == 'Y')
            {
                deg = dy;
                i = 2;
                j = 0;
            }
            else
            {
                deg = dz;
                i = 0;
                j = 1;
            }
            double a = deg * Math.PI / 180.0;
            Mat4 r = Identity();
            r.M[i * 4 + i] = Math.Cos(a);
            r.M[i * 4 + j] = -Math.Sin(a);
            r.M[j * 4 + i] = Math.Sin(a);
            r.M[j * 4 + j] = Math.Cos(a);
            m = r * m;
        }
        return m;
    }

    public Mat4 Inverse()
    {
        // Gauss-Jordan with partial pivoting (numpy's inv is LU with pivoting)
        double[] a = new double[16];
        Array.Copy(M, a, 16);
        Mat4 inv = Identity();
        double[] b = inv.M;
        for (int col = 0; col < 4; col++)
        {
            int piv = col;
            for (int r = col + 1; r < 4; r++)
            {
                if (Math.Abs(a[r * 4 + col]) > Math.Abs(a[piv * 4 + col]))
                {
                    piv = r;
                }
            }
            if (a[piv * 4 + col] == 0)
            {
                throw new InvalidOperationException("singular matrix");
            }
            if (piv != col)
            {
                for (int k = 0; k < 4; k++)
                {
                    (a[col * 4 + k], a[piv * 4 + k]) = (a[piv * 4 + k], a[col * 4 + k]);
                    (b[col * 4 + k], b[piv * 4 + k]) = (b[piv * 4 + k], b[col * 4 + k]);
                }
            }
            double d = a[col * 4 + col];
            for (int k = 0; k < 4; k++)
            {
                a[col * 4 + k] /= d;
                b[col * 4 + k] /= d;
            }
            for (int r = 0; r < 4; r++)
            {
                if (r == col)
                {
                    continue;
                }
                double f = a[r * 4 + col];
                if (f == 0)
                {
                    continue;
                }
                for (int k = 0; k < 4; k++)
                {
                    a[r * 4 + k] -= f * a[col * 4 + k];
                    b[r * 4 + k] -= f * b[col * 4 + k];
                }
            }
        }
        return inv;
    }

    public double Det3()
    {
        return M[0] * (M[5] * M[10] - M[6] * M[9])
             - M[1] * (M[4] * M[10] - M[6] * M[8])
             + M[2] * (M[4] * M[9] - M[5] * M[8]);
    }

    public void TransformPoint(double x, double y, double z, out double ox, out double oy, out double oz)
    {
        ox = M[0] * x + M[1] * y + M[2] * z + M[3];
        oy = M[4] * x + M[5] * y + M[6] * z + M[7];
        oz = M[8] * x + M[9] * y + M[10] * z + M[11];
    }

    public void TransformDir(double x, double y, double z, out double ox, out double oy, out double oz)
    {
        ox = M[0] * x + M[1] * y + M[2] * z;
        oy = M[4] * x + M[5] * y + M[6] * z;
        oz = M[8] * x + M[9] * y + M[10] * z;
    }

    // toglb decompose: translation, quaternion (x, y, z, w), scale; shear is dropped. A
    // mirrored basis keeps a negative X scale. The rotation is the nearest orthonormal one
    // (numpy: U Vt of the SVD), found here by polar iteration, which gives the same matrix.
    public void Decompose(double[] t, double[] q, double[] s)
    {
        t[0] = M[3];
        t[1] = M[7];
        t[2] = M[11];
        double[] r = new double[9];
        for (int c = 0; c < 3; c++)
        {
            double n = Math.Sqrt(M[c] * M[c] + M[4 + c] * M[4 + c] + M[8 + c] * M[8 + c]);
            s[c] = n;
            double d = 1.0;
            if (n > 1e-12)
            {
                d = n;
            }
            for (int k = 0; k < 3; k++)
            {
                r[k * 3 + c] = M[k * 4 + c] / d;
            }
        }
        if (Det3x3(r) < 0)
        {
            s[0] = -s[0];
            for (int k = 0; k < 3; k++)
            {
                r[k * 3] = -r[k * 3];
            }
        }
        Orthonormalise(r);
        if (Det3x3(r) < 0)
        {
            r = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        }
        Quat(r, q);
    }

    static double Det3x3(double[] r)
    {
        return r[0] * (r[4] * r[8] - r[5] * r[7])
             - r[1] * (r[3] * r[8] - r[5] * r[6])
             + r[2] * (r[3] * r[7] - r[4] * r[6]);
    }

    // Polar decomposition by Newton iteration, R <- (R + R^-T) / 2: converges to U Vt.
    static void Orthonormalise(double[] r)
    {
        double det = Det3x3(r);
        if (Math.Abs(det) < 1e-12)
        {
            return;
        }
        for (int it = 0; it < 50; it++)
        {
            double d = Det3x3(r);
            // inverse transpose = cofactor matrix / det
            double[] cof = new double[]
            {
                r[4] * r[8] - r[5] * r[7], r[5] * r[6] - r[3] * r[8], r[3] * r[7] - r[4] * r[6],
                r[2] * r[7] - r[1] * r[8], r[0] * r[8] - r[2] * r[6], r[1] * r[6] - r[0] * r[7],
                r[1] * r[5] - r[2] * r[4], r[2] * r[3] - r[0] * r[5], r[0] * r[4] - r[1] * r[3],
            };
            double change = 0;
            for (int k = 0; k < 9; k++)
            {
                double v = (r[k] + cof[k] / d) / 2;
                change = Math.Max(change, Math.Abs(v - r[k]));
                r[k] = v;
            }
            if (change < 1e-15)
            {
                break;
            }
        }
    }

    // toglb quat_many for one 3x3 rotation (row-major) -> (x, y, z, w)
    static void Quat(double[] m, double[] q)
    {
        double r00 = m[0];
        double r11 = m[4];
        double r22 = m[8];
        double[] cand = { 1 + r00 + r11 + r22, 1 + r00 - r11 - r22, 1 - r00 + r11 - r22, 1 - r00 - r11 + r22 };
        int big = 0;
        for (int k = 1; k < 4; k++)
        {
            if (cand[k] > cand[big])
            {
                big = k;
            }
        }
        if (big == 0)
        {
            double w = Math.Sqrt(Math.Max(cand[0], 1e-12)) / 2;
            q[0] = (m[7] - m[5]) / (4 * w);
            q[1] = (m[2] - m[6]) / (4 * w);
            q[2] = (m[3] - m[1]) / (4 * w);
            q[3] = w;
        }
        else if (big == 1)
        {
            double x = Math.Sqrt(Math.Max(cand[1], 1e-12)) / 2;
            q[0] = x;
            q[1] = (m[1] + m[3]) / (4 * x);
            q[2] = (m[2] + m[6]) / (4 * x);
            q[3] = (m[7] - m[5]) / (4 * x);
        }
        else if (big == 2)
        {
            double y = Math.Sqrt(Math.Max(cand[2], 1e-12)) / 2;
            q[0] = (m[1] + m[3]) / (4 * y);
            q[1] = y;
            q[2] = (m[5] + m[7]) / (4 * y);
            q[3] = (m[2] - m[6]) / (4 * y);
        }
        else
        {
            double z = Math.Sqrt(Math.Max(cand[3], 1e-12)) / 2;
            q[0] = (m[2] + m[6]) / (4 * z);
            q[1] = (m[5] + m[7]) / (4 * z);
            q[2] = z;
            q[3] = (m[3] - m[1]) / (4 * z);
        }
        double len = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
        for (int k = 0; k < 4; k++)
        {
            q[k] /= len;
        }
    }

    // toglb trs_many for one frame
    public static Mat4 FromTrs(double[] t, double[] q, double[] s)
    {
        double len = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
        double x = q[0] / len;
        double y = q[1] / len;
        double z = q[2] / len;
        double w = q[3] / len;
        Mat4 m = Identity();
        m.M[0] = (1 - 2 * (y * y + z * z)) * s[0];
        m.M[1] = 2 * (x * y - z * w) * s[1];
        m.M[2] = 2 * (x * z + y * w) * s[2];
        m.M[4] = 2 * (x * y + z * w) * s[0];
        m.M[5] = (1 - 2 * (x * x + z * z)) * s[1];
        m.M[6] = 2 * (y * z - x * w) * s[2];
        m.M[8] = 2 * (x * z - y * w) * s[0];
        m.M[9] = 2 * (y * z + x * w) * s[1];
        m.M[10] = (1 - 2 * (x * x + y * y)) * s[2];
        m.M[3] = t[0];
        m.M[7] = t[1];
        m.M[11] = t[2];
        return m;
    }
}
