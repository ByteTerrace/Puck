using System.Numerics;
using Puck.Hosting;

namespace Puck.Demo.Ui;

/// <summary>The per-frame toast snapshot the overlay renders: the last console verb result, as a transient chip.
/// <see cref="Sequence"/> is the writer's monotonic publish counter — the overlay diffs it against the last sequence
/// it drew to detect a NEW toast (rather than re-showing a stale one), and stamps its own expiry from the render
/// clock the moment it first sees a new sequence (see <see cref="OverlayPanelsNode"/>). Presentation-only.</summary>
/// <param name="IsError">Whether the result reads as an error (a best-effort heuristic over the free-text output —
/// there is no structured success/failure flag on <c>CommandResult</c>).</param>
/// <param name="Message">The result text.</param>
/// <param name="Sequence">A monotonic counter, incremented once per <see cref="ToastStore.Publish"/> call.</param>
internal readonly record struct ToastFrame(bool IsError, string Message, int Sequence);

/// <summary>The read seam the overlay panels node consumes for the toast surface; <see cref="ToastStore"/> is the writer.</summary>
internal interface IToastSource {
    /// <summary>Copies the latest published frame, when one exists.</summary>
    bool TrySnapshot(out ToastFrame frame);
}

/// <summary>
/// The toast store: the demo's console-verb-result echo. Published from the SAME least-coupled tap the scrollback
/// fix used (<c>DemoHost</c>'s <c>TextCommandSource.onResult</c> callback) so a scripted/piped run's verb results
/// surface as a transient on-screen chip even when the console panel itself is closed — exactly the run mode a toast
/// is FOR (the panel already shows results while it is open, so the overlay suppresses the toast then). A thin named
/// wrapper over <see cref="PublishBuffer{T}"/>, matching <see cref="Puck.Demo.DevConsole.ConsoleTextStore"/>.
/// </summary>
internal sealed class ToastStore : IToastSource {
    private readonly PublishBuffer<ToastFrame> m_buffer = new();
    private int m_sequence;

    /// <summary>Publishes a new toast (the writer side).</summary>
    /// <param name="message">The result text (an enriched line's C0 control delimiters are stripped to visible
    /// runes here — the same rule <c>DemoConsole</c> applies to its stdout copy).</param>
    /// <param name="isError">Whether the result reads as an error.</param>
    public void Publish(string message, bool isError) {
        m_sequence++;
        m_buffer.Publish(frame: new ToastFrame(IsError: isError, Message: StripEnrichment(message: message), Sequence: m_sequence));
    }

    private static string StripEnrichment(string message) {
        foreach (var c in message) {
            if (Puck.Text.TextEnrichmentTags.IsDelimiter(unicodeScalar: c)) {
                return string.Concat(values: Puck.Text.TextEnrichmentTags.EnumerateVisibleRunes(text: message));
            }
        }

        return message;
    }

    /// <inheritdoc/>
    public bool TrySnapshot(out ToastFrame frame) => m_buffer.TrySnapshot(frame: out frame);
}

/// <summary>Classifies a <c>CommandResult.Output</c> string as an error or a plain result — a best-effort heuristic
/// (the command surface has no structured success/failure flag; verb handlers return free text like
/// <c>"[foo: unavailable]"</c> or <c>"Unknown command: x"</c>). Used only to color the toast's icon square; never
/// changes command behavior.</summary>
internal static class ToastClassifier {
    private static readonly string[] ErrorMarkers = [
        "unknown command", "unavailable", "no device", "unsupported", "not found", "invalid", "failed", "usage:", "[err",
    ];

