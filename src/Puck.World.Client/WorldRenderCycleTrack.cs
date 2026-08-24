using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>The lighting and sky fields a frame uploads — the static <c>render.lighting</c>/<c>render.sky</c>
/// values, or one point along a <see cref="WorldRenderCycle"/>.</summary>
public readonly record struct WorldRenderLightingState(
    Vector3 SunDirection,
    float SunWeight,
    Vector3 SunColor,
    float AmbientBase,
    float AmbientHemisphere,
    Vector3 AmbientColor,
    bool SkyEnabled,
    Vector3 SkyZenithColor,
    Vector3 SkyHorizonColor,
    Vector3 SkyGroundColor,
    float SkyFogDensity,
    float SkySunDiscRadians,
    float SkySunDiscIntensity,
    float SkyStarDensity,
    float SkyStarBrightness,
    uint SkyStarSeed,
    float SkyStarTwinkleShare,
    float SkyStarTwinkleDepth,
    float SkyStarTwinkleRate,
    Vector3 SkyCloudColor,
    float SkyCloudCoverage,
    float SkyCloudSoftness,
    float SkyCloudScale,
    uint SkyCloudSeed,
    Vector2 SkyCloudDrift,
    float SkyCloudSpin,
    float SkyCloudCurl,
    Vector2 SkyCloudShear
);
/// <summary>Resolves a definition's lighting and sky each frame: the static <c>render.lighting</c>/<c>render.sky</c>
/// fields, or, when it authors a <c>render.cycle</c>, the two keys bracketing the state row's live value (through the
/// one state read every consumer shares) interpolated. Statics and keys are resolved once per definition revision —
/// a live edit to either section, or to a state cell a color binds to, lands on the next frame — each key holding
/// every field the previous key left it, starting from the statics, the last wrapping into the first.</summary>
public sealed class WorldRenderCycleTrack {
    private WorldRenderLightingState[] m_keys = [];
    private float[] m_keyAts = [];
    private int m_revision = -1;
    private string? m_stateRow;
    private WorldRenderLightingState m_statics;

