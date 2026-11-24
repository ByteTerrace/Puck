using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Maths;
using Puck.Physics.Navigation;

namespace Puck.World.Client;

/// <summary>How a glass out of its viewer's sight was judged (<see cref="WorldPortalReach.Judge"/>).</summary>
/// <param name="Leads">Whether the world behind the glass should stay resident.</param>
/// <param name="Why">How the distance was measured, a constant word: <c>walk</c> when the navigation measured the walk,
/// <c>line</c> when no domain covers the walk and the straight line on the glass's facing side was read instead.</param>
/// <param name="Metres">The walk's length or the straight distance, in metres; zero for a walk longer than the lead.</param>
public readonly record struct WorldPortalLead(bool Leads, string Why, float Metres);

/// <summary>
/// Whether a glass out of its viewer's sight stands near enough, by the way a body would come to it, that the world
/// behind it should stay resident: prediction decides a world's arrival ahead of sight, and observation its retention
/// once seen (<c>docs/plans/streaming-and-memory.md</c>, decision 7). The distance is the walk from the viewer's seat
/// (the body of the seat whose camera it is) to the glass's facing side, across the navigation domains the glass's
/// world plans (<see cref="IWorldSeatNavigation.Reach"/>), so a painting behind a wall or on another storey does not
/// keep its world while a straight line through the floor would. Where no domain covers the seat or the glass, or the
/// viewer is no seat's camera, the straight line from the camera to the glass is read, and only from the side the glass
/// faces (<see cref="WorldPortalVisibility.LeadsInLine"/>). The lead is the glass's world's authored
/// <see cref="WorldViewDefaults.PortalLead"/> plus the glass's reach (<see cref="WorldPortalVisibility.ReachOf"/>).
/// Presentation only: what it decides never reaches simulation state.
/// </summary>
/// <remarks>Every member runs on the thread that pumps and presents, as <see cref="WorldSeatSenses"/> does. A walk is
/// measured again only once its seat's foot has moved <see cref="ReuseMetres"/> or its navigation or lead changed, so a
/// still viewer costs a lookup a glass a frame.</remarks>
/// <param name="senses">What each seat's client was delivered: the seats' bodies and the navigation of their worlds; <see langword="null"/> where no seat is read, which judges every glass by line.</param>
public sealed class WorldPortalReach(WorldSeatSenses? senses) {
    /// <summary>How far in front of a glass, in metres, its facing side reaches: a walk ends at a cell whose foot stands
    /// no farther than this from the glass's plane, on the side it faces.</summary>
    public const float FacingDepth = 2f;
    /// <summary>How far past the glass's edges, in metres along its own axes, its facing side reaches: room for a foot
    /// under a painting hung above the floor, and beside a narrow glass.</summary>
    public const float FacingSlack = 2.5f;
    /// <summary>The farthest, in metres, a seat's camera stands from its body: a camera with no seat's body within it is
    /// no seat's, and its glasses are judged by line.</summary>
    public const float SeatReach = 12f;
    /// <summary>How far, in metres, a seat's foot moves before a walk it measured is measured again.</summary>
    public const float ReuseMetres = 0.25f;

    // One glass's latest walk, kept until a sweep finds it unjudged since the sweep before.
    private sealed class Measured(FacingGoal goal) {
        public FacingGoal Goal { get; } = goal;
        public IWorldSeatNavigation? Navigation;
        public Vector3 From;
        public float Limit;
        public WorldSeatReach Reach;
        public bool Judged;
    }

    private readonly Dictionary<WorldScreen, Measured> m_measured = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<WorldScreen> m_unjudged = [];

    /// <summary>Gets the walks measured since the reach was made, each a bounded route search.</summary>
    public long Measurements { get; private set; }

    /// <summary>Judges whether a glass out of a camera's sight leads.</summary>
    /// <param name="camera">The consumer's camera, in the glass's world.</param>
    /// <param name="glass">The screen row the session shows on.</param>
    /// <param name="seated">Whether the camera may be a seat's: <see langword="false"/> for a window's camera, which
    /// stands where its viewer's eye looks through the glass and has no seat of its own there.</param>
    /// <param name="world">The definition of the world the glass stands in, or <see langword="null"/> when it is not
    /// known, which leaves the walk unmeasured and the line read.</param>
    /// <returns>The judgement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="glass"/> is <see langword="null"/>.</exception>
    public WorldPortalLead Judge(in CameraSnapshot camera, WorldScreen glass, bool seated, WorldDefinition? world) {
        ArgumentNullException.ThrowIfNull(argument: glass);

        var lead = (world?.Views.PortalLead ?? WorldViewDefaults.DefaultPortalLead);
        var limit = (lead + WorldPortalVisibility.ReachOf(glass: glass));

        if (seated && (senses is not null) && (world is not null) && TrySeatOf(camera: in camera, endpoint: out var endpoint, foot: out var foot, world: world)) {
            var navigation = senses.Navigation(endpoint: endpoint);

            navigation.Prepare();
            if (!m_measured.TryGetValue(key: glass, value: out var measured)) {
                m_measured.Add(key: glass, value: measured = new Measured(goal: new FacingGoal(glass: glass)));
            }
            measured.Judged = true;
            if (!ReferenceEquals(objA: measured.Navigation, objB: navigation) || (measured.Limit != limit) || (Vector3.Distance(value1: measured.From, value2: foot) > ReuseMetres)
                || (measured.Reach.Kind == WorldSeatReachKind.Uncovered)) {
                measured.Reach = navigation.Reach(from: FixedVector3.FromVector3(value: foot), goal: measured.Goal, goalProbe: measured.Goal.Probe, limit: FixedQ4816.FromDouble(value: limit));
                measured.Navigation = navigation;
                measured.Limit = limit;
                measured.From = foot;
                Measurements++;
            }
            switch (measured.Reach.Kind) {
                case WorldSeatReachKind.Within:
                    return new WorldPortalLead(Leads: true, Metres: measured.Reach.Metres, Why: "walk");
                case WorldSeatReachKind.Beyond:
                    return new WorldPortalLead(Leads: false, Metres: 0f, Why: "walk");
            }
        }

        var line = WorldPortalVisibility.LeadsInLine(camera: in camera, glass: glass, lead: lead, metres: out var metres);

        return new WorldPortalLead(Leads: line, Metres: metres, Why: "line");
    }
    /// <summary>Forgets the walks of every glass not judged since the previous sweep, so a glass a reload replaced or a
    /// world that closed holds nothing. Called once a frame, before the frame's judgements.</summary>
    public void Sweep() {
        m_unjudged.Clear();
        foreach (var (glass, measured) in m_measured) {
            if (!measured.Judged) { m_unjudged.Add(item: glass); }
            measured.Judged = false;
        }
        foreach (var glass in m_unjudged) { _ = m_measured.Remove(key: glass); }
    }

