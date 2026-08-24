using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Puck.Maths;

namespace Puck.World;

/// <summary>The admitted authored ranges for a <see cref="WorldDynamicsRow"/> triple — the ceilings
/// <see cref="WorldDefinitionValidator"/> enforces (widened only alongside a <c>maths-laws</c> law covering the wider
/// domain).</summary>
public static class WorldDynamics {
    /// <summary>The greatest admitted damping ratio ζ. Zero is admitted (a system that rings forever); there is no
    /// floor beyond non-negative.</summary>
    public const float MaxDamping = 16f;
    /// <summary>The greatest admitted natural frequency, Hz. Chosen well inside the Q32 coefficient carrier's own
    /// derived bound (ω² &lt; 2³¹ ⇔ f ≲ 7375 Hz at ζ = 0) — a document ceiling, not the carrier's.</summary>
    public const float MaxFrequencyHz = 100f;
    /// <summary>The greatest admitted initial response r.</summary>
    public const float MaxResponse = 4f;
    /// <summary>The least admitted initial response r.</summary>
    public const float MinResponse = -4f;
}
/// <summary>
/// One named <c>dynamics</c> row — the t3ssel8r-style second-order "personality" triple every consumer (a look's
/// root/part follower, a camera boom, a kit's planar velocity shaping, a state cell's eased read) names by
/// <see cref="Name"/> rather than authoring inline: one mechanism, referenced everywhere a follower is wanted. A world
/// declares none when nothing wants one — every reference is nullable, so an unauthored world is unchanged.
/// </summary>
/// <param name="Name">The row's stable name, unique within the section — the spelling every consumer's own
/// <c>dynamics</c>/<c>row</c> reference resolves against.</param>
/// <param name="Frequency">The natural frequency f, Hz. Must be finite and positive; higher is snappier. A value that
/// rounds to zero at Q16, or whose derived oscillation rate is too close to critical to resolve at the Q32
/// coefficient scale, is refused (see <c>WorldDefinitionValidator.ValidateDynamics</c>).</param>
/// <param name="Damping">The damping ratio ζ (dimensionless). <c>0</c> rings forever; <c>&lt;1</c> overshoots and
/// rings down; <c>1</c> is critically damped (the fastest approach that never overshoots); <c>&gt;1</c> is
/// overdamped (slower, still no overshoot).</param>
/// <param name="Response">The initial response r (dimensionless). <c>0</c> eases in from rest; <c>&gt;0</c> reacts
/// immediately to the target's own motion; <c>&gt;1</c> overshoots the target's motion before settling; <c>&lt;0</c>
/// anticipates by initially moving opposite the target's motion.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldDynamicsRow(
    string Name,
    [property: JsonPropertyName("f")] float Frequency,
    [property: JsonPropertyName("zeta")] float Damping,
    [property: JsonPropertyName("r")] float Response
) {
    // Keyed on the row instance itself: an authored row is an immutable record and a live retune always installs a
    // fresh instance (never a mutation in place), so the cache can never serve a stale derivation and needs no
    // invalidation of its own. Kept off the record's own equality-compared surface (a lazily-populated field would
    // make two otherwise-identical rows compare unequal purely because one had been read from and the other had
    // not), which is also why this is a table rather than a plain private field.
    private static readonly ConditionalWeakTable<WorldDynamicsRow, StrongBox<SecondOrderDynamics>> CompiledCache = new();

    /// <summary>Gets this row's triple as the plain <see cref="WorldDynamics"/> shape every consumer's own compile
    /// step reads (fixed-point simulation paths through <c>Puck.Maths.SecondOrderDynamics.Create</c>, the
    /// presentation float twin through <c>Puck.SdfVm.Views.SecondOrderResponse.Create</c> — both from this SAME
    /// authored triple, never a second derivation).</summary>
    [JsonIgnore]
    public (float Frequency, float Damping, float Response) Parameters => (Frequency, Damping, Response);
    /// <summary>Gets this row's authored triple derived through <see cref="SecondOrderDynamics.Create"/> — the SAME
    /// derivation the simulation's own fixed-point followers compile from, exposed here for a per-frame reader (the
    /// HUD's eased <c>state.&lt;row&gt;</c> binding) that cannot afford <see cref="SecondOrderDynamics.Create"/>'s
    /// exact <see cref="System.Numerics.BigInteger"/> derivation on every frame. Derived once per row instance and
    /// cached.</summary>
    [JsonIgnore]
    public SecondOrderDynamics Compiled => CompiledCache.GetValue(
        key: this,
        createValueCallback: static row => new StrongBox<SecondOrderDynamics>(value: SecondOrderDynamics.Create(
            frequencyHz: FixedQ4816.FromDouble(value: row.Frequency),
            dampingRatio: FixedQ4816.FromDouble(value: row.Damping),
            initialResponse: FixedQ4816.FromDouble(value: row.Response)
        ))
    ).Value;
}
