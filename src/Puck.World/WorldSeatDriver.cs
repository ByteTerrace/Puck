using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.Maths;
using Puck.Physics.Navigation;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// Plays a route's chapters for one seat with the route's authored seat program (<see cref="WorldSeatProgram"/>):
/// each tick, in the authority tick-complete hook, it reads the seat body's authoritative pose, places and contact
/// witness in whichever world the seat stands in, takes the next steering point on the step's path, evaluates the
/// program's operations into the seat's controls and submits every control that changed through the seat's own
/// ingress (<see cref="WorldTickScript.SubmitSeat"/>, the door <c>player.signal</c> uses). Load therefore runs through
/// the router, the bindings and intent composition, and a shed tick, a scale change or a nudge is read back on the
/// next tick and steered from rather than carried off course.
/// </summary>
/// <remarks>
/// <para>Deterministic given the same start state: every decision reads the authority's fixed-point state, the seat
/// view's facing (advanced once per tick) and tick counts, and every control it submits lands in the tick after the one
/// it read.</para>
/// <para>A chapter fails where a player would see it fail: on a driven contact with a wall (the body's latched
/// obstruction witness), by standing in none of the places its path crosses, by losing its path, or by running past
/// its time. Every member runs on the host pump.</para>
/// </remarks>
internal sealed class WorldSeatDriver {
    /// <summary>The ticks between re-assertions of a held stick, as a player holding it across a crossing presses again.</summary>
    public const int ReassertTicks = 15;
    // The distance from its line, in metres, past which a body that strayed plans its path again; the no-progress span,
    // in ticks, after which it does; and the plans in a row that may make no progress before the step fails.
    private const double StrayMetres = 1.0;
    private const int StuckTicks = 60;
    private const int MaxStuckPlans = 3;
    // The ticks a pose and a line are given to land before the next step reads the world.
    private const int LandTicks = 2;
    // The heading error, in degrees, a crossing turns inside before it walks, and the ticks it gives the turn.
    private const double AlignDegrees = 3.0;
    private const int AlignTicks = 45;

    private enum Phase { Idle, Running, Arrived, Failed }

    private readonly WorldTickScript m_script;
    private readonly Func<InputRouter> m_router;
    private readonly WorldInstanceHost m_instances;
    private readonly WorldSeatAuthorityRouter m_routes;
    private readonly WorldClient m_client;
    private readonly WorldRoute m_route;
    private readonly WorldSeatProgram m_program;
    private readonly int m_slot;
    private readonly FixedQ4816 m_margin;
    private readonly Dictionary<string, string> m_sent = new(comparer: StringComparer.Ordinal);
    private readonly List<NavigationRouteNode> m_nodes = [];
    private readonly List<NavigationRouteNode> m_cornerNodes = [];
    private readonly List<FixedVector3> m_corners = [];
    private readonly HashSet<string> m_corridor = new(comparer: StringComparer.Ordinal);
    private readonly List<(ulong Tick, string Place)> m_visited = [];
    // The chapter's submitted control lines, whose outcomes arrive as their ticks apply.
    private readonly List<WorldTickScript.Line> m_lines = [];

    private WorldRouteChapter? m_chapter;
    private Phase m_phase;
    private int m_step;
    private ulong m_chapterStart;
    private ulong m_stepStart;
    private ulong m_deadline;
    private string? m_routeWorld;
    private int m_routeIndex = -1;
    private ulong m_lastAssert;
    // The step's path: its steering corners and the one steered at, whether a corridor is checked, the world it was
    // planned in, and the progress watch.
    private int m_corner;
    private FixedQ4816 m_firstReach;
    private bool m_checkCorridor;
    private string? m_plannedIn;
    private FixedQ4816 m_best;
    private ulong m_progressTick;
    private int m_stuckPlans;
    // A crossing's phase and its distance walked; a press's tap.
    private bool m_pushing;
    private FixedQ4816? m_heading;
    private FixedQ4816 m_travelled;
    private FixedVector3 m_lastPosition;
    private string? m_tap;
    private bool m_driving;

