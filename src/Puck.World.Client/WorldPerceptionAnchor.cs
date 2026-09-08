namespace Puck.World.Client;

/// <summary>
/// The per-seat PERCEPTION ANCHOR — the ONE body index all seat-relative presentation derives from: the chase-camera
/// anchor pose and the seat-join cue site (<c>WorldFramePresenter</c>), the spatial-audio listener (through the
/// seat's resolved view-camera pose the frame source hands <c>WorldAudioDirector</c>), the crowd soft-shadow
/// centers (<c>WorldSceneEmitter</c>), and the <c>seat.&lt;n&gt;.position.*</c> HUD binding family
/// (<c>WorldHudBindingResolver</c>). The anchor resolves to the seat's bound body (slot n perceives from body
/// n — the positional seat-to-body convention every seat seam follows) UNLESS the seat's acting principal's
/// <c>ControlApplication</c> set (<c>IWorldGrantsView.Applications</c>) OMITS its own-body application and names
/// another BODY — possession means possession: the entire perceived world follows the possessed body as ONE swap.
/// A set that retains the own-body application (mirroring) does not swap: the seat is still driving a target AND
/// walking its own avatar, so it still perceives from that avatar. A screen application (classic engage) does not
/// swap either. <c>WorldSeatContextSync.Publish</c> is the sole writer — it already reads the application set per
/// seat every tick over the loopback view for the engagement context family, so the anchor resolution rides that
/// SAME read rather than opening a second one. Swapped in ONE place,
/// never per-system retargets, else a seat would see through one body and hear from another. Presentation-side
/// only: the anchor is derived, never simulation state, and <c>body.where</c> echoes it
/// (<c>anchor=body:&lt;n&gt;</c>) so the resolution is observable and live.
/// </summary>
public sealed class WorldPerceptionAnchor {
    private readonly int[] m_perceivedBody;

    /// <summary>Constructs the anchor with every seat perceiving from its own bound body — the pre-first-publish
    /// default, matching <c>WorldSeatContextSync.Publish</c>'s boot seed.</summary>
    public WorldPerceptionAnchor() {
        m_perceivedBody = new int[WorldSeatBindings.SeatCount];

        for (var slot = 0; (slot < m_perceivedBody.Length); slot++) {
            m_perceivedBody[slot] = slot;
        }
    }

    /// <summary>Publishes seat <paramref name="slot"/>'s resolved anchor. Called only from
    /// <c>WorldSeatContextSync.Publish</c>, the one per-tick (and boot-seed) loopback read of the grant
    /// table's control-application set.</summary>
    /// <param name="slot">The 0-based local seat slot.</param>
    /// <param name="bodyIndex">The 0-based body index the slot now perceives from.</param>
    public void Publish(int slot, int bodyIndex) {
        if (((uint)slot) < ((uint)m_perceivedBody.Length)) {
            m_perceivedBody[slot] = bodyIndex;
        }
    }
    /// <summary>The body index seat <paramref name="slot"/> perceives from.</summary>
    /// <param name="slot">The 0-based local seat slot.</param>
    /// <returns>The 0-based body index that seat's presentation derives from — the seat's bound body, or a
    /// possessed body while a body-targeted application stands without the own-body application beside it.</returns>
    public int PerceivedBody(int slot) => ((((uint)slot) < ((uint)m_perceivedBody.Length))
        ? m_perceivedBody[slot]
        : slot
    );
}
