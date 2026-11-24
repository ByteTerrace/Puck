namespace Puck.World.Server;

/// <summary>The doors of one authority that stand closed (<see cref="WorldPlacementPortal.Open"/>), as the host's door
/// scan last read them: a closed door's opening stops being one, so the wall behind it holds a body back again
/// (<see cref="WorldPortalHoleContactField"/>), and its owner holds no observation session through its glass
/// (<see cref="WorldInstanceHost.SettleScreenSessions"/>), so a shut door keeps its registration and nothing else.</summary>
/// <remarks>The host's door scan is the one writer, between ticks; the body step reads it during the tick that follows,
/// so every contact query of a tick reads the closures the scan after the previous tick decided. A door is named by its
/// placement and face, which every revision of the document that declares it shares.</remarks>
public sealed class WorldPortalClosures {
    private readonly HashSet<(string PlacementId, string Face)> m_closed = [];

    /// <summary>Gets whether any door stands closed.</summary>
    public bool Any => (m_closed.Count > 0);
    /// <summary>Gets a count that moves whenever a door opens or closes, so a reader that derived something from the
    /// closed set knows when to derive it again.</summary>
    public long Revision { get; private set; }

    /// <summary>Returns whether a door's opening stands closed.</summary>
    /// <param name="placementId">The door's placement.</param>
    /// <param name="face">The door's face.</param>
    /// <returns><see langword="true"/> when the last scan found the door closed.</returns>
    public bool IsClosed(string placementId, string face) => m_closed.Contains(item: (placementId, face));

    /// <summary>Records whether a door stands closed, as the host's scan read its condition.</summary>
    /// <param name="placementId">The door's placement.</param>
    /// <param name="face">The door's face.</param>
    /// <param name="closed">Whether the door is closed.</param>
    public void Set(string placementId, string face, bool closed) {
        if (closed ? m_closed.Add(item: (placementId, face)) : ((m_closed.Count > 0) && m_closed.Remove(item: (placementId, face)))) {
            Revision++;
        }
    }
}
