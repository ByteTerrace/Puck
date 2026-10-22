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
/// swap the body; a captured one (no own-body application beside it) publishes the screen the seat engages
/// (<see cref="EngagedScreen"/>), whose picture the seat camera frames (<c>WorldScreenFraming</c>) and the <c>ScreenFramed</c>
/// overlay fact reads. <c>WorldSeatContextSync.Publish</c> is the sole writer — it already reads the application set per
/// seat every tick over the loopback view for the engagement context family, so the anchor resolution rides that
/// SAME read rather than opening a second one. Swapped in ONE place,
/// never per-system retargets, else a seat would see through one body and hear from another. Presentation-side
/// only: the anchor is derived, never simulation state, and <c>body.where</c> echoes it
/// (<c>anchor=body:&lt;n&gt;</c>) so the resolution is observable and live.
/// </summary>
public sealed class WorldPerceptionAnchor {
    private readonly int[] m_engagedScreen;
    private readonly bool[] m_frameToggled = new bool[WorldSeatBindings.SeatCount];
    private readonly bool[] m_holdView = new bool[WorldSeatBindings.SeatCount];
    private readonly string?[] m_engagedTabletop = new string?[WorldSeatBindings.SeatCount];
    private readonly int[] m_perceivedBody;

    /// <summary>Constructs the anchor with every seat perceiving from its own bound body and engaging no screen — the
    /// pre-first-publish default, matching <c>WorldSeatContextSync.Publish</c>'s boot seed.</summary>
    public WorldPerceptionAnchor() {
        m_perceivedBody = new int[WorldSeatBindings.SeatCount];
        m_engagedScreen = new int[WorldSeatBindings.SeatCount];

        for (var slot = 0; (slot < m_perceivedBody.Length); slot++) {
            m_perceivedBody[slot] = slot;
            m_engagedScreen[slot] = -1;
        }
    }

    /// <summary>The screen seat <paramref name="slot"/> is captured on: the first screen application in its set while
    /// the set holds no own-body application, or -1 when the seat walks its own avatar (unengaged or mirrored),
    /// possesses a body, or is out of range.</summary>
    /// <param name="slot">The 0-based local seat slot.</param>
    /// <returns>The engaged screen's index, or -1.</returns>
    public int EngagedScreen(int slot) => ((((uint)slot) < ((uint)m_engagedScreen.Length))
        ? m_engagedScreen[slot]
        : -1
    );

    /// <summary>The body index seat <paramref name="slot"/> perceives from.</summary>
    /// <param name="slot">The 0-based local seat slot.</param>
    /// <returns>The 0-based body index that seat's presentation derives from — the seat's bound body, or a
    /// possessed body while a body-targeted application stands without the own-body application beside it.</returns>
    public int PerceivedBody(int slot) => ((((uint)slot) < ((uint)m_perceivedBody.Length))
        ? m_perceivedBody[slot]
        : slot
    );
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
    /// <summary>The tabletop seat <paramref name="slot"/> sits at: the placement id of the first tabletop application in
    /// its set while the set holds no own-body application, or <see langword="null"/> when it sits at none or is out of
    /// range. The seat camera eases to that board's view (<see cref="WorldTabletopFraming"/>).</summary>
    /// <param name="slot">The 0-based local seat slot.</param>
    /// <returns>The tabletop's placement id, or <see langword="null"/>.</returns>
    public string? EngagedTabletop(int slot) => ((((uint)slot) < ((uint)m_engagedTabletop.Length))
        ? m_engagedTabletop[slot]
        : null
    );
    /// <summary>Publishes the tabletop seat <paramref name="slot"/> sits at. Called only from
    /// <c>WorldSeatContextSync.Publish</c>, beside <see cref="Publish"/>.</summary>
    /// <param name="slot">The 0-based local seat slot.</param>
    /// <param name="placementId">The tabletop's placement id, or <see langword="null"/> for none.</param>
    public void PublishEngagedTabletop(int slot, string? placementId) {
        if (((uint)slot) < ((uint)m_engagedTabletop.Length)) {
            m_engagedTabletop[slot] = placementId;
        }
    }
    /// <summary>Publishes the screen seat <paramref name="slot"/> is captured on. Called only from
    /// <c>WorldSeatContextSync.Publish</c>, beside <see cref="Publish"/>.</summary>
    /// <param name="slot">The 0-based local seat slot.</param>
    /// <param name="screenIndex">The engaged screen's index, or -1 for none.</param>
    public void PublishEngagedScreen(int slot, int screenIndex) {
        if (((uint)slot) < ((uint)m_engagedScreen.Length)) {
            if (m_engagedScreen[slot] != screenIndex) {
                m_frameToggled[slot] = false;
            }

            m_engagedScreen[slot] = screenIndex;
        }
    }
    /// <summary>Whether seat <paramref name="slot"/>'s view shows the engaged screen's picture alone (the framed screen,
    /// <c>WorldScreenFraming</c>): it engages a screen and is on the view that engagement starts on, or on the other one
    /// after <see cref="ToggleFrame"/>. A screen a body carries with a held view (<see cref="PublishHoldView"/>) starts on
    /// that view, and every other screen on its picture.</summary>
    /// <param name="slot">The 0-based local seat slot.</param>
    /// <returns><see langword="true"/> when the seat's view frames the picture; <see langword="false"/> when it engages
    /// no screen, shows the held view, or is out of range.</returns>
    public bool ScreenFramed(int slot) => ((((uint)slot) < ((uint)m_engagedScreen.Length)) &&
        (m_engagedScreen[slot] >= 0) &&
        (m_holdView[slot] == m_frameToggled[slot]));
    /// <summary>Switches seat <paramref name="slot"/> between the view its engagement starts on and the other one: the
    /// framed picture and, on a carried screen with a held view, the camera that shows the body holding the device. The
    /// choice lasts until the seat's engaged screen changes.</summary>
    /// <param name="slot">The 0-based local seat slot.</param>
    /// <returns><see langword="false"/> when the seat engages no screen (or is out of range) and nothing changed.</returns>
    public bool ToggleFrame(int slot) {
        if (
            (((uint)slot) >= ((uint)m_engagedScreen.Length)) ||
            (m_engagedScreen[slot] < 0)
        ) {
            return false;
        }

        m_frameToggled[slot] = !m_frameToggled[slot];

        return true;
    }
    /// <summary>Whether seat <paramref name="slot"/>'s engaged screen has a held view this frame
    /// (<see cref="PublishHoldView"/>).</summary>
    /// <param name="slot">The 0-based local seat slot.</param>
    /// <returns><see langword="true"/> when a body carries the screen and its carry authors a camera.</returns>
    public bool HoldView(int slot) => ((((uint)slot) < ((uint)m_holdView.Length)) && m_holdView[slot]);
    /// <summary>Publishes whether seat <paramref name="slot"/>'s engaged screen is carried by a body this frame with a
    /// held view to show it on (<see cref="WorldScreenCarry.Camera"/>). Called only from the frame presenter, which
    /// resolves the carry.</summary>
    /// <param name="slot">The 0-based local seat slot.</param>
    /// <param name="holdView"><see langword="true"/> when the engaged screen has a held view this frame.</param>
    public void PublishHoldView(int slot, bool holdView) {
        if (((uint)slot) < ((uint)m_holdView.Length)) {
            m_holdView[slot] = holdView;
        }
    }
}
