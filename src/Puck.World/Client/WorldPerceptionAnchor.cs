using Puck.World.Server;

namespace Puck.World.Client;

/// <summary>
/// The per-seat PERCEPTION ANCHOR — the ONE body index all seat-relative presentation derives from: the chase-camera
/// anchor pose and the seat-join cue site (<see cref="WorldFrameSource"/>), the spatial-audio listener (through the
/// seat's resolved view-camera pose the frame source hands <see cref="WorldAudioDirector"/>), the crowd soft-shadow
/// centers (<see cref="WorldSceneEmitter"/>), and the <c>seat.&lt;n&gt;.position.*</c> HUD binding family
/// (<see cref="WorldHudBindingResolver"/>). The anchor resolves to the seat's bound body (slot n perceives from body
/// n — the positional seat-to-body convention every seat seam follows) UNLESS the seat's acting principal holds a
/// Control route (<see cref="IWorldGrantsView.ControlRoute"/>) targeting a BODY with capture ON
/// (<see cref="IWorldGrantsView.RouteCapture"/>) — possession means possession: the entire perceived world follows
/// the possessed body as ONE swap. A mirror route (capture off) does not swap: the seat is still driving a machine
/// AND walking its own avatar, so it still perceives from that avatar. A screen route (classic engage) does not
/// swap either. <see cref="WorldSeatContextSync.Publish"/> is the sole writer — it already reads
/// <c>ControlRoute</c>/<c>RouteCapture</c> per seat every tick over the loopback view for the engagement context
/// family, so the anchor resolution rides that SAME read rather than opening a second one. Swapped in ONE place,
/// never per-system retargets, else a seat would see through one body and hear from another. Presentation-side
/// only: the anchor is derived, never simulation state, and <c>player.where</c> echoes it
/// (<c>anchor=body:&lt;n&gt;</c>) so the resolution is observable and live.
/// </summary>
internal sealed class WorldPerceptionAnchor {
    private readonly int[] m_perceivedBody;

    /// <summary>Constructs the anchor with every seat perceiving from its own bound body — the pre-first-publish
    /// default, matching <see cref="WorldSeatContextSync.Publish"/>'s boot seed.</summary>
    public WorldPerceptionAnchor() {
        m_perceivedBody = new int[WorldSeatBindings.SeatCount];

        for (var slot = 0; (slot < m_perceivedBody.Length); slot++) {
            m_perceivedBody[slot] = slot;
        }
    }

    /// <summary>Publishes seat <paramref name="slot"/>'s resolved anchor. Called only from
    /// <see cref="WorldSeatContextSync.Publish"/>, the one per-tick (and boot-seed) loopback read of the grant
    /// table's Control route.</summary>
    /// <param name="slot">The 0-based local seat slot.</param>
    /// <param name="bodyIndex">The 0-based body index the slot now perceives from.</param>
    internal void Publish(int slot, int bodyIndex) {
        if (((uint)slot) < ((uint)m_perceivedBody.Length)) {
            m_perceivedBody[slot] = bodyIndex;
        }
    }

    /// <summary>The body index seat <paramref name="slot"/> perceives from.</summary>
    /// <param name="slot">The 0-based local seat slot.</param>
    /// <returns>The 0-based body index that seat's presentation derives from — the seat's bound body, or a
    /// possessed body while a body-targeted, captured Control route is active.</returns>
    public int PerceivedBody(int slot) => ((((uint)slot) < ((uint)m_perceivedBody.Length))
        ? m_perceivedBody[slot]
        : slot
    );
}