    /// <summary>Initializes a new instance of the <see cref="WorldSeatDriver"/> class for one route.</summary>
    /// <param name="route">The route.</param>
    /// <param name="program">The route's seat program.</param>
    /// <param name="script">The tick script whose seat door the controls enter by.</param>
    /// <param name="router">Resolves the live seat router.</param>
    /// <param name="instances">The instances the seat may stand in.</param>
    /// <param name="routes">The seat's authority routes.</param>
    /// <param name="client">The presentation client whose seat view holds the camera's facing.</param>
    public WorldSeatDriver(WorldRoute route, WorldSeatProgram program, WorldTickScript script, Func<InputRouter> router, WorldInstanceHost instances, WorldSeatAuthorityRouter routes, WorldClient client) {
        m_route = route;
        m_program = program;
        m_script = script;
        m_router = router;
        m_instances = instances;
        m_routes = routes;
        m_client = client;
        m_slot = (route.Seat - 1);
        m_margin = FixedQ4816.FromDouble(value: route.Margin);
    }

    /// <summary>Gets the 0-based body index the seat's body holds in the world it stands in, or the seat's slot when it
    /// stands in none.</summary>
    public int Body => ((m_routes.TryRoute(slot: m_slot) is { } route) ? route.EntityIndex : m_slot);
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
    public int Refused => m_lines.Count(predicate: static line => line.Outcome is "refused" or "faulted");
    /// <summary>Gets the control lines submitted.</summary>
    public int Submitted => m_lines.Count;
    /// <summary>Gets every innermost place the body entered, in order, each with the tick it was first read in.</summary>
    public IReadOnlyList<(ulong Tick, string Place)> Visited => m_visited;
    /// <summary>Gets whether the chapter has ended, arrived or failed.</summary>
    public bool Ended => (m_phase is Phase.Arrived or Phase.Failed);
    /// <summary>Gets whether the chapter arrived: every step finished and nothing failed it.</summary>
    public bool Arrived => (m_phase == Phase.Arrived);

