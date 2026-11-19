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
/// A frame's lights table: up to <see cref="MaxLights"/> <see cref="SdfLight"/> records, the stable shadow slots and
/// active handoffs, and the curvature shading the lit path applies. The tables pack the records into the World-group
/// table the shadow and views passes read (<see cref="Pack"/>).
/// </summary>
public sealed class SdfLights {
    /// <summary>The default point or spot light's luminous intensity, in candela: about a 1,000-lumen bulb's.</summary>
    public const float DefaultPointIntensity = 80f;
    /// <summary>The default rim exponent.</summary>
    public const float DefaultRimPower = 3f;
    /// <summary>The pinned sun's illuminance, in lux: a clear midday sun.</summary>
    public const float DefaultSunIlluminance = 100000f;
    /// <summary>The most lights a frame carries: the records of the lights table. Each view shades only the lights it
    /// can see (<see cref="SdfLightSelection"/>), whose mask its pass block carries one bit a record.</summary>
    public const int MaxLights = 128;
    /// <summary>The largest penumbra half-slope a directional light admits; the shadow gather's cone is three times
    /// it and must stay a valid chord.</summary>
    public const float MaxPenumbraSlope = 0.3f;

    private readonly SdfLight[] m_lights = new SdfLight[MaxLights];

    private int m_count;

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

    /// <summary>Gets the frame's explicit stable owners, policy and active handoffs.</summary>
    public SdfShadowSlots ShadowSlots { get; } = new();

    /// <summary>Gets one light.</summary>
    /// <param name="index">The light's index in the table.</param>
    /// <returns>The light.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside the table.</exception>
    public SdfLight this[int index] => m_lights[index];

    /// <summary>Gets the frame's key light: the light holding the primary shadow role (<see cref="RoleSlot"/>), or -1 when
    /// no slotted light emits.</summary>
    public int Key => RoleLight(role: SdfShadowSlots.Primary);

    /// <summary>Returns the stable shadow slot that holds a role, derived from the slots' owners and what each emits so no
    /// writer can leave it stale: the primary role goes to the slotted directional light of greatest luminance (its weight
    /// times its colour's luminance), the secondary to the next, ties to the lower slot. A light that emits nothing holds
    /// no role, nor does a vacant slot, a departed light or one that is not directional.</summary>
    /// <param name="role">The role, <see cref="SdfShadowSlots.Primary"/> or <see cref="SdfShadowSlots.Secondary"/>.</param>
    /// <returns>The slot, or -1 when no slot holds the role.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="role"/> is not a role.</exception>
    public int RoleSlot(int role) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: role);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: role, other: SdfShadowSlots.Roles);
        var taken = 0u;
        var slot = -1;

        for (var pass = 0; (pass <= role); pass++) {
            slot = -1;
            var best = 0f;

            for (var candidate = 0; (candidate < ShadowSlots.SlotCount); candidate++) {
                var light = ShadowSlots[candidate];

                if (((taken & (1u << candidate)) != 0u) || (light < 0) || (light >= m_count) || (m_lights[light].Kind != SdfLightKind.Directional)) { continue; }
                var luminance = (m_lights[light].Weight * SdfPhotometry.LuminanceOf(color: m_lights[light].Color));

                if (luminance > best) { best = luminance; slot = candidate; }
            }
            if (slot < 0) { return -1; }
            taken |= (1u << slot);
        }
        return slot;
    }
    /// <summary>Returns the light that holds a shadow role (<see cref="RoleSlot"/>), or -1 when no slot holds it.</summary>
    /// <param name="role">The role, <see cref="SdfShadowSlots.Primary"/> or <see cref="SdfShadowSlots.Secondary"/>.</param>
    /// <returns>The light's index in the table.</returns>
    public int RoleLight(int role) => ((RoleSlot(role: role) is >= 0 and var slot) ? ShadowSlots[slot] : -1);

    // What a point or occluder light's dynamic slot may be.
    private static string LightSlotRule => $"a point or occluder light's dynamic slot must be {SdfProgram.NoDynamicTransformSlot} or in [0, {SdfProgram.MaxDynamicTransformSlot}].";

    /// <summary>Creates the lights an unauthored world renders: the pinned sun with shadows, a point source (a directional
    /// light's <see cref="SdfLight.Param"/> is its penumbra half-slope, the tangent of its angular radius, and zero
    /// casts the hardest edge the cascades' filter draws).</summary>
    /// <returns>The lights.</returns>
    public static SdfLights Default() {
        var lights = new SdfLights();
        var weight = SdfPhotometry.DirectionalWeight(lux: DefaultSunIlluminance);

        lights.Set(
            index: 0,
            light: new SdfLight(
                Kind: SdfLightKind.Directional,
                Direction: DefaultSunDirection,
                Color: Vector3.One,
                Weight: weight,
                Param: 0f,
                Shadows: true
            )
        );
        lights.Count = 1;
        lights.ShadowSlots.Configure(fadeCapacity: 0, slots: 1);
        lights.ShadowSlots.SetSlot(light: 0, slot: 0);

        return lights;
    }
    /// <summary>Copies every record, the count, the shadow slots and the curvature from another table.</summary>
    /// <param name="source">The table to copy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public void CopyFrom(SdfLights source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        source.m_lights.CopyTo(
            array: m_lights,
            index: 0
        );
        m_count = source.m_count;
        ShadowSlots.CopyFrom(source: source.ShadowSlots);
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
    /// <summary>Sets one light independently of the frame's explicit shadow-slot selection.</summary>
    /// <param name="index">The light's index in the table.</param>
    /// <param name="light">The light.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the table, or a point or
    /// occluder light's <see cref="SdfLight.DynamicSlot"/> is neither <see cref="SdfProgram.NoDynamicTransformSlot"/>
    /// nor a slot in [0, <see cref="SdfProgram.MaxDynamicTransformSlot"/>], or its
    /// <see cref="SdfLight.Bounce"/> is negative or nonfinite.</exception>
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

        if (!float.IsFinite(f: light.Bounce) || (light.Bounce < 0f)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(light), message: $"light {index}'s bounce must be finite and nonnegative.");
        }

        m_lights[index] = (light with {
            DynamicSlot = (SdfLight.IsPositional(kind: light.Kind) ? light.DynamicSlot : 0),
            Shadows = ((light.Shadows != 0u) ? 1u : 0u),
        });
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
