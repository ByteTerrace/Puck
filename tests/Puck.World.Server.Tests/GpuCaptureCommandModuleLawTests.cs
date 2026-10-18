using Puck.Abstractions.Gpu;
using Puck.Commands;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Laws for the operator's <c>gpu.capture</c> verb (<see cref="GpuCaptureCommandModule"/>): a bare form
/// describes the tool, a path arms a capture of one frame or the frames named with the path made absolute, and a refusal
/// or a malformed form answers as an error.</summary>
public sealed class GpuCaptureCommandModuleLawTests {
    private sealed class NamedTool : IGpuCaptureTool {
        public bool CapturesSpanFrames => true;
        public string Name => "law";

        public GpuCaptureEnd End() => GpuCaptureEnd.Written(path: "law.cap");
        public string? Start(string path) => null;
    }

    private static CommandRegistry Console(IGpuCaptureTool? tool) =>
        new(modules: [new GpuCaptureCommandModule(capture: new GpuFrameCapture(narrate: static _ => { }, tool: tool))]);

    [Fact]
    public void APathArmsTheFramesItNamesAndTheBareFormDescribesThem() {
        var registry = Console(tool: new NamedTool());
        var path = Path.GetFullPath(path: "law-capture");

        Assert.Equal(actual: registry.Submit(line: "gpu.capture").Output, expected: "[gpu.capture: tool=law idle]");
        Assert.Equal(actual: registry.Submit(line: "gpu.capture law-capture 4").Output, expected: $"[gpu.capture: armed law frames=4 -> {path}]");
        Assert.Equal(actual: registry.Submit(line: "gpu.capture").Output, expected: $"[gpu.capture: tool=law armed frames=4 remaining=4 -> {path}]");
        Assert.True(condition: registry.Submit(line: "gpu.capture other").IsError);
    }
    [InlineData("gpu.capture p x")]
    [InlineData("gpu.capture p 1 2")]
    [Theory]
    public void AMalformedFormAnswersItsUsage(string line) {
        var result = Console(tool: new NamedTool()).Submit(line: line);

        Assert.True(condition: result.IsError);
        Assert.StartsWith(expectedStartString: "[gpu.capture: expected", actualString: result.Output);
    }
    [Fact]
    public void WithoutAToolACaptureIsRefusedByName() {
        var result = Console(tool: null).Submit(line: "gpu.capture p");

        Assert.True(condition: result.IsError);
        Assert.Contains(expectedSubstring: "--capture-tool", actualString: result.Output);
    }
}
