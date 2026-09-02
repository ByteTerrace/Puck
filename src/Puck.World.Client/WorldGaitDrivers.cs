using System.Numerics;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Authoring;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The per-body animation-driver runtime a creation look's <see cref="CreationDriverDocument"/> rows drive: one
/// running phase and one eased weight per declared driver, advanced once a frame from the body's rendered pose delta
/// and its <see cref="BodyFacts"/>. Presentation-only — the state lives on the stamp pool's registration and nothing
/// derived from it re-enters the simulation.
/// </summary>
/// <remarks>An integrating driver's phase advances on its signal's delta, so an idle body holds its pose and travel
/// (or elapsed time) sets the rate; the weight eases on wall time so a facet returns to rest over
/// <see cref="CreationDriverDocument.WeightSeconds"/> instead of freezing mid-stride when the gate flips.</remarks>
public static class WorldGaitDrivers {
    /// <summary>The travel one frame may charge to an integrating phase, world units — clamps a teleport or an
    /// authority snap so it cannot spin a limb through dozens of cycles in one frame. Shared with the procedural
    /// catalog rig's own gait so a creation limb and a catalog limb answer a jump the same way.</summary>
    public const float MaxTravelPerFrame = WorldMirroredAvatarBand.MaxGaitTravelPerFrame;
    /// <summary>The eased rendered speed, metres per second, above which the <c>moving</c> gate token holds. The
    /// speed it tests is low-passed over <see cref="CreationDriverDocument.WeightSeconds"/>, so a body crossing the
    /// threshold does not flicker the gate frame to frame.</summary>
    public const float MovingSpeed = 0.05f;

    /// <summary>The weight below which an easing-out driver is at rest: the exponential approach never reaches zero,
    /// and a driver whose weight is merely denormal still advances its phase forever. Snapping there stops the phase
    /// as well as the pose, and a milliradian of residual swing is invisible.</summary>
    public const float RestWeight = 1e-3f;

    private const float TwoPi = (2f * MathF.PI);

