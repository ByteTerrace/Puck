using Puck.Maths;
using System.Runtime.InteropServices;
using System.Text;

namespace Puck.World.Server;

/// <summary>The named live-state boundaries exposed by <c>world.state.hash</c>.</summary>
public enum WorldStateHashScope : byte {
    /// <summary>The historical capture digest: the population digest followed by the live value and the clock of
    /// every authored <c>state.world</c> cell.</summary>
    Capture,
    /// <summary>Every active body's authoritative pose, rigid residue, and carry relationship, identical to <see cref="WorldReplaySnapshot.HashState"/>.</summary>
    Pose,
    /// <summary>The store's own contents, the rows its host owns, and the topologies they are declared over.</summary>
    World,
    /// <summary><see cref="World"/> plus poses, rule latches, group progress, body/identity action state, every body's
    /// simulation continuation, navigation, flock perception, search progress, and scoped replay continuations.</summary>
    Authoritative,
}
/// <summary>One named component of the world hash composition. A scope is an ordered list of these and nothing
/// else, so what a hash covers moves only by a deliberate edit to that list.</summary>
public enum WorldStateHashComponent : byte {
    /// <summary>Input epochs, semantic sources, continuation, retained events and their exact interval coordinates.</summary>
    Input,
    /// <summary>The scope's own domain separator.</summary>
    Seed,
    /// <summary>The simulation tick the scope was asked for.</summary>
    Tick,
    /// <summary>Every active body's pose, rigid residue, and carry relationship.</summary>
    PopulationPose,
    /// <summary>Everything the arena stores: every row's cells, presence, member keys, cursors, drawn masks, phase
    /// sequence, and every runtime-state column, across the document, participant, and identity lanes.</summary>
    Arena,
    /// <summary>The rows the arena holds a descriptor for and no columns, folded by the host that serves them.</summary>
    HostOwnedRows,
    /// <summary>What the state section declares rather than stores: each row's kind, envelope, capacity, overflow,
    /// drive and eviction policy, domain, audience, and time traits, and each cell's own traits.</summary>
    Declaration,
    /// <summary>The lattice topologies the document declares its board and field rows over. A topology is
    /// declaration the arena does not store, so it is folded here rather than by the store.</summary>
    Topologies,
    /// <summary>The rule family's edge latch.</summary>
    RuleLatch,
    /// <summary>The interaction family's edge latch.</summary>
    InteractionLatch,
    /// <summary>Every rule group's pass or step progress.</summary>
    RuleGroups,
    /// <summary>Each decision's runtime: selection, commitment, interrupt, draw stream, and retained candidate.</summary>
    Decisions,
    /// <summary>The board enforcement verdict latch.</summary>
    BoardEnforcement,
    /// <summary>Every body's action-state declaration and per-lane trigger runtime. The stored values ride
    /// <see cref="Arena"/> with the rest of the slot lanes.</summary>
    BodyActionState,
    /// <summary>Every active body's simulation continuation, through its population checkpoint field codecs: velocities,
    /// integration remainders, channel timers, the input and tape it carries, its producer, autonomy and navigation
    /// runtime, excluding rendered color and rig, beside the pose <see cref="PopulationPose"/> also folds. Two bodies at one pose but different
    /// continuations diverge at the next tick, so the hash tells them apart now.</summary>
    BodyContinuation,
    /// <summary>Cached navigation: the shared domains and every active route.</summary>
    Navigation,
    /// <summary>Flock perception, slot generations, autonomy cadence, and prior travel.</summary>
    Flock,
    /// <summary>Every search job's progress.</summary>
    Search,
    /// <summary>The scoped tapes' captured starts, accepted inputs, recorded hashes and playback cursors.</summary>
    ScopedReplays,
    /// <summary>The custody records the authority folded: its titles as home and its custody facts as a custodian.</summary>
    Custody,
}
/// <summary>Composes the world's state hash from named components in one declared order.</summary>
/// <remarks>
/// Each component names an owner that folds it; this type owns only which components a scope covers and in what
/// order. <see cref="WorldStateHashScope.Capture"/> is outside that vocabulary: it is the historical
/// capture-manifest fold, a flat walk of the authored cells' live values and clocks, and <c>puck parity</c> compares
/// it per capture.
/// </remarks>
public static partial class WorldStateHashComposition {
    private const ulong AuthoritativeDomain = 0x4155544853543031UL; // "AUTHST01"
    private const ulong WorldDomain = 0x574f524c44535431UL; // "WORLDST1"

