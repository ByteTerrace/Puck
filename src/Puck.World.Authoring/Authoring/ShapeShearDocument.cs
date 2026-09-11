namespace Puck.World.Authoring;

/// <summary>Polynomial point shear: target += linear*driver + quadratic*driver² + cubic*driver³.
/// Target and Driver are distinct axes in [0, 2]. Coefficients carry inverse powers of creation length.</summary>
public sealed record ShapeShearDocument(float Linear, float Quadratic = 0f, float Cubic = 0f, int Target = 0, int Driver = 1) {
    /// <summary>A conservative displacement reach in the same units as the authored coefficients.</summary>
    public float ReachExtra(float reach) => reach * (MathF.Abs(Linear) + reach * (MathF.Abs(Quadratic) + reach * MathF.Abs(Cubic)));
}
