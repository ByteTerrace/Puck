using System.Runtime.CompilerServices;
using Puck.SignedDistance;

namespace Puck.World;

/// <summary>The compiled rate integrals retained by one prepared world. Load and reload share this cache with
/// presentation; no frame reconstructs a polynomial or retains another copy of its coefficient storage.</summary>
public sealed class WorldPresentationRates {
    /// <summary>The per-world retained coefficient budget: 4,096 doubles, or 32 KiB of coefficient payload.
    /// Shared compiled operands count once; literals retain no coefficients.</summary>
    public const int MaximumCoefficients = 4096;

    private static readonly ConditionalWeakTable<WorldDefinition, WorldPresentationRates> Cache = new();

    private readonly Dictionary<WorldRenderSkyLayer, WorldSkyMotion> m_layers = new(comparer: ReferenceEqualityComparer.Instance);

    private WorldPresentationRates(WorldDefinition original) {
        var definition = WorldPresentationValues.Of(definition: original).Definition;
        var errors = new List<string>();
        var compiled = new Dictionary<BindableScalar, WorldRateIntegral>();
        var contributors = new List<(string Name, int Coefficients)>();
        var retainedCoefficients = 0;

        WorldRateIntegral? Rate(BindableScalar? value, string path) {
            if ((value is not { } rate) || (rate.Literal == 0f)) { return null; }
            if (compiled.TryGetValue(key: rate, value: out var existing)) { return existing; }
            if (!WorldRateIntegral.TryCompile(definition: definition, integral: out var integral, name: path, rate: rate, reason: out var reason)) {
                errors.Add(item: $"{path}: {reason}.");
                return null;
            }
            var next = checked((retainedCoefficients + integral!.Cost.Coefficients));

            if (next > MaximumCoefficients) {
                var largest = contributors.Append(element: (Name: path, Coefficients: integral.Cost.Coefficients))
                    .OrderByDescending(keySelector: static item => item.Coefficients).ThenBy(static item => item.Name, StringComparer.Ordinal)
                    .Take(count: 3).Select(selector: static item => $"{item.Name}={item.Coefficients}");

                errors.Add(item: $"{path}: compiled rate would retain {next} coefficients across this world; limit {MaximumCoefficients} ({(MaximumCoefficients * sizeof(double))} coefficient bytes); largest contributors: {string.Join(separator: ", ", values: largest)}.");
                return null;
            }
            retainedCoefficients = next;
            if (integral.Cost.Coefficients != 0) { contributors.Add(item: (path, integral.Cost.Coefficients)); }
            compiled.Add(key: rate, value: integral!);
            return integral;
        }
        (WorldRateIntegral? X, WorldRateIntegral? Y) Pair(BindableVector2? value, string path) {
            if (value is not { } pair) { return default; }
            if (pair.Keys is not { } keys) { return (Rate(pair.X, (path + "[0]")), Rate(pair.Y, (path + "[1]"))); }
            if (!WorldValueValidation.TryValidate(curve: keys, definition: definition, reason: out var reason)) {
                errors.Add(item: $"{path}: {reason}.");
                return default;
            }
            var x = new WorldKey<BindableScalar>[keys.Keys.Count];
            var y = new WorldKey<BindableScalar>[keys.Keys.Count];

            for (var index = 0; (index < keys.Keys.Count); index++) {
                var key = keys.Keys[index];

                if ((key is null) || (key.Value.Keys is not null)) {
                    errors.Add(item: $"{path}.keys[{index}] requires a pair of literal rates, without nested keys.");
                    return default;
                }
                x[index] = new(key.At, key.Value.X, key.Ease);
                y[index] = new(key.At, key.Value.Y, key.Ease);
            }
            return (Rate(new BindableScalar(keys: new WorldKeys<BindableScalar>(Clock: keys.Clock, Keys: x)), (path + "[0]")),
                Rate(new BindableScalar(keys: new WorldKeys<BindableScalar>(Clock: keys.Clock, Keys: y)), (path + "[1]")));
        }
        if (definition.Render.Sky?.Layers is { } layers) {
            for (var index = 0; (index < layers.Count); index++) {
                var layer = layers[index];
                var path = ((layer?.Name is { } name) ? $"render.sky.layers.{name}" : $"render.sky.layers[{index}]");

                switch (layer) {
                    case WorldRenderSkyLayer.Clouds clouds:
                        var drift = Pair(clouds.Drift, (path + ".drift"));
                        var shear = Pair(clouds.Shear, (path + ".shear"));
                        m_layers[layer] = new(DriftX: drift.X, DriftY: drift.Y, ShearX: shear.X, ShearY: shear.Y, Spin: Rate(clouds.Spin, (path + ".spin")), Twinkle: null);
                        break;
                    case WorldRenderSkyLayer.Stars { Twinkle: { } twinkle }:
                        m_layers[layer] = new(DriftX: null, DriftY: null, ShearX: null, ShearY: null, Spin: null, Twinkle: Rate((twinkle.Rate ?? new BindableScalar(literal: SdfLighting.DefaultTwinkleRate)), (path + ".twinkle.rate")));
                        break;
                }
                if (layer?.Transform?.Rate is { } yawRate) {
                    var motion = m_layers.GetValueOrDefault(layer) ?? new(null, null, null, null, null, null);
                    m_layers[layer] = motion with { Yaw = Rate(yawRate, path + ".transform.rate") };
                }
            }
        }
        var pieces = 0;
        var coefficients = 0;
        var degree = 0;

        foreach (var integral in compiled.Values) {
            var cost = integral.Cost;

            pieces = checked((pieces + cost.Pieces));
            coefficients = checked((coefficients + cost.Coefficients));
            degree = Math.Max(val1: degree, val2: cost.Degree);
        }
        Cost = new(Coefficients: coefficients, Degree: degree, Pieces: pieces);
        Errors = errors.ToArray();
    }