    private static WorldRenderLightingState Statics(WorldDefinition definition) {
        var defaults = definition.Render;

        return new WorldRenderLightingState(
            SunDirection: (defaults.Lighting?.Sun?.Direction ?? SdfFrame.DefaultSunDirection),
            SunWeight: (defaults.Lighting?.Sun?.Weight ?? SdfFrame.DefaultSunWeight),
            SunColor: WorldColor.Resolve(
                definition: definition,
                fallback: Vector3.One,
                value: defaults.Lighting?.Sun?.Color
            ),
            AmbientBase: (defaults.Lighting?.Ambient?.Base ?? SdfFrame.DefaultAmbientBase),
            AmbientHemisphere: (defaults.Lighting?.Ambient?.Hemisphere ?? SdfFrame.DefaultAmbientHemisphere),
            AmbientColor: WorldColor.Resolve(
                definition: definition,
                fallback: Vector3.One,
                value: defaults.Lighting?.Ambient?.Color
            ),
            SkyEnabled: (defaults.Sky is not null),
            SkyZenithColor: WorldColor.Resolve(
                definition: definition,
                fallback: SdfFrame.DefaultSkyZenithColor,
                value: defaults.Sky?.Zenith
            ),
            SkyHorizonColor: WorldColor.Resolve(
                definition: definition,
                fallback: SdfFrame.DefaultSkyHorizonColor,
                value: defaults.Sky?.Horizon
            ),
            SkyGroundColor: WorldColor.Resolve(
                definition: definition,
                fallback: SdfFrame.DefaultSkyGroundColor,
                value: defaults.Sky?.Ground
            ),
            SkyFogDensity: (defaults.Sky?.FogDensity ?? SdfFrame.DefaultSkyFogDensity),
            SkySunDiscRadians: (defaults.Sky?.Sun?.DiscRadians ?? SdfFrame.DefaultSkySunDiscRadians),
            SkySunDiscIntensity: (defaults.Sky?.Sun?.Intensity ?? 0f),
            SkyStarDensity: (defaults.Sky?.Stars?.Density ?? SdfFrame.DefaultSkyStarDensity),
            SkyStarBrightness: (defaults.Sky?.Stars?.Brightness ?? 0f),
            SkyStarSeed: (defaults.Sky?.Stars?.Seed ?? 0u),
            SkyStarTwinkleShare: (defaults.Sky?.Stars?.Twinkle?.Share ?? 0f),
            SkyStarTwinkleDepth: (defaults.Sky?.Stars?.Twinkle?.Depth ?? 0f),
            SkyStarTwinkleRate: (defaults.Sky?.Stars?.Twinkle?.Rate ?? SdfFrame.DefaultSkyStarTwinkleRate),
            SkyCloudColor: WorldColor.Resolve(
                definition: definition,
                fallback: Vector3.One,
                value: defaults.Sky?.Clouds?.Color
            ),
            SkyCloudCoverage: (defaults.Sky?.Clouds?.Coverage ?? 0f),
            SkyCloudSoftness: (defaults.Sky?.Clouds?.Softness ?? SdfFrame.DefaultSkyCloudSoftness),
            SkyCloudScale: (defaults.Sky?.Clouds?.Scale ?? SdfFrame.DefaultSkyCloudScale),
            SkyCloudSeed: (defaults.Sky?.Clouds?.Seed ?? 0u),
            SkyCloudDrift: (defaults.Sky?.Clouds?.Drift ?? Vector2.Zero),
            SkyCloudSpin: (defaults.Sky?.Clouds?.Spin ?? 0f),
            SkyCloudCurl: (defaults.Sky?.Clouds?.Curl ?? 0f),
            SkyCloudShear: (defaults.Sky?.Clouds?.Shear ?? Vector2.Zero)
        );
    }
    private static Vector3 Direction(Vector3 from, Vector3 to, float t) {
        var blended = Vector3.Lerp(
            amount: t,
            value1: from,
            value2: to
        );

        return ((blended.LengthSquared() > 1e-8f)
            ? Vector3.Normalize(value: blended)
            : from);
    }
    // Overlays a key's stated fields onto the carried state.
    private static WorldRenderLightingState Apply(WorldDefinition definition, WorldRenderLightingState carried, WorldRenderCycleKey key) {
        var sun = key.Lighting?.Sun;
        var ambient = key.Lighting?.Ambient;
        var sky = key.Sky;

        return carried with {
            SunDirection = (sun?.Direction ?? carried.SunDirection),
            SunWeight = (sun?.Weight ?? carried.SunWeight),
            SunColor = WorldColor.Resolve(
                definition: definition,
                fallback: carried.SunColor,
                value: sun?.Color
            ),
            AmbientBase = (ambient?.Base ?? carried.AmbientBase),
            AmbientHemisphere = (ambient?.Hemisphere ?? carried.AmbientHemisphere),
            AmbientColor = WorldColor.Resolve(
                definition: definition,
                fallback: carried.AmbientColor,
                value: ambient?.Color
            ),
            SkyEnabled = (carried.SkyEnabled || (sky is not null)),
            SkyZenithColor = WorldColor.Resolve(
                definition: definition,
                fallback: carried.SkyZenithColor,
                value: sky?.Zenith
            ),
            SkyHorizonColor = WorldColor.Resolve(
                definition: definition,
                fallback: carried.SkyHorizonColor,
                value: sky?.Horizon
            ),
            SkyGroundColor = WorldColor.Resolve(
                definition: definition,
                fallback: carried.SkyGroundColor,
                value: sky?.Ground
            ),
            SkyFogDensity = (sky?.FogDensity ?? carried.SkyFogDensity),
            SkySunDiscRadians = (sky?.Sun?.DiscRadians ?? carried.SkySunDiscRadians),
            SkySunDiscIntensity = (sky?.Sun?.Intensity ?? carried.SkySunDiscIntensity),
            SkyStarDensity = (sky?.Stars?.Density ?? carried.SkyStarDensity),
            SkyStarBrightness = (sky?.Stars?.Brightness ?? carried.SkyStarBrightness),
            SkyStarSeed = (sky?.Stars?.Seed ?? carried.SkyStarSeed),
            SkyStarTwinkleShare = (sky?.Stars?.Twinkle?.Share ?? carried.SkyStarTwinkleShare),
            SkyStarTwinkleDepth = (sky?.Stars?.Twinkle?.Depth ?? carried.SkyStarTwinkleDepth),
            SkyStarTwinkleRate = (sky?.Stars?.Twinkle?.Rate ?? carried.SkyStarTwinkleRate),
            SkyCloudColor = WorldColor.Resolve(
                definition: definition,
                fallback: carried.SkyCloudColor,
                value: sky?.Clouds?.Color
            ),
            SkyCloudCoverage = (sky?.Clouds?.Coverage ?? carried.SkyCloudCoverage),
            SkyCloudSoftness = (sky?.Clouds?.Softness ?? carried.SkyCloudSoftness),
            SkyCloudScale = (sky?.Clouds?.Scale ?? carried.SkyCloudScale),
            SkyCloudSeed = (sky?.Clouds?.Seed ?? carried.SkyCloudSeed),
            SkyCloudDrift = (sky?.Clouds?.Drift ?? carried.SkyCloudDrift),
            SkyCloudSpin = (sky?.Clouds?.Spin ?? carried.SkyCloudSpin),
            SkyCloudCurl = (sky?.Clouds?.Curl ?? carried.SkyCloudCurl),
            SkyCloudShear = (sky?.Clouds?.Shear ?? carried.SkyCloudShear),
        };
    }
    private static WorldRenderLightingState Blend(WorldRenderLightingState a, WorldRenderLightingState b, float t) => new(
        SunDirection: Direction(
            from: a.SunDirection,
            t: t,
            to: b.SunDirection
        ),
        SunWeight: float.Lerp(value1: a.SunWeight, value2: b.SunWeight, amount: t),
        SunColor: Vector3.Lerp(amount: t, value1: a.SunColor, value2: b.SunColor),
        AmbientBase: float.Lerp(value1: a.AmbientBase, value2: b.AmbientBase, amount: t),
        AmbientHemisphere: float.Lerp(value1: a.AmbientHemisphere, value2: b.AmbientHemisphere, amount: t),
        AmbientColor: Vector3.Lerp(amount: t, value1: a.AmbientColor, value2: b.AmbientColor),
        SkyEnabled: (a.SkyEnabled || b.SkyEnabled),
        SkyZenithColor: Vector3.Lerp(amount: t, value1: a.SkyZenithColor, value2: b.SkyZenithColor),
        SkyHorizonColor: Vector3.Lerp(amount: t, value1: a.SkyHorizonColor, value2: b.SkyHorizonColor),
        SkyGroundColor: Vector3.Lerp(amount: t, value1: a.SkyGroundColor, value2: b.SkyGroundColor),
        SkyFogDensity: float.Lerp(value1: a.SkyFogDensity, value2: b.SkyFogDensity, amount: t),
        SkySunDiscRadians: float.Lerp(value1: a.SkySunDiscRadians, value2: b.SkySunDiscRadians, amount: t),
        SkySunDiscIntensity: float.Lerp(value1: a.SkySunDiscIntensity, value2: b.SkySunDiscIntensity, amount: t),
        SkyStarDensity: float.Lerp(value1: a.SkyStarDensity, value2: b.SkyStarDensity, amount: t),
        SkyStarBrightness: float.Lerp(value1: a.SkyStarBrightness, value2: b.SkyStarBrightness, amount: t),
        SkyStarSeed: a.SkyStarSeed,
        SkyStarTwinkleShare: float.Lerp(value1: a.SkyStarTwinkleShare, value2: b.SkyStarTwinkleShare, amount: t),
        SkyStarTwinkleDepth: float.Lerp(value1: a.SkyStarTwinkleDepth, value2: b.SkyStarTwinkleDepth, amount: t),
        SkyStarTwinkleRate: float.Lerp(value1: a.SkyStarTwinkleRate, value2: b.SkyStarTwinkleRate, amount: t),
        SkyCloudColor: Vector3.Lerp(amount: t, value1: a.SkyCloudColor, value2: b.SkyCloudColor),
        SkyCloudCoverage: float.Lerp(value1: a.SkyCloudCoverage, value2: b.SkyCloudCoverage, amount: t),
        SkyCloudSoftness: float.Lerp(value1: a.SkyCloudSoftness, value2: b.SkyCloudSoftness, amount: t),
        SkyCloudScale: float.Lerp(value1: a.SkyCloudScale, value2: b.SkyCloudScale, amount: t),
        SkyCloudSeed: a.SkyCloudSeed,
        SkyCloudDrift: Vector2.Lerp(amount: t, value1: a.SkyCloudDrift, value2: b.SkyCloudDrift),
        SkyCloudSpin: float.Lerp(value1: a.SkyCloudSpin, value2: b.SkyCloudSpin, amount: t),
        SkyCloudCurl: float.Lerp(value1: a.SkyCloudCurl, value2: b.SkyCloudCurl, amount: t),
        SkyCloudShear: Vector2.Lerp(amount: t, value1: a.SkyCloudShear, value2: b.SkyCloudShear)
    );
    private void Rebuild(WorldDefinition definition, WorldRenderCycle cycle) {
        var keys = cycle.Keys;
        var resolved = new WorldRenderLightingState[keys.Count];
        var ats = new float[keys.Count];
        var carried = m_statics;

        // Two passes: the second lets the first key inherit whatever the last key left, so the wrap holds fields too.
        for (var pass = 0; (pass < 2); pass++) {
            for (var index = 0; (index < keys.Count); index++) {
                carried = Apply(
                    carried: carried,
                    definition: definition,
                    key: keys[index]
                );
                resolved[index] = carried;
                ats[index] = keys[index].At;
            }
        }

        m_keys = resolved;
        m_keyAts = ats;
        m_stateRow = cycle.State;
    }