    /// <summary>Starts a chapter on the tick just completed: its first step reads the world in this tick's hook.</summary>
    /// <param name="chapter">The chapter.</param>
    /// <param name="tick">The completed tick.</param>
    /// <param name="rateHz">The simulation rate, in ticks per second.</param>
    public void Begin(WorldRouteChapter chapter, ulong tick, int rateHz) {
        m_chapter = chapter;
        m_phase = Phase.Running;
        m_step = -1;
        m_chapterStart = tick;
        m_deadline = (tick + (ulong)Math.Ceiling(a: (chapter.TimeoutSeconds * rateHz)));
        m_visited.Clear();
        Failure = null;
        FailureKind = null;
        FailurePlace = "none";
        Plans = 0;
        m_lines.Clear();
        m_routeWorld = null;
        m_routeIndex = -1;
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
        if (!TryRead(read: out var read)) {
            // A seat between authorities (a crossing in flight) has no body to read for a tick or two; it holds its
            // controls and fails only when that outlasts the chapter.
            Fail(kind: "seat", place: "none", reason: "the seat has no body in any world", tick: tick, when: (tick >= m_deadline));
            return;
        }

        Visit(read: in read, tick: tick);
        if (m_driving && (read.Body.LastObstructionNormal != FixedVector3.Zero)) {
            var normal = read.Body.LastObstructionNormal;

            Fail(kind: "wall", place: read.Place, reason: string.Create(provider: CultureInfo.InvariantCulture, handler: $"a driven wall contact at ({Metres(read.Position.X)}, {Metres(read.Position.Y)}, {Metres(read.Position.Z)}), normal ({Metres(normal.X)}, {Metres(normal.Y)}, {Metres(normal.Z)}), on step {m_step} ({Describe(step: m_chapter.Steps[m_step])})"), tick: tick);
            return;
        }
        if (m_checkCorridor && (m_corridor.Count > 0) && ((read.Places.Count == 0) ? !m_corridor.Contains(item: read.Place) : !read.Places.Any(predicate: m_corridor.Contains))) {
            Fail(kind: "corridor", place: read.Place, reason: $"left the corridor ({string.Join(separator: ", ", values: m_corridor.Order(comparer: StringComparer.Ordinal))}) into {read.Place} on step {m_step} ({Describe(step: m_chapter.Steps[m_step])})", tick: tick);
            return;
        }
        if (tick >= m_deadline) {
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
    }

    /// <summary>Describes a step canonically, every member named, the text a chapter's identity hashes.</summary>
    /// <param name="step">The step.</param>
    /// <returns>The description.</returns>
    public static string Describe(WorldRouteStep step) => step switch {
        WorldRouteStep.PoseBody pose => (pose.Spawn is { } spawn) ? $"pose spawn {spawn}" : $"pose {Vector(pose.Point)} yaw {Number(pose.YawDegrees)}",
        WorldRouteStep.Line line => $"line {line.Text}",
        WorldRouteStep.Go go => $"go {(go.Place ?? Vector(go.Point))} pace {go.Pace}{(go.Direct ? " direct" : string.Empty)} within {Number(go.Within)}{((go.Corridor is { Count: > 0 } corridor) ? $" corridor {string.Join(separator: ',', values: corridor)}" : string.Empty)}",
        WorldRouteStep.Cross cross => $"cross {Number(cross.HeadingDegrees)}{(cross.Relative ? " relative" : string.Empty)}{((cross.Until is { } until) ? $" until {until}" : string.Empty)} pace {cross.Pace} metres {Number(cross.Metres)}",
        WorldRouteStep.Rest rest => $"rest {Number(rest.Seconds)}{((rest.FaceDegrees is { } face) ? $" face {Number(face)}" : string.Empty)}{((rest.LookAt is { } look) ? $" look {Vector(look)}" : string.Empty)}",
        WorldRouteStep.PressAction press => $"press {press.Action}{((press.Until is { } until) ? $" until {until}" : string.Empty)} seconds {Number(press.Seconds)}",
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
                _ => "unknown",
            }).Append(value: '\n');
        }

        return text.ToString();
    }

    // What the seat's body is this tick, in the world it stands in.
    private readonly record struct SeatRead(WorldServer Server, string World, int Index, WorldBody Body, FixedVector3 Position, IReadOnlyList<string> Places, string Place, FixedQ4816 Yaw, FixedQ4816 YawPerTick);

    // What the step asks of the controls this tick: a direction to travel (none to stand), whether it ends at a point to
    // stop at and how far that is, the next corner's distance and turn, the facing to turn the camera onto, and the pace.
    private readonly record struct Steer(bool Moves, FixedQ4816 Dx, FixedQ4816 Dz, FixedQ4816 Stop, FixedQ4816 CornerDistance, FixedQ4816 CornerTurn, bool Faces, FixedQ4816 Facing, string Pace);

    private bool TryRead(out SeatRead read) {
        read = default;
        if ((m_routes.TryRoute(slot: m_slot) is not { } route) ||
            !m_instances.TryGet(instance: out var instance, name: route.Endpoint.Identity) ||
            (instance?.Server is not { } server) ||
            (server.Population.EntryBody(index: route.EntityIndex) is not { } body) ||
            !m_client.TrySeatLook(slot: m_slot, yaw: out var yaw, yawPerTick: out var perTick)) {
            return false;
        }

        var places = server.Events.PlacesOf(body: route.EntityIndex);

        read = new SeatRead(
            Body: body,
            Index: route.EntityIndex,
            Place: ((places.Count == 0) ? "none" : places[0]),
            Places: places,
            Position: body.FixedPosition,
            Server: server,
            World: route.Endpoint.Identity,
            Yaw: FixedQ4816.FromDouble(value: yaw),
            YawPerTick: FixedQ4816.FromDouble(value: perTick)
        );
        return true;
    }

    private void Visit(in SeatRead read, ulong tick) {
        var place = ((read.World == WorldInstanceHost.BootInstanceName) ? read.Place : $"{read.World}:{read.Place}");

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
        m_step++;
        m_stepStart = tick;
        m_checkCorridor = false;
        m_corridor.Clear();
        m_corners.Clear();
        m_corner = 0;
        m_plannedIn = null;
        m_stuckPlans = 0;
        m_pushing = false;
        m_heading = null;
        m_travelled = FixedQ4816.Zero;
        m_tap = null;
        if (m_step >= m_chapter!.Steps.Count) {
            m_phase = Phase.Arrived;
            Release();
            return;
        }

        switch (m_chapter.Steps[m_step]) {
            case WorldRouteStep.PoseBody pose:
                Submit(text: (pose.Spawn is { } spawn)
                    ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"body.pose spawn:{spawn} {Body}")
                    : string.Create(provider: CultureInfo.InvariantCulture, handler: $"body.pose {Number(pose.Point!.Value.X)} {Number(pose.Point.Value.Y)} {Number(pose.Point.Value.Z)} {Number(pose.YawDegrees)} 0 0 {Body}"));
                break;
            case WorldRouteStep.Line line:
                Submit(text: line.Text);
                break;
            case WorldRouteStep.PressAction press:
                m_tap = press.Action;
                break;
        }
    }

    // Runs the current step from this tick's read and returns what it asks of the controls; a finished step hands on to
    // the next, which reads the world from the next tick.
    private Steer Step(in SeatRead read, ulong tick, int rateHz) {
        var elapsed = (tick - m_stepStart);
        var step = m_chapter!.Steps[m_step];
        var still = new Steer(Moves: false, Dx: default, Dz: default, Stop: default, CornerDistance: default, CornerTurn: default, Faces: false, Facing: default, Pace: string.Empty);

        switch (step) {
            case WorldRouteStep.PoseBody pose:
                if (elapsed >= LandTicks) {
                    var point = pose.Point;
                    if (pose.Spawn is { } id) {
                        point = WorldDefinitionRows.FindSpawnPoint(spawnPoints: read.Server.Definition.SpawnPoints, id: id)?.Position;
                    }

                    // A pose that did not land (refused, or overtaken by a crossing in flight) leaves the chapter
                    // starting somewhere its author did not mean.
                    if ((point is not { } at) || ((Point(at) - read.Position).LengthSquared > FixedQ4816.One)) {
                        Fail(kind: "pose", place: read.Place, reason: $"the body is not where step {m_step} ({Describe(step: pose)}) set it down", tick: tick);
                        return still;
                    }
                    NextStep(tick: tick, rateHz: rateHz);
                }
                return still;
            case WorldRouteStep.Line:
                if (elapsed >= LandTicks) {
                    NextStep(tick: tick, rateHz: rateHz);
                }
                return still;
            case WorldRouteStep.Rest rest: {
                if (elapsed >= (ulong)Math.Ceiling(a: (rest.Seconds * rateHz))) {
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
                if (elapsed >= 1UL) {
                    m_tap = null;
                }
                if ((press.Until is { } until) && read.Server.Events.IsOccupant(body: read.Index, placementId: until)) {
                    NextStep(tick: tick, rateHz: rateHz);
                } else if (elapsed >= (ulong)Math.Ceiling(a: (press.Seconds * rateHz))) {
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
            default:
                NextStep(tick: tick, rateHz: rateHz);
                return still;
        }
    }

    private Steer Cross(WorldRouteStep.Cross cross, ulong elapsed, in SeatRead read, Steer still, ulong tick, int rateHz) {
        if ((cross.Until is { } until) && read.Server.Events.IsOccupant(body: read.Index, placementId: until)) {
            NextStep(tick: tick, rateHz: rateHz);
            return still;
        }

        var heading = (m_heading ??= FixedQ4816.WrapAngle(radians: (FixedQ4816.FromDegrees(degrees: cross.HeadingDegrees) + (cross.Relative ? read.Yaw : FixedQ4816.Zero))));

        if (!m_pushing) {
            var error = FixedQ4816.Abs(value: FixedQ4816.WrapAngle(radians: (heading - read.Yaw)));

            m_lastPosition = read.Position;
            if ((error > FixedQ4816.FromDegrees(degrees: AlignDegrees)) && (elapsed < AlignTicks)) {
                return still with { Faces = true, Facing = heading };
            }
            m_pushing = true;
        }

        var moved = (read.Position - m_lastPosition);
        var step = FixedQ4816.Sqrt(value: ((moved.X * moved.X) + (moved.Z * moved.Z)));

        // A portal moves the body across the world in one tick; that jump is no distance walked.
        if (step < FixedQ4816.FromDouble(value: 1.5)) {
            m_travelled += step;
        }
        m_lastPosition = read.Position;
        if (m_travelled >= FixedQ4816.FromDouble(value: cross.Metres)) {
            if (cross.Until is null) {
                NextStep(tick: tick, rateHz: rateHz);
            } else {
                Fail(kind: "route", place: read.Place, reason: string.Create(provider: CultureInfo.InvariantCulture, handler: $"walked {cross.Metres:0.#} m on heading {cross.HeadingDegrees:0.#} without reaching {cross.Until}"), tick: tick);
            }
            return still;
        }

        // Straight ahead in the camera's own frame, whatever a crossing did to the world's axes.
        var (sin, cos) = FixedQ4816.SinCos(angle: read.Yaw);

        return still with { Moves = true, Dx = -sin, Dz = -cos, Stop = FixedQ4816.MaxValue, CornerDistance = FixedQ4816.MaxValue, Pace = cross.Pace };
    }

    private Steer Go(WorldRouteStep.Go go, in SeatRead read, Steer still, ulong tick, int rateHz, bool planned = false) {
        if (go.Place is { } place) {
            if (read.Server.Events.IsOccupant(body: read.Index, placementId: place)) {
                NextStep(tick: tick, rateHz: rateHz);
                return still;
            }
        } else if ((Point(go.Point!.Value) - read.Position).IsWithin(radius: FixedQ4816.FromDouble(value: go.Within))) {
            NextStep(tick: tick, rateHz: rateHz);
            return still;
        }
        if (m_plannedIn != read.World) {
            if (!Plan(go: go, read: in read, tick: tick)) {
                return still;
            }
            planned = true;
        }

        // Pass the corners already reached: within reach of one, or past it along the segment that leads to it.
        var reach = FixedQ4816.FromDouble(value: 0.4);

        while (m_corner < (m_corners.Count - 1)) {
            var corner = m_corners[m_corner];
            var from = ((m_corner > 0) ? m_corners[(m_corner - 1)] : corner);
            var along = new FixedVector3(X: (corner.X - from.X), Y: FixedQ4816.Zero, Z: (corner.Z - from.Z));
            var past = new FixedVector3(X: (read.Position.X - corner.X), Y: FixedQ4816.Zero, Z: (read.Position.Z - corner.Z));

            if ((Planar(from: read.Position, to: corner) > ((m_corner == 0) ? m_firstReach : reach)) && ((m_corner == 0) || (FixedVector3.Dot(left: along, right: past) < FixedQ4816.Zero))) {
                break;
            }

            m_corner++;
            m_best = FixedQ4816.MaxValue;
            m_progressTick = tick;
        }

        var target = m_corners[m_corner];
        var distance = Planar(from: read.Position, to: target);

        // A body that has strayed from its line, or made no progress toward its corner for a while, plans again from
        // where it stands; one that keeps making no progress has met something its path did not show.
        if (distance < (m_best - FixedQ4816.FromDouble(value: 0.05))) {
            m_best = distance;
            m_progressTick = tick;
        }
        if (!planned && !go.Direct && (m_corner > 0) && (Stray(from: m_corners[(m_corner - 1)], point: read.Position, to: target) > FixedQ4816.FromDouble(value: StrayMetres))) {
            return Plan(go: go, read: in read, tick: tick) ? Go(go: go, planned: true, rateHz: rateHz, read: in read, still: still, tick: tick) : still;
        }
        if (!planned && ((tick - m_progressTick) > StuckTicks)) {
            if (++m_stuckPlans > MaxStuckPlans) {
                Fail(kind: "stuck", place: read.Place, reason: $"made no progress toward {(go.Place ?? Vector(go.Point))} after {MaxStuckPlans} plans", tick: tick);
                return still;
            }
            return Plan(go: go, read: in read, tick: tick, stuck: true) ? Go(go: go, planned: true, rateHz: rateHz, read: in read, still: still, tick: tick) : still;
        }

        var dx = (target.X - read.Position.X);
        var dz = (target.Z - read.Position.Z);
        var final = (m_corner == (m_corners.Count - 1));
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

        return still with {
            Moves = true,
            Dx = (dx / length),
            Dz = (dz / length),
            Stop = ((final && (go.Point is not null)) ? FixedQ4816.Max(x: FixedQ4816.Zero, y: (distance - (FixedQ4816.FromDouble(value: go.Within) / FixedQ4816.FromInteger(value: 2)))) : FixedQ4816.MaxValue),
            CornerDistance = (final ? FixedQ4816.MaxValue : distance),
            CornerTurn = turn,
            Faces = true,
            Facing = facing,
            Pace = go.Pace,
        };
    }

    // Plans the step's path from where the body stands: straight at the goal for a direct step, otherwise the navigation
    // domains' route straightened to its corners, with the places its nodes stand in as the corridor.
    private bool Plan(WorldRouteStep.Go go, in SeatRead read, ulong tick, bool stuck = false) {
        Plans++;
        m_plannedIn = read.World;
        m_corners.Clear();
        m_corner = 0;
        m_firstReach = FixedQ4816.FromDouble(value: 0.4);
        m_best = FixedQ4816.MaxValue;
        m_progressTick = tick;
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
        // Where the body stands as the path is planned: a step that began on a place's edge may cross back over it.
        _ = m_corridor.Add(item: read.Place);

        var population = read.Server.Population;
        var target = ((go.Point is { } point) ? Point(point) : default);

        if (go.Direct) {
            if (go.Point is null) {
                if (!read.Server.Events.TryPlaceShape(placementId: go.Place!, shape: out var shape)) {
                    Fail(kind: "route", place: read.Place, reason: $"the place {go.Place} is not sensed in {read.World}", tick: tick);
                    return false;
                }
                target = shape.Center;
            }
            m_corners.Add(item: target);
            m_checkCorridor = (go.Corridor is { Count: > 0 });
            return true;
        }

        INavigationRouteGoal destination;

        if (go.Place is { } place) {
            if (!read.Server.Events.TryPlaceShape(placementId: place, shape: out var shape)) {
                Fail(kind: "route", place: read.Place, reason: $"the place {place} is not sensed in {read.World}", tick: tick);
                return false;
            }
            destination = new WorldNavigationPlaceGoal(Shape: shape);
        } else if (population.TrySteeringEndpoint(body: read.Body, cell: out var cell, domain: out var domain, fromPoint: false, point: target)) {
            destination = new NavigationCellGoal(Cell: cell, Domain: domain, Position: population.NavigationFoot(node: new NavigationRouteNode(Cell: cell, Domain: domain, Position: default)));
        } else {
            Fail(kind: "route", place: read.Place, reason: $"no navigation domain holds a walkable cell at {Vector(go.Point)}", tick: tick);
            return false;
        }

        var status = population.PlanSteering(body: read.Body, corners: m_cornerNodes, goal: destination, margin: m_margin, route: m_nodes, work: out _);

        if ((status is not (NavigationStatus.Active or NavigationStatus.Arrived)) || (m_cornerNodes.Count == 0)) {
            Fail(kind: "route", place: read.Place, reason: $"no route from ({Metres(read.Position.X)}, {Metres(read.Position.Y)}, {Metres(read.Position.Z)}) to {(go.Place ?? Vector(go.Point))}: {status.ToString().ToLowerInvariant()}", tick: tick);
            return false;
        }

        foreach (var node in m_cornerNodes) {
            m_corners.Add(item: population.NavigationFoot(node: node));
        }
        // The route starts at the cell under the body, or the nearest one to it. A body that can walk straight from where
        // it stands to the next corner, keeping the margin, steers there at once; one that cannot (standing nearer a wall
        // than a cell's center may, as a body just out of a door does) steps onto that cell first.
        if ((m_corners.Count > 1) && (m_cornerNodes[1].Domain == m_cornerNodes[0].Domain) &&
            population.NavigationDomain(index: m_cornerNodes[0].Domain).IsStraightClear(from: read.Position, margin: m_margin, to: m_corners[1])) {
            m_corners.RemoveAt(index: 0);
        } else {
            m_firstReach = FixedQ4816.FromDouble(value: 0.1);
        }
        if (go.Point is not null) {
            m_corners.Add(item: target);
        }
        foreach (var node in m_nodes) {
            var places = read.Server.Events.PlacesAt(point: population.NavigationFoot(node: node));

            _ = m_corridor.Add(item: ((places.Count == 0) ? "none" : places[0]));
        }
        m_checkCorridor = true;
        return true;
    }

    // Evaluates the program's operations into the seat's controls and submits each one that changed, re-asserting held
    // sticks as a player does and every held control when the seat's world changed under it.
    private void Drive(in SeatRead read, in Steer steer, ulong tick, int rateHz) {
        var crossed = (!string.Equals(a: m_routeWorld, b: read.World, comparisonType: StringComparison.Ordinal) || (m_routeIndex != read.Index));
        var reassert = (crossed || ((tick - m_lastAssert) >= ReassertTicks));

        m_routeWorld = read.World;
        m_routeIndex = read.Index;
        if (reassert) {
            m_lastAssert = tick;
        }
        m_driving = false;

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

    private string Evaluate(WorldSeatProgramOp operation, in SeatRead read, in Steer steer, FixedQ4816 facing, ulong tick, int rateHz) {
        switch (operation) {
            case WorldSeatProgramOp.Move move: {
                if (!steer.Moves) {
                    return "0 0";
                }

                // Slow for a sharp corner, or to stop at a final point, against the speed the body actually has.
                var velocity = read.Body.FixedPlanarVelocity;
                var speed = FixedQ4816.Sqrt(value: ((velocity.X * velocity.X) + (velocity.Z * velocity.Z)));
                var brake = FixedQ4816.FromDouble(value: move.Brake);
                var allowed = FixedQ4816.MaxValue;

                if ((steer.CornerDistance != FixedQ4816.MaxValue) && (steer.CornerTurn > FixedQ4816.FromDegrees(degrees: move.CornerDegrees))) {
                    var corner = FixedQ4816.FromDouble(value: move.CornerSpeed);

                    allowed = FixedQ4816.Sqrt(value: ((corner * corner) + ((FixedQ4816.FromInteger(value: 2) * brake) * steer.CornerDistance)));
                }
                if (steer.Stop != FixedQ4816.MaxValue) {
                    allowed = FixedQ4816.Min(x: allowed, y: FixedQ4816.Sqrt(value: ((FixedQ4816.FromInteger(value: 2) * brake) * steer.Stop)));
                }

                var magnitude = FixedQ4816.One;

                if ((allowed != FixedQ4816.MaxValue) && (speed > allowed)) {
                    magnitude = FixedQ4816.Max(x: FixedQ4816.FromDouble(value: move.Least), y: (allowed / speed));
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
                return ((steer.Moves && hold.Paces.Contains(value: steer.Pace)) ? "press" : "release");
            case WorldSeatProgramOp.Pulse pulse: {
                var period = Math.Max(val1: 2UL, val2: (ulong)Math.Round(a: (pulse.PeriodSeconds * rateHz)));

                return ((steer.Moves && pulse.Paces.Contains(value: steer.Pace) && (((tick - m_stepStart) % period) == 0UL)) ? "press" : "release");
            }
            case WorldSeatProgramOp.Tap tap:
                return ((m_tap == tap.Action) ? "press" : "release");
            default:
                return string.Empty;
        }
    }

    // The X deflection that takes the turn's gain of the way from the camera's facing to the step's, centred inside its
    // deadband and never past full; rounded to the precision a control line carries, so the facing it predicts is the
    // facing the seat turns to.
    private static FixedQ4816 TurnDeflection(WorldSeatProgramOp.Turn turn, in SeatRead read, in Steer steer) {
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

    private void Submit(string text) {
        m_lines.Add(item: m_script.SubmitSeat(router: m_router, slot: m_slot, text: text));
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
