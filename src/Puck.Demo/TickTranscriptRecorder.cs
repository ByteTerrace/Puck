using Puck.Commands;
using Puck.Demo.Overworld;
using Puck.Hosting;

namespace Puck.Demo;

/// <summary>
/// The demo-side wiring that makes <see cref="TickTranscript"/> console-addressable: it feeds the SAME transcript
/// from two independent, additive taps, both observation-only —
/// <list type="bullet">
/// <item>every PUSHED command activation (bound-key / gamepad chords), via <see cref="ICommandObserver.OnCommand"/>,
/// the same contract <see cref="DemoCommandObserver"/> already taps;</item>
/// <item>every TYPED console line (stdin-driven verbs), via <see cref="RecordTextCommand"/>, called from
/// <see cref="DemoHost"/>'s stdin result callback — the Submit path bypasses <see cref="ICommandObserver"/>
/// entirely, so a piped script's verbs need this second tap to show up in the narration.</item>
/// </list>
/// The tick boundary (and its state-hash bracket) comes from <see cref="OverworldWorld.OnTickAdvanced"/>, wired
/// lazily through <see cref="IOverworldControlHost.SetTickObserver"/> the first time any introspection verb runs
/// (mirrors <c>OverworldControlCommandModule</c>'s Host remark: the frame source does not exist before the node's
/// first <c>ProduceFrame</c>). Also owns the <c>hash.mark</c> label table. Nothing here mutates simulation state.
/// </summary>
internal sealed class TickTranscriptRecorder : ICommandObserver {
    private readonly DemoConsole m_console;
    private readonly ICreatorModeHost? m_creatorHost;
    private readonly Dictionary<string, HashMark> m_marks = new(comparer: StringComparer.OrdinalIgnoreCase);
    private bool m_wired;

    /// <summary>Initializes the recorder over the root node (for lazy Host resolution) and the console (for the
    /// live per-tick echo <see cref="Watch"/> enables).</summary>
    public TickTranscriptRecorder(IRenderNode rootNode, DemoConsole console) {
        ArgumentNullException.ThrowIfNull(argument: rootNode);
        ArgumentNullException.ThrowIfNull(argument: console);

        m_console = console;
        m_creatorHost = (rootNode as ICreatorModeHost);
    }

    /// <summary>Gets the recorded tick history.</summary>
    public TickTranscript Transcript { get; } = new();

    /// <summary>Gets or sets whether every recorded tick also echoes live to the console (the <c>tick.watch</c>
    /// verb's toggle). Off by default.</summary>
    public bool Watch { get; set; }

    private IOverworldControlHost? Host => (m_creatorHost?.CreatorFrameSource as IOverworldControlHost);

    /// <summary>Ensures the tick-bracket hook is wired to the live world — idempotent and cheap, safe to call from
    /// every verb handler. A no-op until the frame source exists.</summary>
    /// <returns><see langword="true"/> once wired.</returns>
    public bool EnsureWired() {
        if (m_wired) {
            return true;
        }

        if (Host is not { } host) {
            return false;
        }

        host.SetTickObserver(observer: OnTickAdvanced);
        m_wired = true;

        return true;
    }

    /// <inheritdoc/>
    public void OnCommand(in CommandActivation activation) {
        Transcript.RecordCommand(text: (string.IsNullOrEmpty(value: activation.Text)
            ? activation.Name
            : $"{activation.Name} {activation.Text}"));
    }

    /// <summary>Records one TYPED console line — the Submit path <see cref="ICommandObserver"/> never sees. Called
    /// from <see cref="DemoHost"/>'s stdin result callback for every submitted line, including this module's own
    /// verbs (full transparency over the pipe).</summary>
    /// <param name="line">The raw submitted line.</param>
    public void RecordTextCommand(string line) {
        if (!string.IsNullOrWhiteSpace(value: line)) {
            Transcript.RecordCommand(text: line);
        }
    }

    /// <summary>Records the current state hash under a label (overwriting any prior mark of the same label) — the
    /// cheap divergence-bisection primitive: mark, act, mark, compare across two scripted runs.</summary>
    /// <param name="label">The mark's label (case-insensitive).</param>
    /// <param name="tick">The tick the mark was taken at.</param>
    /// <param name="hash">The state hash sampled at that tick.</param>
    public string Mark(string label, ulong tick, ulong hash) {
        m_marks[label] = new HashMark(Tick: tick, Hash: hash);

        return $"[hash.mark: {label} @ tick {tick} hash=0x{hash:X16}]";
    }

    /// <summary>Lists every recorded mark with its tick and full hash.</summary>
    public string DescribeMarks() {
        if (m_marks.Count == 0) {
            return "[hash.marks: none yet — hash.mark <label> records one]";
        }

        var lines = m_marks.Select(selector: pair => $"  {pair.Key,-16} tick={pair.Value.Tick,-8} hash=0x{pair.Value.Hash:X16}");

        return string.Join(separator: '\n', values: new[] { "[hash.marks]", }.Concat(second: lines));
    }

    // Seals the tick into the transcript and, when watching, echoes the same narration a tick.explain would show.
    private void OnTickAdvanced(ulong tick, ulong hashBefore, ulong hashAfter) {
        var entry = Transcript.RecordTick(tick: tick, hashBefore: hashBefore, hashAfter: hashAfter);

        if (Watch) {
            m_console.WriteLine(message: TickNarration.Describe(entry: entry, tag: "tick.watch", full: false));
        }
    }

    private readonly record struct HashMark(ulong Tick, ulong Hash);
}
