using Puck.Overlays;

namespace Puck.World.Client;

/// <summary>Watches delivered placement and speaker rows and pulses the ones a delivery changed — the visible answer to
/// "what did that just do?". A mutation's committed row glows briefly where it landed; a <c>world.undo</c> makes
/// history flow backward through the world as every rewound row shimmers at once. Presentation-only: intensities ride
/// the render clock, never simulation state, and the pulse tint changes albedo values only, so the probe's capacity
/// math and a shimmering build count identical words.</summary>
/// <remarks>Placements and speakers (placements are exactly this class's intended audience — a stamped
/// creation's arrival/move/undo reads at a glance; a speaker's pulse rides its editor GIZMO chip, since the row has
/// no world geometry) — a moved screen already narrates itself by snapping position, and cameras have no visible
/// body at all. The row families key into one pulse table under distinct internal prefixes, so rows sharing an id
/// never cross-pulse. The first <see cref="Observe"/> is the baseline and never pulses; a whole-document swap pulses
/// everything it changed, which is honest.</remarks>
public sealed class WorldChangeShimmer {
    // The internal pulse-key prefixes (placement / speaker) — never surfaced.
    private const string PlacementKeyPrefix = "p:";
    // The pulse duration token (DesignTokens.Feedback — the shimmer's feel is design data, not a local literal).
    private const double PulseSeconds = DesignTokens.Feedback.ChangeShimmerPulseSeconds;
    private const string SpeakerKeyPrefix = "k:";

    private bool m_hasBaseline;

    private readonly Dictionary<string, object> m_previous = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, double> m_pulses = new(comparer: StringComparer.Ordinal);
    private readonly List<string> m_expired = [];

    private float KeyIntensity(string key, double now) {
        if (!m_pulses.TryGetValue(
            key: key,
            value: out var start
        )) {
            return 0f;
        }

        var age = ((now - start) / PulseSeconds);

        if (
            (age < 0d) ||
            (age >= 1d)
        ) {
            return 0f;
        }

        var remaining = ((float)(1d - age));

        return (remaining * remaining);
    }
    private void Pulse(string key, object row, double now) {
        if (
            !m_previous.TryGetValue(
            key: key,
            value: out var previous
        ) ||
            !previous.Equals(obj: row)
        ) {
            m_pulses[key] = now;
        }
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
    /// <summary>Diffs delivered placement + speaker sets against the previous delivery and starts a pulse
    /// on every added or changed row. Call once per definition delivery, with the presentation clock.</summary>
    /// <param name="placements">The delivered placement rows.</param>
    /// <param name="speakers">The delivered speaker rows.</param>
    /// <param name="now">The presentation clock, in seconds.</param>
    public void Observe(IReadOnlyList<WorldPlacement> placements, IReadOnlyList<WorldSpeaker> speakers, double now) {
        if (m_hasBaseline) {
            foreach (var placement in placements) {
                Pulse(
                    key: (PlacementKeyPrefix + placement.Id),
                    row: placement,
                    now: now
                );
            }

            foreach (var speaker in speakers) {
                Pulse(
                    key: (SpeakerKeyPrefix + speaker.Name),
                    row: speaker,
                    now: now
                );
            }
        }

        m_hasBaseline = true;
        m_previous.Clear();

        foreach (var placement in placements) {
            m_previous[(PlacementKeyPrefix + placement.Id)] = placement;
        }

        foreach (var speaker in speakers) {
            m_previous[(SpeakerKeyPrefix + speaker.Name)] = speaker;
        }
    }
    /// <summary>The pulse intensity for one placement row.</summary>
    /// <param name="id">The placement id.</param>
    /// <param name="now">The presentation clock, in seconds.</param>
    public float PlacementIntensity(string id, double now) => KeyIntensity(
        key: (PlacementKeyPrefix + id),
        now: now
    );
    /// <summary>The pulse intensity for one speaker row.</summary>
    /// <param name="name">The speaker name.</param>
    /// <param name="now">The presentation clock, in seconds.</param>
    public float SpeakerIntensity(string name, double now) => KeyIntensity(
        key: (SpeakerKeyPrefix + name),
        now: now
    );
}
