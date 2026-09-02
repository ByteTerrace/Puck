using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Assets.Documents;

namespace Puck.World.Authoring;

/// <summary>
/// One named animation driver a creation declares: a scalar signal read off the body the creation is stamped on,
/// times a cadence, gated by a conjunction of condition tokens. A driver yields a phase φ and an eased weight w ∈ [0, 1]; a shape's
/// <see cref="ShapeSwingDocument"/>/<see cref="ShapeSlideDocument"/> facets name a driver and turn (φ, w) into a
/// rotation or a translation. Three composable parts — driver (signal + gate), waveform, joint — cover a walker's
/// limbs, a wheel, a rotor, a tail, and a bobbing hull without any of them being named in engine code.
/// </summary>
/// <param name="Name">The driver's name, unique within the creation — the spelling a facet's <c>driver</c> member
/// resolves against.</param>
/// <param name="Signal">One of <see cref="SignalPlanarTravel"/>, <see cref="SignalTravel"/>,
/// <see cref="SignalTime"/>, <see cref="SignalSpeed"/>, <see cref="SignalVerticalSpeed"/>, or
/// <see cref="SignalTurnRate"/>. The first three integrate: φ accumulates <see cref="Cadence"/> × the signal's
/// per-frame delta while w &gt; 0, and wraps modulo 2π so a wheel spins forever without losing precision. The last
/// three are instantaneous: φ is set to <see cref="Cadence"/> × the current value, so a facet reading them tracks
/// rather than cycles.</param>
/// <param name="Cadence">The signal-to-phase gain: radians per metre for the travel signals, radians per second for
/// <see cref="SignalTime"/>, radians per metre-per-second for <see cref="SignalSpeed"/>/<see cref="SignalVerticalSpeed"/>,
/// and radians per radian-per-second for <see cref="SignalTurnRate"/>. Magnitude at most
/// <see cref="MaxCadence"/>.</param>
/// <param name="When">The gate: every token must hold for the driver's weight to ease toward 1 (it eases
/// toward 0 otherwise, over <see cref="WeightSeconds"/>, so a limb returns to rest instead of freezing mid-stride).
/// Authored as one token or an array of them — a bare string reads as a one-token gate and canonicalizes to the
/// array form. A token is a <c>Puck.Physics.Motion.BodyFacts</c> member name, <see cref="WhenAlways"/>,
/// <see cref="TokenMoving"/>, or <see cref="TokenStill"/>; null is ungated. At most <see cref="MaxGateTokens"/>.
/// A token no consumer can resolve gates the driver permanently off.</param>
public sealed record CreationDriverDocument(
    string Name,
    string Signal,
    DocumentScalar Cadence,
    [property: JsonConverter(typeof(DriverGateJsonConverter)), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? When = null
) {
    /// <summary>The largest signal-to-phase gain magnitude.</summary>
    public const float MaxCadence = 256f;
    /// <summary>Planar (horizontal) rendered travel, metres — a stride that a ramp's rise must not charge.</summary>
    public const string SignalPlanarTravel = "planarTravel";
    /// <summary>The body's total rendered speed, metres per second.</summary>
    public const string SignalSpeed = "speed";
    /// <summary>Elapsed presentation time, seconds — the signal a rotor or an idle breath rides.</summary>
    public const string SignalTime = "time";
    /// <summary>Total rendered travel, metres — the climb signal (a wall-riding body has no planar component worth
    /// reading).</summary>
    public const string SignalTravel = "travel";
    /// <summary>The body's rendered yaw rate about world up, radians per second; positive turns right-handed about
    /// +Y.</summary>
    public const string SignalTurnRate = "turnRate";
    /// <summary>The body's rendered vertical speed, metres per second; positive rises.</summary>
    public const string SignalVerticalSpeed = "verticalSpeed";
    /// <summary>The exponential time constant a driver's weight eases toward its gate over, seconds.</summary>
    public const float WeightSeconds = 0.15f;
    /// <summary>The most tokens a gate carries — a gate is a conjunction of independent conditions, and four is
    /// already more than any authored rig needs.</summary>
    public const int MaxGateTokens = 4;
    /// <summary>The client-derived gate token holding while the body's eased rendered speed is above
    /// <c>Puck.World.Client.WorldGaitDrivers.MovingSpeed</c> — a presentation predicate, not a sim fact, so a
    /// walker's stride returns its limbs to rest when the body stops without the simulation publishing anything.</summary>
    public const string TokenMoving = "moving";
    /// <summary>The negation of <see cref="TokenMoving"/>.</summary>
    public const string TokenStill = "still";
    /// <summary>The ungated <see cref="When"/> token, refused alongside any other token: a conjunction with "no
    /// condition" says nothing the other tokens do not. KEEP IN SYNC with
    /// <c>Puck.Physics.Motion.BodyFactVocabulary.Always</c>, the resolver on the consuming side — this document family
    /// carries fact names as tokens because it does not reference the simulation's motion vocabulary.</summary>
    public const string WhenAlways = "always";

    /// <summary>Returns whether a signal accumulates into its phase (as opposed to setting it outright).</summary>
    /// <param name="signal">The signal name.</param>
    /// <returns><see langword="true"/> for <see cref="SignalPlanarTravel"/>, <see cref="SignalTravel"/>, and
    /// <see cref="SignalTime"/>.</returns>
    public static bool Integrates(string? signal) => (signal is (SignalPlanarTravel or SignalTravel or SignalTime));
    /// <summary>The prefix of a state-cell signal: <c>state.&lt;row&gt;[.&lt;key&gt;]</c>, whose numeric value at the
    /// frame's tick is the phase (times the cadence) — a cycle-trait clock, a drawn value, an advancing counter.</summary>
    public const string SignalStatePrefix = "state.";
    /// <summary>Returns whether a signal names a state cell.</summary>
    /// <param name="signal">The signal name.</param>
    public static bool IsStateSignal(string? signal) => ((signal is { } named) && named.StartsWith(
        value: SignalStatePrefix,
        comparisonType: StringComparison.Ordinal
    ) && (named.Length > SignalStatePrefix.Length));
    /// <summary>Returns whether a signal name is recognized.</summary>
    /// <param name="signal">The signal name.</param>
    /// <returns><see langword="true"/> when the name is one of the six signals.</returns>
    public static bool IsSignal(string? signal) => (Integrates(signal: signal) || (signal is (SignalSpeed or SignalVerticalSpeed or SignalTurnRate)) || IsStateSignal(signal: signal));
}
/// <summary>Reads a driver's gate as either one token or an array of them, and always writes the array — the
/// single-token spelling stays authorable while every consumer past the parse sees one shape.</summary>
public sealed class DriverGateJsonConverter : JsonConverter<IReadOnlyList<string>> {
    /// <inheritdoc/>
    public override IReadOnlyList<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.String) {
            return [(reader.GetString() ?? string.Empty)];
        }

        if (reader.TokenType != JsonTokenType.StartArray) {
            throw new JsonException(message: "a driver gate is one token or an array of tokens.");
        }

        var tokens = new List<string>();

        while (reader.Read()) {
            if (reader.TokenType == JsonTokenType.EndArray) {
                return tokens;
            }

            if (reader.TokenType != JsonTokenType.String) {
                throw new JsonException(message: "a driver gate token is a string.");
            }

            tokens.Add(item: (reader.GetString() ?? string.Empty));
        }

        throw new JsonException(message: "a driver gate array is unterminated.");
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options) {
        ArgumentNullException.ThrowIfNull(argument: writer);
        ArgumentNullException.ThrowIfNull(argument: value);
        writer.WriteStartArray();

        foreach (var token in value) {
            writer.WriteStringValue(value: token);
        }

        writer.WriteEndArray();
    }
}
/// <summary>The waveform a swing or slide maps its driver's phase through — the artist's control over the shape of a
/// motion, separate from what drives it and where it hinges.</summary>
public static class CreationWave {
    /// <summary>The authored waveform: <c>curve:&lt;row name&gt;</c> samples the containing world's <c>curves</c> row
    /// by arc fraction — the argument's fraction of a turn maps onto the row's arc length, and the sampled Z is the
    /// value — so a path drawn left to right in the XZ plane is the shape of the motion. The world validator refuses
    /// a name its <c>curves</c> section does not declare.</summary>
    public const string CurvePrefix = "curve:";
    /// <summary>The identity waveform: the argument itself, unbounded. A wheel or a rotor takes amplitude 1 and this
    /// waveform, so the cadence alone reads as radians per metre or radians per second.</summary>
    public const string Linear = "linear";
    /// <summary>The default waveform: <c>sin(argument)</c>.</summary>
    public const string Sine = "sine";
    /// <summary>The constant waveform: 1 whatever the argument, so the facet reads <c>amplitude · w</c> — a pose the
    /// driver's weight blends in while its gate holds (arms raised while climbing, a crouch while sneaking) rather
    /// than a cycle.</summary>
    public const string Constant = "constant";
    /// <summary>The positive lobe of <see cref="Sine"/>: <c>max(0, sin(argument))</c>, zero for half of every cycle.
    /// A knee or an elbow bends one way only, so its swing takes this waveform and a phase that puts the lobe on
    /// the swing-through.</summary>
    public const string HalfSine = "halfSine";

