namespace DotrAssetModder;
// Mostly AI

/* A 4x4 double matrix, row-major, column vectors (p' = M p), as numpy in tools/toglb.py
   (DotR-PC): M[r * 4 + c]. Doubles, not System.Numerics' float Matrix4x4, so the conversion
   matches the Python reference to float precision. */
public sealed class Matrix4
{
    public double[] Elements = new double[16];

    // numpy.allclose: every element within atol + 1e-5 * |b|.
    public static bool AllClose(Matrix4 left, Matrix4 right, double absoluteTolerance)
    {
        for (int k = 0; k < 16; k++)
        {
            if (Math.Abs(left.Elements[k] - right.Elements[k]) > absoluteTolerance + 1e-5 * Math.Abs(right.Elements[k]))
            {
                return false;
            }
        }
        return true;
    }

    public static Matrix4 Identity()
    {
        Matrix4 result = new Matrix4();
        result.Elements[0] = 1;
        result.Elements[5] = 1;
        result.Elements[10] = 1;
        result.Elements[15] = 1;
        return result;
    }

    public double this[int row, int column]
    {
        get { return Elements[row * 4 + column]; }
        set { Elements[row * 4 + column] = value; }
    }

    public Matrix4 Copy()
    {
        Matrix4 copy = new Matrix4();
        Array.Copy(Elements, copy.Elements, 16);
        return copy;
    }