    /// <summary>Whether <paramref name="message"/> reads as an error.</summary>
    public static bool IsError(string message) {
        if (string.IsNullOrEmpty(value: message)) {
            return false;
        }

        foreach (var marker in ErrorMarkers) {
            if (message.Contains(value: marker, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }
}

/// <summary>The per-frame hub-picker snapshot: whether the workbench authoring hub is up, the highlighted mode index,
/// and every mode's display label (index 0 first) — the picker draws one row per label, index digits right-aligned,
/// with an accent tick on the selected row.</summary>
/// <param name="Active">Whether the hub is up.</param>
/// <param name="Selection">The highlighted mode index.</param>
/// <param name="Labels">Every authoring mode's display label, in registry order.</param>
internal readonly record struct HubPanelFrame(bool Active, int Selection, IReadOnlyList<string> Labels);

/// <summary>The read seam the overlay panels node consumes for the hub surface; <see cref="HubPanelStore"/> is the writer.</summary>
internal interface IHubPanelSource {
    /// <summary>Copies the latest published frame, when one exists.</summary>
    bool TrySnapshot(out HubPanelFrame frame);
}

/// <summary>
/// The hub-picker store. <see cref="Overworld.OverworldFrameSource"/> already exposes the hub's live state as bare
/// primitives (<c>HubActive</c>/<c>HubSelection</c>) — it sits at its analyzer coupling ceiling and may reference no
/// new type to publish here itself, so <see cref="Overworld.OverlayPanelsFeed"/> (an unconstrained helper) reads
/// those primitives each frame and feeds this store. A thin named wrapper over <see cref="PublishBuffer{T}"/>.
/// </summary>
internal sealed class HubPanelStore : IHubPanelSource {
    private readonly PublishBuffer<HubPanelFrame> m_buffer = new();

    /// <summary>Publishes a frame (the writer side).</summary>
    public void Publish(bool active, int selection, IReadOnlyList<string> labels) =>
        m_buffer.Publish(frame: new HubPanelFrame(Active: active, Labels: labels, Selection: selection));

    /// <inheritdoc/>
    public bool TrySnapshot(out HubPanelFrame frame) => m_buffer.TrySnapshot(frame: out frame);
}

/// <summary>The per-frame tracker transport snapshot: the live pattern/row position, play state, tempo, and the
/// working document's name. Mirrors the console-narrated readout <c>TrackerScene.RenderRows</c> already prints, as
/// bare primitives.</summary>
internal readonly record struct TrackerPanelFrame(
    bool Active,
    bool Playing,
    int Pattern,
    int PatternCount,
    int Row,
    int RowCount,
    int Tempo,
    string Name
);

/// <summary>The read seam the overlay panels node consumes for the tracker surface; <see cref="TrackerPanelStore"/> is the writer.</summary>
internal interface ITrackerPanelSource {
    /// <summary>Copies the latest published frame, when one exists.</summary>
    bool TrySnapshot(out TrackerPanelFrame frame);
}

/// <summary>
/// The tracker transport store. Tracker mode has ZERO render coupling today (the console is its whole display —
/// see <see cref="Tracker.TrackerModeState"/>'s doc comment), so <see cref="Overworld.OverlayPanelsFeed"/> reads its
/// state through the same ceiling-safe forwarder the console verbs use (<c>Forge.ForgeCommands.TrackerModeInstance</c>)
/// and feeds this store each frame. A thin named wrapper over <see cref="PublishBuffer{T}"/>.
/// </summary>
internal sealed class TrackerPanelStore : ITrackerPanelSource {
    private readonly PublishBuffer<TrackerPanelFrame> m_buffer = new();

    /// <summary>Publishes a frame (the writer side).</summary>
    public void Publish(bool active, bool playing, int pattern, int patternCount, int row, int rowCount, int tempo, string name) =>
        m_buffer.Publish(frame: new TrackerPanelFrame(
            Active: active,
            Name: name,
            Pattern: pattern,
            PatternCount: patternCount,
            Playing: playing,
            Row: row,
            RowCount: rowCount,
            Tempo: tempo
        ));

    /// <inheritdoc/>
    public bool TrySnapshot(out TrackerPanelFrame frame) => m_buffer.TrySnapshot(frame: out frame);
}

/// <summary>The per-frame gallery-plaque snapshot: the current SDF torture-museum exhibit's title and one metadata line.</summary>
/// <param name="Active">Whether the gallery tour is showing an exhibit (fullscreen debug mode AND the tour both active).</param>
/// <param name="Title">The exhibit's title.</param>
/// <param name="Meta">A single metadata line (index/count and the jump name).</param>
internal readonly record struct GalleryPanelFrame(bool Active, string Title, string Meta);

/// <summary>The read seam the overlay panels node consumes for the gallery surface; <see cref="GalleryPanelStore"/> is the writer.</summary>
internal interface IGalleryPanelSource {
    /// <summary>Copies the latest published frame, when one exists.</summary>
    bool TrySnapshot(out GalleryPanelFrame frame);
}

/// <summary>
/// The gallery-plaque store. <c>SdfGalleryScene</c> prints its plaque to stdout today (the <c>sdf.gallery</c> console
/// verb's whole surface); <see cref="Overworld.OverlayPanelsFeed"/> reads the SAME exhibit state through
/// <c>OverworldFrameSource.SdfDebug.Gallery</c> (an already-public forwarder) and feeds this store each frame. A thin
/// named wrapper over <see cref="PublishBuffer{T}"/>.
/// </summary>
internal sealed class GalleryPanelStore : IGalleryPanelSource {
    private readonly PublishBuffer<GalleryPanelFrame> m_buffer = new();

    /// <summary>Publishes a frame (the writer side).</summary>
    public void Publish(bool active, string title, string meta) =>
        m_buffer.Publish(frame: new GalleryPanelFrame(Active: active, Meta: meta, Title: title));

    /// <inheritdoc/>
    public bool TrySnapshot(out GalleryPanelFrame frame) => m_buffer.TrySnapshot(frame: out frame);
}

/// <summary>
/// The overlay panels' CONTROL plane: master visibility (<c>ui.panels on|off</c>) and each draggable panel's position
/// OVERRIDE (<c>ui.panel.move</c>/<c>ui.panel.reset</c>), shared between <see cref="OverlayPanelsCommandModule"/> (the
/// scriptable mirror) and <see cref="OverlayPanelsNode"/> (which also writes an override live while a panel is being
/// dragged, so a verb and a drag agree on the SAME authoritative position). Small, low-frequency state — a plain
/// lock rather than a lock-free publish buffer, since it is read-modify-written from both the command thread and the
/// render thread.
/// </summary>
internal sealed class OverlayPanelsControlStore {
    private readonly object m_gate = new();
    private readonly Dictionary<string, Vector2> m_overrides = new(comparer: StringComparer.OrdinalIgnoreCase);
    private bool m_panelsVisible = true;

    /// <summary>Whether the new node's panels draw at all (the master toggle; a closed console/suppressed toast are
    /// separate, per-surface rules layered on top of this).</summary>
    public bool PanelsVisible {
        get { lock (m_gate) { return m_panelsVisible; } }
        set { lock (m_gate) { m_panelsVisible = value; } }
    }

    /// <summary>Sets a panel's position override (its dragged/moved top-left, in pixels).</summary>
    public void SetOverride(string panelName, Vector2 position) {
        lock (m_gate) {
            m_overrides[panelName] = position;
        }
    }

    /// <summary>Clears a panel's override, reverting it to its anchored default position.</summary>
    public void ClearOverride(string panelName) {
        lock (m_gate) {
            _ = m_overrides.Remove(key: panelName);
        }
    }

    /// <summary>Reads a panel's override, when one is set.</summary>
    public bool TryGetOverride(string panelName, out Vector2 position) {
        lock (m_gate) {
            return m_overrides.TryGetValue(key: panelName, value: out position);
        }
    }
}
