namespace Puck.ShaderVm.Programs;

using static Puck.ShaderVm.ShaderMath;
using Expression = Puck.ShaderVm.ShaderExpression;

/// <summary>Builds the sky a world document authors as one Shader VM value graph.</summary>
/// <remarks>
/// The evaluation coordinate is the unit ray direction in world axes, with +Y up. Everything the day cycle
/// interpolates arrives through <see cref="SkyParameters"/>; only the field seeds are fixed at compile time.
/// </remarks>
public static class SkyProgram {
    private const float CloudDomeRadius = 6f;
    private const float CloudHeight = 0.7f;
    private const float CloudHorizonFade = 0.05f;
    private const float CloudNormalTap = 0.18f;
    private const float CloudOpacity = 3.5f;
    private const float CloudSelfShadow = 0.6f;
    private const float CloudSilverLining = 0.5f;
    private const float CloudWarp = 0.6f;
    private const int CloudOctaves = 4;
    private const float StarInset = 0.3f;
    private const float StarLuminosityFloor = 0.125f;
    private const float StarRadiusFraction = 0.12f;
    private const float StarSparsity = 0.08f;
    private const float Tau = 6.28318531f;

    /// <summary>Compiles the sky program for one set of field seeds.</summary>
    /// <param name="starSeed">The seed the star field hashes.</param>
    /// <param name="cloudSeed">The seed the cloud field hashes.</param>
    /// <returns>The packed program.</returns>
    public static ShaderProgram Compile(uint starSeed = 1337u, uint cloudSeed = 4242u) => ShaderExpressionCompiler.Compile(root: Build(
        cloudSeed: cloudSeed,
        starSeed: starSeed
    ));
    /// <summary>Builds the sky value graph for one set of field seeds.</summary>
    /// <param name="starSeed">The seed the star field hashes.</param>
    /// <param name="cloudSeed">The seed the cloud field hashes.</param>
    /// <returns>The colour the sky shows along the evaluation coordinate.</returns>
    public static Expression Build(uint starSeed = 1337u, uint cloudSeed = 4242u) {
        var direction = Expression.Input(input: ShaderInput.Coordinate).Normalized3;
        var elevation = direction.Y;
        var sun = Expression.Parameter(index: SkyParameters.Sun);
        var color = Gradient(direction: direction, elevation: elevation);

        color += SunDisc(direction: direction, sun: sun);
        color += Select(
            condition: Step(edge: 0f, value: elevation),
            whenFalse: 0f,
            whenTrue: StarField(direction: direction, seed: starSeed)
        );

        var clouds = CloudLayer(direction: direction, elevation: elevation, seed: cloudSeed, sun: sun);

        return Lerp(amount: clouds.W, from: color, to: clouds);
    }
    // The three-stop vertical ramp: ground below the horizon, horizon to zenith above it.
    private static Expression Gradient(Expression direction, Expression elevation) {
        var ground = Expression.Parameter(index: SkyParameters.Ground);
        var horizon = Expression.Parameter(index: SkyParameters.Horizon);
        var zenith = Expression.Parameter(index: SkyParameters.Zenith);

        return Select(
            condition: Step(edge: 0f, value: elevation),
            whenFalse: Lerp(amount: Saturate(value: (elevation + 1f)), from: ground, to: horizon),
            whenTrue: Lerp(amount: Saturate(value: elevation), from: horizon, to: zenith)
        );
    }
    // An additive pow(cosAngle, k) highlight about the lighting sun, k baked host-side from the authored radius.
    private static Expression SunDisc(Expression direction, Expression sun) => (Expression.Parameter(index: SkyParameters.Ground).W * Pow(
        exponent: sun.W,
        value: Saturate(value: Dot3(left: direction, right: sun))
    ));
    // One PCG cell per octahedral sky cell picks the stars; a second hash of the first gives each its luminosity,
    // colour and twinkle. The disc is measured angularly, so a star reads round wherever the projection stretches.
    private static Expression StarField(Expression direction, uint seed) {
        var stars = Expression.Parameter(index: SkyParameters.Stars);
        var density = stars.X;
        var cell = Floor(value: (((OctahedralEncode(direction: direction) * 0.5f) + 0.5f) * density));
        var hash = Hash3(value: (Seeded(position: cell, seed: seed).Swizzle(x: 0, y: 1, z: 3, w: 3)));
        var unit = Unit(value: hash);
        var second = Unit(value: Hash3(value: hash));
        var luminosity = Min(
            left: 1f,
            right: (StarLuminosityFloor * Pow(exponent: -0.6666667f, value: Max(left: second.X, right: 1e-6f)))
        );
        var twinkled = (luminosity * Twinkle(
            depth: stars.W,
            phase: stars.Y,
            second: second,
            share: stars.Z,
            hash: hash
        ));
        var offset = Lerp(
            amount: unit.Swizzle(x: 1, y: 2, z: 1, w: 2),
            from: StarInset,
            to: (1f - StarInset)
        );
        var target = OctahedralDecode(point: ((((cell + offset) / density) * 2f) - 1f));
        var radius = (((StarRadiusFraction * MathF.PI) / density) * Lerp(amount: Sqrt(value: twinkled), from: 0.6f, to: 1f));
        var coverage = SmoothStep(
            edge0: ((radius * radius) * 0.5f),
            edge1: 0f,
            value: (1f - Dot3(left: direction, right: target))
        );

        return Select(
            condition: Step(edge: unit.X, value: StarSparsity),
            whenFalse: 0f,
            whenTrue: (((coverage * Expression.Parameter(index: SkyParameters.SunColor).W) * twinkled) * Spectrum(temperature: second.Y))
        );
    }
    // Two sines at distinct small harmonics of the period, phase-offset per star and multiplied: an irregular dip
    // that still closes exactly at the period boundary.
    private static Expression Twinkle(Expression hash, Expression second, Expression share, Expression depth, Expression phase) {
        var third = Unit(value: Hash3(value: Hash3(value: hash)));
        var harmonicA = (1f + Floor(value: (third.X * 3f)));
        var harmonicB = (2f + Floor(value: (third.Y * 3f)));
        var flicker = (0.5f + (0.5f * (Sin(value: (Tau * ((harmonicA * phase) + third.Z))) * Sin(value: (Tau * ((harmonicB * phase) + (third.Z * 1.7f)))))));

        return Select(
            condition: (second.Z < share),
            whenFalse: 1f,
            whenTrue: (1f - (depth * flicker))
        );
    }
    // A blackbody ramp from roughly 3000 K through white to 15000 K, each tint at unit peak channel.
    private static Expression Spectrum(Expression temperature) {
        var cool = Expression.Constant(x: 0.71f, y: 0.80f, z: 1f, w: 1f);
        var warm = Expression.Constant(x: 1f, y: 0.71f, z: 0.42f, w: 1f);
        var white = Expression.Constant(x: 1f, y: 0.98f, z: 0.99f, w: 1f);

        return Select(
            condition: (temperature > 0.5f),
            whenFalse: Lerp(amount: Saturate(value: (temperature * 2f)), from: warm, to: white),
            whenTrue: Lerp(amount: Saturate(value: ((temperature * 2f) - 1f)), from: white, to: cool)
        );
    }
    // A heightfield of cloud on a dome about a centre CloudDomeRadius below the camera, so the layer compresses
    // toward the horizon as a real one does and no direction needs a clamp. Alpha rides in lane w.
    private static Expression CloudLayer(Expression direction, Expression elevation, uint seed, Expression sun) {
        var shape = Expression.Parameter(index: SkyParameters.CloudShape);
        var wind = Expression.Parameter(index: SkyParameters.CloudWind);
        var clouds = Expression.Parameter(index: SkyParameters.Clouds);
        var b = (CloudDomeRadius * elevation);
        var reach = (Sqrt(value: ((b * b) + ((2f * CloudDomeRadius) + 1f))) - b);
        var layer = (direction.Swizzle(x: 0, y: 2, z: 0, w: 2) * reach);
        var radius = layer.Length2;
        var angle = (shape.Z + (shape.W * ((2f * radius) / (1f + (radius * radius)))));
        var turn = Cos(value: angle);
        var swing = Sin(value: angle);
        var point = ((Rotate(cosine: turn, sine: swing, value: layer) / shape.Y) + wind);
        var threshold = (1f - clouds.W);
        var thickness = Thickness(
            point: point,
            seed: seed,
            shear: wind.Swizzle(x: 2, y: 3, z: 2, w: 3),
            softness: shape.X,
            threshold: threshold
        );
        var sunTurned = Rotate(cosine: turn, sine: swing, value: sun.Swizzle(x: 0, y: 2, z: 0, w: 2));
        var alongX = Thickness(
            point: (point + Expression.Constant(x: CloudNormalTap, y: 0f)),
            seed: seed,
            shear: wind.Swizzle(x: 2, y: 3, z: 2, w: 3),
            softness: shape.X,
            threshold: threshold
        );
        var alongY = Thickness(
            point: (point + Expression.Constant(x: 0f, y: CloudNormalTap)),
            seed: seed,
            shear: wind.Swizzle(x: 2, y: 3, z: 2, w: 3),
            softness: shape.X,
            threshold: threshold
        );
        var sunward = Thickness(
            point: (point + (((CloudNormalTap * 2f) * (sunTurned + 1e-5f).Normalized2))),
            seed: seed,
            shear: wind.Swizzle(x: 2, y: 3, z: 2, w: 3),
            softness: shape.X,
            threshold: threshold
        );
        var normal = Expression.Combine(
            x: (-((alongX - thickness) / CloudNormalTap) * CloudHeight),
            y: 1f,
            z: (-((alongY - thickness) / CloudNormalTap) * CloudHeight)
        ).Normalized3;
        var diffuse = Saturate(value: Dot3(
            left: normal,
            right: Expression.Combine(x: sunTurned.X, y: sun.Y, z: sunTurned.Y).Normalized3
        ));
        var shadow = (1f - (CloudSelfShadow * Saturate(value: (sunward - thickness))));
        var lining = ((CloudSilverLining * Pow(exponent: 8f, value: Saturate(value: Dot3(left: direction, right: sun)))) * (1f - thickness));
        var shade = ((clouds * (Lerp(amount: diffuse, from: 0.45f, to: 1f) * shadow)) + (Expression.Parameter(index: SkyParameters.SunColor) * lining));
        var alpha = (((1f - Exp(value: -(thickness * CloudOpacity))) * SmoothStep(edge0: 0f, edge1: CloudHorizonFade, value: elevation)) * Step(edge: 0f, value: (clouds.W - 1e-6f)));

        return Expression.Combine(x: shade.X, y: shade.Y, z: shade.Z, w: alpha);
    }
    // A domain-warped fbm thresholded at the coverage edge: a first field bends the second's domain, which is what
    // gives the puffed, lobed silhouettes flat noise never does.
    private static Expression Thickness(Expression point, Expression shear, uint seed, Expression threshold, Expression softness) {
        var warp = Fbm2(octaves: CloudOctaves, position: Seeded(position: (point + shear), seed: (seed ^ 0x9E3779B9u)));
        var density = Fbm2(octaves: CloudOctaves, position: Seeded(position: (point + (CloudWarp * (warp - 0.5f))), seed: seed));

        return SmoothStep(edge0: threshold, edge1: (threshold + softness), value: density);
    }
    private static Expression Rotate(Expression value, Expression cosine, Expression sine) => Expression.Combine(
        x: ((value.X * cosine) - (value.Y * sine)),
        y: ((value.X * sine) + (value.Y * cosine))
    );
    // The unit sphere onto the [-1, 1] square: the octahedral projection the star cells are laid out on.
    private static Expression OctahedralEncode(Expression direction) {
        var folded = direction.Swizzle(x: 0, y: 1, z: 0, w: 1) / (Abs(value: direction.X) + Abs(value: direction.Y) + Abs(value: direction.Z));

        return Select(
            condition: (direction.Z < 0f),
            whenFalse: folded,
            whenTrue: ((1f - Abs(value: folded.Swizzle(x: 1, y: 0, z: 1, w: 0))) * SignPositive(value: folded))
        );
    }
    private static Expression OctahedralDecode(Expression point) {
        var raised = Expression.Combine(
            x: point.X,
            y: point.Y,
            z: ((1f - Abs(value: point.X)) - Abs(value: point.Y))
        );
        var folded = ((1f - Abs(value: raised.Swizzle(x: 1, y: 0, z: 1, w: 0))) * SignPositive(value: raised));

        return Select(
            condition: (raised.Z < 0f),
            whenFalse: raised,
            whenTrue: Expression.Combine(x: folded.X, y: folded.Y, z: raised.Z)
        ).Normalized3;
    }
    // Zero counts as positive, matching the ternary the octahedral fold is written with.
    private static Expression SignPositive(Expression value) => ((Step(edge: 0f, value: value) * 2f) - 1f);
}
