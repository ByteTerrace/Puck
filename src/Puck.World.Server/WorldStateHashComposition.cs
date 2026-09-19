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
    /// <summary><see cref="World"/> plus poses, rule latches, group progress, body/identity action state, navigation,
    /// flock perception, and search progress.</summary>
    Authoritative,
}
/// <summary>One named component of the world hash composition. A scope is an ordered list of these and nothing
/// else, so what a hash covers moves only by a deliberate edit to that list.</summary>
public enum WorldStateHashComponent : byte {
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
    /// <summary>Cached navigation: the shared domains and every active route.</summary>
    Navigation,
    /// <summary>Flock perception, slot generations, autonomy cadence, and prior travel.</summary>
    Flock,
    /// <summary>Every search job's progress.</summary>
    Search,
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
        WorldStateHashComponent.Navigation,
        WorldStateHashComponent.Flock,
        WorldStateHashComponent.Search,
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
        WorldStateHashComponent.Arena => "Puck.State.StateArena",
        WorldStateHashComponent.BoardEnforcement => "Puck.World.Server.WorldServer.BoardEnforcement",
        WorldStateHashComponent.BodyActionState => "Puck.World.Server.WorldBody.ActionState",
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
    /// <returns>The digest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    public static ulong HashAuthoritative(WorldServer server, ulong tick) => Fold(
        order: AuthoritativeOrder,
        seed: AuthoritativeDomain,
        server: server,
        tick: tick
    );
    /// <summary>Folds the store's own contents, the rows its host owns, and the topologies they are declared
    /// over.</summary>
    /// <param name="server">The live server.</param>
    /// <param name="tick">The simulation tick.</param>
    /// <returns>The digest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    public static ulong HashWorld(WorldServer server, ulong tick) => Fold(
        order: WorldOrder,
        seed: WorldDomain,
        server: server,
        tick: tick
    );
    /// <summary>Folds an explicit component order under an explicit seed.</summary>
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

        foreach (var component in order) {
            server.Persistence.AppendStateHashComponent(
                component: component,
                hash: ref hash,
                seed: seed,
                tick: tick
            );
        }

        return hash.Value;
    }

    // The array is walked by index rather than through IReadOnlyList: every authoritative hash a replay tick takes
    // runs here, and an interface enumerator would allocate one box per call.
    private static ulong Fold(WorldServer server, ulong tick, ulong seed, WorldStateHashComponent[] order) {
        ArgumentNullException.ThrowIfNull(argument: server);

        var hash = Fnv1aHash.Create();

        for (var index = 0; (index < order.Length); index++) {
            server.Persistence.AppendStateHashComponent(
                component: order[index],
                hash: ref hash,
                seed: seed,
                tick: tick
            );
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

        foreach (var row in server.Definition.State) {
            if (!catalog.TryResolve(
                handle: out var handle,
                lane: StateLane.Document,
                name: row.Name
            )) {
                continue;
            }

            var rowOrdinal = handle.Ordinal;

            foreach (var cell in (row.Cells ?? [])) {
                if (
                    !catalog.Keys.TryResolve(
                    key: out var key,
                    name: cell.Key
                ) ||
                    !arena.TryReadLive(
                    key: key,
                    rowOrdinal: rowOrdinal,
                    time: in time,
                    value: out var value
                )
                ) {
                    continue;
                }

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