    /// <summary>Advances one body's driver phases and weights for a frame. Reseeds — every phase and weight to zero,
    /// no signal charged — when the entity address changes or this is the first call for the registration, so a
    /// reused body slot never inherits the previous inhabitant's stride.</summary>
    /// <param name="drivers">The creation's declared drivers; entries past <paramref name="phases"/>'s length are
    /// ignored (the registration's table is sized by <see cref="CreationDocument.MaxDrivers"/>).</param>
    /// <param name="phases">The per-driver running phase, indexed by driver row.</param>
    /// <param name="weights">The per-driver ease weight in [0, 1], same indexing.</param>
    /// <param name="deltaSeconds">The frame delta the weights ease over and the time/rate signals read.</param>
    /// <param name="facts">The body's sim facts this frame — each driver's <c>when</c> gate tests these.</param>
    /// <param name="position">The body's render position this frame.</param>
    /// <param name="orientation">The body's render attitude this frame.</param>
    /// <param name="lastPosition">The position latched by the previous call; updated every call.</param>
    /// <param name="lastOrientation">The attitude latched by the previous call; updated every call.</param>
    /// <param name="seeded">Whether a prior call latched an address; set <see langword="true"/> on return.</param>
    /// <param name="lastAddress">The address latched by the previous call; updated on a reseed.</param>
    /// <param name="easedSpeed">The low-passed rendered speed backing the <c>moving</c>/<c>still</c> gate tokens,
    /// metres per second; updated every call and zeroed on a reseed.</param>
    /// <param name="address">This call's entity address.</param>
    /// <param name="definition">The live definition a state-cell signal reads, or null when none can.</param>
    /// <param name="tick">The tick a state-cell signal is read at.</param>
    public static void Advance(IReadOnlyList<CreationDriverDocument>? drivers, Span<float> phases, Span<float> weights, float deltaSeconds, BodyFacts facts, Vector3 position, Quaternion orientation, ref Vector3 lastPosition, ref Quaternion lastOrientation, ref bool seeded, ref WorldEntityAddress lastAddress, ref float easedSpeed, WorldEntityAddress address, WorldDefinition? definition = null, ulong tick = 0UL) {
        if (
            !seeded ||
            (lastAddress != address)
        ) {
            seeded = true;
            lastAddress = address;
            lastPosition = position;
            lastOrientation = orientation;
            easedSpeed = 0f;
            phases.Clear();
            weights.Clear();

            return;
        }

        var travel = (position - lastPosition);
        var yawDelta = WrapAngle(radians: (Yaw(rotation: orientation) - Yaw(rotation: lastOrientation)));

        lastPosition = position;
        lastOrientation = orientation;

        var blend = WeightBlend(deltaSeconds: deltaSeconds);

        // The eased speed is latched whether or not the creation declares any driver, so a look gaining one mid-run
        // reads a settled speed rather than a cold zero.
        easedSpeed += (((((deltaSeconds > 0f)
            ? (travel.Length() / deltaSeconds)
            : 0f) - easedSpeed) * blend));

        var moving = (easedSpeed > MovingSpeed);

        if (drivers is not { Count: > 0 } rows) {
            return;
        }

        var planar = MathF.Min(
            x: new Vector3(
                x: travel.X,
                y: 0f,
                z: travel.Z
            ).Length(),
            y: MaxTravelPerFrame
        );
        var total = MathF.Min(
            x: travel.Length(),
            y: MaxTravelPerFrame
        );
        // A zero delta yields zero rates rather than infinities: the frame charged no time, so nothing moved per
        // second either.
        var perSecond = ((deltaSeconds > 0f)
            ? (1f / deltaSeconds)
            : 0f
        );
        var count = Math.Min(
            val1: rows.Count,
            val2: phases.Length
        );

        for (var index = 0; (index < count); index++) {
            var driver = rows[index];
            var holds = GateHolds(
                facts: facts,
                gate: driver.When,
                moving: moving
            );

            var weight = (weights[index] + (((holds
                ? 1f
                : 0f) - weights[index]) * blend));

            weights[index] = ((!holds && (weight < RestWeight))
                ? 0f
                : weight
            );

            if (CreationDriverDocument.IsStateSignal(signal: driver.Signal)) {
                // A state cell read at the frame's tick: a cycle-trait clock, a drawn value, an advancing counter —
                // the phase is the sim's own number, so two clients (and a replay) agree on it exactly.
                phases[index] = (((definition is not null) && TryReadStateNumber(
                    definition: definition,
                    reference: driver.Signal!,
                    tick: tick,
                    value: out var stateValue
                ))
                    ? (driver.Cadence.Value * stateValue)
                    : 0f
                );

                continue;
            }
            if (!CreationDriverDocument.Integrates(signal: driver.Signal)) {
                phases[index] = (driver.Cadence.Value * (driver.Signal switch {
                    CreationDriverDocument.SignalSpeed => (total * perSecond),
                    CreationDriverDocument.SignalVerticalSpeed => (travel.Y * perSecond),
                    _ => (yawDelta * perSecond),
                }));

                continue;
            }

            if (weights[index] <= 0f) {
                continue;
            }

            // Wrapped into [0, 2π) every frame: both waveforms are 2π-periodic in their argument, so wrapping is
            // exact for the motion and is what keeps a rotor's phase from losing float precision after an hour.
            phases[index] = Wrap(radians: (phases[index] + (driver.Cadence.Value * (driver.Signal switch {
                CreationDriverDocument.SignalPlanarTravel => planar,
                CreationDriverDocument.SignalTravel => total,
                _ => deltaSeconds,
            }))));
        }
    }
    /// <summary>Composes every swing and slide a shape declares onto its creation-space rest pose. Swings apply in
    /// authored order, then slides, so a slide reads as an offset along its own axis rather than one the swings
    /// have turned.</summary>
    /// <param name="shape">The shape whose facets are composed.</param>
    /// <param name="drivers">The creation's declared drivers — a facet naming none composes nothing.</param>
    /// <param name="phases">The per-driver phases.</param>
    /// <param name="weights">The per-driver weights.</param>
    /// <param name="position">The shape's position; replaced by the animated position.</param>
    /// <param name="rotation">The shape's orientation; replaced by the animated orientation.</param>
    public static void Compose(ShapeDocument shape, IReadOnlyList<CreationDriverDocument>? drivers, ReadOnlySpan<float> phases, ReadOnlySpan<float> weights, ref Vector3 position, ref Quaternion rotation) {
        ComposeDelta(
            drivers: drivers,
            phases: phases,
            rotation: out var deltaRotation,
            shape: shape,
            translation: out var deltaTranslation,
            weights: weights
        );
        Apply(
            deltaRotation: deltaRotation,
            deltaTranslation: deltaTranslation,
            position: ref position,
            rotation: ref rotation
        );
    }
    /// <summary>Applies a rigid delta (<c>x → R·x + t</c>) to a shape's creation-space pose.</summary>
    /// <param name="deltaRotation">The delta's rotation.</param>
    /// <param name="deltaTranslation">The delta's translation.</param>
    /// <param name="position">The shape's position; replaced by the carried position.</param>
    /// <param name="rotation">The shape's orientation; replaced by the carried orientation.</param>
    public static void Apply(Quaternion deltaRotation, Vector3 deltaTranslation, ref Vector3 position, ref Quaternion rotation) {
        position = (Vector3.Transform(
            rotation: deltaRotation,
            value: position
        ) + deltaTranslation);
        rotation = (deltaRotation * rotation);
    }
    /// <summary>Chains a child's rigid delta under its parent's: the parent's motion carries the child's whole
    /// frame, pivots included, so <c>x → Rp·(Rc·x + tc) + tp</c>.</summary>
    /// <param name="parentRotation">The parent's composed rotation.</param>
    /// <param name="parentTranslation">The parent's composed translation.</param>
    /// <param name="rotation">The child's own delta rotation; replaced by the chained rotation.</param>
    /// <param name="translation">The child's own delta translation; replaced by the chained translation.</param>
    public static void Chain(Quaternion parentRotation, Vector3 parentTranslation, ref Quaternion rotation, ref Vector3 translation) {
        translation = (Vector3.Transform(
            rotation: parentRotation,
            value: translation
        ) + parentTranslation);
        rotation = (parentRotation * rotation);
    }
    /// <summary>Computes the rigid delta (<c>x → R·x + t</c>, engine-frame creation space) a shape's swings and
    /// slides produce this frame, independent of the shape's own rest pose — what a child shape rides.</summary>
    /// <param name="shape">The shape whose facets are composed.</param>
    /// <param name="drivers">The creation's declared drivers — a facet naming none composes nothing.</param>
    /// <param name="phases">The per-driver phases.</param>
    /// <param name="weights">The per-driver weights.</param>
    /// <param name="rotation">The delta's rotation (identity when nothing composes).</param>
    /// <param name="translation">The delta's translation (zero when nothing composes).</param>
    /// <param name="definition">The live definition a curve waveform samples, or null when none can.</param>
    public static void ComposeDelta(ShapeDocument shape, IReadOnlyList<CreationDriverDocument>? drivers, ReadOnlySpan<float> phases, ReadOnlySpan<float> weights, out Quaternion rotation, out Vector3 translation, WorldDefinition? definition = null) {
        ArgumentNullException.ThrowIfNull(argument: shape);

        rotation = Quaternion.Identity;
        translation = Vector3.Zero;

        if (drivers is not { Count: > 0 } rows) {
            return;
        }

        if (shape.Swings is { Count: > 0 } swings) {
            for (var index = 0; (index < swings.Count); index++) {
                var swing = swings[index];

                if (!TryDriver(
                    driver: swing.Driver,
                    phase: out var phase,
                    phases: phases,
                    rows: rows,
                    weight: out var weight,
                    weights: weights
                )) {
                    continue;
                }

                var turn = Quaternion.CreateFromAxisAngle(
                    angle: (swing.Amplitude.Value * Wave(
                    argument: (phase + (swing.Phase?.Value ?? 0f)),
                    definition: definition,
                    wave: swing.Wave
                ) * weight),
                    axis: swing.Axis.Value
                );
                var pivot = swing.Pivot.Value;

                Chain(
                    parentRotation: turn,
                    parentTranslation: (pivot - Vector3.Transform(
                        rotation: turn,
                        value: pivot
                    )),
                    rotation: ref rotation,
                    translation: ref translation
                );
            }
        }

        if (shape.Slides is { Count: > 0 } slides) {
            for (var index = 0; (index < slides.Count); index++) {
                var slide = slides[index];

                if (!TryDriver(
                    driver: slide.Driver,
                    phase: out var phase,
                    phases: phases,
                    rows: rows,
                    weight: out var weight,
                    weights: weights
                )) {
                    continue;
                }

                translation += (slide.Axis.Value * (slide.Amplitude.Value * Wave(
                    argument: (phase + (slide.Phase?.Value ?? 0f)),
                    definition: definition,
                    wave: slide.Wave
                ) * weight));
            }
        }
    }
    /// <summary>Evaluates a facet's waveform: the built-in shapes through <see cref="CreationWave.Evaluate"/>, a
    /// <c>curve:&lt;row&gt;</c> form by sampling the world's row at the argument's fraction of a turn along its arc
    /// (Z is the value). A curve the world does not declare evaluates to zero — the validator refuses it at boot.</summary>
    /// <param name="wave">The waveform name.</param>
    /// <param name="argument">The driver phase plus the facet's phase offset, radians.</param>
    /// <param name="definition">The live definition the curve rows come from.</param>
    /// <returns>The waveform value.</returns>
    public static float Wave(string? wave, float argument, WorldDefinition? definition) {
        if (!CreationWave.TryCurveName(
            name: out var name,
            wave: wave
        )) {
            return CreationWave.Evaluate(
                argument: argument,
                wave: wave
            );
        }
        if ((definition is null) || (WorldDefinitionRows.FindCurve(
            curves: definition.Curves,
            name: name
        ) is not { } row)) {
            return 0f;
        }

        var turns = (argument / (2f * MathF.PI));
        var fraction = (turns - MathF.Floor(x: turns));
        var compiled = row.Compiled;
        var sample = compiled.Evaluate(arcLength: FixedQ4816.FromDouble(value: (((double)compiled.TotalLength) * fraction)));

        return ((float)((double)sample.Position.Z));
    }
    /// <summary>Reads a <c>state.&lt;row&gt;[.&lt;key&gt;]</c> numeric cell at a tick as a float.</summary>
    /// <param name="definition">The live definition.</param>
    /// <param name="reference">The state reference.</param>
    /// <param name="tick">The tick an advancing or cycling row is read at.</param>
    /// <param name="value">The cell's value; zero when the cell is absent or not numeric.</param>
    /// <returns><see langword="true"/> when a numeric cell answered.</returns>
    public static bool TryReadStateNumber(WorldDefinition definition, string reference, ulong tick, out float value) {
        value = 0f;

        if (
            !WorldColor.TryParseBinding(
            key: out var key,
            row: out var rowName,
            value: reference
        ) ||
            !WorldStateReader.TryRead(
            definition: definition,
            key: key,
            rawValue: out var raw,
            row: out var row,
            rowName: rowName,
            text: out _,
            tick: tick
        ) ||
            (raw is not { } bits)
        ) {
            return false;
        }

        value = (row.Kind switch {
            CellKind.Fixed => ((float)((double)FixedQ4816.FromRawBits(value: bits))),
            CellKind.Text => 0f,
            _ => ((float)bits),
        });

        return (row.Kind != CellKind.Text);
    }
    /// <summary>Reads a <c>state.&lt;row&gt;[.&lt;key&gt;]</c> text cell spelling a world-space <c>[x, y, z]</c>.</summary>
    /// <param name="definition">The live definition.</param>
    /// <param name="reference">The state reference.</param>
    /// <param name="tick">The tick an advancing or cycling row is read at.</param>
    /// <param name="value">The parsed point; zero when the cell is absent, not text, or not three numbers.</param>
    /// <returns><see langword="true"/> when a text cell parsed as three numbers.</returns>
    /// <remarks>Parses off the cell's own string rather than deserializing, so a per-frame read allocates
    /// nothing.</remarks>
    public static bool TryReadStateVector(WorldDefinition definition, string reference, ulong tick, out Vector3 value) {
        value = Vector3.Zero;

        if (
            !WorldColor.TryParseBinding(
            key: out var key,
            row: out var rowName,
            value: reference
        ) ||
            !WorldStateReader.TryRead(
            definition: definition,
            key: key,
            rawValue: out _,
            row: out _,
            rowName: rowName,
            text: out var text,
            tick: tick
        ) ||
            (text is not { Length: > 0 } spelling)
        ) {
            return false;
        }

        var cursor = spelling.AsSpan().Trim();

        if (
            (cursor.Length > 1) &&
            (cursor[0] == '[') &&
            (cursor[^1] == ']')
        ) {
            cursor = cursor[1..^1];
        }

        Span<float> components = stackalloc float[3];

        for (var index = 0; (index < 3); index++) {
            var separator = cursor.IndexOf(value: ',');
            var term = ((separator < 0)
                ? cursor
                : cursor[..separator]
            );

            if (!float.TryParse(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out components[index],
                s: term.Trim(),
                style: System.Globalization.NumberStyles.Float
            )) {
                return false;
            }

            cursor = ((separator < 0)
                ? []
                : cursor[(separator + 1)..]
            );
        }

        value = new Vector3(
            x: components[0],
            y: components[1],
            z: components[2]
        );

        return true;
    }
    /// <summary>Returns whether a driver's gate holds — every token must, and an absent or empty gate is ungated.
    /// A token no side can resolve fails the conjunction, so an unrecognized gate holds the driver at rest rather
    /// than running it unconditionally.</summary>
    /// <param name="gate">The authored gate tokens.</param>
    /// <param name="facts">The body's sim facts this frame.</param>
    /// <param name="moving">Whether the body's eased speed is above <see cref="MovingSpeed"/>.</param>
    /// <returns><see langword="true"/> when every token holds.</returns>
    public static bool GateHolds(IReadOnlyList<string>? gate, BodyFacts facts, bool moving) {
        if (gate is not { Count: > 0 } tokens) {
            return true;
        }

        for (var index = 0; (index < tokens.Count); index++) {
            var token = tokens[index];

            if (string.Equals(
                a: token,
                b: CreationDriverDocument.TokenMoving,
                comparisonType: StringComparison.Ordinal
            )) {
                if (!moving) {
                    return false;
                }

                continue;
            }

            if (string.Equals(
                a: token,
                b: CreationDriverDocument.TokenStill,
                comparisonType: StringComparison.Ordinal
            )) {
                if (moving) {
                    return false;
                }

                continue;
            }

            if (
                !BodyFactVocabulary.TryResolve(
                gate: out var bit,
                name: token
            ) || !BodyFactVocabulary.Holds(
                facts: facts,
                gate: bit
            )
            ) {
                return false;
            }
        }

        return true;
    }
    /// <summary>Returns the yaw of a rotation about world up, radians, under the engine's −Z-forward convention.</summary>
    /// <param name="rotation">The attitude.</param>
    /// <returns>The yaw in (−π, π].</returns>
    public static float Yaw(Quaternion rotation) {
        var forward = Vector3.Transform(
            rotation: rotation,
            value: -Vector3.UnitZ
        );

        // Engine forward is −Z, so a rotation of θ about +Y carries it to (−sin θ, 0, −cos θ); negating both
        // components recovers θ with the sign the axis-angle convention gives it.
        return MathF.Atan2(
            x: -forward.Z,
            y: -forward.X
        );
    }
    /// <summary>Returns the fraction of the remaining error a weight closes in one frame — the frame-rate independent
    /// exponential approach over <see cref="CreationDriverDocument.WeightSeconds"/>.</summary>
    /// <param name="deltaSeconds">The frame delta; a non-positive delta closes nothing.</param>
    /// <returns>The blend factor in [0, 1].</returns>
    public static float WeightBlend(float deltaSeconds) => ((deltaSeconds > 0f)
        ? (1f - MathF.Exp(x: (-deltaSeconds / CreationDriverDocument.WeightSeconds)))
        : 0f
    );

