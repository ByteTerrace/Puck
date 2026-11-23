namespace Puck.World.Server;

/// <summary>Who a <see cref="WorldEventFeed"/> collects collision edges for: every body, or the bodies the held
/// event-budgeted observe grants name (<see cref="WorldGrants.ObservedEventBodies"/>). Collision sensing visits only
/// pairs with at least one listened body, so a crowd no grant observes costs it nothing. The grant table is simulation
/// state, so the listened set, and every edge that follows from it, is the same on every run.</summary>
public readonly struct WorldEventListeners {
    private readonly WorldGrants? m_grants;

    private WorldEventListeners(WorldGrants grants) => m_grants = grants;

    /// <summary>Gets the listeners that observe every body: collision sensing over the whole population. The default
    /// value is the same.</summary>
    public static WorldEventListeners Everyone => default;

    /// <summary>Returns the listeners a grant table's event-budgeted observe grants name, read at each collection.</summary>
    /// <param name="grants">The live grant table.</param>
    /// <returns>The listeners.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="grants"/> is <see langword="null"/>.</exception>
    public static WorldEventListeners Granted(WorldGrants grants) => new(grants: (grants ?? throw new ArgumentNullException(paramName: nameof(grants))));

    // Replaces bodies with the listened bodies, ascending and distinct; true when every body is listened.
    internal bool Listened(List<int> bodies, int capacity) {
        if (m_grants is null) { bodies.Clear(); return true; }
        return m_grants.ObservedEventBodies(bodies: bodies, capacity: capacity);
    }
}
