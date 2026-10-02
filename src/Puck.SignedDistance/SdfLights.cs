using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>The lit path's stylized curvature shading (<c>render.lighting.curvature</c>), which every pass block carries
/// as values: the surface pass reads whether any gain is set, and the views pass shades by them.</summary>
/// <param name="Cavity">The cavity-darkening gain.</param>
/// <param name="Rim">The curvature rim gain.</param>
/// <param name="Ink">The ink outline gain.</param>
/// <param name="InkLow">The curvature magnitude at which the ink outline starts.</param>
/// <param name="InkHigh">The curvature magnitude at which the ink outline saturates.</param>
/// <param name="InkColor">The ink outline's linear RGB color.</param>
public readonly record struct SdfCurvature(float Cavity, float Rim, float Ink, float InkLow, float InkHigh, Vector3 InkColor) {
    /// <summary>The default curvature magnitude at which the ink outline saturates.</summary>
    public const float DefaultInkHigh = 16f;
    /// <summary>The default curvature magnitude at which the ink outline starts.</summary>
    public const float DefaultInkLow = 6f;

    /// <summary>Gets the default ink outline color.</summary>
    public static Vector3 DefaultInkColor { get; } = new(
        x: 0.02f,
        y: 0.02f,
        z: 0.03f
    );
    /// <summary>Gets the curvature shading an unauthored world renders: every gain zero, the ink band and color at their
    /// defaults.</summary>
    public static SdfCurvature Default { get; } = new(
        Cavity: 0f,
        Ink: 0f,
        InkColor: DefaultInkColor,
        InkHigh: DefaultInkHigh,
        InkLow: DefaultInkLow,
        Rim: 0f
    );
}
/// <summary>
/// A frame's lights table: up to <see cref="MaxLights"/> <see cref="SdfLight"/> records, the light that drives the
/// soft-shadow march, and the curvature shading the lit path applies. The tables pack the records into the World-group
/// table the shadow and views passes read (<see cref="Pack"/>), and every pass block carries the count, the shadow light
/// and the curvature gains as values.
/// </summary>
public sealed class SdfLights {
    /// <summary>The pinned ambient floor.</summary>
    public const float DefaultAmbientBase = 0.25f;
    /// <summary>The pinned ambient hemisphere gradient.</summary>
    public const float DefaultAmbientHemisphere = 0.25f;
    /// <summary>The pinned penumbra half-slope.</summary>
    public const float DefaultPenumbraSlope = (1f / 9f);
    /// <summary>The default point-light falloff radius, in world units.</summary>
    public const float DefaultPointRadius = 1f;
    /// <summary>The default point-light weight.</summary>
    public const float DefaultPointWeight = 1f;
    /// <summary>The default rim exponent.</summary>
    public const float DefaultRimPower = 3f;
    /// <summary>The pinned sun diffuse weight.</summary>
    public const float DefaultSunWeight = 0.85f;
    /// <summary>The most lights a frame carries: the records of the lights table.</summary>
    public const int MaxLights = 8;
    /// <summary>The largest penumbra half-slope a directional light admits; the shadow gather's cone is three times
    /// it and must stay a valid chord.</summary>
    public const float MaxPenumbraSlope = 0.3f;

    private readonly SdfLight[] m_lights = new SdfLight[MaxLights];

    private int m_count;

    private int m_shadowLight = -1;

    /// <summary>Gets the pinned sun direction, from a surface toward the sun.</summary>
    public static Vector3 DefaultSunDirection { get; } = new(
        x: 0.51343602f,
        y: 0.79349202f,
        z: 0.32673201f
    );

    /// <summary>Gets or sets the curvature shading.</summary>
    public SdfCurvature Curvature { get; set; } = SdfCurvature.Default;

    /// <summary>Gets or sets the number of lights, clamped to [0, <see cref="MaxLights"/>].</summary>
    public int Count {
        get => m_count;
        set => m_count = Math.Clamp(
            max: MaxLights,
            min: 0,
            value: value
        );
    }
    /// <summary>Gets every record of the table, <see cref="MaxLights"/> of them; those at or past <see cref="Count"/> are
    /// never lit.</summary>
    public ReadOnlySpan<SdfLight> Records => m_lights;
    /// <summary>Gets the index of the light that drives the soft-shadow march, or −1 when none does: the last
    /// directional set with shadows, until a light without them is set in its place.</summary>
    public int ShadowLight => m_shadowLight;

    /// <summary>Gets one light.</summary>
    /// <param name="index">The light's index in the table.</param>
    /// <returns>The light.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside the table.</exception>
    public SdfLight this[int index] => m_lights[index];

    // What a point or occluder light's dynamic slot may be.
    private static string LightSlotRule => $"a point or occluder light's dynamic slot must be {SdfProgram.NoDynamicTransformSlot} or in [0, {SdfProgram.MaxDynamicTransformSlot}].";

