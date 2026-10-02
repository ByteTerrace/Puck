namespace Puck.SignedDistance.Illumination;

/// <summary>A point or direction in world units as three doubles: the illumination reference and its CPU model
/// compute in scalar double arithmetic in a written order, so their answers do not depend on the machine.</summary>
/// <param name="X">The X component.</param>
/// <param name="Y">The Y component.</param>
/// <param name="Z">The Z component.</param>
public readonly record struct Double3(double X, double Y, double Z) {
    /// <summary>Gets the zero vector.</summary>
    public static Double3 Zero => default;
    /// <summary>Gets the length.</summary>
    public double Length => Math.Sqrt(d: Dot(a: this, b: this));

    /// <summary>Adds two vectors.</summary>
    public static Double3 operator +(Double3 a, Double3 b) => new(X: (a.X + b.X), Y: (a.Y + b.Y), Z: (a.Z + b.Z));
    /// <summary>Subtracts two vectors.</summary>
    public static Double3 operator -(Double3 a, Double3 b) => new(X: (a.X - b.X), Y: (a.Y - b.Y), Z: (a.Z - b.Z));
    /// <summary>Negates a vector.</summary>
    public static Double3 operator -(Double3 a) => new(X: -a.X, Y: -a.Y, Z: -a.Z);
    /// <summary>Scales a vector.</summary>
    public static Double3 operator *(Double3 a, double s) => new(X: (a.X * s), Y: (a.Y * s), Z: (a.Z * s));
    /// <summary>Scales a vector.</summary>
    public static Double3 operator *(double s, Double3 a) => (a * s);

    /// <summary>Returns the dot product.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>The dot product.</returns>
    public static double Dot(Double3 a, Double3 b) => (((a.X * b.X) + (a.Y * b.Y)) + (a.Z * b.Z));
    /// <summary>Returns the componentwise product, which filters a colour by another.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>The componentwise product.</returns>
    public static Double3 Multiply(Double3 a, Double3 b) => new(X: (a.X * b.X), Y: (a.Y * b.Y), Z: (a.Z * b.Z));
    /// <summary>Returns the cross product.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>The cross product.</returns>
    public static Double3 Cross(Double3 a, Double3 b) => new(
        X: ((a.Y * b.Z) - (a.Z * b.Y)),
        Y: ((a.Z * b.X) - (a.X * b.Z)),
        Z: ((a.X * b.Y) - (a.Y * b.X))
    );
    /// <summary>Returns the unit vector in this direction, or zero for the zero vector.</summary>
    /// <returns>The unit vector.</returns>
    public Double3 Normalize() {
        var length = Length;

        return ((length > 0.0) ? (this * (1.0 / length)) : Zero);
    }
}