    private static readonly WorldStateHashComponent[] AuthoritativeOrder = [
        WorldStateHashComponent.Seed,
        WorldStateHashComponent.Tick,
        WorldStateHashComponent.PopulationPose,
        WorldStateHashComponent.Arena,
        WorldStateHashComponent.HostOwnedRows,
        WorldStateHashComponent.Declaration,
        WorldStateHashComponent.Topologies,
        WorldStateHashComponent.RuleLatch,
        WorldStateHashComponent.InteractionLatch,
        WorldStateHashComponent.RuleGroups,
        WorldStateHashComponent.Decisions,
        WorldStateHashComponent.BoardEnforcement,
        WorldStateHashComponent.BodyActionState,
        WorldStateHashComponent.BodyContinuation,
        WorldStateHashComponent.Input,
        WorldStateHashComponent.Navigation,
        WorldStateHashComponent.Flock,
        WorldStateHashComponent.Search,
        WorldStateHashComponent.ScopedReplays,
        WorldStateHashComponent.Custody,
    ];
    private static readonly WorldStateHashComponent[] PoseOrder = [
        WorldStateHashComponent.PopulationPose,
    ];
    private static readonly WorldStateHashComponent[] WorldOrder = [
        WorldStateHashComponent.Seed,
        WorldStateHashComponent.Arena,
        WorldStateHashComponent.HostOwnedRows,
        WorldStateHashComponent.Declaration,
        WorldStateHashComponent.Topologies,
    ];

    /// <summary>Gets the components <see cref="WorldStateHashScope.Authoritative"/> folds, in fold order.</summary>
    public static IReadOnlyList<WorldStateHashComponent> Authoritative => AuthoritativeOrder;
    /// <summary>Gets the components <see cref="WorldStateHashScope.Pose"/> folds, in fold order.</summary>
    public static IReadOnlyList<WorldStateHashComponent> Pose => PoseOrder;
    /// <summary>Gets the components <see cref="WorldStateHashScope.World"/> folds, in fold order.</summary>
    public static IReadOnlyList<WorldStateHashComponent> World => WorldOrder;

