using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>The world's far sources (<see cref="WorldGravityTide"/>) reduced to one tide: the sum of each source's
/// tidal tensor at the world's tide origin, formed once a gravity solve and applied to every body's offset from that
/// origin.</summary>
/// <remarks>
/// <para>The invariant is that a far source never moves a body's frame: only the tensor is formed
/// (<see cref="GravityTide.TryFromSource"/>), and nothing here evaluates a source's pull itself, so no caller can add
/// the uniform term a freely falling world does not feel.</para>
/// <para>The tensor is formed again only when an input moved: a source riding an orbit or an attached placement, or a
/// mass row whose value changed. A world whose sources stand still pays one evaluation for its whole run.</para>
/// </remarks>
public sealed class WorldGravityTides {
    private readonly bool[] m_active;
    private readonly FixedWorldGravity m_compiled;
    private readonly FixedVector3[] m_positions;
    private readonly FixedQ4816[] m_scales;

    private bool m_formed;
    private GravityTide m_tide;

    private bool TryForm(out GravityTide tide) {
        tide = default;

        var kernel = new GravityParameters(
            GravitationalConstant: FixedQ4816.One,
            SofteningLength: m_compiled.Parameters.SofteningLength
        );
        var refused = 0;

        for (var index = 0; (index < m_compiled.Tides.Length); index++) {
            if (!m_active[index] || (m_scales[index] <= FixedQ4816.Zero)) {
                continue;
            }

            if (
                !GravityTide.TryFromSource(
                    mass: FixedSaturate.Multiply(left: m_compiled.Tides[index].Strength, right: m_scales[index]),
                    origin: m_compiled.TideOrigin,
                    parameters: in kernel,
                    source: m_positions[index],
                    tide: out var source
                ) ||
                !GravityTide.TryAdd(left: tide, right: source, sum: out var sum)
            ) {
                // A source the fixed-point kernel cannot carry (too near, too far or too massive) raises no tide
                // rather than a wrong one; the read-back counts it.
                refused++;

                continue;
            }

            tide = sum;
        }

        RefusedSourceCount = refused;

        return true;
    }

    /// <summary>Returns the tidal acceleration at a world position.</summary>
    /// <param name="position">The world position.</param>
    /// <returns>The tide applied to the position's offset from the tide origin; zero when the product leaves the
    /// grid.</returns>
    public FixedVector3 AccelerationAt(FixedVector3 position) => (m_tide.TryAccelerationAt(acceleration: out var acceleration, offset: (position - m_compiled.TideOrigin))
        ? acceleration
        : FixedVector3.Zero
    );
    /// <summary>Reads the sources' positions and mass rows for a solve and forms the tide again when any moved.</summary>
    /// <param name="inputs">The solve's state and orbits.</param>
    /// <returns><see langword="true"/> when the world has a tide to apply.</returns>
    public bool Refresh(in WorldGravityInputs inputs) {
        if (m_compiled.Tides.Length == 0) {
            return false;
        }

        var moved = !m_formed;

        for (var index = 0; (index < m_compiled.Tides.Length); index++) {
            ref readonly var source = ref m_compiled.Tides[index];

            if ((source.OrbitIndex >= 0) && (inputs.Orbits is { } orbits) && (source.OrbitIndex < orbits.Count)) {
                var position = orbits.Position(index: source.OrbitIndex);

                moved |= (position != m_positions[index]);
                m_positions[index] = position;
            }

            var scale = ((source.ScaleRow is { } row)
                ? inputs.ReadFixed(fallback: FixedQ4816.One, key: null, row: row)
                : FixedQ4816.One
            );

            moved |= (scale != m_scales[index]);
            m_scales[index] = scale;
        }

        if (moved) {
            _ = TryForm(tide: out m_tide);
            m_formed = true;
            FormCount++;
        }

        return !m_tide.IsZero;
    }
    /// <summary>Refreshes sources riding attached placements from the current authoritative body poses.</summary>
    /// <param name="population">The live population used by the established placement-attachment resolver.</param>
    public void RefreshAttached(WorldPopulation population) {
        for (var index = 0; (index < m_compiled.Tides.Length); index++) {
            if (m_compiled.Tides[index].Attach is not { } attach) {
                continue;
            }

            var resolved = WorldPlacementAttachment.TryResolve(attach: attach, population: population, position: out var position, reason: out _, yawRadians: out _);

            if ((resolved != m_active[index]) || (resolved && (position != m_positions[index]))) {
                m_formed = false;
            }

            m_active[index] = resolved;
            if (resolved) { m_positions[index] = position; }
        }
    }

    /// <summary>Initializes the tide over a compiled gravity section's far sources.</summary>
    /// <param name="compiled">The compiled gravity section.</param>
    public WorldGravityTides(FixedWorldGravity compiled) {
        m_compiled = compiled;
        m_active = new bool[compiled.Tides.Length];
        m_positions = new FixedVector3[compiled.Tides.Length];
        m_scales = new FixedQ4816[compiled.Tides.Length];

        for (var index = 0; (index < compiled.Tides.Length); index++) {
            m_active[index] = (compiled.Tides[index].Attach is null);
            m_positions[index] = compiled.Tides[index].AuthoredPosition;
            m_scales[index] = FixedQ4816.One;
        }
    }

    /// <summary>Gets how many times the tensor has been formed: once for a world whose sources stand still.</summary>
    public long FormCount { get; private set; }
    /// <summary>Gets the sources the last forming could not carry in fixed point, which raised no tide.</summary>
    public int RefusedSourceCount { get; private set; }
    /// <summary>Gets the number of far sources.</summary>
    public int SourceCount => m_compiled.Tides.Length;
    /// <summary>Gets the tide as last formed.</summary>
    public GravityTide Tide => m_tide;
}
