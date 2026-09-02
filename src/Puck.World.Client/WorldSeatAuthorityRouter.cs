using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The one-writer authority table for locally occupied seats. Each cell is a CAS-published
/// <see cref="WorldAuthorityRoute"/>; rendering, input, audio, HUD, bindings, targeting, and read-back consume the
/// same claim rather than independently interpreting an instance name.
/// </summary>
public sealed class WorldSeatAuthorityRouter {
    private readonly WorldAuthorityRoute?[] m_routes = new WorldAuthorityRoute?[WorldSeatBindings.SeatCount];

    private int m_revision;

    /// <summary>Monotonic presentation watch bumped for every successful complete-claim publication.</summary>
    public int Revision => Volatile.Read(location: ref m_revision);

    /// <summary>Raised after a successful claim change.</summary>
    public event Action<int>? RouteChanged;

    /// <summary>Whether any locally followed seat currently claims this exact generation-addressed entity.</summary>
    public bool Claims(in WorldEntityAddress entity) {
        for (var slot = 0; (slot < m_routes.Length); slot++) {
            if (
                (Volatile.Read(location: ref m_routes[slot]) is { } route) &&
                (route.Entity == entity)
            ) {
                return true;
            }
        }
        return false;
    }
    /// <summary>
    /// Retargets a claim only if it is still the expected claim. This is the route-level CAS used when a federated
    /// observation reports an onward handoff: a stale callback cannot overwrite a newer authority epoch.
    /// </summary>
    public bool CompareExchangeEntity(int slot, WorldAuthorityRoute expected, WorldEntityAddress entity, out WorldAuthorityRoute current) {
        ArgumentNullException.ThrowIfNull(argument: expected);
        if (((uint)slot) >= ((uint)m_routes.Length)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(slot));
        }

        var next = new WorldAuthorityRoute(
            Endpoint: expected.Endpoint,
            Entity: entity,
            Epoch: (expected.Epoch + 1UL)
        );
        var observed = Interlocked.CompareExchange(
            location1: ref m_routes[slot],
            value: next,
            comparand: expected
        );

        if (ReferenceEquals(
            objA: observed,
            objB: expected
        )) {
            current = next;
            _ = Interlocked.Increment(location: ref m_revision);
            RouteChanged?.Invoke(obj: slot);
            return true;
        }

        current = (observed ?? throw new InvalidOperationException(message: $"seat {(slot + 1)} lost its authority claim"));
        return false;
    }
    /// <summary>Publishes a new endpoint/entity claim using the currently observed route as the CAS comparand.</summary>
    public WorldAuthorityRoute Publish(int slot, WorldAuthorityEndpoint endpoint, WorldEntityAddress entity) {
        ArgumentNullException.ThrowIfNull(argument: endpoint);
        if (((uint)slot) >= ((uint)m_routes.Length)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(slot));
        }

        while (true) {
            var previous = Volatile.Read(location: ref m_routes[slot]);

            if (
                (previous is not null) &&
                ReferenceEquals(
                objA: previous.Endpoint,
                objB: endpoint
            ) &&
                (previous.Entity == entity)
            ) {
                return previous;
            }

            var next = new WorldAuthorityRoute(
                Endpoint: endpoint,
                Entity: entity,
                Epoch: ((previous?.Epoch ?? 0UL) + 1UL)
            );

            if (ReferenceEquals(
                objA: Interlocked.CompareExchange(
                    location1: ref m_routes[slot],
                    value: next,
                    comparand: previous
                ),
                objB: previous
            )) {
                _ = Interlocked.Increment(location: ref m_revision);
                RouteChanged?.Invoke(obj: slot);
                return next;
            }
        }
    }
    /// <summary>Returns the current complete authority claim.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="slot"/> was never published — a world declaring
    /// fewer local seats than the host's seat ceiling (<see cref="WorldBodiesLimits.LocalSeatCount"/>) never
    /// routes the seats it did not declare; use <see cref="TryRoute"/> for a slot that may be unrouted by
    /// design.</exception>
    public WorldAuthorityRoute Route(int slot) {
        if (((uint)slot) >= ((uint)m_routes.Length)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(slot));
        }

        return (Volatile.Read(location: ref m_routes[slot]) ??
            throw new InvalidOperationException(message: $"seat {(slot + 1)} has no authority claim"));
    }
    /// <summary>Returns the current authority claim, or <see langword="null"/> for a slot never published (a world
    /// declaring fewer local seats than the host's seat ceiling has no route for the seats it did not declare).</summary>
    public WorldAuthorityRoute? TryRoute(int slot) {
        if (((uint)slot) >= ((uint)m_routes.Length)) {
            return null;
        }

        return Volatile.Read(location: ref m_routes[slot]);
    }
    /// <summary>Routes a read-back query to the slot's currently claimed authority — the shared body every routed
    /// query verb reduces to. Submits <paramref name="factory"/>'s query (built from the claim's own
    /// <see cref="WorldAuthorityRoute.QueryIndex"/>) through the claim's own submission door; when
    /// <paramref name="tagInstance"/> and the claim's identity is not the boot identity, the answer text is suffixed
    /// with <c>instance:&lt;identity&gt;</c> before its closing bracket. Does not throw: a slot with no published
    /// claim returns <see langword="false"/> with no result.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="factory">Builds the query from the claim's own 1-based entity index.</param>
    /// <param name="tagInstance">Whether a non-boot answer is suffixed with its routed instance identity.</param>
    /// <param name="result">The routed answer, on success.</param>
    /// <returns>Whether the slot held a published claim to route through.</returns>
    public bool TryRouteQuery(int slot, Func<int, WorldQuery> factory, bool tagInstance, out CommandResult result) {
        if (TryRoute(slot: slot) is not { } route) {
            result = default;

            return false;
        }

        var routed = default(CommandResult);
        var tag = ((tagInstance && !string.Equals(
            a: route.Endpoint.Identity,
            b: WorldDefinitionLoader.BootInstanceName,
            comparisonType: StringComparison.Ordinal
        ))
            ? route.Endpoint.Identity
            : null
        );

        route.Endpoint.Submissions.Query(
            query: factory(route.QueryIndex),
            completion: answer => {
                var text = ((tag is { } instanceName)
                    ? WithInstanceTag(
                    text: answer.Text,
                    instanceName: instanceName
                )
                    : answer.Text
                );

                routed = new CommandResult(Output: text) { IsError = answer.Refused };
            }
        );

        result = routed;

        return true;
    }

    // Splices ` instance:<name>` just inside a bracketed echo's closing ']' — the same surgery the world's own
    // instance-addressed verbs use, so a routed answer reports which instance answered.
    private static string WithInstanceTag(string text, string instanceName) => CommandEcho.SpliceTag(
        prefix: "instance:",
        text: text,
        value: instanceName
    );
}