    // The seat whose camera this is: of the seats standing in a world of the glass's document, the one whose body stands
    // nearest the camera, within SeatReach of it.
    private bool TrySeatOf(in CameraSnapshot camera, WorldDefinition world, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldAuthorityEndpoint? endpoint, out Vector3 foot) {
        endpoint = null;
        foot = default;
        var nearest = SeatReach;

        for (var slot = 0; (slot < WorldSeatBindings.SeatCount); slot++) {
            if (!senses!.TryBody(body: out var body, route: out var route, slot: slot)
                || !(ReferenceEquals(objA: route.Endpoint.Definition, objB: world) || string.Equals(a: route.Endpoint.Definition.DocumentId, b: world.DocumentId, comparisonType: StringComparison.Ordinal))) {
                continue;
            }

            var distance = Vector3.Distance(value1: body.Position, value2: camera.Position);

            if (distance <= nearest) {
                nearest = distance;
                endpoint = route.Endpoint;
                foot = body.Position;
            }
        }

        return (endpoint is not null);
    }

    // The cells a walk to a glass may end at: feet standing within FacingDepth of its plane on the side it faces, and
    // within its half-extents and FacingSlack of its centre along its own two axes.
    private sealed class FacingGoal : INavigationRouteGoal {
        private readonly Vector3 m_origin;
        private readonly Vector3 m_right;
        private readonly Vector3 m_up;
        private readonly Vector3 m_facing;
        private readonly float m_halfRight;
        private readonly float m_halfUp;

        public FacingGoal(WorldScreen glass) {
            m_origin = glass.Origin;
            m_right = Vector3.Normalize(value: glass.Right);
            m_up = Vector3.Normalize(value: glass.Up);
            m_facing = WorldPortalVisibility.FacingOf(glass: glass);
            m_halfRight = (glass.HalfWidth + FacingSlack);
            m_halfUp = (glass.HalfHeight + FacingSlack);

            var low = new Vector3(value: float.PositiveInfinity);
            var high = new Vector3(value: float.NegativeInfinity);

            for (var corner = 0; (corner < 8); corner++) {
                var point = (m_origin
                    + (m_right * (((corner & 1) == 0) ? -m_halfRight : m_halfRight))
                    + (m_up * (((corner & 2) == 0) ? -m_halfUp : m_halfUp))
                    + (m_facing * (((corner & 4) == 0) ? 0f : FacingDepth)));

                low = Vector3.Min(value1: low, value2: point);
                high = Vector3.Max(value1: high, value2: point);
            }
            Bounds = new FixedAabb(Maximum: FixedVector3.FromVector3(value: high), Minimum: FixedVector3.FromVector3(value: low));

            var rise = ((Math.Abs(value: m_up.Y) * glass.HalfHeight) + (Math.Abs(value: m_right.Y) * glass.HalfWidth));
            // In front of the glass's middle, at the height of its lowest edge: over the ground a body stands on to see it.
            var front = (m_origin + (m_facing * Math.Min(val1: 1f, val2: FacingDepth)));

            m_top = (m_origin.Y + rise + FootRise);
            Probe = FixedVector3.FromVector3(value: front with { Y = (front.Y - rise) });
        }

        // How far above the glass's highest edge a foot may stand and still count as before it: a floor glass's
        // neighbours stand on the floor it lies in, a little above its face.
        private const float FootRise = 0.5f;
        // No foot above this stands before the glass: one on the storey over a painting is not in front of it.
        private readonly float m_top;

        public FixedAabb Bounds { get; }
        public FixedVector3 Probe { get; }

        public bool Accepts(int domain, int cell, in FixedVector3 foot) {
            var point = foot.ToVector3();
            var offset = (point - m_origin);
            var depth = Vector3.Dot(vector1: offset, vector2: m_facing);

            return ((point.Y <= m_top) && (depth >= 0f) && (depth <= FacingDepth)
                && (Math.Abs(value: Vector3.Dot(vector1: offset, vector2: m_right)) <= m_halfRight)
                && (Math.Abs(value: Vector3.Dot(vector1: offset, vector2: m_up)) <= m_halfUp));
        }
    }
}
