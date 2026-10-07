using System.Numerics;

namespace Puck.SignedDistance;

public static partial class SdfSkyEnvironment {
    /// <summary>One eight-bit display code in linear irradiance.</summary>
    public const float DisplayCode = (1f / 255f);

    /// <summary>Evaluates diffuse irradiance by convolving the three radiance bands with the cosine kernel:
    /// π, 2π/3 and π/4. A constant radiance C gives πC in every direction.</summary>
    /// <param name="coefficients">The nine radiance coefficients.</param>
    /// <param name="normal">The unit surface normal.</param>
    /// <returns>The nonnegative irradiance.</returns>
    public static Vector3 Irradiance(ReadOnlySpan<Vector3> coefficients, Vector3 normal) {
        Span<double> basis = stackalloc double[CoefficientCount];

        Basis(basis: basis, direction: normal);
        var result = Vector3.Zero;

        for (var k = 0; (k < CoefficientCount); k++) {
            result += (coefficients[k] * ((float)(basis[k] * Convolution(band: k))));
        }
        return Vector3.Max(value1: result, value2: Vector3.Zero);
    }
    /// <summary>Returns the largest absolute irradiance difference over all unit normals and RGB channels.
    /// Each channel is a quadratic on the sphere; its extrema are the two trust-region solves.</summary>
    /// <param name="coefficients">One sky's coefficients.</param>
    /// <param name="other">The rendered sky's coefficients.</param>
    /// <returns>The largest difference before nonnegative clamping.</returns>
    public static double IrradianceDifference(ReadOnlySpan<Vector3> coefficients, ReadOnlySpan<Vector3> other) {
        Span<double> c = stackalloc double[CoefficientCount];
        Span<double> matrix = stackalloc double[9];
        Span<double> linear = stackalloc double[3];
        var maximum = 0d;

        for (var channel = 0; (channel < 3); channel++) {
            for (var k = 0; (k < CoefficientCount); k++) {
                c[k] = ((((double)coefficients[k][channel]) - other[k][channel]) * Convolution(band: k));
            }
            for (var sign = -1; (sign <= 1); sign += 2) {
                matrix[0] = ((sign * c[8]) * Band2Square);
                matrix[4] = -matrix[0];
                matrix[8] = (((sign * 3d) * c[6]) * Band2Zonal);
                matrix[1] = matrix[3] = (((sign * c[4]) * Band2Cross) * 0.5d);
                matrix[2] = matrix[6] = (((sign * c[7]) * Band2Cross) * 0.5d);
                matrix[5] = matrix[7] = (((sign * c[5]) * Band2Cross) * 0.5d);
                linear[0] = ((sign * c[3]) * Band1);
                linear[1] = ((sign * c[1]) * Band1);
                linear[2] = ((sign * c[2]) * Band1);
                var constant = (sign * ((c[0] * Band0) - (c[6] * Band2Zonal)));

                maximum = Math.Max(val1: maximum, val2: (constant + QuadraticMaximum(linear: linear, matrix: matrix)));
            }
        }
        return maximum;
    }
    /// <summary>Evaluates a panel in its layer frame, with the same angular rectangle and energy compensation
    /// as its kernel. This is the reference used by environment projection.</summary>
    /// <param name="panel">The packed panel.</param>
    /// <param name="direction">The unit layer-frame direction.</param>
    /// <param name="roughness">The reflection roughness, zero for ambient projection.</param>
    /// <returns>The radiance and coverage.</returns>
    public static Vector4 Panel(in SdfSkyPanel panel, Vector3 direction, float roughness = 0f) {
        var facing = Vector3.Dot(vector1: direction, vector2: panel.Direction);

        if (!(facing > 0f) || !(panel.Intensity > 0f)) {
            return Vector4.Zero;
        }
        var axis = ((MathF.Abs(x: panel.Direction.Y) < 0.999f) ? Vector3.UnitY : Vector3.UnitZ);
        var right = Vector3.Normalize(value: Vector3.Cross(vector1: axis, vector2: panel.Direction));
        var up = Vector3.Cross(vector1: panel.Direction, vector2: right);
        var angle = Vector2.Abs(value: new Vector2(x: MathF.Atan2(Vector3.Dot(vector1: direction, vector2: right), facing), y: MathF.Atan2(Vector3.Dot(vector1: direction, vector2: up), facing)));
        var width = MathF.Max(x: panel.Blur, y: roughness);
        var extent = (panel.Size + new Vector2(value: roughness));
        var alpha = ((1f - Rise(edge: (extent.X + width), soft: width, value: angle.X)) * (1f - Rise(edge: (extent.Y + width), soft: width, value: angle.Y)));
        var gain = ((panel.Size.X * panel.Size.Y) / MathF.Max(x: (extent.X * extent.Y), y: 1e-12f));

        return new Vector4(value: (panel.Color * (panel.Intensity * gain)), w: alpha);
    }