    /// <summary>Returns whether a waveform name is one this engine evaluates.</summary>
    /// <param name="wave">The waveform name, or null for <see cref="Sine"/>.</param>
    /// <returns><see langword="true"/> for null, <see cref="Sine"/>, <see cref="HalfSine"/>, <see cref="Linear"/>,
    /// and a <see cref="CurvePrefix"/> form naming a row (the world resolves the row).</returns>
    public static bool IsEvaluable(string? wave) => ((wave is (null or Sine or HalfSine or Linear or Constant)) || TryCurveName(
        name: out _,
        wave: wave
    ));
    /// <summary>Returns the curve row a <c>curve:&lt;row&gt;</c> waveform names.</summary>
    /// <param name="wave">The waveform name.</param>
    /// <param name="name">The row name, or empty.</param>
    /// <returns><see langword="true"/> for a non-empty <see cref="CurvePrefix"/> form.</returns>
    public static bool TryCurveName(string? wave, out string name) {
        name = string.Empty;

        if (
            (wave is not { } named) ||
            !named.StartsWith(
                value: CurvePrefix,
                comparisonType: StringComparison.Ordinal
            ) ||
            (named.Length <= CurvePrefix.Length)
        ) {
            return false;
        }

        name = named[CurvePrefix.Length..];

        return true;
    }
    /// <summary>Evaluates a waveform.</summary>
    /// <param name="wave">The waveform name, or null for <see cref="Sine"/>.</param>
    /// <param name="argument">The driver phase plus the facet's own phase offset, radians.</param>
    /// <returns>The waveform's value — in [-1, 1] for <see cref="Sine"/>, in [0, 1] for <see cref="HalfSine"/>,
    /// unbounded for <see cref="Linear"/>.</returns>
    public static float Evaluate(string? wave, float argument) => wave switch {
        Linear => argument,
        Constant => 1f,
        HalfSine => MathF.Max(
            x: 0f,
            y: MathF.Sin(x: argument)
        ),
        _ => MathF.Sin(x: argument),
    };
}
/// <summary>
/// One driver-fed rotation authored on a <see cref="ShapeDocument"/>: the shape turns about <see cref="Axis"/> at
/// <see cref="Pivot"/> by <c>amplitude · wave(φ + phase) · w</c>. Presentation-only — nothing downstream of the
/// dynamic transform buffer reads it, so the SDF program, the analytic colliders, the compiled solid field, and every
/// simulation value are untouched by an authored swing.
/// </summary>
/// <param name="Driver">The <see cref="CreationDriverDocument.Name"/> supplying (φ, w). An unresolvable name is
/// refused at canonicalization.</param>
/// <param name="Pivot">The joint the shape turns about, in the creation's author frame (see
/// <see cref="CreationFrame"/>) — a shoulder or a hip, not the shape's own centre.</param>
/// <param name="Axis">The rotation axis, in the creation's author frame; normalized at canonicalization. A positive
/// angle turns right-handed about it.</param>
/// <param name="Amplitude">The peak angle in radians, magnitude at most <see cref="MaxAmplitude"/>. A negative
/// amplitude mirrors the swing, which is how a limb on the body's other side reads.</param>
/// <param name="Phase">The phase offset added to the driver's phase, radians (null = 0) — how two limbs on one driver
/// are put out of step (contralateral arms differ by π).</param>
/// <param name="Wave">The waveform name (null = <see cref="CreationWave.Sine"/>); see
/// <see cref="CreationWave"/>.</param>
public sealed record ShapeSwingDocument(
    string Driver,
    DocumentVector3 Pivot,
    DocumentVector3 Axis,
    DocumentScalar Amplitude,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentScalar? Phase = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Wave = null
) {
    /// <summary>The largest peak angle magnitude, radians — a half turn, past which a limb passes through its own
    /// body. It does not bound a <see cref="CreationWave.Linear"/> swing's angle, which the unbounded waveform
    /// carries.</summary>
    public const float MaxAmplitude = MathF.PI;

    /// <summary>Composes this swing onto a shape's creation-space rest pose. Both arguments and both results are
    /// engine-frame creation-space values, before the placement's uniform scale — a uniform scale commutes with a
    /// rotation about a scaled pivot, so applying it afterwards is the same pose.</summary>
    /// <param name="angle">The signed angle in radians (<c>amplitude · wave(φ + phase) · w</c>).</param>
    /// <param name="position">The shape's position; replaced by the position turned about <see cref="Pivot"/>.</param>
    /// <param name="rotation">The shape's orientation; replaced by the swing pre-multiplied onto it.</param>
    public void Compose(float angle, ref Vector3 position, ref Quaternion rotation) {
        var swing = Quaternion.CreateFromAxisAngle(
            angle: angle,
            axis: Axis.Value
        );
        var pivot = Pivot.Value;

        position = (pivot + Vector3.Transform(
            rotation: swing,
            value: (position - pivot)
        ));
        rotation = (swing * rotation);
    }
}
/// <summary>
/// One driver-fed translation authored on a <see cref="ShapeDocument"/>: the shape slides along <see cref="Axis"/> by
/// <c>amplitude · wave(φ + phase) · w</c> — a piston, a hull's bob, a breathing chest. Presentation-only on the same
/// terms as <see cref="ShapeSwingDocument"/>.
/// </summary>
/// <param name="Driver">The <see cref="CreationDriverDocument.Name"/> supplying (φ, w). An unresolvable name is
/// refused at canonicalization.</param>
/// <param name="Axis">The slide direction, in the creation's author frame; normalized at canonicalization.</param>
/// <param name="Amplitude">The peak offset in creation units, magnitude at most <see cref="MaxAmplitude"/>.</param>
/// <param name="Phase">The phase offset added to the driver's phase, radians (null = 0).</param>
/// <param name="Wave">The waveform name (null = <see cref="CreationWave.Sine"/>); see
/// <see cref="CreationWave"/>.</param>
public sealed record ShapeSlideDocument(
    string Driver,
    DocumentVector3 Axis,
    DocumentScalar Amplitude,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentScalar? Phase = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Wave = null
) {
    /// <summary>The largest peak offset magnitude, creation units — a shape's reach bound is the un-slid geometry's,
    /// so a slide past this would leave its own placement's render bound.</summary>
    public const float MaxAmplitude = 4f;

    /// <summary>Composes this slide onto a shape's creation-space rest position, before the placement's uniform
    /// scale.</summary>
    /// <param name="offset">The signed offset (<c>amplitude · wave(φ + phase) · w</c>).</param>
    /// <param name="position">The shape's position; displaced along <see cref="Axis"/>.</param>
    public void Compose(float offset, ref Vector3 position) => position += (Axis.Value * offset);
}