    /// <summary>Reads a named driver's current phase and weight.</summary>
    /// <param name="rows">The creation's declared drivers.</param>
    /// <param name="driver">The driver name a facet, or an effector's plant window, resolves against.</param>
    /// <param name="phases">The per-driver phases.</param>
    /// <param name="weights">The per-driver weights.</param>
    /// <param name="phase">The driver's phase, radians; zero when the name resolves to no driver.</param>
    /// <param name="weight">The driver's eased weight in [0, 1]; zero when the name resolves to no driver.</param>
    /// <returns><see langword="true"/> when the name resolves.</returns>
    public static bool TryDriver(IReadOnlyList<CreationDriverDocument> rows, string driver, ReadOnlySpan<float> phases, ReadOnlySpan<float> weights, out float phase, out float weight) {
        var count = Math.Min(
            val1: rows.Count,
            val2: phases.Length
        );

        for (var index = 0; (index < count); index++) {
            if (string.Equals(
                a: rows[index].Name,
                b: driver,
                comparisonType: StringComparison.Ordinal
            )) {
                phase = phases[index];
                weight = weights[index];

                return true;
            }
        }

        phase = 0f;
        weight = 0f;

        return false;
    }
    private static float Wrap(float radians) {
        var wrapped = MathF.IEEERemainder(
            x: radians,
            y: TwoPi
        );

        return ((wrapped < 0f)
            ? (wrapped + TwoPi)
            : wrapped
        );
    }
    private static float WrapAngle(float radians) {
        var wrapped = Wrap(radians: radians);

        return ((wrapped > MathF.PI)
            ? (wrapped - TwoPi)
            : wrapped
        );
    }
}