    /// <summary>Returns the components a scope folds, in fold order.</summary>
    /// <param name="scope">The scope.</param>
    /// <returns>The declared order, empty for <see cref="WorldStateHashScope.Capture"/>, which is not composed of
    /// components.</returns>
    public static IReadOnlyList<WorldStateHashComponent> Order(WorldStateHashScope scope) => (scope switch {
        WorldStateHashScope.Authoritative => AuthoritativeOrder,
        WorldStateHashScope.Pose => PoseOrder,
        WorldStateHashScope.World => WorldOrder,
        _ => [],
    });
    /// <summary>Returns the type that folds one component.</summary>
    /// <param name="component">The component.</param>
    /// <returns>The owner's name.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The component names no owner.</exception>
    public static string Owner(WorldStateHashComponent component) => (component switch {
        WorldStateHashComponent.Input => "Puck.World.Server.WorldInputRuntime",
        WorldStateHashComponent.Arena => "Puck.State.StateArena",
        WorldStateHashComponent.BoardEnforcement => "Puck.World.Server.WorldServer.BoardEnforcement",
        WorldStateHashComponent.BodyActionState => "Puck.World.Server.WorldBody.ActionState",
        WorldStateHashComponent.BodyContinuation => "Puck.World.Server.WorldPopulation.CaptureEntries",
        WorldStateHashComponent.Declaration => "Puck.World.Server.WorldStateHashComposition",
        WorldStateHashComponent.Decisions => "Puck.World.Server.WorldServer.Decisions",
        WorldStateHashComponent.Flock => "Puck.World.Server.WorldPopulation.Flock",
        WorldStateHashComponent.HostOwnedRows => "Puck.Physics.Fields.FieldLattice",
        WorldStateHashComponent.InteractionLatch => "Puck.State.Rules.RuleLatch",
        WorldStateHashComponent.Navigation => "Puck.World.Server.WorldPopulation.Query",
        WorldStateHashComponent.PopulationPose => "Puck.World.Server.WorldReplaySnapshot",
        WorldStateHashComponent.RuleGroups => "Puck.State.Rules.RuleGroupState",
        WorldStateHashComponent.RuleLatch => "Puck.State.Rules.RuleLatch",
        WorldStateHashComponent.Search => "Puck.State.ArenaSearch",
        WorldStateHashComponent.ScopedReplays => "Puck.World.Server.WorldScopedReplayRuntime",
        WorldStateHashComponent.Custody => "Puck.World.Server.WorldCompanionCustody",
        WorldStateHashComponent.Seed => "Puck.World.Server.WorldStateHashComposition",
        WorldStateHashComponent.Tick => "Puck.World.Server.WorldServer",
        WorldStateHashComponent.Topologies => "Puck.World.Server.WorldStateHashComposition",
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(component)),
    });
    /// <summary>Computes one named state scope.</summary>
    /// <param name="server">The live server.</param>
    /// <param name="tick">The simulation tick to read live values at.</param>
    /// <param name="scope">The boundary to fold.</param>
    /// <returns>The digest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scope"/> names no boundary.</exception>
    public static ulong Hash(WorldServer server, ulong tick, WorldStateHashScope scope) {
        ArgumentNullException.ThrowIfNull(argument: server);

        return (scope switch {
            WorldStateHashScope.Authoritative => HashAuthoritative(
                server: server,
                tick: tick
            ),
            WorldStateHashScope.Capture => HashCapture(
                server: server,
                tick: tick
            ),
            WorldStateHashScope.Pose => WorldReplaySnapshot.HashState(population: server.Population),
            WorldStateHashScope.World => HashWorld(
                server: server,
                tick: tick
            ),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(scope)),
        });
    }
    /// <summary>Folds the state system's authoritative live lanes. The rest of the world document, grants,
    /// presentation caches, pending transport work, diagnostics, and screen-machine cores are deliberately outside
    /// this boundary.</summary>
    /// <param name="server">The live server.</param>
    /// <param name="tick">The simulation tick.</param>
    /// <returns>The digest: one fold over each component's own digest (<see cref="AuthoritativeComponents"/>), in
    /// <see cref="Authoritative"/> order.</returns>
    /// <remarks>The arena and the body continuations fold incrementally: a row or a body slot whose version has not
    /// moved contributes its cached digest. <see cref="RecomputeAuthoritative"/> is the same value with nothing
    /// cached.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    public static ulong HashAuthoritative(WorldServer server, ulong tick) => Fold(
        order: AuthoritativeOrder,
        recompute: false,
        seed: AuthoritativeDomain,
        server: server,
        tick: tick
    );
    /// <summary>Computes <see cref="HashAuthoritative"/>'s value with every incremental component folded afresh,
    /// reading and writing no cached digest: the full recompute a verification run compares the incremental digest
    /// against.</summary>
    /// <param name="server">The live server.</param>
    /// <param name="tick">The simulation tick.</param>
    /// <returns>The digest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    public static ulong RecomputeAuthoritative(WorldServer server, ulong tick) => Fold(
        order: AuthoritativeOrder,
        recompute: true,
        seed: AuthoritativeDomain,
        server: server,
        tick: tick
    );
    /// <summary>Returns each component of <see cref="Authoritative"/>'s own digest under the authoritative domain, in
    /// fold order: the values <see cref="HashAuthoritative"/> folds, and the read that names which component two
    /// authoritative digests disagree in.</summary>
    /// <param name="server">The live server.</param>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="recompute">Whether the incremental components fold afresh rather than from their caches.</param>
    /// <returns>One digest per component of <see cref="Authoritative"/>, in its order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    public static ulong[] AuthoritativeComponents(WorldServer server, ulong tick, bool recompute = false) {
        ArgumentNullException.ThrowIfNull(argument: server);

        var digests = new ulong[AuthoritativeOrder.Length];

        for (var index = 0; (index < digests.Length); index++) {
            digests[index] = ComponentDigest(
                component: AuthoritativeOrder[index],
                recompute: recompute,
                seed: AuthoritativeDomain,
                server: server,
                tick: tick
            );
        }

        return digests;
    }
    /// <summary>Names the components of <see cref="Authoritative"/> whose digests differ between two per-component
    /// reads (<see cref="AuthoritativeComponents"/>).</summary>
    /// <param name="left">One read.</param>
    /// <param name="right">The other.</param>
    /// <returns>The differing components, in fold order; empty when the reads agree.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="left"/> or <paramref name="right"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The reads are not both one digest per authoritative component.</exception>
    public static IReadOnlyList<WorldStateHashComponent> DifferingComponents(IReadOnlyList<ulong> left, IReadOnlyList<ulong> right) {
        ArgumentNullException.ThrowIfNull(argument: left);
        ArgumentNullException.ThrowIfNull(argument: right);

        if (
            (left.Count != AuthoritativeOrder.Length) ||
            (right.Count != AuthoritativeOrder.Length)
        ) {
            throw new ArgumentException(message: $"a per-component read holds {AuthoritativeOrder.Length} digests; got {left.Count} and {right.Count}");
        }

        var differing = new List<WorldStateHashComponent>();

        for (var index = 0; (index < AuthoritativeOrder.Length); index++) {
            if (left[index] != right[index]) {
                differing.Add(item: AuthoritativeOrder[index]);
            }
        }

        return differing;
    }
    /// <summary>Checks the incremental authoritative digest against a full recompute of the server's current state.
    /// A stale component is one whose cached digests no longer describe what it stores: a write moved its state
    /// without moving the version the cache is keyed by, which would hide a divergence.</summary>
    /// <param name="server">The live server.</param>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="hash">The incremental digest the caller already took at this state.</param>
    /// <returns>An empty string when the recomputed digest is <paramref name="hash"/>; otherwise the stale components
    /// with both digests.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    public static string DescribeStaleDigest(WorldServer server, ulong tick, ulong hash) {
        ArgumentNullException.ThrowIfNull(argument: server);

        var recomputed = RecomputeAuthoritative(server: server, tick: tick);

        if (recomputed == hash) { return string.Empty; }
        var stale = DifferingComponents(
            left: AuthoritativeComponents(server: server, tick: tick),
            right: AuthoritativeComponents(recompute: true, server: server, tick: tick)
        );

        return $"incremental state hash {hash:x16} at tick {tick} differs from its full recompute {recomputed:x16} in {((stale.Count == 0) ? "no single component" : string.Join(separator: ", ", values: stale))}";
    }
    /// <summary>Folds the store's own contents, the rows its host owns, and the topologies they are declared
    /// over.</summary>
    /// <param name="server">The live server.</param>
    /// <param name="tick">The simulation tick.</param>
    /// <returns>The digest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    public static ulong HashWorld(WorldServer server, ulong tick) => Fold(
        order: WorldOrder,
        recompute: false,
        seed: WorldDomain,
        server: server,
        tick: tick
    );
    /// <summary>Folds an explicit component order under an explicit seed: one fold over each component's own
    /// digest.</summary>
    /// <param name="server">The live server.</param>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="seed">The domain separator the <see cref="WorldStateHashComponent.Seed"/> component folds.</param>
    /// <param name="order">The components to fold, in fold order.</param>
    /// <returns>The digest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> or <paramref name="order"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="order"/> names a component nothing folds.</exception>
    public static ulong Compose(WorldServer server, ulong tick, ulong seed, IReadOnlyList<WorldStateHashComponent> order) {
        ArgumentNullException.ThrowIfNull(argument: order);
        ArgumentNullException.ThrowIfNull(argument: server);

        var hash = Fnv1aHash.Create();

        // Walked by index: an interface enumerator would allocate one box per composed hash.
        for (var index = 0; (index < order.Count); index++) {
            hash.Add(value: ComponentDigest(
                component: order[index],
                recompute: false,
                seed: seed,
                server: server,
                tick: tick
            ));
        }

        return hash.Value;
    }

    // Each component folds into its own fresh hash and a scope folds those digests, so a component's digest is a value
    // of its own: a per-component read is exactly what the scope combines, and a component that folds in segments
    // (the arena by rows, the bodies by slots) does so behind its digest without moving any other.
    private static ulong ComponentDigest(WorldServer server, WorldStateHashComponent component, ulong seed, ulong tick, bool recompute) {
        var hash = Fnv1aHash.Create();

        server.Persistence.AppendStateHashComponent(
            component: component,
            hash: ref hash,
            recompute: recompute,
            seed: seed,
            tick: tick
        );
        return hash.Value;
    }
    // The array is walked by index rather than through IReadOnlyList: every authoritative hash a replay tick takes
    // runs here, and an interface enumerator would allocate one box per call.
    private static ulong Fold(WorldServer server, ulong tick, ulong seed, WorldStateHashComponent[] order, bool recompute) {
        ArgumentNullException.ThrowIfNull(argument: server);

        var hash = Fnv1aHash.Create();

        for (var index = 0; (index < order.Length); index++) {
            hash.Add(value: ComponentDigest(
                component: order[index],
                recompute: recompute,
                seed: seed,
                server: server,
                tick: tick
            ));
        }

        return hash.Value;
    }
    // The historical capture-manifest fold: the population digest, then every authored cell's live value, its UTF-8
    // text on a Text row, its vector components, and its clock. Every value is read from the arena, the state the
    // tick left.
    private static ulong HashCapture(WorldServer server, ulong tick) {
        var arena = server.Arena;
        var catalog = arena.Catalog;
        var hash = Fnv1aHash.Create();
        var time = ArenaTime.At(
            engineTick: server.CompletedEngineTicks,
            tick: tick
        );
        Span<byte> utf8 = stackalloc byte[Encoding.UTF8.GetMaxByteCount(charCount: StateCapacity.MaxTextValueLength)];

        hash.Add(value: WorldReplaySnapshot.HashState(population: server.Population));

        for (var rowOrdinal = 0; (rowOrdinal < arena.Rows.Count); rowOrdinal++) {
            var row = arena.Rows[rowOrdinal];
            var cursor = 0;

            while (arena.TryNextCell(cursor: ref cursor, key: out var key, rowOrdinal: rowOrdinal)) {
                if (!arena.TryReadLive(key: key, rowOrdinal: rowOrdinal, time: in time, value: out var value)) { continue; }
                hash.Add(value: (value.Kind switch {
                    CellKind.Bool => (value.AsBool
                        ? 1L
                        : 0L),
                    CellKind.Fixed => value.AsFixed,
                    CellKind.Int => value.AsInt,
                    _ => 0L,
                }));

                if (
                    (row.Kind == CellKind.Text) &&
                    (value.Kind == CellKind.Text) &&
                    (value.AsText is { Length: > 0 } text)
                ) {
                    var written = Encoding.UTF8.GetBytes(
                        bytes: utf8,
                        chars: text.AsSpan()
                    );

                    hash.Add(values: utf8[..written]);
                }
                if (
                    (value.Kind == CellKind.Vector) &&
                    arena.TryReadVector(
                    components: out var components,
                    key: key,
                    rowOrdinal: rowOrdinal
                )
                ) {
                    hash.Add(value: ((uint)components.Length));
                    hash.Add(values: MemoryMarshal.Cast<sbyte, byte>(span: components));
                }

                // A follower's sampled position and velocity are not its live value — a dynamics cell reads its
                // stored target — so two worlds differing only in a clock would fold the same values here.
                if (arena.TryReadClock(
                    epochEngineTick: out var epochEngineTick,
                    epochTick: out var epochTick,
                    key: key,
                    rowOrdinal: rowOrdinal,
                    set: out var clockSet,
                    substepTicks: out var substepTicks,
                    v0: out var v0,
                    y0: out var y0
                )) {
                    hash.Add(value: (clockSet
                        ? 1UL
                        : 0UL
                    ));
                    hash.Add(value: epochTick);
                    hash.Add(value: epochEngineTick);
                    hash.Add(value: y0);
                    hash.Add(value: v0);
                    hash.Add(value: substepTicks);
                }
            }
        }

        return hash.Value;
    }
}
