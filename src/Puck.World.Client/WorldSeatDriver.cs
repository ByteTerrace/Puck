using System.Globalization;
using System.Text;
using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Client;

/// <summary>
/// Plays a route's chapters for one seat with the route's authored seat program (<see cref="WorldSeatProgram"/>), as
/// that seat's client: each tick it reads what the authority of the world the seat stands in delivered
/// (<see cref="WorldSeatSenses"/>: the body's pose, speed and places, and a path over the delivered document), takes
/// the next steering point on the step's path, evaluates the program's operations into the seat's controls and
/// submits every control that changed through the seat's own command door, the one <c>player.signal</c> uses. Load
/// therefore runs through the router, the bindings and intent composition, and a shed tick, a scale change or a nudge
/// is read back on a later tick and steered from rather than carried off course.
/// </summary>
/// <remarks>
/// <para>The driver reads its seat's world through the seat's route and nothing else, whether the authority runs in
/// this process or another: it holds no authority object, and this project cannot name one. A body it reads is
/// therefore as old as its last delivery, and a control it submits lands when the authority admits it.</para>
/// <para>Given the same deliveries on the same ticks it submits the same controls: every decision is fixed-point
/// arithmetic over the delivered values, the seat view's facing (advanced once per tick) and tick counts.</para>
/// <para>A chapter fails where a player would see it fail: by pushing its stick for <see cref="StallTicks"/> delivered ticks
/// without the body moving, by standing in none of the places its path crosses, by losing its path, or by running
/// past its time. Every member runs on the host pump.</para>
/// </remarks>
public sealed class WorldSeatDriver {
    /// <summary>The ticks between re-assertions of a held stick, as a player holding it across a crossing presses again.</summary>
    public const int ReassertTicks = 15;
    // The distance from its line, in metres, past which a body that strayed plans its path again; the no-progress span,
    // in ticks, after which it does; and the plans in a row that may make no progress before the step fails.
    private const double StrayMetres = 1.0;
    private const int StuckTicks = 60;
    private const int MaxStuckPlans = 3;
    // The ticks a pose and a line are given to land before the next step reads the world.
    private const int LandTicks = 2;
    // How long, in seconds of the authority's time, a pose step waits for its pose to show in a delivery before it fails:
    // a pose sent to an authority in another process crosses a lane, may carry the body home from a secondary instance,
    // applies at a tick and returns in a later delivery.
    private const int PoseLandSeconds = 30;
    // The heading error, in degrees, a crossing turns inside before it walks, and the ticks it gives the turn.
    private const double AlignDegrees = 3.0;
    private const int AlignTicks = 45;
    /// <summary>The authority ticks, as its deliveries report them, a deflected move stick may leave the body within
    /// <see cref="StallMetres"/> of where it stood before the chapter fails as stalled against a wall: long enough for
    /// a control to cross to a remote authority and its effect to come back. Counted in delivered ticks, so a span the
    /// client was delivered nothing in says nothing about a wall.</summary>
    public const int StallTicks = 20;
    /// <summary>The planar distance, in metres, a body moves to count as moving under a deflected stick.</summary>
    public const double StallMetres = 0.05;

    private enum Phase { Idle, Running, Arrived, Failed }

    private readonly WorldSeatSenses m_senses;
    private readonly Func<string, Func<string>> m_submit;
    private readonly Func<int, bool>? m_canPressUntil;
    private readonly WorldSeatProgram m_program;
    private readonly int m_slot;
    private readonly FixedQ4816 m_margin;
    private readonly Dictionary<string, string> m_sent = new(comparer: StringComparer.Ordinal);
    private readonly List<FixedVector3> m_routeFeet = [];
    private readonly List<FixedVector3> m_corners = [];
    private readonly HashSet<string> m_corridor = new(comparer: StringComparer.Ordinal);
    private readonly List<(ulong Tick, string Place)> m_visited = [];
    // The chapter's submitted control lines, each a read of its outcome, which arrives as its tick applies.
    private readonly List<Func<string>> m_lines = [];

    private WorldRouteChapter? m_chapter;
    private Phase m_phase;
    private int m_step;
    private ulong m_chapterStart;
    private ulong m_stepStart;
    private ulong m_stepAuthorityStart;
    private ulong m_deadline;
    private ulong m_authorityTicks;
    private ulong m_authorityBudget;
    private ulong m_lastBodyTick;
    private string? m_lastBodyAuthority;
    private ulong m_noBodyStart;
    private bool m_noBody;
    private string? m_routeWorld;
    private string? m_routeAuthority;
    private int m_routeIndex = -1;
    private bool m_routeAdmitted;
    // Whether the chapter plans any path, and the world whose navigation it last started baking.
    private bool m_navigates;
    private string? m_preparedIn;
    private ulong m_lastAssert;
    // The step's path: its steering corners and the one steered at, whether a corridor is checked, the world it was
    // planned in, and the progress watch.
    private int m_corner;
    private FixedQ4816 m_firstReach;
    private bool m_checkCorridor;
    private string? m_plannedIn;
    private IReadOnlyList<string> m_previousStepPlaces = [];
    private FixedQ4816 m_best;
    private ulong m_progressTick;
    private int m_stuckPlans;
    // A crossing's phase and its distance walked; a press's tap.
    private bool m_pushing;
    private bool m_crossedPortal;
    private ulong m_crossedPortalTick;
    private string? m_crossAuthority;
    private int m_crossIndex = -1;
    private int m_crossGeneration;
    private int m_crossPoseEpoch;
    private int m_crossCarryEpoch;
    private ulong m_crossBodyTick;
    private FixedVector3 m_planStart;
    private bool m_hasGoRead;
    private FixedVector3 m_lastGoPosition;
    private string? m_lastGoAuthority;
    private int m_lastGoIndex = -1;
    private int m_lastGoGeneration;
    private int m_lastGoPoseEpoch;
    private int m_lastGoCarryEpoch;
    private ulong m_lastGoTick;
    // The documentId of the world a flight started in.
    private string? m_flownIn;
    private FixedQ4816? m_heading;
    private FixedQ4816 m_travelled;
    private FixedVector3 m_lastPosition;
    private string? m_tap;
    private bool m_tapAwaitingParty;
    private bool m_tapAnswered;
    private ulong m_tapAnsweredTick;
    private ulong m_tapTick;
    private ulong m_lastTapAuthorityTick;
    private string? m_tapAuthority;
    private bool m_poseSubmitted;
    private ulong m_poseAttemptTick;
    private Func<string>? m_poseOutcome;
    private Func<string>? m_lineOutcome;
    private bool m_poseHadPreRead;
    private string? m_posePreAuthority;
    private int m_posePreIndex;
    private int m_posePreGeneration;
    private int m_posePreEpoch;
    private bool m_driving;
    private bool m_docking;
    // Where the body stood when it last moved under a deflected stick, and the authority tick whose delivery showed it.
    private FixedVector3 m_stallOrigin;
    private ulong m_stallTick;
    private string? m_stallAuthority;