    private static double Convolution(int band) => ((band == 0) ? Math.PI : ((band < 4) ? ((2d * Math.PI) / 3d) : (Math.PI / 4d)));
    // Jacobi diagonalization rotates the linear term with the quadratic. In that basis the maximizing normal is
    // b_i / (2(lambda - eigenvalue_i)); choose lambda above the largest eigenvalue so its squared norm is one.
    // If the linear term vanishes in the largest eigenspace, that space takes the unused norm (the hard case).
    private static double QuadraticMaximum(Span<double> matrix, Span<double> linear) {
        for (var sweep = 0; (sweep < 16); sweep++) {
            for (var p = 0; (p < 2); p++) {
                for (var q = (p + 1); (q < 3); q++) {
                    var off = matrix[((p * 3) + q)];

                    if (Math.Abs(value: off) <= 1e-16) {
                        continue;
                    }
                    var angle = (0.5d * Math.Atan2((2d * off), (matrix[((q * 3) + q)] - matrix[((p * 3) + p)])));

                    var (sin, cos) = Math.SinCos(x: angle);
                    for (var row = 0; (row < 3); row++) {
                        var a = matrix[((row * 3) + p)];
                        var b = matrix[((row * 3) + q)];

                        matrix[((row * 3) + p)] = ((cos * a) - (sin * b));
                        matrix[((row * 3) + q)] = ((sin * a) + (cos * b));
                    }
                    for (var column = 0; (column < 3); column++) {
                        var a = matrix[((p * 3) + column)];
                        var b = matrix[((q * 3) + column)];

                        matrix[((p * 3) + column)] = ((cos * a) - (sin * b));
                        matrix[((q * 3) + column)] = ((sin * a) + (cos * b));
                    }
                    var lp = linear[p];

                    linear[p] = ((cos * lp) - (sin * linear[q]));
                    linear[q] = ((sin * lp) + (cos * linear[q]));
                }
            }
        }
        var top = Math.Max(val1: matrix[0], val2: Math.Max(val1: matrix[4], val2: matrix[8]));
        var low = top;
        var high = ((((top + Math.Abs(value: linear[0])) + Math.Abs(value: linear[1])) + Math.Abs(value: linear[2])) + 1d);

        for (var iteration = 0; (iteration < 80); iteration++) {
            var middle = ((low + high) * 0.5d);
            var norm = 0d;

            for (var i = 0; (i < 3); i++) {
                var denominator = (2d * (middle - matrix[(i * 4)]));
                var component = ((denominator == 0d) ? ((linear[i] == 0d) ? 0d : double.PositiveInfinity) : (linear[i] / denominator));

                norm += (component * component);
            }
            if (norm > 1d) {
                low = middle;
            } else {
                high = middle;
            }
        }
        // The dual value lambda + sum(b_i² / (4(lambda-e_i))) also handles the unused norm in the hard case.
        var result = high;

        for (var i = 0; (i < 3); i++) {
            var gap = (high - matrix[(i * 4)]);

            if (gap > 0d) {
                result += ((linear[i] * linear[i]) / (4d * gap));
            }
        }
        return result;
    }
}