    /// <summary>Resolves this frame's lighting: the cycle's interpolation at the state row's live value, or the
    /// static fields when the definition authors no cycle or the row cannot be read.</summary>
    /// <param name="definition">The live definition.</param>
    /// <param name="revision">The definition revision (statics and keys are resolved once per revision).</param>
    /// <param name="tick">The tick to read the state row as of.</param>
    public WorldRenderLightingState Resolve(WorldDefinition definition, int revision, ulong tick) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var cycle = definition.Render.Cycle;

        if (revision != m_revision) {
            m_revision = revision;
            m_statics = Statics(definition: definition);

            if (cycle is { Keys.Count: >= 2 }) {
                Rebuild(
                    cycle: cycle,
                    definition: definition
                );
            }
        }

        if (cycle is not { Keys.Count: >= 2 }) {
            return m_statics;
        }

        if (
            !WorldStateReader.TryRead(
                definition: definition,
                key: null,
                rawValue: out var rawValue,
                row: out var row,
                rowName: m_stateRow!,
                text: out _,
                tick: tick
            ) ||
            (rawValue is not { } raw)
        ) {
            return m_statics;
        }

        var value = ((row.Kind == CellKind.Fixed)
            ? ((double)FixedQ4816.FromRawBits(value: raw))
            : ((double)raw)
        );
        var fraction = ((float)(value - Math.Floor(d: value)));
        var count = m_keys.Length;
        var index = 0;

        while (
            ((index + 1) < count) &&
            (m_keyAts[(index + 1)] <= fraction)
        ) {
            index++;
        }

        // Before the first key, or after the last: the segment from the last key wrapping round to the first.
        var fromIndex = ((fraction < m_keyAts[0])
            ? (count - 1)
            : index);
        var toIndex = ((fromIndex + 1) % count);
        var fromAt = m_keyAts[fromIndex];
        var toAt = m_keyAts[toIndex];
        var span = ((toIndex == 0)
            ? ((1f - fromAt) + toAt)
            : (toAt - fromAt));
        var offset = ((fraction >= fromAt)
            ? (fraction - fromAt)
            : ((1f - fromAt) + fraction));
        var t = ((span > 0f)
            ? Math.Clamp(
                max: 1f,
                min: 0f,
                value: (offset / span)
            )
            : 0f);

        return Blend(
            a: m_keys[fromIndex],
            b: m_keys[toIndex],
            t: t
        );
    }
}
