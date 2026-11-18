using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>An authored seat program: how a seat's own controls are worked to carry its body along a route, the way a
/// player at a pad works them (<c>seatPrograms</c> rows). A route (<see cref="WorldRoute"/>) says where to go and how
/// (a place at a pace, a rest facing a heading, an action pressed); the program says which controls do it, in an
/// ordered list of operations the engine evaluates once each tick from the seat body's authoritative pose, its
/// camera's facing and the route's next steering point. Every value it produces enters through the seat's own ingress
/// (the door <c>player.signal</c> uses), so the router, the bindings and intent composition carry it as they carry a
/// pad. Paces and actions are the author's words: the engine knows only the ones a program's operations name.</summary>
/// <param name="Name">The program's name, unique within the document, which a route names.</param>
/// <param name="Operations">The operations, evaluated in order each tick; each drives the input sources it names.</param>
public sealed record WorldSeatProgram(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("operations")] IReadOnlyList<WorldSeatProgramOp> Operations
);
/// <summary>One operation of a <see cref="WorldSeatProgram"/>: the input sources it drives and how it reads the tick's
/// steering state. A source is an input-source id (<c>gamepad.leftStick</c>, <c>keyboard.shiftLeft</c>); a stick
/// source takes an X and a Y deflection, a button source a press or a release.</summary>
[JsonDerivedType(typeof(WorldSeatProgramOp.Move), typeDiscriminator: "move")]
[JsonDerivedType(typeof(WorldSeatProgramOp.Turn), typeDiscriminator: "turn")]
[JsonDerivedType(typeof(WorldSeatProgramOp.Hold), typeDiscriminator: "hold")]
[JsonDerivedType(typeof(WorldSeatProgramOp.Pulse), typeDiscriminator: "pulse")]
[JsonDerivedType(typeof(WorldSeatProgramOp.Tap), typeDiscriminator: "tap")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldSeatProgramOp {
    private WorldSeatProgramOp() {
    }

    /// <summary>Gets this operation's authored <c>$type</c> token.</summary>
    [JsonIgnore]
    public string Opcode => (this switch {
        Move => "move",
        Turn => "turn",
        Hold => "hold",
        Pulse => "pulse",
        Tap => "tap",
        _ => "unknown",
    });
    /// <summary>Gets the input source this operation drives.</summary>
    [JsonIgnore]
    public abstract string Driven { get; }

    /// <summary>Deflects a stick to carry the body toward the tick's steering point, the direction expressed in the
    /// camera's frame the seat moves against (X strafes right, Y advances). The deflection is full except where the
    /// body must slow: before a corner sharper than <paramref name="CornerDegrees"/>, so it reaches the corner no faster
    /// than <paramref name="CornerSpeed"/> braking at <paramref name="Brake"/>, and before a final point, so it stops
    /// there. The slowing is read against the body's own planar speed, so it holds at any pace and any body scale.
    /// With no steering point (a rest, a wait) the stick is centred.</summary>
    /// <param name="Source">The stick source.</param>
    /// <param name="Brake">The deceleration, in m/s², the slowing plans for.</param>
    /// <param name="CornerSpeed">The speed, in m/s, a sharp corner is taken at.</param>
    /// <param name="CornerDegrees">The turn, in degrees, from which a corner is sharp.</param>
    /// <param name="Least">The least deflection the stick keeps while the body is still short of its point.</param>
    public sealed record Move(
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("brake")] float Brake = 30f,
        [property: JsonPropertyName("cornerSpeed")] float CornerSpeed = 4f,
        [property: JsonPropertyName("cornerDegrees")] float CornerDegrees = 30f,
        [property: JsonPropertyName("least")] float Least = 0.2f
    ) : WorldSeatProgramOp {
        /// <inheritdoc/>
        public override string Driven => Source;
    }
    /// <summary>Deflects a stick's X to turn the seat's camera onto the tick's facing: the travel direction while the
    /// body moves, the heading or look target a rest names while it stands. Each tick it removes
    /// <paramref name="Gain"/> of the remaining turn, no more than full deflection turns the camera in a tick (the seat's
    /// own look rate), and holds the stick centred within <paramref name="DeadbandDegrees"/>.</summary>
    /// <param name="Source">The stick source.</param>
    /// <param name="Gain">The fraction of the remaining turn taken each tick, in (0, 1].</param>
    /// <param name="DeadbandDegrees">The turn, in degrees, inside which the stick stays centred.</param>
    public sealed record Turn(
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("gain")] float Gain = 0.5f,
        [property: JsonPropertyName("deadbandDegrees")] float DeadbandDegrees = 1f
    ) : WorldSeatProgramOp {
        /// <inheritdoc/>
        public override string Driven => Source;
    }
    /// <summary>Holds a button down while the step the route is on travels at one of <paramref name="Paces"/>, and lets
    /// it go otherwise (a run button).</summary>
    /// <param name="Source">The button source.</param>
    /// <param name="Paces">The paces, the route's own words, the button is held through.</param>
    public sealed record Hold(
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("paces")] IReadOnlyList<string> Paces
    ) : WorldSeatProgramOp {
        /// <inheritdoc/>
        public override string Driven => Source;
    }
    /// <summary>Taps a button once every <paramref name="PeriodSeconds"/> while the step the route is on travels at one
    /// of <paramref name="Paces"/> (a dash on its cooldown): pressed for one tick, released the next.</summary>
    /// <param name="Source">The button source.</param>
    /// <param name="Paces">The paces the button is tapped through.</param>
    /// <param name="PeriodSeconds">The time from one tap to the next.</param>
    public sealed record Pulse(
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("paces")] IReadOnlyList<string> Paces,
        [property: JsonPropertyName("periodSeconds")] float PeriodSeconds
    ) : WorldSeatProgramOp {
        /// <inheritdoc/>
        public override string Driven => Source;
    }
    /// <summary>Taps a button when a route step presses <paramref name="Action"/> (the context button a lift's call
    /// plate answers): pressed for one tick, released the next.</summary>
    /// <param name="Action">The action, the route's own word, this button answers.</param>
    /// <param name="Source">The button source.</param>
    public sealed record Tap(
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("source")] string Source
    ) : WorldSeatProgramOp {
        /// <inheritdoc/>
        public override string Driven => Source;
    }
}
