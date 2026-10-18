using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>Laws for the operator's on-demand GPU capture (<see cref="GpuFrameCapture"/>): an armed capture starts at
/// the next frame's start and ends after its last frame, once for a tool that spans frames and once a frame for one
/// that does not; a pending end is asked again on the next frame; failures are narrated; and an arm is refused with no
/// tool, outside 1 to <see cref="GpuFrameCapture.MaxFrames"/> frames, or while a capture is armed. Driven over a
/// recording tool, so no device is involved.</summary>
public sealed class GpuFrameCaptureLawTests {
    private sealed class RecordingTool(bool spans) : IGpuCaptureTool {
        public List<string> Calls { get; } = [];
        public Queue<GpuCaptureEnd> Ends { get; } = new();
        public string? StartFailure { get; set; }

        public bool CapturesSpanFrames => spans;
        public string Name => "law";

        public GpuCaptureEnd End() {
            Calls.Add(item: "end");

            return ((Ends.Count > 0) ? Ends.Dequeue() : GpuCaptureEnd.Written(path: "law.cap"));
        }
        public string? Start(string path) {
            Calls.Add(item: $"start {path}");

            return StartFailure;
        }
    }

    [Fact]
    public void ASpanningToolCapturesTheArmedFramesAsOneCapture() {
        var tool = new RecordingTool(spans: true);
        var lines = new List<string>();
        var capture = new GpuFrameCapture(narrate: lines.Add, tool: tool);

        capture.BeginFrame();
        capture.EndFrame();
        Assert.True(condition: capture.TryArm(frames: 3, path: "C:/law/cap", refusal: out var refusal));
        Assert.Null(@object: refusal);
        Assert.Equal(actual: capture.Describe(), expected: "[gpu.capture: tool=law armed frames=3 remaining=3 -> C:/law/cap]");

        for (var frame = 0; (frame < 3); frame++) {
            capture.BeginFrame();
            capture.EndFrame();
        }

        Assert.Equal(actual: tool.Calls, expected: ["start C:/law/cap", "end"]);
        Assert.Equal(actual: lines, expected: ["[gpu.capture] law wrote law.cap"]);
        Assert.Equal(actual: capture.Describe(), expected: "[gpu.capture: tool=law idle]");
    }
    [Fact]
    public void AFrameAtATimeToolIsStartedAndEndedEveryFrame() {
        var tool = new RecordingTool(spans: false);
        var lines = new List<string>();
        var capture = new GpuFrameCapture(narrate: lines.Add, tool: tool);

        Assert.True(condition: capture.TryArm(frames: 2, path: "p", refusal: out _));
        capture.BeginFrame();
        Assert.Equal(actual: capture.Describe(), expected: "[gpu.capture: tool=law capturing frames=2 remaining=2 -> p]");
        capture.EndFrame();
        capture.BeginFrame();
        capture.EndFrame();
        capture.BeginFrame();
        capture.EndFrame();

        Assert.Equal(actual: tool.Calls, expected: ["start p", "end", "start p", "end"]);
        Assert.Equal(actual: lines.Count, expected: 2);
    }
    [Fact]
    public void APendingEndIsAskedAgainAndAFailureIsNarrated() {
        var tool = new RecordingTool(spans: true);
        var lines = new List<string>();
        var capture = new GpuFrameCapture(narrate: lines.Add, tool: tool);

        tool.Ends.Enqueue(item: GpuCaptureEnd.StillWriting());
        tool.Ends.Enqueue(item: GpuCaptureEnd.Failed(reason: "disk full"));
        Assert.True(condition: capture.TryArm(frames: 1, path: "p", refusal: out _));
        capture.BeginFrame();
        capture.EndFrame();
        Assert.Equal(actual: capture.Describe(), expected: "[gpu.capture: tool=law writing]");
        Assert.False(condition: capture.TryArm(frames: 1, path: "q", refusal: out var busy));
        Assert.Contains(expectedSubstring: "being written", actualString: busy);
        capture.BeginFrame();
        Assert.Equal(actual: lines, expected: ["[gpu.capture] law capture failed: disk full"]);

        tool.StartFailure = "no device";
        Assert.True(condition: capture.TryArm(frames: 1, path: "r", refusal: out _));
        capture.BeginFrame();
        capture.EndFrame();
        Assert.Equal(actual: lines[^1], expected: "[gpu.capture] law could not start a capture to r: no device");
        Assert.Equal(actual: capture.Describe(), expected: "[gpu.capture: tool=law idle]");
    }
    [InlineData(0)]
    [InlineData(GpuFrameCapture.MaxFrames + 1)]
    [Theory]
    public void AnArmOutsideTheFrameRangeIsRefused(int frames) {
        var capture = new GpuFrameCapture(narrate: static _ => { }, tool: new RecordingTool(spans: true));

        Assert.False(condition: capture.TryArm(frames: frames, path: "p", refusal: out var refusal));
        Assert.Contains(expectedSubstring: "frames", actualString: refusal);
    }
    [Fact]
    public void WithoutAToolEveryArmIsRefusedAndTheFramesDoNothing() {
        var capture = new GpuFrameCapture(narrate: static _ => throw new InvalidOperationException(), tool: null);

        Assert.False(condition: capture.TryArm(frames: 1, path: "p", refusal: out var refusal));
        Assert.Contains(expectedSubstring: "--capture-tool", actualString: refusal);
        capture.BeginFrame();
        capture.EndFrame();
        Assert.Equal(actual: capture.Describe(), expected: "[gpu.capture: tool=none no tool]");
        Assert.Throws<ArgumentNullException>(testCode: () => new GpuFrameCapture(narrate: null!, tool: null));
        Assert.Throws<ArgumentException>(testCode: () => capture.TryArm(frames: 1, path: " ", refusal: out _));
    }
}