    public static Matrix4 operator *(Matrix4 left, Matrix4 right)
    {
        Matrix4 product = new Matrix4();
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                double sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    sum += left.Elements[row * 4 + k] * right.Elements[k * 4 + column];
                }
                product.Elements[row * 4 + column] = sum;
            }
        }
        return product;
    }

    public static Matrix4 Translation(double x, double y, double z)
    {
        Matrix4 result = Identity();
        result.Elements[3] = x;
        result.Elements[7] = y;
        result.Elements[11] = z;
        return result;
    }

    public static Matrix4 Scale(double x, double y, double z)
    {
        Matrix4 result = Identity();
        result.Elements[0] = x;
        result.Elements[5] = y;
        result.Elements[10] = z;
        return result;
    }

    // toglb mrot: turn about an axis, in degrees
    public static Matrix4 Rotation(double axisX, double axisY, double axisZ, double degrees)
    {
        double length = Math.Sqrt(axisX * axisX + axisY * axisY + axisZ * axisZ);
        double x = axisX / length;
        double y = axisY / length;
        double z = axisZ / length;
        double radians = degrees * Math.PI / 180.0;
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        double oneMinusCosine = 1 - cosine;
        Matrix4 result = Identity();
        result.Elements[0] = oneMinusCosine * x * x + cosine;
        result.Elements[1] = oneMinusCosine * x * y - sine * z;
        result.Elements[2] = oneMinusCosine * x * z + sine * y;
        result.Elements[4] = oneMinusCosine * x * y + sine * z;
        result.Elements[5] = oneMinusCosine * y * y + cosine;
        result.Elements[6] = oneMinusCosine * y * z - sine * x;
        result.Elements[8] = oneMinusCosine * x * z - sine * y;
        result.Elements[9] = oneMinusCosine * y * z + sine * x;
        result.Elements[10] = oneMinusCosine * z * z + cosine;
        return result;
    }

    // toglb euler: Euler angles in degrees, the first letter of order applied first
    public static Matrix4 EulerAngles(double degreesX, double degreesY, double degreesZ, string order = "XYZ")
    {
        Matrix4 result = Identity();
        foreach (char axisLetter in order)
        {
            double degrees;
            int i;
            int j;
            if (axisLetter == 'X')
            {
                degrees = degreesX;
                i = 1;
                j = 2;
            }
            else if (axisLetter == 'Y')
            {
                degrees = degreesY;
                i = 2;
                j = 0;
            }
            else
            {
                degrees = degreesZ;
                i = 0;
                j = 1;
            }
            double radians = degrees * Math.PI / 180.0;
            Matrix4 axisRotation = Identity();
            axisRotation.Elements[i * 4 + i] = Math.Cos(radians);
            axisRotation.Elements[i * 4 + j] = -Math.Sin(radians);
            axisRotation.Elements[j * 4 + i] = Math.Sin(radians);
            axisRotation.Elements[j * 4 + j] = Math.Cos(radians);
            result = axisRotation * result;
        }
        return result;
    }

    public Matrix4 Inverse()
    {
        // Gauss-Jordan with partial pivoting (numpy's inv is LU with pivoting)
        double[] work = new double[16];
        Array.Copy(Elements, work, 16);
        Matrix4 inverse = Identity();
        double[] inverseElements = inverse.Elements;
        for (int column = 0; column < 4; column++)
        {
            int pivotRow = column;
            for (int row = column + 1; row < 4; row++)
            {
                if (Math.Abs(work[row * 4 + column]) > Math.Abs(work[pivotRow * 4 + column]))
                {
                    pivotRow = row;
                }
            }
            if (work[pivotRow * 4 + column] == 0)
            {
                throw new InvalidOperationException("singular matrix");
            }
            if (pivotRow != column)
            {
                for (int k = 0; k < 4; k++)
                {
                    (work[column * 4 + k], work[pivotRow * 4 + k]) = (work[pivotRow * 4 + k], work[column * 4 + k]);
                    (inverseElements[column * 4 + k], inverseElements[pivotRow * 4 + k]) = (inverseElements[pivotRow * 4 + k], inverseElements[column * 4 + k]);
                }
            }
            double pivot = work[column * 4 + column];
            for (int k = 0; k < 4; k++)
            {
                work[column * 4 + k] /= pivot;
                inverseElements[column * 4 + k] /= pivot;
            }
            for (int row = 0; row < 4; row++)
            {
                if (row == column)
                {
                    continue;
                }
                double factor = work[row * 4 + column];
                if (factor == 0)
                {
                    continue;
                }
                for (int k = 0; k < 4; k++)
                {
                    work[row * 4 + k] -= factor * work[column * 4 + k];
                    inverseElements[row * 4 + k] -= factor * inverseElements[column * 4 + k];
                }
            }
        }
        return inverse;
    }

    public double Determinant3x3()
    {
        return Elements[0] * (Elements[5] * Elements[10] - Elements[6] * Elements[9])
             - Elements[1] * (Elements[4] * Elements[10] - Elements[6] * Elements[8])
             + Elements[2] * (Elements[4] * Elements[9] - Elements[5] * Elements[8]);
    }

    public void TransformPoint(double x, double y, double z, out double resultX, out double resultY, out double resultZ)
    {
        resultX = Elements[0] * x + Elements[1] * y + Elements[2] * z + Elements[3];
        resultY = Elements[4] * x + Elements[5] * y + Elements[6] * z + Elements[7];
        resultZ = Elements[8] * x + Elements[9] * y + Elements[10] * z + Elements[11];
    }

    public void TransformDir(double x, double y, double z, out double resultX, out double resultY, out double resultZ)
    {
        resultX = Elements[0] * x + Elements[1] * y + Elements[2] * z;
        resultY = Elements[4] * x + Elements[5] * y + Elements[6] * z;
        resultZ = Elements[8] * x + Elements[9] * y + Elements[10] * z;
    }

    /* toglb decompose: translation, quaternion (x, y, z, w), scale; shear is dropped. A
       mirrored basis keeps a negative X scale. The rotation is the nearest orthonormal one
       (numpy: U Vt of the SVD), found here by polar iteration, which gives the same matrix. */
    public void Decompose(double[] translation, double[] rotation, double[] scale)
    {
        translation[0] = Elements[3];
        translation[1] = Elements[7];
        translation[2] = Elements[11];
        double[] rotationMatrix = new double[9];
        for (int column = 0; column < 3; column++)
        {
            double columnLength = Math.Sqrt(Elements[column] * Elements[column] + Elements[4 + column] * Elements[4 + column] + Elements[8 + column] * Elements[8 + column]);
            scale[column] = columnLength;
            double divisor = 1.0;
            if (columnLength > 1e-12)
            {
                divisor = columnLength;
            }
            for (int k = 0; k < 3; k++)
            {
                rotationMatrix[k * 3 + column] = Elements[k * 4 + column] / divisor;
            }
        }
        if (Det3x3(rotationMatrix) < 0)
        {
            scale[0] = -scale[0];
            for (int k = 0; k < 3; k++)
            {
                rotationMatrix[k * 3] = -rotationMatrix[k * 3];
            }
        }
        Orthonormalise(rotationMatrix);
        if (Det3x3(rotationMatrix) < 0)
        {
            rotationMatrix = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        }
        ToQuaternion(rotationMatrix, rotation);
    }

    static double Det3x3(double[] rotation)
    {
        return rotation[0] * (rotation[4] * rotation[8] - rotation[5] * rotation[7])
             - rotation[1] * (rotation[3] * rotation[8] - rotation[5] * rotation[6])
             + rotation[2] * (rotation[3] * rotation[7] - rotation[4] * rotation[6]);
    }

    // Polar decomposition by Newton iteration, R <- (R + R^-T) / 2: converges to U Vt.
    static void Orthonormalise(double[] rotation)
    {
        double determinant = Det3x3(rotation);
        if (Math.Abs(determinant) < 1e-12)
        {
            return;
        }
        for (int iteration = 0; iteration < 50; iteration++)
        {
            double iterationDeterminant = Det3x3(rotation);
            // inverse transpose = cofactor matrix / det
            double[] cofactors = new double[]
            {
                rotation[4] * rotation[8] - rotation[5] * rotation[7], rotation[5] * rotation[6] - rotation[3] * rotation[8], rotation[3] * rotation[7] - rotation[4] * rotation[6],
                rotation[2] * rotation[7] - rotation[1] * rotation[8], rotation[0] * rotation[8] - rotation[2] * rotation[6], rotation[1] * rotation[6] - rotation[0] * rotation[7],
                rotation[1] * rotation[5] - rotation[2] * rotation[4], rotation[2] * rotation[3] - rotation[0] * rotation[5], rotation[0] * rotation[4] - rotation[1] * rotation[3],
            };
            double change = 0;
            for (int k = 0; k < 9; k++)
            {
                double averaged = (rotation[k] + cofactors[k] / iterationDeterminant) / 2;
                change = Math.Max(change, Math.Abs(averaged - rotation[k]));
                rotation[k] = averaged;
            }
            if (change < 1e-15)
            {
                break;
            }
        }
    }

    // toglb quat_many for one 3x3 rotation (row-major) -> (x, y, z, w)
    static void ToQuaternion(double[] rotation, double[] quaternion)
    {
        double r00 = rotation[0];
        double r11 = rotation[4];
        double r22 = rotation[8];
        double[] candidates = { 1 + r00 + r11 + r22, 1 + r00 - r11 - r22, 1 - r00 + r11 - r22, 1 - r00 - r11 + r22 };
        int largest = 0;
        for (int k = 1; k < 4; k++)
        {
            if (candidates[k] > candidates[largest])
            {
                largest = k;
            }
        }
        if (largest == 0)
        {
            double w = Math.Sqrt(Math.Max(candidates[0], 1e-12)) / 2;
            quaternion[0] = (rotation[7] - rotation[5]) / (4 * w);
            quaternion[1] = (rotation[2] - rotation[6]) / (4 * w);
            quaternion[2] = (rotation[3] - rotation[1]) / (4 * w);
            quaternion[3] = w;
        }
        else if (largest == 1)
        {
            double x = Math.Sqrt(Math.Max(candidates[1], 1e-12)) / 2;
            quaternion[0] = x;
            quaternion[1] = (rotation[1] + rotation[3]) / (4 * x);
            quaternion[2] = (rotation[2] + rotation[6]) / (4 * x);
            quaternion[3] = (rotation[7] - rotation[5]) / (4 * x);
        }
        else if (largest == 2)
        {
            double y = Math.Sqrt(Math.Max(candidates[2], 1e-12)) / 2;
            quaternion[0] = (rotation[1] + rotation[3]) / (4 * y);
            quaternion[1] = y;
            quaternion[2] = (rotation[5] + rotation[7]) / (4 * y);
            quaternion[3] = (rotation[2] - rotation[6]) / (4 * y);
        }
        else
        {
            double z = Math.Sqrt(Math.Max(candidates[3], 1e-12)) / 2;
            quaternion[0] = (rotation[2] + rotation[6]) / (4 * z);
            quaternion[1] = (rotation[5] + rotation[7]) / (4 * z);
            quaternion[2] = z;
            quaternion[3] = (rotation[3] - rotation[1]) / (4 * z);
        }
        double length = Math.Sqrt(quaternion[0] * quaternion[0] + quaternion[1] * quaternion[1] + quaternion[2] * quaternion[2] + quaternion[3] * quaternion[3]);
        for (int k = 0; k < 4; k++)
        {
            quaternion[k] /= length;
        }
    }

    // toglb trs_many for one frame
    public static Matrix4 FromTrs(double[] translation, double[] rotation, double[] scale)
    {
        double length = Math.Sqrt(rotation[0] * rotation[0] + rotation[1] * rotation[1] + rotation[2] * rotation[2] + rotation[3] * rotation[3]);
        double x = rotation[0] / length;
        double y = rotation[1] / length;
        double z = rotation[2] / length;
        double w = rotation[3] / length;
        Matrix4 result = Identity();
        result.Elements[0] = (1 - 2 * (y * y + z * z)) * scale[0];
        result.Elements[1] = 2 * (x * y - z * w) * scale[1];
        result.Elements[2] = 2 * (x * z + y * w) * scale[2];
        result.Elements[4] = 2 * (x * y + z * w) * scale[0];
        result.Elements[5] = (1 - 2 * (x * x + z * z)) * scale[1];
        result.Elements[6] = 2 * (y * z - x * w) * scale[2];
        result.Elements[8] = 2 * (x * z - y * w) * scale[0];
        result.Elements[9] = 2 * (y * z + x * w) * scale[1];
        result.Elements[10] = (1 - 2 * (x * x + y * y)) * scale[2];
        result.Elements[3] = translation[0];
        result.Elements[7] = translation[1];
        result.Elements[11] = translation[2];
        return result;
    }
}
