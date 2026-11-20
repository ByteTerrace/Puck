using System.Diagnostics;
using System.Globalization;
using Puck.Abstractions.Capture;
using Puck.Hosting;
using Puck.Recording.Capture;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// Keeps the runs of presented frames a world's <c>highlights</c> rows ask for: each fixed step it reads every row's
/// signal from delivered state and, when one changes, arms a <see cref="CaptureSink"/> on the host's
/// <see cref="FrameCaptureController"/> for the row's frame count, then disarms it once they are written. The files go
/// under the host's own highlight directory, named for the row and a running number, so a document names no path.
/// </summary>
/// <remarks>
/// The controller holds one session, so a highlight that comes due while a recording or another highlight holds it is
/// passed over and counted, never queued behind it. Presentation only: nothing here reaches simulation.
/// </remarks>
internal sealed class WorldHighlightDirector(FrameCaptureController capture, WorldClient client, string directory) {
    // The pace frames are written at: a highlight is a short burst a person steps through, not a recording.
    private const int FramesPerSecond = 30;

    private readonly WorldSignalEdges m_edges = new();

    private CaptureSink? m_armed;
    private string? m_armedStem;
    private ulong m_busy;
    private string? m_lastStem;
    private long m_pendingDue;
    private int m_pendingFrames;
    private string? m_pendingId;
    private ulong m_written;

    /// <summary>Gets the directory highlight frames are written under.</summary>
    public string Directory => directory;

    /// <summary>Reads every row's signal for the completed tick, arms a highlight that has come due and disarms one
    /// that has written its frames.</summary>
    public void Tick() {
        Finish();

        var mirror = client.StateMirror;
        var rows = client.Definition.Highlights;

        foreach (var row in rows) {
            var signal = row.Signal;

            if (m_edges.Observe(mirror: mirror, id: row.Id, signal: in signal).HasFlag(flag: SignalEdge.Changed) && (m_pendingId is null)) {
                m_pendingId = row.Id;
                m_pendingFrames = row.Frames;
                m_pendingDue = (Stopwatch.GetTimestamp() + ((long)(row.DelaySeconds * Stopwatch.Frequency)));
            }
        }

        if ((m_pendingId is { } id) && (Stopwatch.GetTimestamp() >= m_pendingDue)) {
            m_pendingId = null;
            _ = TryArm(frames: m_pendingFrames, name: id, reason: out _);
        }
    }
    /// <summary>Arms a run of frames by name, as a row's trigger does.</summary>
    /// <param name="name">The stem the files carry: ASCII letters, digits, <c>-</c> and <c>_</c>.</param>
    /// <param name="frames">How many presented frames to write.</param>
    /// <param name="reason">Why nothing was armed, or the stem of the files being written.</param>
    /// <returns><see langword="true"/> when the frames are being written.</returns>
    public bool TryArm(string name, int frames, out string reason) {
        if (capture.IsArmed) {
            m_busy++;
            reason = "frame capture is already armed";

            return false;
        }

        var stem = string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{name}-{(m_written + 1UL):0000}"
        );
        var options = new CaptureOptions {
            FileNamePrefix = stem,
            FrameRate = FramesPerSecond,
            MaxFrames = frames,
            OutputDirectory = directory,
        };

        m_armed = new CaptureSink(options: options);
        m_armedStem = stem;
        capture.Arm(options: options, sink: m_armed);
        reason = stem;

        return true;
    }
    /// <summary>Formats the highlight half of <c>capture.status</c>.</summary>
    /// <returns>The authored row count, the runs written and passed over, the run being written and the last one.</returns>
    public string Describe() => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"highlights rows={client.Definition.Highlights.Count} written={m_written} busy={m_busy} writing={(m_armedStem ?? "none")} last={(m_lastStem ?? "none")} directory={directory}"
    );

    private void Finish() {
        if ((m_armed is not { } armed) || (!capture.IsExhausted && (capture.Fault is null))) {
            return;
        }
        if (ReferenceEquals(objA: capture.CurrentSink, objB: armed)) {
            _ = capture.Disarm();
        }

        armed.Dispose();
        m_armed = null;
        m_lastStem = m_armedStem;
        m_armedStem = null;
        m_written++;
    }
}