    /// <summary>Initializes a new instance of the <see cref="WorldSeatDriver"/> class for one route.</summary>
    /// <param name="route">The route.</param>
    /// <param name="program">The route's seat program.</param>
    /// <param name="senses">What the seat's client was delivered.</param>
    /// <param name="submit">Submits a console line as the seat, through the seat's ordinary ingress, and returns a
    /// read of the line's outcome: <c>pending</c> until its tick applies, then <c>ran</c>, <c>refused</c> or
    /// <c>faulted</c>.</param>
    /// <param name="canPressUntil">When several seats play one chapter together, reports whether every active seat has
    /// reached the step before a shared <c>press ... until</c> triggers a vehicle or lift departure.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldSeatDriver(WorldRoute route, WorldSeatProgram program, WorldSeatSenses senses, Func<string, Func<string>> submit, Func<int, bool>? canPressUntil = null) {
        ArgumentNullException.ThrowIfNull(argument: route);
        ArgumentNullException.ThrowIfNull(argument: program);
        ArgumentNullException.ThrowIfNull(argument: senses);
        ArgumentNullException.ThrowIfNull(argument: submit);
        m_program = program;
        m_senses = senses;
        m_submit = submit;
        m_canPressUntil = canPressUntil;
        m_slot = (route.Seat - 1);
        m_margin = FixedQ4816.FromDouble(value: route.Margin);
    }

    /// <summary>Gets the 0-based seat slot the driver plays, the index a seat's own body verbs take.</summary>
    public int Seat => m_slot;
    /// <summary>Gets the reason the chapter failed, or <see langword="null"/>.</summary>
    public string? Failure { get; private set; }
    /// <summary>Gets the kind of the chapter's failure (<c>wall</c>, <c>corridor</c>, <c>route</c>, <c>stuck</c>,
    /// <c>timeout</c>, <c>seat</c>), or <see langword="null"/>.</summary>
    public string? FailureKind { get; private set; }
    /// <summary>Gets the tick the chapter failed at.</summary>
    public ulong FailureTick { get; private set; }
    /// <summary>Gets the innermost place the body stood in when the chapter failed, or <c>none</c>.</summary>
    public string FailurePlace { get; private set; } = "none";
    /// <summary>Gets the index of the step the chapter reached.</summary>
    public int StepReached => m_step;
    /// <summary>Gets the times the chapter planned a path.</summary>
    public int Plans { get; private set; }
    /// <summary>Gets the control lines the seat's ingress refused.</summary>
    public int Refused => m_lines.Count(predicate: static outcome => outcome() is "refused" or "faulted");
    /// <summary>Gets the control lines submitted.</summary>
    public int Submitted => m_lines.Count;
    /// <summary>Gets every innermost place the body entered, in order, each with the tick it was first read in.</summary>
    public IReadOnlyList<(ulong Tick, string Place)> Visited => m_visited;
    /// <summary>Gets whether the chapter has ended, arrived or failed.</summary>
    public bool Ended => (m_phase is Phase.Arrived or Phase.Failed);
    /// <summary>Gets whether the chapter arrived: every step finished and nothing failed it.</summary>
    public bool Arrived => (m_phase == Phase.Arrived);
    /// <summary>Gets the seat's body as the latest call to <see cref="Advance"/> read it, or <see langword="null"/>
    /// when the seat had no body to read.</summary>
    public WorldSeatSenses.Reading? Read { get; private set; }

    /// <summary>Starts a chapter on the tick just completed: its first step reads the world in this tick's hook.</summary>
    /// <param name="chapter">The chapter.</param>
    /// <param name="tick">The completed tick.</param>
    /// <param name="rateHz">The simulation rate, in ticks per second.</param>
    public void Begin(WorldRouteChapter chapter, ulong tick, int rateHz) {
        m_chapter = chapter;
        m_phase = Phase.Running;
        m_step = -1;
        m_chapterStart = tick;
        m_authorityBudget = (ulong)Math.Ceiling(a: (chapter.TimeoutSeconds * rateHz));
        m_authorityTicks = 0UL;
        m_lastBodyAuthority = null;
        m_lastBodyTick = 0UL;
        m_noBody = false;
        m_noBodyStart = tick;
        m_deadline = (tick + Math.Max(val1: (m_authorityBudget * 4UL), val2: ((ulong)(60 * rateHz))));
        m_visited.Clear();
        Failure = null;
        FailureKind = null;
        FailurePlace = "none";
        Plans = 0;
        m_lines.Clear();
        m_routeWorld = null;
        m_routeAuthority = null;
        m_routeIndex = -1;
        m_routeAdmitted = false;
        m_stallAuthority = null;
        m_preparedIn = null;
        m_previousStepPlaces = (m_senses.TryRead(reading: out var openingRead, slot: m_slot) ? openingRead.Places : []);
        Read = null;
        m_navigates = chapter.Steps.Any(predicate: static step => step is WorldRouteStep.Go { Direct: false });
        Release();
        NextStep(tick: tick, rateHz: rateHz);
    }

    /// <summary>Advances the chapter from the tick just completed and submits the seat's controls for the next.</summary>
    /// <param name="tick">The completed tick.</param>
    /// <param name="rateHz">The simulation rate, in ticks per second.</param>
    public void Advance(ulong tick, int rateHz) {
        if ((m_phase != Phase.Running) || (m_chapter is null)) {
            return;
        }
        if (!m_senses.TryRead(reading: out var read, slot: m_slot)) {
            // A seat between authorities (a crossing in flight) has no body to read until the destination's prime
            // arrives; it holds its controls and fails only when that outlasts the chapter's window.
            if (!m_noBody) {
                m_noBody = true;
                m_noBodyStart = tick;
            }
            Read = null;
            var noBodyLimit = Math.Max(val1: m_authorityBudget, val2: ((ulong)(30 * rateHz)));
            Fail(kind: "seat", place: "none", reason: "the seat's client was delivered no body for it in any world", tick: tick, when: (((tick - m_noBodyStart) >= noBodyLimit) || (tick >= m_deadline)));
            return;
        }

        m_noBody = false;
        var awaitingNavigationBake = (
            (m_step >= 0) &&
            (m_step < m_chapter.Steps.Count) &&
            (m_chapter.Steps[m_step] is WorldRouteStep.Go { Direct: false }) &&
            !m_senses.Navigation(endpoint: read.Endpoint).Ready
        );
        if (m_lastBodyAuthority is null) {
            m_lastBodyAuthority = read.Endpoint.Authority;
            m_lastBodyTick = read.Body.Tick;
        } else if (string.Equals(a: m_lastBodyAuthority, b: read.Endpoint.Authority, comparisonType: StringComparison.Ordinal)) {
            if (read.Body.Tick > m_lastBodyTick) {
                if (!awaitingNavigationBake) {
                    m_authorityTicks += (read.Body.Tick - m_lastBodyTick);
                }
                m_lastBodyTick = read.Body.Tick;
            } else if (read.Body.Tick < m_lastBodyTick) {
                m_lastBodyTick = read.Body.Tick;
            }
        } else {
            if (!awaitingNavigationBake) {
                m_authorityTicks += 1UL;
            }
            m_lastBodyAuthority = read.Endpoint.Authority;
            m_lastBodyTick = read.Body.Tick;
        }
        if (m_previousStepPlaces.Count == 0) {
            m_previousStepPlaces = read.Places;
        }

        Read = read;
        Visit(read: in read, tick: tick);
        var worldKey = WorldKey(read: in read);
        // A chapter that navigates starts the navigation of each world it enters baking at once, so the walk across a
        // threshold covers the bake its next path waits for.
        if (m_navigates && !string.Equals(a: m_preparedIn, b: worldKey, comparisonType: StringComparison.Ordinal)) {
            m_senses.Navigation(endpoint: read.Endpoint).Prepare();
            m_preparedIn = worldKey;
        }
        // A stick held while the body goes nowhere is a wall, as a player meets one. A body that moves, however it is
        // moved, starts the watch again, and so does an authority whose ticks are not the last one's or whose input
        // admission or initial push for this seat has not yet been answered.
        var admitted = m_senses.InputAdmitted(slot: m_slot);
        var moveAnswered = (admitted && m_senses.MoveInputAnswered(slot: m_slot, bodyTick: read.Body.Tick));
        var scale = FixedQ4816.Max(x: FixedQ4816.FromDouble(value: 0.05), y: m_senses.BodyScale(reading: in read));
        var stallThreshold = (FixedQ4816.FromDouble(value: StallMetres) * scale);
        var stallLimit = (read.Endpoint.ClockOwnedHere
            ? ((ulong)StallTicks)
            : (((ulong)StallTicks) + (m_docking ? 15UL : 0UL) + Math.Min(val1: 20UL, val2: m_senses.MoveLagTicks(slot: m_slot, bodyTick: read.Body.Tick))));
        if (!m_driving || !moveAnswered || !string.Equals(a: m_stallAuthority, b: read.Endpoint.Authority, comparisonType: StringComparison.Ordinal) ||
            (read.Body.Tick < m_stallTick) || (Planar(from: m_stallOrigin, to: read.Position) > stallThreshold)) {
            m_stallOrigin = read.Position;
            m_stallTick = read.Body.Tick;
            m_stallAuthority = read.Endpoint.Authority;
        } else if ((read.Body.Tick - m_stallTick) >= stallLimit) {
            Fail(kind: "wall", place: read.Place, reason: string.Create(provider: CultureInfo.InvariantCulture, handler: $"the move stick was held across {StallTicks} delivered ticks and the body stayed within {((double)stallThreshold):0.###} m of ({Metres(m_stallOrigin.X)}, {Metres(m_stallOrigin.Y)}, {Metres(m_stallOrigin.Z)}), on step {m_step} ({Describe(step: m_chapter.Steps[m_step])})"), tick: tick);
            return;
        }
        if (m_checkCorridor && (m_corridor.Count > 0) && ((read.Places.Count == 0) ? !m_corridor.Contains(item: read.Place) : !read.Places.Any(predicate: m_corridor.Contains))) {
            Fail(kind: "corridor", place: read.Place, reason: $"left the corridor ({string.Join(separator: ", ", values: m_corridor.Order(comparer: StringComparer.Ordinal))}) into {read.Place} on step {m_step} ({Describe(step: m_chapter.Steps[m_step])})", tick: tick);
            return;
        }
        if (m_authorityTicks >= m_authorityBudget) {
            Fail(kind: "timeout", place: read.Place, reason: string.Create(provider: CultureInfo.InvariantCulture, handler: $"ran past its {m_chapter.TimeoutSeconds:0.#} s on step {m_step} ({Describe(step: m_chapter.Steps[m_step])})"), tick: tick);
            return;
        }

        var steer = Step(read: in read, tick: tick, rateHz: rateHz);

        if (m_phase != Phase.Running) {
            return;
        }

        Drive(read: in read, steer: in steer, tick: tick, rateHz: rateHz);
    }

    /// <summary>Gets the lines that let go every control the driver holds, each a <c>player.signal</c> line, in the
    /// program's operation order, and forgets them.</summary>
    /// <returns>The release lines.</returns>
    public IReadOnlyList<string> TakeReleases() {
        var lines = new List<string>();

        foreach (var operation in m_program.Operations) {
            if (m_sent.TryGetValue(key: operation.Driven, value: out var value) && (value != NeutralOf(operation: operation))) {
                lines.Add(item: $"player.signal {operation.Driven} {NeutralOf(operation: operation)}");
            }
        }

        m_sent.Clear();
        return lines;
    }

    /// <summary>Lets go every control the driver holds, through the seat's door.</summary>
    public void Release() {
        foreach (var line in TakeReleases()) {
            Submit(text: line);
        }
        m_driving = false;
        m_docking = false;
    }

    /// <summary>Describes a step canonically, every member named, the text a chapter's identity hashes.</summary>
    /// <param name="step">The step.</param>
    /// <returns>The description.</returns>
    public static string Describe(WorldRouteStep step) => step switch {
        WorldRouteStep.PoseBody pose => (pose.Spawn is { } spawn) ? $"pose spawn {spawn}" : $"pose {Vector(pose.Point)} yaw {Number(pose.YawDegrees)}",
        WorldRouteStep.Line line => $"line {line.Text}",
        WorldRouteStep.Go go => $"go {(go.Place ?? Vector(go.Point))}{((go.World is { } world) ? $" in {world}" : string.Empty)} pace {go.Pace}{(go.Direct ? " direct" : string.Empty)} within {Number(go.Within)}{((go.Corridor is { Count: > 0 } corridor) ? $" corridor {string.Join(separator: ',', values: corridor)}" : string.Empty)}",
        WorldRouteStep.Cross cross => $"cross {Number(cross.HeadingDegrees)}{(cross.Relative ? " relative" : string.Empty)}{((cross.Until is { } until) ? $" until {until}" : string.Empty)} pace {cross.Pace} metres {Number(cross.Metres)}",
        WorldRouteStep.Fly fly => $"fly {Vector(fly.Point)}{((fly.World is { } world) ? $" in {world}" : string.Empty)}{((fly.Enters is { } enters) ? $" enters {enters}" : string.Empty)}{(fly.Lands ? " lands" : string.Empty)} pace {fly.Pace} within {Number(fly.Within)}",
        WorldRouteStep.Rest rest => $"rest {Number(rest.Seconds)}{((rest.FaceDegrees is { } face) ? $" face {Number(face)}" : string.Empty)}{((rest.LookAt is { } look) ? $" look {Vector(look)}" : string.Empty)}",
        WorldRouteStep.PressAction press => $"press {press.Action}{((press.Until is { } until) ? $" until {until}" : string.Empty)}{((press.World is { } world) ? $" in {world}" : string.Empty)} seconds {Number(press.Seconds)}",
        _ => "unknown",
    };

    /// <summary>Describes a seat program canonically, the text a chapter's identity hashes beside its steps.</summary>
    /// <param name="program">The program.</param>
    /// <returns>The description.</returns>
    public static string Describe(WorldSeatProgram program) {
        var text = new StringBuilder();

        foreach (var operation in program.Operations) {
            _ = text.Append(value: operation switch {
                WorldSeatProgramOp.Move move => $"move {move.Source} brake {Number(move.Brake)} corner {Number(move.CornerSpeed)} at {Number(move.CornerDegrees)} least {Number(move.Least)}",
                WorldSeatProgramOp.Turn turn => $"turn {turn.Source} gain {Number(turn.Gain)} deadband {Number(turn.DeadbandDegrees)}",
                WorldSeatProgramOp.Hold hold => $"hold {hold.Source} {string.Join(separator: ',', values: hold.Paces)}",
                WorldSeatProgramOp.Pulse pulse => $"pulse {pulse.Source} {string.Join(separator: ',', values: pulse.Paces)} every {Number(pulse.PeriodSeconds)}",
                WorldSeatProgramOp.Tap tap => $"tap {tap.Action} {tap.Source}",
                WorldSeatProgramOp.Rise rise => $"rise {rise.Source} {rise.Toward} band {Number(rise.Band)} brake {Number(rise.Brake)}",
                _ => "unknown",
            }).Append(value: '\n');
        }

        return text.ToString();
    }

    // What the step asks of the controls this tick: a direction to travel (none to stand), whether it ends at a point to
    // stop at and how far that is, the next corner's distance and turn, the facing to turn the camera onto, and the pace.
    // A flight adds how far the point stands along the body's own up (negative below it) and the body's delivered speed
    // along it, which the rise controls read.
    private readonly record struct Steer(bool Moves, FixedQ4816 Dx, FixedQ4816 Dz, FixedQ4816 Stop, FixedQ4816 CornerDistance, FixedQ4816 CornerTurn, bool Faces, FixedQ4816 Facing, string Pace, FixedQ4816 Rise = default, FixedQ4816 Climb = default);

    // Whether the seat stands in the world a step names by its documentId; a step that names none is played wherever
    // the seat stands.
    private static bool Stands(string? world, in WorldSeatSenses.Reading read) =>
        ((world is null) || string.Equals(a: world, b: read.Endpoint.Definition.DocumentId, comparisonType: StringComparison.Ordinal));

    private static string WorldKey(in WorldSeatSenses.Reading read) =>
        (string.IsNullOrEmpty(value: read.Endpoint.Authority) ? read.World : $"{read.World}\0{read.Endpoint.Authority}");

    private void Visit(in WorldSeatSenses.Reading read, ulong tick) {
        var place = ((read.World == WorldDefinitionLoader.BootInstanceName) ? read.Place : $"{read.World}:{read.Place}");

        if ((m_visited.Count == 0) || (m_visited[^1].Place != place)) {
            m_visited.Add(item: (tick, place));
        }
    }

    private void Fail(string kind, string place, string reason, ulong tick, bool when = true) {
        if (!when) {
            return;
        }

        m_phase = Phase.Failed;
        FailureKind = kind;
        FailurePlace = place;
        FailureTick = tick;
        Failure = string.Create(provider: CultureInfo.InvariantCulture, handler: $"{kind} at tick {tick} in {place}: {reason}");
        Release();
    }

    private void NextStep(ulong tick, int rateHz) {
        if (Read is { } completedRead) {
            m_previousStepPlaces = completedRead.Places;
        }
        m_step++;
        m_stepStart = tick;
        m_stepAuthorityStart = m_authorityTicks;
        m_checkCorridor = false;
        m_corridor.Clear();
        m_corners.Clear();
        m_corner = 0;
        m_plannedIn = null;
        m_stuckPlans = 0;
        m_stallAuthority = null;
        m_pushing = false;
        m_crossedPortal = false;
        m_crossedPortalTick = 0UL;
        m_crossAuthority = null;
        m_crossIndex = -1;
        m_crossGeneration = 0;
        m_crossPoseEpoch = 0;
        m_crossCarryEpoch = 0;
        m_crossBodyTick = 0UL;
        m_planStart = default;
        m_hasGoRead = false;
        m_lastGoPosition = default;
        m_lastGoAuthority = null;
        m_lastGoIndex = -1;
        m_lastGoGeneration = 0;
        m_lastGoPoseEpoch = 0;
        m_lastGoCarryEpoch = 0;
        m_lastGoTick = 0UL;
        m_flownIn = null;
        m_heading = null;
        m_travelled = FixedQ4816.Zero;
        m_tap = null;
        m_tapAwaitingParty = false;
        m_tapAnswered = false;
        m_tapAnsweredTick = 0UL;
        m_tapTick = 0UL;
        m_lastTapAuthorityTick = m_authorityTicks;
        m_tapAuthority = null;
        m_poseSubmitted = false;
        m_poseAttemptTick = 0UL;
        m_poseOutcome = null;
        m_lineOutcome = null;
        m_poseHadPreRead = false;
        m_posePreAuthority = null;
        m_posePreIndex = -1;
        m_posePreGeneration = 0;
        m_posePreEpoch = 0;
        if (m_step >= m_chapter!.Steps.Count) {
            m_phase = Phase.Arrived;
            Release();
            return;
        }

        switch (m_chapter.Steps[m_step]) {
            case WorldRouteStep.PoseBody pose:
                if ((pose.Spawn is null) || m_senses.TryRead(reading: out _, slot: m_slot)) {
                    SubmitPose(pose: pose, tick: tick, countLine: true);
                }
                break;
            case WorldRouteStep.Line line:
                m_lineOutcome = Submit(text: line.Text);
                break;
            case WorldRouteStep.PressAction press:
                if ((press.Until is not null) && (m_canPressUntil?.Invoke(arg: m_step) == false)) {
                    m_tapAwaitingParty = true;
                } else {
                    ArmTap(action: press.Action, read: Read);
                }
                break;
        }
    }

    private void ArmTap(string action, WorldSeatSenses.Reading? read) {
        m_tap = action;
        m_tapAwaitingParty = false;
        m_tapAnswered = false;
        m_tapAnsweredTick = 0UL;
        m_lastTapAuthorityTick = m_authorityTicks;
        if (read is { } current) {
            m_tapTick = current.Body.Tick;
            m_tapAuthority = current.Endpoint.Authority;
        } else {
            m_tapTick = 0UL;
            m_tapAuthority = null;
        }
    }

    private bool TapAnswered(string action, in WorldSeatSenses.Reading read) {
        foreach (var operation in m_program.Operations) {
            if ((operation is WorldSeatProgramOp.Tap tap) && string.Equals(a: tap.Action, b: action, comparisonType: StringComparison.Ordinal)) {
                if (!m_senses.SourcePressAnswered(slot: m_slot, source: tap.Driven, bodyTick: read.Body.Tick)) {
                    return false;
                }
            }
        }

        return true;
    }

    private void SubmitPose(WorldRouteStep.PoseBody pose, ulong tick, bool countLine) {
        m_poseSubmitted = true;
        m_poseAttemptTick = tick;
        if (countLine && m_senses.TryRead(reading: out var preRead, slot: m_slot)) {
            m_poseHadPreRead = true;
            m_posePreAuthority = preRead.Endpoint.Authority;
            m_posePreIndex = preRead.Index;
            m_posePreGeneration = preRead.Body.Generation;
            m_posePreEpoch = preRead.Body.PoseEpoch;
        }
        var lineText = ((pose.Spawn is { } spawn)
            ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"body.pose spawn:{spawn} {Seat}")
            : string.Create(provider: CultureInfo.InvariantCulture, handler: $"body.pose {Number(pose.Point!.Value.X)} {Number(pose.Point.Value.Y)} {Number(pose.Point.Value.Z)} {Number(pose.YawDegrees)} 0 0 {Seat}"));
        m_poseOutcome = (countLine ? Submit(text: lineText) : m_submit(arg: lineText));
    }

    // Runs the current step from this tick's read and returns what it asks of the controls; a finished step hands on to
    // the next, which reads the world from the next tick.
    private Steer Step(in WorldSeatSenses.Reading read, ulong tick, int rateHz) {
        if (!m_poseSubmitted && (m_chapter!.Steps[m_step] is WorldRouteStep.PoseBody deferredPose)) {
            m_stepStart = tick;
            m_stepAuthorityStart = m_authorityTicks;
            SubmitPose(pose: deferredPose, tick: tick, countLine: true);
            return new Steer(Moves: false, Dx: default, Dz: default, Stop: default, CornerDistance: default, CornerTurn: default, Faces: false, Facing: default, Pace: string.Empty);
        }

        var elapsed = (tick - m_stepStart);
        var elapsedAuthority = (m_authorityTicks - m_stepAuthorityStart);
        var step = m_chapter!.Steps[m_step];
        var still = new Steer(Moves: false, Dx: default, Dz: default, Stop: default, CornerDistance: default, CornerTurn: default, Faces: false, Facing: default, Pace: string.Empty);

        switch (step) {
            case WorldRouteStep.PoseBody pose:
                if (elapsed >= LandTicks) {
                    if (m_poseOutcome?.Invoke() is "refused" or "faulted") {
                        Fail(kind: "pose", place: read.Place, reason: $"the body is not where step {m_step} ({Describe(step: pose)}) set it down", tick: tick);
                        return still;
                    }

                    var point = pose.Point;
                    if (pose.Spawn is { } id) {
                        point = WorldDefinitionRows.FindSpawnPoint(spawnPoints: read.Endpoint.Definition.SpawnPoints, id: id)?.Position;
                    }
                    var poseConfirmed = (
                        read.Endpoint.ClockOwnedHere ||
                        !m_poseHadPreRead ||
                        !string.Equals(a: read.Endpoint.Authority, b: m_posePreAuthority, comparisonType: StringComparison.Ordinal) ||
                        (read.Index != m_posePreIndex) ||
                        (read.Body.Generation != m_posePreGeneration) ||
                        (read.Body.PoseEpoch != m_posePreEpoch)
                    );

                    // A pose that did not land (refused, or overtaken by a crossing in flight) leaves the chapter
                    // starting somewhere its author did not mean. A pose sent to an authority in another process (or
                    // carrying the body home from a secondary world) lands in a later delivery, so the step waits for
                    // the new pose epoch or authority to show within PoseLandSeconds before it fails.
                    if ((point is not { } at) || !poseConfirmed || ((Point(at) - read.Position).LengthSquared > FixedQ4816.One)) {
                        var poseAuthorityLimit = (ulong)(PoseLandSeconds * rateHz);
                        if ((elapsedAuthority >= poseAuthorityLimit) || (elapsed >= (poseAuthorityLimit * 4UL))) {
                            Fail(kind: "pose", place: read.Place, reason: $"the body is not where step {m_step} ({Describe(step: pose)}) set it down", tick: tick);
                            return still;
                        }
                        if (!read.Endpoint.ClockOwnedHere && ((tick - m_poseAttemptTick) >= ((ulong)(4 * rateHz)))) {
                            SubmitPose(pose: pose, tick: tick, countLine: false);
                        }
                        return still;
                    }
                    m_previousStepPlaces = read.Places;
                    NextStep(tick: tick, rateHz: rateHz);
                }
                return still;
            case WorldRouteStep.Line:
                if (
                    (elapsed >= LandTicks) &&
                    ((m_lineOutcome is null) || (m_lineOutcome() is not "pending") || (elapsed >= ((ulong)rateHz)))
                ) {
                    NextStep(tick: tick, rateHz: rateHz);
                }
                return still;
            case WorldRouteStep.Rest rest: {
                var restTicks = (ulong)Math.Ceiling(a: (rest.Seconds * rateHz));
                var turnsCamera = ((rest.FaceDegrees is not null) || (rest.LookAt is not null));
                if ((elapsedAuthority >= restTicks) && (!turnsCamera || (elapsed >= restTicks))) {
                    NextStep(tick: tick, rateHz: rateHz);
                    return still;
                }
                if (rest.FaceDegrees is { } face) {
                    return still with { Faces = true, Facing = FixedQ4816.FromDegrees(degrees: face) };
                }
                if (rest.LookAt is { } look) {
                    return still with { Faces = true, Facing = Heading(dx: (Point(look).X - read.Position.X), dz: (Point(look).Z - read.Position.Z)) };
                }
                return still;
            }
            case WorldRouteStep.PressAction press: {
                if ((press.Until is { } immediateUntil) && Stands(world: press.World, read: in read) && read.Places.Contains(value: immediateUntil)) {
                    NextStep(tick: tick, rateHz: rateHz);
                    return still;
                }
                if (m_tapAwaitingParty) {
                    if (m_canPressUntil?.Invoke(arg: m_step) == false) {
                        m_stepStart = tick;
                        m_stepAuthorityStart = m_authorityTicks;
                        return still;
                    }
                    m_stepStart = tick;
                    m_stepAuthorityStart = m_authorityTicks;
                    ArmTap(action: press.Action, read: read);
                    return still;
                }
                // Hold the tap until the authority has answered its press edge and stepped past it, so socket jitter
                // or burst bunching never coalesces a press and its release into one authority tick before an
                // edge-latched rule reads the held channel.
                if ((m_tap is not null) && (m_tapAuthority is null)) {
                    m_tapTick = read.Body.Tick;
                    m_tapAuthority = read.Endpoint.Authority;
                } else if ((m_tap is not null) && (elapsed >= LandTicks)) {
                    if (read.Endpoint.ClockOwnedHere || !string.Equals(a: m_tapAuthority, b: read.Endpoint.Authority, comparisonType: StringComparison.Ordinal)) {
                        m_tap = null;
                    } else if (TapAnswered(action: m_tap, read: in read)) {
                        if (!m_tapAnswered) {
                            m_tapAnswered = true;
                            m_tapAnsweredTick = read.Body.Tick;
                        } else if (read.Body.Tick > m_tapAnsweredTick) {
                            m_tap = null;
                        }
                    }
                } else if (
                    (m_tap is null) &&
                    (press.Until is not null) &&
                    !read.Endpoint.ClockOwnedHere &&
                    ((m_authorityTicks - m_lastTapAuthorityTick) >= ((ulong)(8 * rateHz)))
                ) {
                    ArmTap(action: press.Action, read: read);
                }
                if ((press.Until is { } until) && Stands(world: press.World, read: in read) && read.Places.Contains(value: until)) {
                    NextStep(tick: tick, rateHz: rateHz);
                } else if (elapsedAuthority >= (ulong)Math.Ceiling(a: (press.Seconds * rateHz))) {
                    if (press.Until is null) {
                        NextStep(tick: tick, rateHz: rateHz);
                    } else {
                        Fail(kind: "timeout", place: read.Place, reason: string.Create(provider: CultureInfo.InvariantCulture, handler: $"pressed {press.Action} and was not carried into {press.Until} within {press.Seconds:0.#} s"), tick: tick);
                    }
                }
                return still;
            }
            case WorldRouteStep.Cross cross:
                return Cross(cross: cross, elapsed: elapsed, rateHz: rateHz, read: in read, still: still, tick: tick);
            case WorldRouteStep.Go go:
                return Go(go: go, rateHz: rateHz, read: in read, still: still, tick: tick);
            case WorldRouteStep.Fly fly:
                return Fly(fly: fly, rateHz: rateHz, read: in read, still: still, tick: tick);
            default:
                NextStep(tick: tick, rateHz: rateHz);
                return still;
        }
    }

    private Steer Fly(WorldRouteStep.Fly fly, in WorldSeatSenses.Reading read, Steer still, ulong tick, int rateHz) {
        var here = read.Endpoint.Definition.DocumentId;

        // The world the step started in, by its documentId: the point is in its coordinates, and a seam on the way
        // changes the world under the seat.
        m_flownIn ??= here;
        if (!string.Equals(a: here, b: m_flownIn, comparisonType: StringComparison.Ordinal)) {
            if (string.Equals(a: here, b: fly.Enters, comparisonType: StringComparison.Ordinal)) {
                NextStep(tick: tick, rateHz: rateHz);
            } else {
                Fail(kind: "route", place: read.Place, reason: $"step {m_step} ({Describe(step: fly)}) flew out of '{m_flownIn}' into '{here}' ({read.World})", tick: tick);
            }
            return still;
        }
        if (!Stands(world: fly.World, read: in read)) {
            Fail(kind: "route", place: read.Place, reason: $"step {m_step} ({Describe(step: fly)}) is played in the world '{fly.World}', and the seat stands in '{here}' ({read.World})", tick: tick);
            return still;
        }

        var offset = (Point(fly.Point) - read.Position);

        if (fly.Lands) {
            // A rider's own body stands on nothing; the body it rides is the one that lands.
            var mirror = read.Endpoint.Mirror;
            var craft = ((mirror.Mount(index: read.Index) is var ridden and >= 0) ? ridden : read.Index);

            if (mirror.Facts(index: craft).HasFlag(flag: Puck.Physics.Motion.BodyFacts.Grounded)) {
                NextStep(tick: tick, rateHz: rateHz);
                return still;
            }
        } else if (offset.IsWithin(radius: FixedQ4816.FromDouble(value: fly.Within))) {
            if (fly.Enters is null) {
                NextStep(tick: tick, rateHz: rateHz);
            } else {
                Fail(kind: "route", place: read.Place, reason: $"step {m_step} ({Describe(step: fly)}) reached its point without arriving in '{fly.Enters}'", tick: tick);
            }
            return still;
        }

        // Over the ground toward the point, and the point's height along the body's own up: a craft's rise control
        // works along the up it stands by, which a round world turns.
        var bodyUp = System.Numerics.Vector3.Transform(value: System.Numerics.Vector3.UnitY, rotation: read.Body.Orientation);
        var up = FixedVector3.FromVector3(value: bodyUp);
        var rise = ((offset.X * up.X) + (offset.Y * up.Y) + (offset.Z * up.Z));
        var planar = Planar(from: read.Position, to: Point(fly.Point));
        var steers = (planar > FixedQ4816.FromDouble(value: (fly.Lands ? 0.5 : Math.Min(val1: 0.5, val2: fly.Within))));
        var dx = (steers ? ((Point(fly.Point).X - read.Position.X) / planar) : FixedQ4816.Zero);
        var dz = (steers ? ((Point(fly.Point).Z - read.Position.Z) / planar) : FixedQ4816.Zero);

        return still with {
            Moves = true,
            Dx = dx,
            Dz = dz,
            Stop = planar,
            CornerDistance = FixedQ4816.MaxValue,
            Faces = steers,
            Facing = (steers ? Heading(dx: dx, dz: dz) : default),
            Pace = fly.Pace,
            Rise = rise,
            Climb = FixedQ4816.FromDouble(value: System.Numerics.Vector3.Dot(vector1: read.Body.Velocity, vector2: bodyUp)),
        };
    }

    private Steer Cross(WorldRouteStep.Cross cross, ulong elapsed, in WorldSeatSenses.Reading read, Steer still, ulong tick, int rateHz) {
        if ((cross.Until is { } until) && read.Places.Contains(value: until)) {
            NextStep(tick: tick, rateHz: rateHz);
            return still;
        }

        var heading = (m_heading ??= FixedQ4816.WrapAngle(radians: (FixedQ4816.FromDegrees(degrees: cross.HeadingDegrees) + (cross.Relative ? read.Yaw : FixedQ4816.Zero))));
        var carryEpoch = read.Endpoint.Mirror.Carries.Epoch(index: read.Index);

        if (!m_pushing) {
            var error = FixedQ4816.Abs(value: FixedQ4816.WrapAngle(radians: (heading - read.Yaw)));

            m_crossAuthority = read.Endpoint.Authority;
            m_crossIndex = read.Index;
            m_crossGeneration = read.Body.Generation;
            m_crossPoseEpoch = read.Body.PoseEpoch;
            m_crossCarryEpoch = carryEpoch;
            m_crossBodyTick = read.Body.Tick;
            m_lastPosition = read.Position;
            if ((error > FixedQ4816.FromDegrees(degrees: AlignDegrees)) && (elapsed < AlignTicks)) {
                return still with { Faces = true, Facing = heading };
            }
            m_pushing = true;
        }

        var portalJump = (
            !string.Equals(a: m_crossAuthority, b: read.Endpoint.Authority, comparisonType: StringComparison.Ordinal) ||
            (m_crossIndex != read.Index) ||
            (m_crossGeneration != read.Body.Generation) ||
            (m_crossPoseEpoch != read.Body.PoseEpoch) ||
            (m_crossCarryEpoch != carryEpoch)
        );
        var deltaTicks = ((!portalJump && (read.Body.Tick > m_crossBodyTick))
            ? Math.Clamp(value: (read.Body.Tick - m_crossBodyTick), min: 1UL, max: 60UL)
            : 1UL);
        var moved = (read.Position - m_lastPosition);
        var step = FixedQ4816.Sqrt(value: ((moved.X * moved.X) + (moved.Z * moved.Z)));

        // A portal moves the body across the world or into another authority; bunched continuous ticks scale with deltaTicks.
        if (portalJump) {
            m_crossedPortal = true;
            m_crossedPortalTick = tick;
        } else if (step < (FixedQ4816.FromDouble(value: 1.5) * FixedQ4816.FromInteger(value: (int)deltaTicks))) {
            m_travelled += step;
        }
        m_crossAuthority = read.Endpoint.Authority;
        m_crossIndex = read.Index;
        m_crossGeneration = read.Body.Generation;
        m_crossPoseEpoch = read.Body.PoseEpoch;
        m_crossCarryEpoch = carryEpoch;
        m_crossBodyTick = read.Body.Tick;
        m_lastPosition = read.Position;

        var crossLagTicks = Math.Clamp(value: Math.Max(val1: 10UL, val2: m_senses.MoveLagTicks(slot: m_slot, bodyTick: read.Body.Tick)), min: 10UL, max: 20UL);
        var inFlight = (read.Endpoint.ClockOwnedHere
            ? FixedQ4816.Zero
            : (read.PlanarSpeed * FixedQ4816.FromDouble(value: ((double)crossLagTicks / rateHz))));
        var targetMetres = FixedQ4816.FromDouble(value: cross.Metres);

        if (cross.Until is null) {
            if ((m_travelled + inFlight) >= targetMetres) {
                NextStep(tick: tick, rateHz: rateHz);
                return still;
            }
        } else if (m_travelled >= targetMetres) {
            if (!read.Endpoint.ClockOwnedHere && m_crossedPortal && ((tick - m_crossedPortalTick) < ((ulong)(3 * rateHz)))) {
                return still;
            }
            if (m_travelled >= (targetMetres + inFlight)) {
                Fail(kind: "route", place: read.Place, reason: string.Create(provider: CultureInfo.InvariantCulture, handler: $"walked {cross.Metres:0.#} m on heading {cross.HeadingDegrees:0.#} without reaching {cross.Until}"), tick: tick);
                return still;
            }
        }

        // Straight ahead in the camera's own frame, holding the entry heading until a portal crosses and then riding the
        // camera's carried heading out of the destination face.
        var (sin, cos) = FixedQ4816.SinCos(angle: read.Yaw);

        return still with {
            Moves = true,
            Dx = -sin,
            Dz = -cos,
            Stop = FixedQ4816.MaxValue,
            CornerDistance = FixedQ4816.MaxValue,
            Faces = !m_crossedPortal,
            Facing = (m_crossedPortal ? default : heading),
            Pace = cross.Pace,
        };
    }

    private Steer Go(WorldRouteStep.Go go, in WorldSeatSenses.Reading read, Steer still, ulong tick, int rateHz, bool planned = false) {
        if (!Stands(world: go.World, read: in read)) {
            Fail(kind: "route", place: read.Place, reason: $"step {m_step} ({Describe(step: go)}) is played in the world '{go.World}', and the seat stands in '{read.Endpoint.Definition.DocumentId}' ({read.World})", tick: tick);
            return still;
        }
        if (go.Place is { } place) {
            if (read.Places.Contains(value: place)) {
                NextStep(tick: tick, rateHz: rateHz);
                return still;
            }
        } else if ((Point(go.Point!.Value) - read.Position).IsWithin(radius: FixedQ4816.FromDouble(value: go.Within))) {
            var pointScale = FixedQ4816.Max(x: FixedQ4816.FromDouble(value: 0.05), y: m_senses.BodyScale(reading: in read));
            var settleSpeed = (FixedQ4816.FromDouble(value: 0.10) * pointScale);

            if (
                read.Endpoint.ClockOwnedHere ||
                (go.Within > 0.35) ||
                (!m_driving && m_senses.MoveStoppedAnswered(slot: m_slot, bodyTick: read.Body.Tick) && (read.PlanarSpeed <= settleSpeed))
            ) {
                NextStep(tick: tick, rateHz: rateHz);
                return still;
            }
            m_progressTick = m_authorityTicks;
            return still;
        }
        if (m_plannedIn != WorldKey(read: in read)) {
            if (!Plan(go: go, read: in read, tick: tick)) {
                return still;
            }
            planned = true;
        }

        // Pass the corners already reached: within reach of one, swept across it during a bunched delivery, or past it
        // along the segment that leads to it.
        var reach = FixedQ4816.FromDouble(value: 0.4);
        var goCarryEpoch = read.Endpoint.Mirror.Carries.Epoch(index: read.Index);
        var continuousSegment = (
            m_hasGoRead &&
            string.Equals(a: m_lastGoAuthority, b: read.Endpoint.Authority, comparisonType: StringComparison.Ordinal) &&
            (m_lastGoIndex == read.Index) &&
            (m_lastGoGeneration == read.Body.Generation) &&
            (m_lastGoPoseEpoch == read.Body.PoseEpoch) &&
            (m_lastGoCarryEpoch == goCarryEpoch)
        );
        var goDeltaTicks = ((continuousSegment && (read.Body.Tick > m_lastGoTick))
            ? Math.Clamp(value: (read.Body.Tick - m_lastGoTick), min: 1UL, max: 60UL)
            : 1UL);
        var sweptSegment = (continuousSegment && (Planar(from: m_lastGoPosition, to: read.Position) < (FixedQ4816.FromDouble(value: 1.5) * FixedQ4816.FromInteger(value: (int)goDeltaTicks))));

        while (m_corner < (m_corners.Count - 1)) {
            var corner = m_corners[m_corner];
            var cornerReach = ((m_corner == 0) ? m_firstReach : reach);
            var from = ((m_corner > 0) ? m_corners[(m_corner - 1)] : m_planStart);
            var along = new FixedVector3(X: (corner.X - from.X), Y: FixedQ4816.Zero, Z: (corner.Z - from.Z));
            var past = new FixedVector3(X: (read.Position.X - corner.X), Y: FixedQ4816.Zero, Z: (read.Position.Z - corner.Z));
            var hasSegment = (Planar(from: from, to: corner) > FixedQ4816.FromDouble(value: 0.05));
            var withinCorner = (
                (Planar(from: read.Position, to: corner) <= cornerReach) ||
                (sweptSegment && (Stray(from: m_lastGoPosition, to: read.Position, point: corner) <= FixedQ4816.Max(x: cornerReach, y: FixedQ4816.FromDouble(value: 0.25))))
            );

            if (!withinCorner && (!hasSegment || (FixedVector3.Dot(left: along, right: past) < FixedQ4816.Zero))) {
                break;
            }

            m_corner++;
            m_best = FixedQ4816.MaxValue;
            m_progressTick = m_authorityTicks;
        }
        m_hasGoRead = true;
        m_lastGoPosition = read.Position;
        m_lastGoAuthority = read.Endpoint.Authority;
        m_lastGoIndex = read.Index;
        m_lastGoGeneration = read.Body.Generation;
        m_lastGoPoseEpoch = read.Body.PoseEpoch;
        m_lastGoCarryEpoch = goCarryEpoch;
        m_lastGoTick = read.Body.Tick;

        var target = m_corners[m_corner];
        var distance = Planar(from: read.Position, to: target);
        var scale = FixedQ4816.Max(x: FixedQ4816.FromDouble(value: 0.05), y: m_senses.BodyScale(reading: in read));
        var moveAnswered = (m_senses.InputAdmitted(slot: m_slot) && m_senses.MoveInputAnswered(slot: m_slot, bodyTick: read.Body.Tick));

        // A body that has strayed from its line, or made no progress toward its corner across StuckTicks answered
        // authority ticks, plans again from where it stands; one that keeps making no progress has met something its
        // path did not show.
        if (distance < (m_best - (FixedQ4816.FromDouble(value: 0.05) * scale))) {
            m_best = distance;
            m_progressTick = m_authorityTicks;
        } else if (!moveAnswered) {
            m_progressTick = m_authorityTicks;
        }
        var final = (m_corner == (m_corners.Count - 1));
        if (!planned && !go.Direct && (m_corner > 0) && (Stray(from: m_corners[(m_corner - 1)], point: read.Position, to: target) > FixedQ4816.FromDouble(value: StrayMetres))) {
            return Plan(go: go, read: in read, tick: tick) ? Go(go: go, planned: true, rateHz: rateHz, read: in read, still: still, tick: tick) : still;
        }
        if (!planned && moveAnswered && ((m_authorityTicks - m_progressTick) > StuckTicks)) {
            if (++m_stuckPlans > MaxStuckPlans) {
                Fail(kind: "stuck", place: read.Place, reason: $"made no progress toward {(go.Place ?? Vector(go.Point))} after {MaxStuckPlans} plans", tick: tick);
                return still;
            }
            return Plan(go: go, read: in read, tick: tick, stuck: true) ? Go(go: go, planned: true, rateHz: rateHz, read: in read, still: still, tick: tick) : still;
        }

        var dx = (target.X - read.Position.X);
        var dz = (target.Z - read.Position.Z);
        var turn = FixedQ4816.Zero;
        var facing = Heading(dx: dx, dz: dz);

        if (!final) {
            var next = m_corners[(m_corner + 1)];

            turn = FixedQ4816.Abs(value: FixedQ4816.WrapAngle(radians: (Heading(dx: (next.X - target.X), dz: (next.Z - target.Z)) - facing)));
            // Near a corner the camera already looks along the next leg, as a player looks where they turn.
            if (distance < FixedQ4816.One) {
                facing = Heading(dx: (next.X - read.Position.X), dz: (next.Z - read.Position.Z));
            }
        }

        var length = FixedQ4816.Max(x: distance, y: FixedQ4816.FromDouble(value: 0.001));
        var stop = FixedQ4816.MaxValue;

        if (go.Point is not null) {
            var remaining = distance;

            for (var index = m_corner; (index < (m_corners.Count - 1)); index++) {
                remaining += Planar(from: m_corners[index], to: m_corners[(index + 1)]);
            }
            var margin = (read.Endpoint.ClockOwnedHere
                ? (FixedQ4816.FromDouble(value: go.Within) / FixedQ4816.FromInteger(value: 2))
                : FixedQ4816.Zero);
            stop = FixedQ4816.Max(x: FixedQ4816.Zero, y: (remaining - margin));
        } else if (
            !read.Endpoint.ClockOwnedHere &&
            (go.Place is { } goalPlace) &&
            m_senses.TryPlaceShape(endpoint: read.Endpoint, placementId: goalPlace, shape: out var goalShape) &&
            !goalShape.Contains(point: (goalShape.Center + new FixedVector3(X: FixedQ4816.FromInteger(value: 2), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero))) &&
            !goalShape.Contains(point: (goalShape.Center + new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.FromInteger(value: 2))))
        ) {
            var remaining = distance;

            for (var index = m_corner; (index < (m_corners.Count - 1)); index++) {
                remaining += Planar(from: m_corners[index], to: m_corners[(index + 1)]);
            }
            stop = FixedQ4816.Max(x: FixedQ4816.FromDouble(value: 1.5), y: remaining);
        }
        if (!read.Endpoint.ClockOwnedHere && (m_corner == 0) && (m_firstReach < reach)) {
            // Stepping onto a tight start cell (m_firstReach = 0.1 m) against a remote clock eases into the cell rather
            // than sprinting over its 20 cm diameter between 30 Hz readings.
            stop = FixedQ4816.Min(x: stop, y: FixedQ4816.Max(x: FixedQ4816.Zero, y: (distance - (m_firstReach / FixedQ4816.FromInteger(value: 2)))));
        }

        return still with {
            Moves = true,
            Dx = (dx / length),
            Dz = (dz / length),
            Stop = stop,
            CornerDistance = (final ? FixedQ4816.MaxValue : distance),
            CornerTurn = turn,
            Faces = true,
            Facing = facing,
            Pace = go.Pace,
        };
    }

    // Plans the step's path from where the body stands: straight at the goal for a direct step, otherwise the navigation
    // domains' route straightened to its corners, with the places its nodes stand in as the corridor.
    // A navigated path waits for the navigation of the world the seat stands in to finish baking off the pump: the
    // body stands, and the step plans on the tick it is ready, within the chapter's time.
    private bool Plan(WorldRouteStep.Go go, in WorldSeatSenses.Reading read, ulong tick, bool stuck = false) {
        if (!go.Direct && (m_senses.Navigation(endpoint: read.Endpoint) is { Ready: false } baking)) {
            baking.Prepare();
            m_plannedIn = null;
            return false;
        }
        Plans++;
        m_plannedIn = WorldKey(read: in read);
        m_planStart = read.Position;
        m_hasGoRead = false;
        m_corners.Clear();
        m_corner = 0;
        m_firstReach = FixedQ4816.FromDouble(value: 0.4);
        m_best = FixedQ4816.MaxValue;
        m_progressTick = m_authorityTicks;
        m_corridor.Clear();
        if (!stuck) {
            m_stuckPlans = 0;
        }
        foreach (var allowed in (go.Corridor ?? [])) {
            _ = m_corridor.Add(item: allowed);
        }
        if (go.Place is { } goal) {
            _ = m_corridor.Add(item: goal);
        }
        // Where the body stands as the path is planned, and the places the preceding step handed off from: a step that
        // began on a place's edge may cross back over it before a remote turn takes effect.
        _ = m_corridor.Add(item: read.Place);
        foreach (var standing in read.Places) {
            _ = m_corridor.Add(item: standing);
        }
        foreach (var prior in m_previousStepPlaces) {
            _ = m_corridor.Add(item: prior);
        }
        if ((m_step > 0) && (m_chapter?.Steps[(m_step - 1)] is WorldRouteStep.Go { Place: { } previousPlace })) {
            _ = m_corridor.Add(item: previousPlace);
        }

        var target = ((go.Point is { } point) ? Point(point) : default);
        var shape = default(FixedSpatialShape);

        if ((go.Place is { } named) && !m_senses.TryPlaceShape(endpoint: read.Endpoint, placementId: named, shape: out shape)) {
            Fail(kind: "route", place: read.Place, reason: $"the place {named} is not in the document {read.World} delivered", tick: tick);
            return false;
        }
        if (go.Direct) {
            m_corners.Add(item: ((go.Point is null) ? shape.Center : target));
            m_checkCorridor = (go.Corridor is { Count: > 0 });
            return true;
        }

        var plan = m_senses.Navigation(endpoint: read.Endpoint).Plan(
            corners: m_corners,
            goal: new WorldSeatGoal(Place: ((go.Place is null) ? null : shape), Point: target),
            margin: m_margin,
            route: m_routeFeet,
            walker: m_senses.Walker(reading: in read)
        );

        if (!plan.Found) {
            Fail(kind: "route", place: read.Place, reason: $"no route from ({Metres(read.Position.X)}, {Metres(read.Position.Y)}, {Metres(read.Position.Z)}) to {(go.Place ?? Vector(go.Point))} over the document {read.World} delivered: {plan.Refusal}", tick: tick);
            return false;
        }
        if (plan.StartsOnCell) {
            m_firstReach = FixedQ4816.FromDouble(value: 0.1);
        }
        if (
            (go.Point is not null) &&
            (m_corners.Count >= 2) &&
            ((m_corners.Count > 2) || !plan.StartsOnCell) &&
            (Planar(from: m_corners[^2], to: m_corners[^1]) < FixedQ4816.FromDouble(value: 0.85))
        ) {
            m_corners.RemoveAt(index: (m_corners.Count - 2));
        }
        var endpoint = read.Endpoint;

        void AddPlacePoint(in FixedVector3 point) {
            var places = m_senses.PlacesAt(endpoint: endpoint, point: point);

            _ = m_corridor.Add(item: ((places.Count == 0) ? "none" : places[0]));
        }

        void AddPlaceAt(in FixedVector3 sample) {
            AddPlacePoint(point: in sample);
            if (m_margin > FixedQ4816.Zero) {
                AddPlacePoint(point: sample with { X = (sample.X - m_margin) });
                AddPlacePoint(point: sample with { X = (sample.X + m_margin) });
                AddPlacePoint(point: sample with { Z = (sample.Z - m_margin) });
                AddPlacePoint(point: sample with { Z = (sample.Z + m_margin) });
            }
        }

        foreach (var foot in m_routeFeet) {
            AddPlaceAt(sample: in foot);
        }

        // Straightening shortcuts grid staircases diagonally across shared district corners, so every metre of the
        // straightened segments the driver actually steers along belongs to the corridor too.
        var segmentStart = read.Position;

        foreach (var corner in m_corners) {
            var span = (double)Planar(from: segmentStart, to: corner);
            var steps = (int)Math.Ceiling(a: span);

            for (var step = 1; (step <= steps); step++) {
                var fraction = FixedQ4816.FromDouble(value: (step / (double)steps));
                var sample = new FixedVector3(
                    X: (segmentStart.X + ((corner.X - segmentStart.X) * fraction)),
                    Y: (segmentStart.Y + ((corner.Y - segmentStart.Y) * fraction)),
                    Z: (segmentStart.Z + ((corner.Z - segmentStart.Z) * fraction))
                );

                AddPlaceAt(sample: in sample);
            }
            segmentStart = corner;
        }
        m_checkCorridor = true;
        return true;
    }

    // Evaluates the program's operations into the seat's controls and submits each one that changed, re-asserting held
    // sticks as a player does and every held control when the seat's world changed under it or its input just settled.
    private void Drive(in WorldSeatSenses.Reading read, in Steer steer, ulong tick, int rateHz) {
        var admitted = m_senses.InputAdmitted(slot: m_slot);
        var crossed = (
            !string.Equals(a: m_routeWorld, b: read.World, comparisonType: StringComparison.Ordinal) ||
            !string.Equals(a: m_routeAuthority, b: read.Endpoint.Authority, comparisonType: StringComparison.Ordinal) ||
            (m_routeIndex != read.Index) ||
            (!m_routeAdmitted && admitted)
        );
        var reassert = (crossed || ((tick - m_lastAssert) >= ReassertTicks));

        m_routeWorld = read.World;
        m_routeAuthority = read.Endpoint.Authority;
        m_routeIndex = read.Index;
        m_routeAdmitted = admitted;
        if (reassert) {
            m_lastAssert = tick;
        }
        var driveScale = FixedQ4816.Max(x: FixedQ4816.FromDouble(value: 0.05), y: m_senses.BodyScale(reading: in read));
        m_driving = false;
        m_docking = (!read.Endpoint.ClockOwnedHere && steer.Moves && (steer.Stop < driveScale));

        // The seat turns its camera by this tick's look before it composes this tick's move, so the move is framed
        // against the facing the turn leaves, not the one read.
        var facing = read.Yaw;

        foreach (var operation in m_program.Operations) {
            if (operation is WorldSeatProgramOp.Turn turn) {
                facing += (TurnDeflection(read: in read, steer: in steer, turn: turn) * read.YawPerTick);
            }
        }
        foreach (var operation in m_program.Operations) {
            var value = Evaluate(facing: facing, operation: operation, rateHz: rateHz, read: in read, steer: in steer, tick: tick);
            var neutral = NeutralOf(operation: operation);
            var stick = (operation is WorldSeatProgramOp.Move or WorldSeatProgramOp.Turn);
            var sent = (m_sent.TryGetValue(key: operation.Driven, value: out var previous) ? previous : neutral);

            if ((operation is WorldSeatProgramOp.Move) && (value != neutral)) {
                m_driving = true;
            }
            if ((value != sent) || ((value != neutral) && ((stick && reassert) || crossed))) {
                m_sent[operation.Driven] = value;
                Submit(text: $"player.signal {operation.Driven} {value}");
            }
        }
    }

    private string Evaluate(WorldSeatProgramOp operation, in WorldSeatSenses.Reading read, in Steer steer, FixedQ4816 facing, ulong tick, int rateHz) {
        var scale = FixedQ4816.Max(x: FixedQ4816.FromDouble(value: 0.05), y: m_senses.BodyScale(reading: in read));
        var lagTicks = Math.Clamp(value: m_senses.MoveLagTicks(slot: m_slot, bodyTick: read.Body.Tick), min: 4UL, max: 18UL);
        var lag = (read.Endpoint.ClockOwnedHere
            ? FixedQ4816.Zero
            : FixedQ4816.FromDouble(value: ((double)lagTicks / rateHz)));

        switch (operation) {
            case WorldSeatProgramOp.Move move: {
                if (!steer.Moves) {
                    return "0 0";
                }

                // Slow for a sharp corner, or to stop at a final point, against the speed the body was delivered at
                // and any round-trip pipeline lag between this reading and when a release reaches the authority.
                var speed = read.PlanarSpeed;
                var brake = FixedQ4816.FromDouble(value: move.Brake);
                var bTau = (brake * lag);
                var allowed = FixedQ4816.MaxValue;

                if ((steer.CornerDistance != FixedQ4816.MaxValue) && (steer.CornerTurn > FixedQ4816.FromDegrees(degrees: move.CornerDegrees))) {
                    var cornerRate = ((!read.Endpoint.ClockOwnedHere && (steer.CornerTurn > FixedQ4816.FromDegrees(degrees: 60.0)))
                        ? Math.Min(val1: move.CornerSpeed, val2: 2.2)
                        : move.CornerSpeed);
                    var corner = (FixedQ4816.FromDouble(value: cornerRate) * scale);
                    var cornerAllowed = (FixedQ4816.Sqrt(value: (((bTau * bTau) + (corner * corner)) + ((FixedQ4816.FromInteger(value: 2) * brake) * steer.CornerDistance))) - bTau);

                    if (!read.Endpoint.ClockOwnedHere && (steer.CornerTurn > FixedQ4816.FromDegrees(degrees: 55.0)) && (steer.CornerDistance < (FixedQ4816.FromDouble(value: 3.5) * scale))) {
                        var cornerDamped = FixedQ4816.Max(
                            x: (FixedQ4816.FromDouble(value: 1.8) * scale),
                            y: (FixedQ4816.FromDouble(value: 0.85) * steer.CornerDistance)
                        );

                        cornerAllowed = FixedQ4816.Min(x: cornerAllowed, y: cornerDamped);
                    }
                    allowed = cornerAllowed;
                }
                if (steer.Stop != FixedQ4816.MaxValue) {
                    var stopAllowed = (FixedQ4816.Sqrt(value: ((bTau * bTau) + ((FixedQ4816.FromInteger(value: 2) * brake) * steer.Stop))) - bTau);

                    if (!read.Endpoint.ClockOwnedHere) {
                        var knee = (FixedQ4816.FromDouble(value: 3.5) * scale);
                        var damped = ((steer.Stop <= knee)
                            ? (FixedQ4816.FromDouble(value: 0.34) * steer.Stop)
                            : ((FixedQ4816.FromDouble(value: 1.19) * scale) + (FixedQ4816.FromDouble(value: 0.70) * (steer.Stop - knee))));

                        stopAllowed = FixedQ4816.Min(x: stopAllowed, y: damped);
                    }
                    allowed = FixedQ4816.Min(x: allowed, y: stopAllowed);
                }

                var magnitude = FixedQ4816.One;

                var floor = (read.Endpoint.ClockOwnedHere
                    ? FixedQ4816.FromDouble(value: move.Least)
                    : ((steer.Stop < scale) ? FixedQ4816.FromDouble(value: 0.028) : FixedQ4816.FromDouble(value: 0.055)));

                if ((allowed != FixedQ4816.MaxValue) && (speed > allowed)) {
                    magnitude = FixedQ4816.Max(x: floor, y: (allowed / speed));
                }
                if (!read.Endpoint.ClockOwnedHere && (allowed != FixedQ4816.MaxValue)) {
                    var refWalkSpeed = (FixedQ4816.FromInteger(value: 5) * scale);
                    var approach = FixedQ4816.Clamp(
                        value: (allowed / refWalkSpeed),
                        minimum: floor,
                        maximum: FixedQ4816.One
                    );

                    magnitude = FixedQ4816.Min(x: magnitude, y: approach);
                }

                // The stick that makes intent composition's rotation by the facing yield the world direction.
                var (sin, cos) = FixedQ4816.SinCos(angle: facing);
                var forward = (-(steer.Dz * cos) - (steer.Dx * sin));
                var strafe = ((steer.Dx * cos) - (steer.Dz * sin));

                return $"{Number(strafe * magnitude)} {Number(forward * magnitude)}";
            }
            case WorldSeatProgramOp.Turn turn: {
                var deflection = TurnDeflection(read: in read, steer: in steer, turn: turn);

                return ((deflection == FixedQ4816.Zero) ? "0 0" : $"{Number(deflection)} 0");
            }
            case WorldSeatProgramOp.Hold hold:
                return ((steer.Moves && CanSprint(lag: lag, read: in read, scale: scale, steer: in steer) && hold.Paces.Contains(value: steer.Pace)) ? "press" : "release");
            case WorldSeatProgramOp.Pulse pulse: {
                var period = Math.Max(val1: 2UL, val2: (ulong)Math.Round(a: (pulse.PeriodSeconds * rateHz)));
                var pulseElapsed = (read.Endpoint.ClockOwnedHere ? (tick - m_stepStart) : (m_authorityTicks - m_stepAuthorityStart));

                return ((steer.Moves && CanSprint(lag: lag, read: in read, scale: scale, steer: in steer) && pulse.Paces.Contains(value: steer.Pace) && ((pulseElapsed % period) == 0UL)) ? "press" : "release");
            }
            case WorldSeatProgramOp.Tap tap:
                return ((m_tap == tap.Action) ? "press" : "release");
            case WorldSeatProgramOp.Rise rise: {
                // The height left to the band's edge and the speed toward it, both along the button's own way; the
                // button is held while a body let go now would still stop short of the edge.
                var sign = ((rise.Toward == WorldSeatRiseDirection.Up) ? FixedQ4816.One : -FixedQ4816.One);
                var left = ((steer.Rise * sign) - FixedQ4816.FromDouble(value: rise.Band));
                var speed = (steer.Climb * sign);

                return ((steer.Moves && (left > FixedQ4816.Zero) && ((speed <= FixedQ4816.Zero) || ((speed * speed) < ((FixedQ4816.FromInteger(value: 2) * FixedQ4816.FromDouble(value: rise.Brake)) * left)))) ? "press" : "release");
            }
            default:
                return string.Empty;
        }
    }

    // The X deflection that takes the turn's gain of the way from the camera's facing to the step's, centred inside its
    // deadband and never past full; rounded to the precision a control line carries, so the facing it predicts is the
    // facing the seat turns to.
    private static FixedQ4816 TurnDeflection(WorldSeatProgramOp.Turn turn, in WorldSeatSenses.Reading read, in Steer steer) {
        if (!steer.Faces || (read.YawPerTick == FixedQ4816.Zero)) {
            return FixedQ4816.Zero;
        }

        var error = FixedQ4816.WrapAngle(radians: (steer.Facing - read.Yaw));

        if (FixedQ4816.Abs(value: error) <= FixedQ4816.FromDegrees(degrees: turn.DeadbandDegrees)) {
            return FixedQ4816.Zero;
        }

        var deflection = FixedQ4816.Clamp(value: ((error * FixedQ4816.FromDouble(value: turn.Gain)) / read.YawPerTick), minimum: -FixedQ4816.One, maximum: FixedQ4816.One);

        return FixedQ4816.FromDouble(value: Math.Round(value: (double)deflection, digits: 4, mode: MidpointRounding.AwayFromZero));
    }

    private static bool CanSprint(in WorldSeatSenses.Reading read, in Steer steer, FixedQ4816 scale, FixedQ4816 lag) {
        if (read.Endpoint.ClockOwnedHere) {
            return true;
        }
        if (steer.Stop < FixedQ4816.Max(x: (FixedQ4816.FromDouble(value: 11.5) * scale), y: ((FixedQ4816.FromInteger(value: 13) * scale) * lag))) {
            return false;
        }
        if (
            (steer.CornerDistance != FixedQ4816.MaxValue) &&
            (steer.CornerTurn > FixedQ4816.FromDegrees(degrees: 45.0)) &&
            (steer.CornerDistance < FixedQ4816.Max(x: (FixedQ4816.FromDouble(value: 7.5) * scale), y: ((FixedQ4816.FromInteger(value: 12) * scale) * lag)))
        ) {
            return false;
        }
        if (steer.Faces && (FixedQ4816.Abs(value: FixedQ4816.WrapAngle(radians: (steer.Facing - read.Yaw))) > FixedQ4816.FromDegrees(degrees: 35.0))) {
            return false;
        }

        return true;
    }

    private Func<string> Submit(string text) {
        var outcome = m_submit(arg: text);

        m_lines.Add(item: outcome);
        return outcome;
    }

    private static string NeutralOf(WorldSeatProgramOp operation) => ((operation is WorldSeatProgramOp.Move or WorldSeatProgramOp.Turn) ? "0 0" : "release");

    // The yaw that faces a planar direction: facing is (-sin yaw, -cos yaw) in X and Z.
    private static FixedQ4816 Heading(FixedQ4816 dx, FixedQ4816 dz) => new FixedVector3(X: dx, Y: FixedQ4816.Zero, Z: dz).Yaw();

    private static FixedQ4816 Planar(in FixedVector3 from, in FixedVector3 to) {
        var dx = (to.X - from.X);
        var dz = (to.Z - from.Z);

        return FixedQ4816.Sqrt(value: ((dx * dx) + (dz * dz)));
    }

    // The planar distance of a point from the segment between two corners.
    private static FixedQ4816 Stray(in FixedVector3 from, in FixedVector3 to, in FixedVector3 point) {
        var ax = (to.X - from.X);
        var az = (to.Z - from.Z);
        var length = ((ax * ax) + (az * az));

        if (length == FixedQ4816.Zero) {
            return Planar(from: from, to: point);
        }

        var t = FixedQ4816.Clamp(value: ((((point.X - from.X) * ax) + ((point.Z - from.Z) * az)) / length), minimum: FixedQ4816.Zero, maximum: FixedQ4816.One);

        return Planar(from: point, to: new FixedVector3(X: (from.X + (ax * t)), Y: point.Y, Z: (from.Z + (az * t))));
    }

    private static FixedVector3 Point(Puck.Assets.Documents.DocumentVector3 point) => FixedVector3.FromVector3(value: point.Value);

    private static string Number(double value) => value.ToString(format: "0.####", provider: CultureInfo.InvariantCulture);

    private static string Number(FixedQ4816 value) => Number(value: (double)value);

    private static string Metres(FixedQ4816 value) => ((double)value).ToString(format: "0.00", provider: CultureInfo.InvariantCulture);

    private static string Vector(Puck.Assets.Documents.DocumentVector3? point) => ((point is { } value) ? $"({Number(value.X)}, {Number(value.Y)}, {Number(value.Z)})" : "nowhere");
}
