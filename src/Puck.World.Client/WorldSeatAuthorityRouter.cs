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
}
