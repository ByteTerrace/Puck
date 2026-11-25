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
    private const int PoseLandSeconds = 45;
    // The heading error, in degrees, a crossing turns inside before it walks, and the ticks it gives the turn.
    private const double AlignDegrees = 3.0;
    private const int AlignTicks = 45;
    /// <summary>The authority ticks, as its deliveries report them, a deflected move stick may leave the body within
    /// <see cref="StallMetres"/> of where it stood before the chapter fails as stalled against a wall: long enough for
    /// a control to cross to a remote authority and its effect to come back. Counted in delivered ticks, so a span the
    /// client was delivered nothing in says nothing about a wall.</summary>
    public const int StallTicks = 20;
    /// <summary>The distance, in metres, a body moves in any direction to count as moving under a deflected stick: a
    /// craft climbing or descending under a held stick is moving, though it gains no ground.</summary>
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
    private readonly List<(int Index, WorldDeliveredBody Body)> m_solidScratch = [];
    private readonly List<WorldSeatSenses.SolidWalker> m_walkers = [];

    private WorldRouteChapter? m_chapter;
    private Phase m_phase;
    private int m_step;
    private ulong m_leadInUntilTick;
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
    private ulong m_poseAttemptAuthorityTick;
    private Func<string>? m_poseOutcome;
    private Func<string>? m_lineOutcome;
    private bool m_poseHadPreRead;
    private string? m_posePreAuthority;
    private int m_posePreIndex;
    private int m_posePreGeneration;
    private int m_posePreEpoch;
    private bool m_driving;
    // The planar speed, in metres per second, the move stick last asked of the body (its walking speed for an
    // unslowed stick), which decides whether a held stick is pushing hard enough to judge a wall.
    private FixedQ4816 m_commandedSpeed;
    // The longest control lag, in authority ticks, a remote approach has met lately, and the authority and tick its
    // decay was last taken at (ControlLag).
    private ulong m_lagPeak;
    private ulong m_lagTick;
    private string? m_lagAuthority;
    private bool m_yielding;
    private int m_yieldLeaderIndex = -1;
    private ulong m_yieldStartBodyTick;
    private bool m_settledBehindParty;
    private bool m_enteredGoalPlace;
    private FixedVector3 m_enteredGoalPlaceOrigin;
    private FixedVector3? m_stepAnchorPoint;
    private string? m_stepAnchorWorld;
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

    /// <summary>Starts a chapter on the tick just completed: its first step reads the world in this tick's hook, or
    /// after <paramref name="leadInTicks"/> when several seats stagger their entry into a chapter.</summary>
    /// <param name="chapter">The chapter.</param>
    /// <param name="tick">The completed tick.</param>
    /// <param name="rateHz">The simulation rate, in ticks per second.</param>
    /// <param name="leadInTicks">The client ticks to hold idle before starting step 0.</param>
    public void Begin(WorldRouteChapter chapter, ulong tick, int rateHz, ulong leadInTicks = 0UL) {
        m_chapter = chapter;
        m_phase = Phase.Running;
        m_step = -1;
        m_leadInUntilTick = (tick + leadInTicks);
        m_chapterStart = tick;
        m_authorityBudget = (ulong)Math.Ceiling(a: (chapter.TimeoutSeconds * rateHz));
        m_authorityTicks = 0UL;
        m_lastBodyAuthority = null;
        m_lastBodyTick = 0UL;
        m_noBody = false;
        m_noBodyStart = tick;
        m_deadline = (tick + leadInTicks + Math.Max(val1: (m_authorityBudget * ((m_canPressUntil is null) ? 4UL : 6UL)), val2: ((ulong)(60 * rateHz))));
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
        m_yielding = false;
        m_yieldLeaderIndex = -1;
        m_yieldStartBodyTick = 0UL;
        m_settledBehindParty = false;
        m_enteredGoalPlace = false;
        m_enteredGoalPlaceOrigin = default;
        m_stepAnchorPoint = null;
        m_stepAnchorWorld = null;
        m_previousStepPlaces = (m_senses.TryRead(reading: out var openingRead, slot: m_slot) ? openingRead.Places : []);
        Read = null;
        m_navigates = chapter.Steps.Any(predicate: static step => step is WorldRouteStep.Go { Direct: false });
        Release();
        if (leadInTicks == 0UL) {
            NextStep(tick: tick, rateHz: rateHz);
        }
    }

    /// <summary>Advances the chapter from the tick just completed and submits the seat's controls for the next.</summary>
    /// <param name="tick">The completed tick.</param>
    /// <param name="rateHz">The simulation rate, in ticks per second.</param>
    public void Advance(ulong tick, int rateHz) {
        if ((m_phase != Phase.Running) || (m_chapter is null)) {
            return;
        }
        if (m_step < 0) {
            if (tick < m_leadInUntilTick) {
                return;
            }
            m_chapterStart = tick;
            m_noBody = false;
            m_noBodyStart = tick;
            m_lastBodyAuthority = null;
            m_lastBodyTick = 0UL;
            m_previousStepPlaces = (m_senses.TryRead(reading: out var delayedRead, slot: m_slot) ? delayedRead.Places : []);
            NextStep(tick: tick, rateHz: rateHz);
            if (m_phase != Phase.Running) {
                return;
            }
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
        var pausedBudget = (
            awaitingNavigationBake ||
            m_tapAwaitingParty ||
            m_yielding
        );
        if (m_lastBodyAuthority is null) {
            m_lastBodyAuthority = read.Endpoint.Authority;
            m_lastBodyTick = read.Body.Tick;
        } else if (string.Equals(a: m_lastBodyAuthority, b: read.Endpoint.Authority, comparisonType: StringComparison.Ordinal)) {
            if (read.Body.Tick > m_lastBodyTick) {
                if (!pausedBudget) {
                    m_authorityTicks += (read.Body.Tick - m_lastBodyTick);
                }
                m_lastBodyTick = read.Body.Tick;
            } else if (read.Body.Tick < m_lastBodyTick) {
                m_lastBodyTick = read.Body.Tick;
            }
        } else {
            if (!pausedBudget) {
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
        // admission or initial push for this seat has not yet been answered, and so does a stick held so gently (a
        // remote approach easing onto its goal) that the speed it asks for would not carry the body past the watch's
        // distance in the watch's ticks.
        var admitted = m_senses.InputAdmitted(slot: m_slot);
        var moveAnswered = (admitted && m_senses.MoveInputAnswered(slot: m_slot, bodyTick: read.Body.Tick));
        var scale = FixedQ4816.Max(x: FixedQ4816.FromDouble(value: 0.05), y: m_senses.BodyScale(reading: in read));
        var stallThreshold = (FixedQ4816.FromDouble(value: StallMetres) * scale);
        var watched = ((m_commandedSpeed * FixedQ4816.FromDouble(value: ((double)StallTicks / rateHz))) >= (stallThreshold * FixedQ4816.FromInteger(value: 2)));
        if (!m_driving || !watched || m_yielding || !moveAnswered || !string.Equals(a: m_stallAuthority, b: read.Endpoint.Authority, comparisonType: StringComparison.Ordinal) ||
            (read.Body.Tick < m_stallTick) || !(read.Position - m_stallOrigin).IsWithin(radius: stallThreshold)) {
            m_stallOrigin = read.Position;
            m_stallTick = read.Body.Tick;
            m_stallAuthority = read.Endpoint.Authority;
        } else if ((read.Body.Tick - m_stallTick) >= StallTicks) {
            Fail(kind: "wall", place: read.Place, reason: string.Create(provider: CultureInfo.InvariantCulture, handler: $"the move stick was held across {StallTicks} delivered ticks and the body stayed within {((double)stallThreshold):0.###} m of ({Metres(m_stallOrigin.X)}, {Metres(m_stallOrigin.Y)}, {Metres(m_stallOrigin.Z)}), on step {m_step} ({Describe(step: m_chapter.Steps[m_step])})"), tick: tick);
            return;
        }
        if (m_checkCorridor && (m_corridor.Count > 0) && ((read.Places.Count == 0) ? !m_corridor.Contains(item: read.Place) : !read.Places.Any(predicate: m_corridor.Contains))) {
            Fail(kind: "corridor", place: read.Place, reason: $"left the corridor ({string.Join(separator: ", ", values: m_corridor.Order(comparer: StringComparer.Ordinal))}) into {read.Place} on step {m_step} ({Describe(step: m_chapter.Steps[m_step])})", tick: tick);
            return;
        }
        if ((m_authorityTicks >= m_authorityBudget) || (tick >= m_deadline)) {
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
        m_commandedSpeed = FixedQ4816.Zero;
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
    private readonly record struct Steer(bool Moves, FixedQ4816 Dx, FixedQ4816 Dz, FixedQ4816 Stop, FixedQ4816 CornerDistance, FixedQ4816 CornerTurn, bool Faces, FixedQ4816 Facing, string Pace, FixedQ4816 Rise = default, FixedQ4816 Climb = default, bool HoldWalk = false);

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
        if ((m_step >= 0) && (m_step < m_chapter!.Steps.Count)) {
            switch (m_chapter.Steps[m_step]) {
                case WorldRouteStep.PressAction:
                    m_settledBehindParty = false;
                    break;
                case WorldRouteStep.PoseBody finishedPose:
                    if (Read is { } poseRead) {
                        var anchorPt = finishedPose.Point;
                        if (finishedPose.Spawn is { } spawnId) {
                            anchorPt = WorldDefinitionRows.FindSpawnPoint(spawnPoints: poseRead.Endpoint.Definition.SpawnPoints, id: spawnId)?.Position;
                        }
                        m_stepAnchorPoint = ((anchorPt is { } at) ? Point(at) : poseRead.Position);
                        m_stepAnchorWorld = WorldKey(read: in poseRead);
                    }
                    break;
                case WorldRouteStep.Go { Point: { } goPt }:
                    if (!m_settledBehindParty && (Read is { } goRead)) {
                        m_stepAnchorPoint = Point(goPt);
                        m_stepAnchorWorld = WorldKey(read: in goRead);
                    } else {
                        m_stepAnchorPoint = null;
                        m_stepAnchorWorld = null;
                    }
                    break;
                case WorldRouteStep.Rest or WorldRouteStep.Line:
                    break;
                default:
                    m_stepAnchorPoint = null;
                    m_stepAnchorWorld = null;
                    break;
            }
        }
        m_step++;
        m_stepStart = tick;
        m_stepAuthorityStart = m_authorityTicks;
        m_yielding = false;
        m_yieldLeaderIndex = -1;
        m_yieldStartBodyTick = 0UL;
        m_enteredGoalPlace = false;
        m_enteredGoalPlaceOrigin = default;
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
        m_poseAttemptAuthorityTick = 0UL;
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
                } else if (!m_settledBehindParty || (press.Until is null)) {
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
        m_poseAttemptAuthorityTick = m_authorityTicks;
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
        m_yielding = false;
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
                        if (
                            !read.Endpoint.ClockOwnedHere &&
                            (m_poseOutcome?.Invoke() == "ran") &&
                            ((m_authorityTicks - m_poseAttemptAuthorityTick) >= ((ulong)(10 * rateHz)))
                        ) {
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
                    if (!m_settledBehindParty) {
                        ArmTap(action: press.Action, read: read);
                    } else {
                        m_tapAwaitingParty = false;
                        m_lastTapAuthorityTick = m_authorityTicks;
                    }
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
                    !m_settledBehindParty &&
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
        // camera's carried heading out of the destination face; before the portal crosses, any lateral nudge off the
        // entry step's anchor point is steered smoothly back onto the crossing ray.
        var (sin, cos) = FixedQ4816.SinCos(angle: read.Yaw);
        var moveDx = -sin;
        var moveDz = -cos;
        var scale = FixedQ4816.Max(x: FixedQ4816.FromDouble(value: 0.05), y: m_senses.BodyScale(reading: in read));

        if (!m_crossedPortal && !cross.Relative) {
            var (headSin, headCos) = FixedQ4816.SinCos(angle: heading);

            moveDx = -headSin;
            moveDz = -headCos;
            if (
                (m_stepAnchorPoint is { } anchor) &&
                string.Equals(a: m_stepAnchorWorld, b: WorldKey(read: in read), comparisonType: StringComparison.Ordinal) &&
                (Planar(from: anchor, to: read.Position) <= (FixedQ4816.FromDouble(value: 12.0) * scale))
            ) {
                var rightX = headCos;
                var rightZ = -headSin;
                var crossTrack = (((read.Position.X - anchor.X) * rightX) + ((read.Position.Z - anchor.Z) * rightZ));

                if (FixedQ4816.Abs(value: crossTrack) > (FixedQ4816.FromDouble(value: 0.08) * scale)) {
                    var corr = FixedQ4816.Clamp(
                        value: (-crossTrack * FixedQ4816.FromDouble(value: 0.85)),
                        minimum: FixedQ4816.FromDouble(value: -0.55),
                        maximum: FixedQ4816.FromDouble(value: 0.55)
                    );
                    var rawDx = (-headSin + (rightX * corr));
                    var rawDz = (-headCos + (rightZ * corr));
                    var rawLen = FixedQ4816.Sqrt(value: ((rawDx * rawDx) + (rawDz * rawDz)));

                    if (rawLen > FixedQ4816.FromDouble(value: 0.001)) {
                        moveDx = (rawDx / rawLen);
                        moveDz = (rawDz / rawLen);
                    }
                }
            }
        }

        return still with {
            Moves = true,
            Dx = moveDx,
            Dz = moveDz,
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
        m_senses.CopySolidWalkers(reading: in read, scratch: m_solidScratch, walkers: m_walkers);
        var scale = FixedQ4816.Max(x: FixedQ4816.FromDouble(value: 0.05), y: m_senses.BodyScale(reading: in read));
        var myRadius = m_senses.PlanarRadius(reading: in read);
        var (holdsPartySync, holdsChapterEnd) = HoldsAfterGo();
        var effectivePoint = default(FixedVector3);
        var effectiveWithin = FixedQ4816.FromDouble(value: go.Within);
        var behindParty = false;

        if (go.Place is { } place) {
            if (read.Places.Contains(value: place)) {
                if ((m_canPressUntil is null) || !holdsChapterEnd) {
                    NextStep(tick: tick, rateHz: rateHz);
                    return still;
                }
                if (!m_enteredGoalPlace) {
                    m_enteredGoalPlace = true;
                    m_enteredGoalPlaceOrigin = read.Position;
                }
                if (
                    (Planar(from: m_enteredGoalPlaceOrigin, to: read.Position) >= (FixedQ4816.FromDouble(value: 1.8) * scale)) ||
                    ((m_corners.Count > 0) && (Planar(from: read.Position, to: m_corners[^1]) <= scale))
                ) {
                    NextStep(tick: tick, rateHz: rateHz);
                    return still;
                }
            }
        } else if (go.Point is { } rawPoint) {
            var authoredPoint = Point(rawPoint);

            effectivePoint = authoredPoint;
            if (m_walkers.Count > 0) {
                if (holdsPartySync && go.Direct) {
                    ResolvePartyHoldSpot(
                        authoredPoint: in authoredPoint,
                        authoredWithin: effectiveWithin,
                        behindParty: out behindParty,
                        effectivePoint: out effectivePoint,
                        effectiveWithin: out effectiveWithin,
                        myRadius: myRadius,
                        read: in read,
                        scale: scale
                    );
                } else if (holdsPartySync || holdsChapterEnd) {
                    var myDist = Planar(from: read.Position, to: authoredPoint);
                    var parkedNear = 0;

                    foreach (var other in m_walkers) {
                        var sep = (myRadius + other.Radius);
                        var otherDist = Planar(from: other.Position, to: authoredPoint);

                        if (
                            (other.PlanarSpeed <= (FixedQ4816.FromDouble(value: 0.35) * scale)) &&
                            (otherDist <= (effectiveWithin + (sep * FixedQ4816.FromInteger(value: 2)))) &&
                            (otherDist < myDist)
                        ) {
                            parkedNear++;
                        }
                    }
                    if (parkedNear > 0) {
                        effectiveWithin += ((myRadius * FixedQ4816.FromDouble(value: 2.2)) * FixedQ4816.FromInteger(value: Math.Min(val1: parkedNear, val2: 2)));
                        behindParty = holdsPartySync;
                    }
                }
            }
            if ((effectivePoint - read.Position).IsWithin(radius: effectiveWithin)) {
                var settleSpeed = (FixedQ4816.FromDouble(value: 0.10) * scale);

                if (
                    read.Endpoint.ClockOwnedHere ||
                    ((double)effectiveWithin > (0.35 * (double)scale)) ||
                    (!m_driving && m_senses.MoveStoppedAnswered(slot: m_slot, bodyTick: read.Body.Tick) && (read.PlanarSpeed <= settleSpeed))
                ) {
                    m_settledBehindParty = behindParty;
                    NextStep(tick: tick, rateHz: rateHz);
                    return still;
                }
                m_progressTick = m_authorityTicks;
                return still;
            }
        }
        if (m_plannedIn != WorldKey(read: in read)) {
            if (!Plan(go: go, read: in read, tick: tick)) {
                return still;
            }
            planned = true;
        }
        if (go.Direct && (go.Point is not null) && (m_corners.Count == 1)) {
            m_corners[0] = effectivePoint;
        }

        // Pass the corners already reached: within reach of one, swept across it during a bunched delivery, or past it
        // along the segment that leads to it.
        var reachScale = FixedQ4816.Min(x: FixedQ4816.One, y: scale);
        var reach = (read.Endpoint.ClockOwnedHere
            ? FixedQ4816.FromDouble(value: 0.4)
            : (FixedQ4816.FromDouble(value: 0.50) * reachScale));
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
                (sweptSegment && (Stray(from: m_lastGoPosition, to: read.Position, point: corner) <= FixedQ4816.Max(x: cornerReach, y: (FixedQ4816.FromDouble(value: 0.35) * reachScale))))
            );
            var passedAlongSegment = (
                hasSegment &&
                (FixedVector3.Dot(left: along, right: past) >= FixedQ4816.Zero) &&
                (read.Endpoint.ClockOwnedHere || (
                    (Planar(from: read.Position, to: corner) <= FixedQ4816.Max(x: cornerReach, y: (FixedQ4816.FromDouble(value: 0.65) * reachScale))) &&
                    (Stray(from: from, to: corner, point: read.Position) <= (FixedQ4816.FromDouble(value: 0.45) * reachScale))
                ))
            );
            if (!withinCorner && !passedAlongSegment) {
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
        var moveAnswered = (m_senses.InputAdmitted(slot: m_slot) && m_senses.MoveInputAnswered(slot: m_slot, bodyTick: read.Body.Tick));
        var final = (m_corner == (m_corners.Count - 1));

        if (
            final &&
            (go.Point is not null) &&
            !holdsPartySync &&
            !holdsChapterEnd &&
            (go.Within < (0.45 * (double)scale)) &&
            (m_walkers.Count > 0) &&
            TryYieldAtPoint(
                authoredPoint: in effectivePoint,
                authoredWithin: effectiveWithin,
                myDist: distance,
                myRadius: myRadius,
                rateHz: rateHz,
                read: in read,
                scale: scale,
                still: still,
                yieldSteer: out var yieldSteer
            )
        ) {
            return yieldSteer;
        }
        m_yieldLeaderIndex = -1;

        // A body that has strayed from its line, or made no progress toward its corner across StuckTicks answered
        // authority ticks, plans again from where it stands; one that keeps making no progress has met something its
        // path did not show.
        if (distance < (m_best - (FixedQ4816.FromDouble(value: 0.05) * scale))) {
            m_best = distance;
            m_progressTick = m_authorityTicks;
        } else if (!moveAnswered) {
            m_progressTick = m_authorityTicks;
        }
        var strayLimit = FixedQ4816.FromDouble(value: (read.Endpoint.ClockOwnedHere ? StrayMetres : 1.6));
        var strayFrom = ((m_corner > 0) ? m_corners[(m_corner - 1)] : m_planStart);
        if (
            !planned &&
            !go.Direct &&
            (Planar(from: strayFrom, to: target) > FixedQ4816.FromDouble(value: 0.25)) &&
            (Stray(from: strayFrom, point: read.Position, to: target) > strayLimit)
        ) {
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
            if (read.Endpoint.ClockOwnedHere && (distance < FixedQ4816.One)) {
                facing = Heading(dx: (next.X - read.Position.X), dz: (next.Z - read.Position.Z));
            }
        }

        var length = FixedQ4816.Max(x: distance, y: FixedQ4816.FromDouble(value: 0.001));
        var moveDx = (dx / length);
        var moveDz = (dz / length);
        var stop = FixedQ4816.MaxValue;

        if (go.Point is not null) {
            var remaining = distance;

            for (var index = m_corner; (index < (m_corners.Count - 1)); index++) {
                remaining += Planar(from: m_corners[index], to: m_corners[(index + 1)]);
            }
            var margin = (read.Endpoint.ClockOwnedHere
                ? (effectiveWithin / FixedQ4816.FromInteger(value: 2))
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

        return still with {
            Moves = true,
            Dx = moveDx,
            Dz = moveDz,
            Stop = stop,
            CornerDistance = (final ? FixedQ4816.MaxValue : distance),
            CornerTurn = turn,
            Faces = true,
            Facing = facing,
            Pace = go.Pace,
            HoldWalk = ((m_corner == 0) && (m_firstReach < reach)),
        };
    }

    private (bool HoldsPartySync, bool HoldsChapterEnd) HoldsAfterGo() {
        var scan = (m_step + 1);

        while ((scan < m_chapter!.Steps.Count) && (m_chapter.Steps[scan] is WorldRouteStep.Rest or WorldRouteStep.Line)) {
            scan++;
        }
        if (scan >= m_chapter.Steps.Count) {
            return (HoldsPartySync: false, HoldsChapterEnd: true);
        }
        if ((m_canPressUntil is not null) && (m_chapter.Steps[scan] is WorldRouteStep.PressAction { Until: not null })) {
            return (HoldsPartySync: true, HoldsChapterEnd: false);
        }

        return (HoldsPartySync: false, HoldsChapterEnd: false);
    }

    private void ResolvePartyHoldSpot(
        in FixedVector3 authoredPoint,
        FixedQ4816 authoredWithin,
        in WorldSeatSenses.Reading read,
        FixedQ4816 scale,
        FixedQ4816 myRadius,
        out FixedVector3 effectivePoint,
        out FixedQ4816 effectiveWithin,
        out bool behindParty
    ) {
        var tieEps = (FixedQ4816.FromDouble(value: 0.05) * scale);
        var myGoalDist = Planar(from: read.Position, to: authoredPoint);
        var claimRadius = (authoredWithin + (myRadius * FixedQ4816.FromInteger(value: 2)));
        var goalClaimed = false;

        foreach (var other in m_walkers) {
            var otherDist = Planar(from: other.Position, to: authoredPoint);
            var otherLeads = (((otherDist + tieEps) < myGoalDist) || ((FixedQ4816.Abs(value: (otherDist - myGoalDist)) <= tieEps) && (other.Index < read.Index)));

            if ((otherDist <= claimRadius) && otherLeads) {
                goalClaimed = true;
                break;
            }
        }
        if (!goalClaimed) {
            effectivePoint = authoredPoint;
            effectiveWithin = authoredWithin;
            behindParty = false;
            return;
        }

        var anchor = (((m_step > 0) && (m_chapter!.Steps[(m_step - 1)] is WorldRouteStep.Go { Point: { } prevPt })) ? Point(point: prevPt) : ((m_plannedIn == WorldKey(read: in read)) ? m_planStart : read.Position));
        var entryDir = new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One);

        if ((m_step > 1) && (m_chapter!.Steps[(m_step - 2)] is WorldRouteStep.Go { Point: { } doorPt }) && (Planar(from: Point(point: doorPt), to: anchor) > (FixedQ4816.FromDouble(value: 0.1) * scale))) {
            entryDir = PlanarUnit(from: Point(point: doorPt), to: anchor);
        } else if (Planar(from: anchor, to: authoredPoint) > (FixedQ4816.FromDouble(value: 0.1) * scale)) {
            entryDir = PlanarUnit(from: anchor, to: authoredPoint);
        }

        var entrySide = new FixedVector3(X: entryDir.Z, Y: FixedQ4816.Zero, Z: -entryDir.X);
        var offset = (authoredPoint - anchor);
        var openSide = ((((offset.X * entrySide.X) + (offset.Z * entrySide.Z)) >= FixedQ4816.Zero)
            ? new FixedVector3(X: -entrySide.X, Y: FixedQ4816.Zero, Z: -entrySide.Z)
            : entrySide);
        var spot1 = new FixedVector3(
            X: (anchor.X + (entryDir.X * (FixedQ4816.FromDouble(value: 0.75) * scale)) + (openSide.X * (FixedQ4816.FromDouble(value: 0.55) * scale))),
            Y: authoredPoint.Y,
            Z: (anchor.Z + (entryDir.Z * (FixedQ4816.FromDouble(value: 0.75) * scale)) + (openSide.Z * (FixedQ4816.FromDouble(value: 0.55) * scale)))
        );
        var spot2 = new FixedVector3(
            X: (anchor.X + (entryDir.X * (FixedQ4816.FromDouble(value: 0.45) * scale)) - (openSide.X * (FixedQ4816.FromDouble(value: 0.65) * scale))),
            Y: authoredPoint.Y,
            Z: (anchor.Z + (entryDir.Z * (FixedQ4816.FromDouble(value: 0.45) * scale)) - (openSide.Z * (FixedQ4816.FromDouble(value: 0.65) * scale)))
        );
        var spot3 = (anchor with { Y = authoredPoint.Y });

        behindParty = true;
        if (!SpotClaimed(myRadius: myRadius, read: in read, scale: scale, spot: in spot1, tieEps: tieEps)) {
            effectivePoint = spot1;
            effectiveWithin = (FixedQ4816.FromDouble(value: 0.25) * scale);
        } else if (!SpotClaimed(myRadius: myRadius, read: in read, scale: scale, spot: in spot2, tieEps: tieEps)) {
            effectivePoint = spot2;
            effectiveWithin = (FixedQ4816.FromDouble(value: 0.25) * scale);
        } else {
            effectivePoint = spot3;
            effectiveWithin = (SpotClaimed(myRadius: myRadius, read: in read, scale: scale, spot: in spot3, tieEps: tieEps)
                ? (FixedQ4816.FromDouble(value: 0.45) * scale)
                : (FixedQ4816.FromDouble(value: 0.35) * scale));
        }
    }

    private bool SpotClaimed(in FixedVector3 spot, in WorldSeatSenses.Reading read, FixedQ4816 scale, FixedQ4816 myRadius, FixedQ4816 tieEps) {
        var myDist = Planar(from: read.Position, to: spot);

        foreach (var other in m_walkers) {
            var clearance = ((myRadius + other.Radius) - (FixedQ4816.FromDouble(value: 0.05) * scale));
            var otherDist = Planar(from: other.Position, to: spot);
            var otherLeads = (((otherDist + tieEps) < myDist) || ((FixedQ4816.Abs(value: (otherDist - myDist)) <= tieEps) && (other.Index < read.Index)));

            if ((otherDist <= clearance) && otherLeads) {
                return true;
            }
        }

        return false;
    }

    private bool TryYieldAtPoint(
        in FixedVector3 authoredPoint,
        FixedQ4816 authoredWithin,
        FixedQ4816 myDist,
        FixedQ4816 myRadius,
        in WorldSeatSenses.Reading read,
        FixedQ4816 scale,
        int rateHz,
        Steer still,
        out Steer yieldSteer
    ) {
        var tieEps = (FixedQ4816.FromDouble(value: 0.05) * scale);

        foreach (var other in m_walkers) {
            var sep = (myRadius + other.Radius);
            var blockRadius = FixedQ4816.Max(x: authoredWithin, y: (sep - (FixedQ4816.FromDouble(value: 0.08) * scale)));
            var approachZone = (blockRadius + (FixedQ4816.FromDouble(value: 1.15) * scale));

            if (myDist > approachZone) {
                continue;
            }

            var otherDist = Planar(from: other.Position, to: authoredPoint);
            var otherLeads = (((otherDist + tieEps) < myDist) || ((FixedQ4816.Abs(value: (otherDist - myDist)) <= tieEps) && (other.Index < read.Index)));

            if (!otherLeads) {
                continue;
            }

            var approaching = false;

            if ((otherDist <= approachZone) && (otherDist > FixedQ4816.FromDouble(value: 0.01))) {
                var toGoal = PlanarUnit(from: other.Position, to: authoredPoint);
                var speedToward = ((other.Velocity.X * toGoal.X) + (other.Velocity.Z * toGoal.Z));

                approaching = (speedToward > (FixedQ4816.FromDouble(value: 0.25) * scale));
            }
            if ((otherDist > blockRadius) && !approaching) {
                continue;
            }
            if (m_yieldLeaderIndex != other.Index) {
                m_yieldLeaderIndex = other.Index;
                m_yieldStartBodyTick = read.Body.Tick;
            } else if ((read.Body.Tick > m_yieldStartBodyTick) && ((read.Body.Tick - m_yieldStartBodyTick) >= ((ulong)(8 * rateHz)))) {
                continue;
            }

            m_yielding = true;
            m_progressTick = m_authorityTicks;
            m_stallOrigin = read.Position;
            m_stallTick = read.Body.Tick;
            m_stallAuthority = read.Endpoint.Authority;
            var clearance = (sep + (FixedQ4816.FromDouble(value: 0.15) * scale));

            if (myDist < clearance) {
                var away = ((myDist > FixedQ4816.FromDouble(value: 0.01))
                    ? PlanarUnit(from: authoredPoint, to: read.Position)
                    : new FixedVector3(X: (((read.Index & 1) == 0) ? FixedQ4816.One : -FixedQ4816.One), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));

                yieldSteer = still with {
                    Moves = true,
                    Dx = away.X,
                    Dz = away.Z,
                    Stop = (FixedQ4816.FromDouble(value: 0.45) * scale),
                    CornerDistance = FixedQ4816.MaxValue,
                    Faces = true,
                    Facing = Heading(dx: -away.X, dz: -away.Z),
                    Pace = "walk",
                };
                return true;
            }

            yieldSteer = still with {
                Faces = true,
                Facing = Heading(dx: (authoredPoint.X - read.Position.X), dz: (authoredPoint.Z - read.Position.Z)),
            };
            return true;
        }

        yieldSteer = default;
        return false;
    }

    private static FixedVector3 PlanarUnit(in FixedVector3 from, in FixedVector3 to) {
        var dx = (to.X - from.X);
        var dz = (to.Z - from.Z);
        var len = FixedQ4816.Sqrt(value: ((dx * dx) + (dz * dz)));

        return ((len > FixedQ4816.FromDouble(value: 0.001))
            ? new FixedVector3(X: (dx / len), Y: FixedQ4816.Zero, Z: (dz / len))
            : new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One));
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
        m_settledBehindParty = false;
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

        var scale = FixedQ4816.Max(x: FixedQ4816.FromDouble(value: 0.05), y: m_senses.BodyScale(reading: in read));
        var reachScale = FixedQ4816.Min(x: FixedQ4816.One, y: scale);
        var corridorMargin = (read.Endpoint.ClockOwnedHere
            ? m_margin
            : FixedQ4816.Max(x: m_margin, y: (FixedQ4816.FromDouble(value: 0.45) * reachScale)));
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
            m_firstReach = (read.Endpoint.ClockOwnedHere
                ? FixedQ4816.FromDouble(value: 0.1)
                : (FixedQ4816.FromDouble(value: (stuck ? 0.15 : 0.55)) * reachScale));
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
            if (corridorMargin > FixedQ4816.Zero) {
                AddPlacePoint(point: sample with { X = (sample.X - corridorMargin) });
                AddPlacePoint(point: sample with { X = (sample.X + corridorMargin) });
                AddPlacePoint(point: sample with { Z = (sample.Z - corridorMargin) });
                AddPlacePoint(point: sample with { Z = (sample.Z + corridorMargin) });
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
        m_driving = false;
        m_commandedSpeed = FixedQ4816.Zero;

        // The seat turns its camera by this tick's look before it composes this tick's move, so the move is framed
        // against the facing the turn leaves, unless third-person movement holds a latched camera frame yaw.
        var facing = (read.LatchedMoveYaw ?? read.Yaw);

        if (read.LatchedMoveYaw is null) {
            foreach (var operation in m_program.Operations) {
                if (operation is WorldSeatProgramOp.Turn turn) {
                    facing += (TurnDeflection(read: in read, steer: in steer, turn: turn) * read.YawPerTick);
                }
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
        switch (operation) {
            case WorldSeatProgramOp.Move move: {
                if (!steer.Moves) {
                    return "0 0";
                }

                var walking = WalkingSpeed(read: in read);
                var magnitude = FixedQ4816.One;

                if (read.Endpoint.ClockOwnedHere) {
                    // Slow for a sharp corner, or to stop at a final point, against the speed the body was delivered at.
                    var allowed = AllowedSpeed(move: move, read: in read, steer: in steer);

                    if ((allowed != FixedQ4816.MaxValue) && (read.PlanarSpeed > allowed)) {
                        magnitude = FixedQ4816.Max(x: FixedQ4816.FromDouble(value: move.Least), y: (allowed / read.PlanarSpeed));
                    }
                    m_commandedSpeed = walking;
                } else {
                    // A remote authority applies a stick a control lag after it is sent: the stick asks for the speed
                    // itself, never a fraction of a delivered speed that is already a lag old, so a body at rest beside
                    // its goal is not sent off at full deflection.
                    var allowed = AllowedSpeed(move: move, read: in read, steer: in steer);
                    var top = (Running(read: in read, steer: in steer) ? RunningSpeed(read: in read) : walking);

                    if (allowed < top) {
                        magnitude = (allowed / top);
                    }
                    m_commandedSpeed = FixedQ4816.Min(x: allowed, y: top);
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
                return ((steer.Moves && hold.Paces.Contains(value: steer.Pace) && (read.Endpoint.ClockOwnedHere || Running(read: in read, steer: in steer))) ? "press" : "release");
            case WorldSeatProgramOp.Pulse pulse: {
                var period = Math.Max(val1: 2UL, val2: (ulong)Math.Round(a: (pulse.PeriodSeconds * rateHz)));
                var pulseElapsed = (read.Endpoint.ClockOwnedHere ? (tick - m_stepStart) : (m_authorityTicks - m_stepAuthorityStart));

                return ((steer.Moves && pulse.Paces.Contains(value: steer.Pace) && CanDash(read: in read, steer: in steer) && ((pulseElapsed % period) == 0UL)) ? "press" : "release");
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

    // Whether a remote seat holds its run: the step's pace runs and the speed it allows is past a walk. The stick then
    // asks for its speed against the running speed, so running is only a higher top speed and never outruns a stop.
    private bool Running(in WorldSeatSenses.Reading read, in Steer steer) {
        if (steer.HoldWalk || !steer.Moves) {
            return false;
        }
        foreach (var operation in m_program.Operations) {
            if ((operation is WorldSeatProgramOp.Hold hold) && hold.Paces.Contains(value: steer.Pace)) {
                return (ProgramAllowedSpeed(read: in read, steer: in steer) > WalkingSpeed(read: in read));
            }
        }

        return false;
    }

    // Whether a dash may be pulsed: always against a local clock; against a remote one only while the step allows at
    // least twice the running speed, since a dash's burst cannot be taken back a lag later.
    private bool CanDash(in WorldSeatSenses.Reading read, in Steer steer) =>
        (read.Endpoint.ClockOwnedHere || (!steer.HoldWalk && (ProgramAllowedSpeed(read: in read, steer: in steer) >= (RunningSpeed(read: in read) * FixedQ4816.FromInteger(value: 2)))));

    private FixedQ4816 ProgramAllowedSpeed(in WorldSeatSenses.Reading read, in Steer steer) {
        foreach (var operation in m_program.Operations) {
            if (operation is WorldSeatProgramOp.Move move) {
                return AllowedSpeed(move: move, read: in read, steer: in steer);
            }
        }

        return FixedQ4816.MaxValue;
    }

    // The body's walking speed at full deflection, in metres per second: its kit's authored speed at its live scale.
    private FixedQ4816 WalkingSpeed(in WorldSeatSenses.Reading read) {
        var kits = read.Endpoint.Definition.Kits;
        var speed = ((read.Body.Kit < kits.Count) ? kits[read.Body.Kit].Motion.Speed.Value : 5f);

        return (FixedQ4816.FromDouble(value: Math.Max(val1: speed, val2: 0.1f)) * FixedQ4816.Max(x: FixedQ4816.FromDouble(value: 0.05), y: m_senses.BodyScale(reading: in read)));
    }

    // The body's speed at full deflection with its held speed channel down: the walking speed times the kit's held
    // multiplier, or the walking speed for a kit with none.
    private FixedQ4816 RunningSpeed(in WorldSeatSenses.Reading read) {
        var kits = read.Endpoint.Definition.Kits;
        var multiplier = (((read.Body.Kit < kits.Count) && (kits[read.Body.Kit].Motion.Speed.Held is { Multiplier: > 0f } held)) ? held.Multiplier : 1f);

        return (WalkingSpeed(read: in read) * FixedQ4816.FromDouble(value: multiplier));
    }

    // The control lag, in seconds, a remote approach plans against: the latest round trip from sending input to seeing
    // it answered, or the longest seen lately when that is longer, decaying by eight ticks each second of the
    // authority's time. A burst after a held delivery is random, so the approach assumes the worst it has recently met.
    private (FixedQ4816 Latest, FixedQ4816 Peak) ControlLag(in WorldSeatSenses.Reading read) {
        var rate = Math.Max(val1: 1, val2: read.Endpoint.Definition.SimulationRateHz);
        var step = (ulong)Math.Max(val1: 1, val2: (rate / 8));
        var observed = Math.Clamp(value: m_senses.MoveLagTicks(slot: m_slot, bodyTick: read.Body.Tick), min: 2UL, max: 90UL);

        if (!string.Equals(a: m_lagAuthority, b: read.Endpoint.Authority, comparisonType: StringComparison.Ordinal) || (read.Body.Tick < m_lagTick)) {
            m_lagAuthority = read.Endpoint.Authority;
            m_lagTick = read.Body.Tick;
        }
        var decayed = ((read.Body.Tick - m_lagTick) / step);

        if (decayed > 0UL) {
            m_lagPeak = ((m_lagPeak > decayed) ? (m_lagPeak - decayed) : 0UL);
            m_lagTick += (decayed * step);
        }
        m_lagPeak = Math.Max(val1: m_lagPeak, val2: observed);

        return (Latest: FixedQ4816.FromDouble(value: ((double)observed / rate)), Peak: FixedQ4816.FromDouble(value: ((double)m_lagPeak / rate)));
    }

    // The fastest planar speed, in metres per second, the step lets the body hold now: braking at the program's rate
    // it still slows to the corner's speed before a sharp corner and stops at a final point. Against a remote clock the
    // braking starts a control lag (the authority ticks between sending input and seeing it answered) late: the latest
    // lag for a corner, which a late turn only widens, and the longest lag met lately for a stop, which a tight goal
    // must not overshoot. Near a stop the speed is also held to half the speed that closes the distance left in one
    // lag, so a late or bunched delivery overshoots by less than the distance it had to cover and the approach
    // converges whatever the lag. MaxValue when nothing ahead limits it.
    private FixedQ4816 AllowedSpeed(WorldSeatProgramOp.Move move, in WorldSeatSenses.Reading read, in Steer steer) {
        var brake = FixedQ4816.FromDouble(value: move.Brake);
        var (turnLag, lag) = (read.Endpoint.ClockOwnedHere ? (FixedQ4816.Zero, FixedQ4816.Zero) : ControlLag(read: in read));
        var late = (brake * lag);
        var allowed = FixedQ4816.MaxValue;

        if ((steer.CornerDistance != FixedQ4816.MaxValue) && (steer.CornerTurn > FixedQ4816.FromDegrees(degrees: move.CornerDegrees))) {
            var corner = (FixedQ4816.FromDouble(value: move.CornerSpeed) * FixedQ4816.Max(x: FixedQ4816.FromDouble(value: 0.05), y: m_senses.BodyScale(reading: in read)));
            var turnLate = (brake * turnLag);

            allowed = (FixedQ4816.Sqrt(value: (((turnLate * turnLate) + (corner * corner)) + ((FixedQ4816.FromInteger(value: 2) * brake) * steer.CornerDistance))) - turnLate);
        }
        if (steer.Stop != FixedQ4816.MaxValue) {
            var stop = (FixedQ4816.Sqrt(value: ((late * late) + ((FixedQ4816.FromInteger(value: 2) * brake) * steer.Stop))) - late);

            if (lag > FixedQ4816.Zero) {
                stop = FixedQ4816.Min(x: stop, y: (steer.Stop / (lag * FixedQ4816.FromInteger(value: 2))));
            }
            allowed = FixedQ4816.Min(x: allowed, y: stop);
        }

        return allowed;
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