    /// <summary>Creates the lights an unauthored world renders: the pinned sun with shadows and the pinned hemisphere
    /// ambient.</summary>
    /// <returns>The lights.</returns>
    public static SdfLights Default() {
        var lights = new SdfLights();

        lights.Set(
            index: 0,
            light: new SdfLight(
                Kind: SdfLightKind.Directional,
                Direction: DefaultSunDirection,
                Color: Vector3.One,
                Weight: DefaultSunWeight,
                Param: DefaultPenumbraSlope,
                Shadows: true
            )
        );
        lights.Set(
            index: 1,
            light: new SdfLight(
                Kind: SdfLightKind.Hemisphere,
                Direction: Vector3.Zero,
                Color: Vector3.One,
                Weight: DefaultAmbientBase,
                Param: DefaultAmbientHemisphere,
                Shadows: false
            )
        );
        lights.Count = 2;

        return lights;
    }
    /// <summary>Copies every record, the count, the shadow light and the curvature from another table.</summary>
    /// <param name="source">The table to copy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public void CopyFrom(SdfLights source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        source.m_lights.CopyTo(
            array: m_lights,
            index: 0
        );
        m_count = source.m_count;
        m_shadowLight = source.m_shadowLight;
        Curvature = source.Curvature;
    }
    /// <summary>Packs the records into the lights table the kernels read: each record as set, a directional's direction
    /// normalized in double and rounded once (DXC's DXIL backend constant-folds a <c>normalize()</c> while its SPIR-V
    /// backend emits a runtime call; a value the host uploads has no such asymmetry), and a zero directional the pinned
    /// sun's.</summary>
    /// <param name="records">The table, at least <see cref="MaxLights"/> records.</param>
    /// <exception cref="ArgumentException"><paramref name="records"/> holds fewer than <see cref="MaxLights"/>
    /// records.</exception>
    public void Pack(Span<SdfLight> records) {
        if (records.Length < MaxLights) {
            throw new ArgumentException(
                message: $"The lights table holds {MaxLights} records; the span holds {records.Length}.",
                paramName: nameof(records)
            );
        }

        for (var index = 0; (index < MaxLights); index++) {
            var light = m_lights[index];

            if (light.Kind == SdfLightKind.Directional) {
                light.Direction = UnitDirection(direction: light.Direction);
            }

            records[index] = light;
        }
    }
    /// <summary>Sets one light and, when it shadows, makes it the shadow light; a light set without shadows where the
    /// shadow light was leaves no shadow light.</summary>
    /// <param name="index">The light's index in the table.</param>
    /// <param name="light">The light.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the table, or a point or
    /// occluder light's <see cref="SdfLight.DynamicSlot"/> is neither <see cref="SdfProgram.NoDynamicTransformSlot"/>
    /// nor a slot in [0, <see cref="SdfProgram.MaxDynamicTransformSlot"/>].</exception>
    public void Set(int index, SdfLight light) {
        if (
            (index < 0) ||
            (index >= MaxLights)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(index));
        }
        if (
            SdfLight.IsPositional(kind: light.Kind) &&
            (light.DynamicSlot != SdfProgram.NoDynamicTransformSlot) &&
            ((light.DynamicSlot < 0) || (light.DynamicSlot > SdfProgram.MaxDynamicTransformSlot))
        ) {
            throw new ArgumentOutOfRangeException(
                message: $"light {index}'s dynamic slot is {light.DynamicSlot}; {LightSlotRule}",
                paramName: nameof(light)
            );
        }

        m_lights[index] = (light with {
            DynamicSlot = (SdfLight.IsPositional(kind: light.Kind) ? light.DynamicSlot : 0),
            Shadows = ((light.Shadows != 0u) ? 1u : 0u),
        });

        if (light.CastsShadow) {
            m_shadowLight = index;
        } else if (m_shadowLight == index) {
            m_shadowLight = -1;
        }
    }
    /// <summary>Returns a direction normalized in double and rounded once, or the pinned sun's for a zero one: a zero
    /// direction has no Lambert term, the authoring doors refuse one by name, and a frame assembled in code still must
    /// not upload NaNs into every shaded pixel.</summary>
    /// <param name="direction">The direction, any length.</param>
    /// <returns>The unit direction.</returns>
    public static Vector3 UnitDirection(Vector3 direction) {
        double x = direction.X, y = direction.Y, z = direction.Z;
        var length = Math.Sqrt(d: (((x * x) + (y * y)) + (z * z)));

        if (length <= 0d) {
            x = DefaultSunDirection.X; y = DefaultSunDirection.Y; z = DefaultSunDirection.Z;
            length = Math.Sqrt(d: (((x * x) + (y * y)) + (z * z)));
        }

        return new Vector3(
            x: ((float)(x / length)),
            y: ((float)(y / length)),
            z: ((float)(z / length))
        );
    }
}