    /// <summary>Gets the load-time compilation refusals, each naming its authored rate.</summary>
    public IReadOnlyList<string> Errors { get; }
    /// <summary>Gets each retained coefficient allocation counted once across this world.</summary>
    public WorldRateCost Cost { get; }

    /// <summary>Compiles one world's rates once; the cache does not keep an unused world alive.</summary>
    /// <param name="definition">The original immutable world definition.</param>
    /// <returns>The world's shared compiled rates and counted storage.</returns>
    public static WorldPresentationRates Of(WorldDefinition definition) => Cache.GetValue(definition, static world => new(original: world));
    /// <summary>Finds the compiled motion of a layer in the world's prepared definition.</summary>
    /// <param name="layer">The prepared layer.</param>
    /// <returns>Its integrals, or null for a layer without rates.</returns>
    public WorldSkyMotion? Of(WorldRenderSkyLayer layer) => m_layers.GetValueOrDefault(key: layer);
}
/// <summary>The optional rate integrals a sky layer uses. A missing component is identically zero.</summary>
/// <param name="DriftX">The first cloud-drift component, in layer units per second.</param>
/// <param name="DriftY">The second cloud-drift component, in layer units per second.</param>
/// <param name="ShearX">The first shaping-field component, in layer units per second.</param>
/// <param name="ShearY">The second shaping-field component, in layer units per second.</param>
/// <param name="Spin">Cloud rotation, in radians per second.</param>
/// <param name="Twinkle">Star scintillation, in cycles per second.</param>
/// <param name="Yaw">Common layer rotation, in radians per second.</param>
public sealed record WorldSkyMotion(WorldRateIntegral? DriftX, WorldRateIntegral? DriftY,
    WorldRateIntegral? ShearX, WorldRateIntegral? ShearY, WorldRateIntegral? Spin, WorldRateIntegral? Twinkle,
    WorldRateIntegral? Yaw = null) {
    /// <summary>Whether this layer retains a nonzero authored rate or a keyed rate.</summary>
    public bool Moves => ((DriftX is not null) || (DriftY is not null) || (ShearX is not null) || (ShearY is not null) || (Spin is not null) || (Twinkle is not null) || (Yaw is not null));
}
