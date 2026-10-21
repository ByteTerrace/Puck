using Puck.Abstractions.Presentation;
using Puck.Commands;

namespace Puck.Hosting.Tests;

// A presented capture over the control session: one frame answers its PNG and its report, a burst answers its report and
// keeps its frames for the caller, and a call that stops waiting withdraws it.
public sealed class ControlPresentedCaptureLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ControlRequest Capture(ControlCapture? options, int timeout = 30_000) => new(
        Capture: options,
        Command: null,
        Id: 1,
        Operation: "capture",
        TimeoutMilliseconds: timeout
    );
    private static void Encode(string path, byte[] rgba, int width, int height) => File.WriteAllBytes(bytes: [1, 2, 3], path: path);

    [Fact]
    public async Task ABurstAnswersItsReportAndKeepsItsFrames() {
        var source = new TextCommandSource(new CommandRegistry([]));
        FrameCaptureRequest? armed = null;
        using var session = new ConsoleControlSession(source, request => armed = request);
        var request = Capture(options: new ControlCapture(Frames: 2, Mode: ControlCapture.Presented, Region: [0, 0, 0.5, 0.5]));
        var operation = session.ExecuteAsync(request, Token);

        source.Collect();
        Assert.NotNull(@object: armed);
        Assert.Equal(expected: FrameCaptureMode.Presented, actual: armed.Mode);
        Assert.Equal(expected: new FrameCaptureRegion(Height: 0.5, Width: 0.5, X: 0, Y: 0), actual: armed.Region);
        _ = armed.Present(encode: Encode, height: 1, presentation: 0, rendered: 0, rgba: new byte[4], tick: 1, width: 1);
        _ = armed.Present(encode: Encode, height: 1, presentation: 1, rendered: 1, rgba: new byte[4], tick: 1, width: 1);

        var response = await operation;
        var directory = Path.GetDirectoryName(path: armed.Path)!;

        try {
            Assert.True(condition: response.IsValidFor(request: request));
            Assert.Equal(expected: "completed", actual: response.Status);
            Assert.Null(@object: response.Png);
            Assert.StartsWith(expectedStartString: "[capture: presented 2 frames 1x1", actualString: response.Output, comparisonType: StringComparison.Ordinal);
            Assert.True(condition: File.Exists(path: armed.PathOf(index: 1)));
            Assert.False(condition: (response with { Png = [1] }).IsValidFor(request: request));
        } finally {
            Directory.Delete(path: directory, recursive: true);
        }
    }
    [Fact]
    public async Task OnePresentedFrameAnswersItsPngAndAWithdrawnOneIsRefused() {
        var source = new TextCommandSource(new CommandRegistry([]));
        FrameCaptureRequest? armed = null;
        using var session = new ConsoleControlSession(source, request => armed = request);
        var operation = session.ExecuteAsync(Capture(options: new ControlCapture(Mode: ControlCapture.Presented)), Token);

        source.Collect();
        _ = armed!.Present(encode: Encode, height: 1, presentation: 0, rendered: 0, rgba: new byte[4], tick: null, width: 1);

        var response = await operation;

        Assert.Equal(expected: new byte[] { 1, 2, 3 }, actual: response.Png);
        Assert.StartsWith(expectedStartString: "Next presented frame", actualString: response.Output, comparisonType: StringComparison.Ordinal);
        Assert.False(condition: Directory.Exists(path: Path.GetDirectoryName(path: armed.Path)));

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token: Token);
        var abandoned = session.ExecuteAsync(Capture(options: new ControlCapture(Frames: 3, Mode: ControlCapture.Presented)), cancel.Token);

        source.Collect();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => abandoned);
        Assert.IsType<OperationCanceledException>(@object: (await armed.Completion).Error);
    }
    [Fact]
    public void CaptureOptionsAreValidatedBeforeTheyReachTheHost() {
        Assert.Null(@object: LocalControlServer.Validate(request: Capture(options: new ControlCapture(Mode: ControlCapture.Settled))));
        Assert.Null(@object: LocalControlServer.Validate(request: Capture(options: new ControlCapture(Frames: 120, Mode: ControlCapture.Presented, Region: [0.25, 0.25, 0.5, 0.5]))));
        Assert.Equal(expected: "Capture mode must be settled or presented.", actual: LocalControlServer.Validate(request: Capture(options: new ControlCapture(Mode: "held")))!.Output);
        Assert.Equal(expected: "Capture frames must be 1..120.", actual: LocalControlServer.Validate(request: Capture(options: new ControlCapture(Frames: 121, Mode: ControlCapture.Presented)))!.Output);
        Assert.StartsWith(expectedStartString: "A settled capture is one frame", actualString: LocalControlServer.Validate(request: Capture(options: new ControlCapture(Frames: 2, Mode: ControlCapture.Settled)))!.Output, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: "Capture region must be four numbers: x, y, width, height.", actual: LocalControlServer.Validate(request: Capture(options: new ControlCapture(Mode: ControlCapture.Presented, Region: [0, 0])))!.Output);
        Assert.StartsWith(expectedStartString: "Capture region: ", actualString: LocalControlServer.Validate(request: Capture(options: new ControlCapture(Mode: ControlCapture.Presented, Region: [0.5, 0, 0.75, 1])))!.Output, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: "Exec takes no capture options.", actual: LocalControlServer.Validate(request: new ControlRequest(Capture: new ControlCapture(Mode: ControlCapture.Settled), Command: "probe", Id: 1, Operation: "exec", TimeoutMilliseconds: 1000))!.Output);
    }
}
