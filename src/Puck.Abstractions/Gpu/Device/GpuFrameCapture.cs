using System.Globalization;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// The operator's on-demand GPU capture of the next frames a host produces (<c>gpu.capture</c>), through the capture
/// tool the process loaded before its device existed. <see cref="TryArm"/> arms a capture of a number of frames to a
/// path; a GPU host calls <see cref="BeginFrame"/> before each frame's GPU work and <see cref="EndFrame"/> after it
/// presents, so the capture holds exactly the armed frames: one file for them all where the tool spans frames, one file
/// a frame where it does not. Each written file, and each failure, is narrated as one <c>[gpu.capture]</c> line. One
/// capture is armed at a time. Safe to arm from any thread; the frame calls come from the host's frame thread.
/// </summary>
public sealed class GpuFrameCapture {
    /// <summary>The prefix of every line the capture narrates.</summary>
    public const string LinePrefix = "[gpu.capture]";
    /// <summary>The most frames one capture holds.</summary>
    public const int MaxFrames = 16;

    private readonly Lock m_gate = new();
    private readonly Action<string> m_narrate;

    private bool m_capturing;
    private bool m_ending;
    private int m_frames;
    private string? m_path;
    private int m_remaining;

    /// <summary>Initializes a new instance of the <see cref="GpuFrameCapture"/> class.</summary>
    /// <param name="tool">The loaded capture tool, or <see langword="null"/> when the process loaded none, so every
    /// arm is refused.</param>
    /// <param name="narrate">Writes one narration line.</param>
    /// <exception cref="ArgumentNullException"><paramref name="narrate"/> is <see langword="null"/>.</exception>
    public GpuFrameCapture(IGpuCaptureTool? tool, Action<string> narrate) {
        ArgumentNullException.ThrowIfNull(narrate);

        Tool = tool;
        m_narrate = narrate;
    }

    /// <summary>Gets the loaded capture tool, or <see langword="null"/> when there is none.</summary>
    public IGpuCaptureTool? Tool { get; }

    /// <summary>Arms a capture of the next <paramref name="frames"/> frames, written to <paramref name="path"/>.</summary>
    /// <param name="path">The absolute path the capture is written to.</param>
    /// <param name="frames">The number of frames, 1 to <see cref="MaxFrames"/>.</param>
    /// <param name="refusal">Why the capture was not armed, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the capture was armed.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or white space.</exception>
    public bool TryArm(string path, int frames, out string? refusal) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (Tool is null) {
            refusal = "no capture tool is loaded; boot the World with --capture-tool pix|renderdoc";

            return false;
        }

        if (frames is < 1 or > MaxFrames) {
            refusal = string.Create(provider: CultureInfo.InvariantCulture, handler: $"a capture holds 1 to {MaxFrames} frames, not {frames}");

            return false;
        }

        lock (m_gate) {
            if ((m_path is not null) || m_ending) {
                refusal = $"a capture to {m_path ?? "the previous file"} is still armed or being written";

                return false;
            }

            m_frames = frames;
            m_path = path;
            m_remaining = frames;
            refusal = null;

            return true;
        }
    }
    /// <summary>Describes the capture: the tool, and what is armed or being captured.</summary>
    /// <returns>One line.</returns>
    public string Describe() {
        lock (m_gate) {
            var state = ((Tool is null)
                ? "no tool"
                : ((m_path is null)
                    ? (m_ending ? "writing" : "idle")
                    : string.Create(provider: CultureInfo.InvariantCulture, handler: $"{(m_capturing ? "capturing" : "armed")} frames={m_frames} remaining={m_remaining} -> {m_path}")));

            return $"[gpu.capture: tool={Tool?.Name ?? "none"} {state}]";
        }
    }
    /// <summary>Starts the armed capture before a frame's GPU work, and asks a tool still writing the last one again.
    /// A host calls it once per frame, before the frame records anything.</summary>
    public void BeginFrame() {
        lock (m_gate) {
            if (m_ending) {
                Finish(outcome: Tool!.End());

                return;
            }

            if ((m_path is null) || m_capturing) {
                return;
            }

            if (Tool!.Start(path: m_path) is { } failure) {
                m_narrate($"{LinePrefix} {Tool.Name} could not start a capture to {m_path}: {failure}");
                m_path = null;

                return;
            }

            m_capturing = true;
        }
    }
    /// <summary>Counts a frame the armed capture holds once it has presented, and ends the capture after its last
    /// frame, or after every frame where the tool captures one at a time.</summary>
    public void EndFrame() {
        lock (m_gate) {
            if (!m_capturing) {
                return;
            }

            m_remaining--;

            if ((m_remaining > 0) && Tool!.CapturesSpanFrames) {
                return;
            }

            m_capturing = false;
            Finish(outcome: Tool!.End());

            if (m_remaining == 0) {
                m_path = null;
            }
        }
    }

    // Narrates a written or failed capture, or holds the end for the next frame while the tool is still writing.
    private void Finish(GpuCaptureEnd outcome) {
        m_ending = outcome.Pending;

        if (outcome.Pending) {
            return;
        }

        m_narrate((outcome.Failure is { } failure)
            ? $"{LinePrefix} {Tool!.Name} capture failed: {failure}"
            : $"{LinePrefix} {Tool!.Name} wrote {outcome.Path}");
    }
}
