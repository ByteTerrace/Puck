using Puck.Overlays;

namespace Puck.World.Client;

/// <summary>Watches delivered scene AND placement rows and pulses the ones a delivery changed — the visible answer to
/// "what did that just do?". A mutation's committed row glows briefly where it landed; a <c>world.undo</c> makes
/// history flow backward through the world as every rewound row shimmers at once. Presentation-only: intensities ride
/// the render clock, never simulation state, and the pulse tint changes albedo values only, so the probe's capacity
/// math and a shimmering build count identical words.</summary>
/// <remarks>Scene rows, placements, and speakers (placements are exactly this class's intended audience — a stamped
/// creation's arrival/move/undo reads at a glance; a speaker's pulse rides its editor GIZMO chip, since the row has
/// no world geometry) — a moved screen already narrates itself by snapping position, and cameras have no visible
/// body at all. The row families key into one pulse table under distinct internal prefixes, so rows sharing an id
/// never cross-pulse. The first <see cref="Observe"/> is the baseline and never pulses; a whole-document swap pulses
/// everything it changed, which is honest.</remarks>
internal sealed class WorldChangeShimmer {
    // The pulse duration token (DesignTokens.Feedback — the shimmer's feel is design data, not a local literal).
    private const double PulseSeconds = DesignTokens.Feedback.ChangeShimmerPulseSeconds;
    // The internal pulse-key prefixes (scene / placement / speaker) — never surfaced.
    private const string SceneKeyPrefix = "s:";
    private const string PlacementKeyPrefix = "p:";
    private const string SpeakerKeyPrefix = "k:";

    private readonly Dictionary<string, object> m_previous = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, double> m_pulses = new(comparer: StringComparer.Ordinal);
    private readonly List<string> m_expired = [];
    private bool m_hasBaseline;

    /// <summary>Diffs a delivered scene + placement + speaker set against the previous delivery and starts a pulse
    /// on every added or changed row. Call once per definition delivery, with the presentation clock.</summary>
    /// <param name="scene">The delivered scene.</param>
    /// <param name="placements">The delivered placement rows.</param>
    /// <param name="speakers">The delivered speaker rows.</param>
    /// <param name="now">The presentation clock, in seconds.</param>
    public void Observe(WorldScene scene, IReadOnlyList<WorldPlacement> placements, IReadOnlyList<WorldSpeaker> speakers, double now) {
        if (m_hasBaseline) {
            foreach (var row in scene.Rows) {
                Pulse(key: (SceneKeyPrefix + row.Id), row: row, now: now);
            }

            foreach (var placement in placements) {
                Pulse(key: (PlacementKeyPrefix + placement.Id), row: placement, now: now);
            }

            foreach (var speaker in speakers) {
                Pulse(key: (SpeakerKeyPrefix + speaker.Name), row: speaker, now: now);
            }
        }

        m_hasBaseline = true;
        m_previous.Clear();

        foreach (var row in scene.Rows) {
            m_previous[(SceneKeyPrefix + row.Id)] = row;
        }

        foreach (var placement in placements) {
            m_previous[(PlacementKeyPrefix + placement.Id)] = placement;
        }

        foreach (var speaker in speakers) {
            m_previous[(SpeakerKeyPrefix + speaker.Name)] = speaker;
        }
    }

    /// <summary>The pulse intensity for one scene row, eased 1 → 0 over the pulse window; 0 when the row is quiet.</summary>
    /// <param name="id">The scene-row id.</param>
    /// <param name="now">The presentation clock, in seconds.</param>
    public float Intensity(string id, double now) => KeyIntensity(key: (SceneKeyPrefix + id), now: now);

    /// <summary>The pulse intensity for one placement row (see <see cref="Intensity"/>).</summary>
    /// <param name="id">The placement id.</param>
    /// <param name="now">The presentation clock, in seconds.</param>
    public float PlacementIntensity(string id, double now) => KeyIntensity(key: (PlacementKeyPrefix + id), now: now);

    /// <summary>The pulse intensity for one speaker row (the gizmo chip's HELD-tier key; see <see cref="Intensity"/>).</summary>
    /// <param name="name">The speaker name.</param>
    /// <param name="now">The presentation clock, in seconds.</param>
    public float SpeakerIntensity(string name, double now) => KeyIntensity(key: (SpeakerKeyPrefix + name), now: now);

    private void Pulse(string key, object row, double now) {
        if (!m_previous.TryGetValue(key: key, value: out var previous) || !previous.Equals(obj: row)) {
            m_pulses[key] = now;
        }
    }

    private float KeyIntensity(string key, double now) {
        if (!m_pulses.TryGetValue(key: key, value: out var start)) {
            return 0f;
        }

        var age = ((now - start) / PulseSeconds);

        if ((age < 0d) || (age >= 1d)) {
            return 0f;
        }

        var remaining = (float)(1d - age);

        return (remaining * remaining);
    }

    /// <summary>Whether any pulse is still live — the frame source keeps rebuilding while true so the decay
    /// animates. Prunes expired pulses as it answers; call once per produced frame.</summary>
    /// <param name="now">The presentation clock, in seconds.</param>
    public bool HasLivePulse(double now) {
        if (m_pulses.Count == 0) {
            return false;
        }

        m_expired.Clear();

        foreach (var pulse in m_pulses) {
            if ((now - pulse.Value) >= PulseSeconds) {
                m_expired.Add(item: pulse.Key);
            }
        }

        foreach (var key in m_expired) {
            _ = m_pulses.Remove(key: key);
        }

        return (m_pulses.Count != 0);
    }
}
