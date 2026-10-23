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
    // Stands in for the World's world.wait until: tick 999 is refused, any other holds the session until released.
    private sealed class WaitUntil(Func<bool> released) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "Holds until released.",
                handler: (context, args) => {
                    if (args.Is(index: 1, value: "999")) {
                        return CommandResult.Error(output: "[world.wait: refused]");
                    }

                    context.TextSession!.HoldWhile(hold: () => !released(), released: _ => new CommandResult(Output: "[world.wait: released]"));

                    return new CommandResult(Output: "[world.wait: holding]");
                },
                name: "world.wait"
            );
        }
    }

    [Fact]
    public async Task APinnedStillArmsOnlyOnceItsTickHasCompletedAndAnswersWithItsStamp() {
        var released = false;
        var source = new TextCommandSource(new CommandRegistry([new WaitUntil(released: () => released)]));
        FrameCaptureRequest? armed = null;
        var heldAt = 40UL;
        using var session = new ConsoleControlSession(source, request => {
            armed = request;
            request.StateTick = heldAt;
            request.StateStamp = $"tick={heldAt} hash=00 pose=00";
        });
        var operation = session.ExecuteAsync(Capture(options: new ControlCapture(Mode: ControlCapture.Settled, Tick: 40UL)), Token);

        source.Collect();
        source.Collect();
        Assert.Null(@object: armed);

        released = true;
        source.Collect();
        Assert.NotNull(@object: armed);
        _ = armed.Write(writer: static path => File.WriteAllBytes(bytes: [1, 2, 3], path: path), tick: 40UL);

        var response = await operation;

        Assert.Equal(expected: "completed", actual: response.Status);
        Assert.Equal(expected: "Settled still held at tick=40 hash=00 pose=00; the frame shows tick 40.", actual: response.Output);

        // A pin the world had passed when the still armed is withdrawn rather than answered as the pin.
        armed = null;
        heldAt = 41UL;
        var passed = session.ExecuteAsync(Capture(options: new ControlCapture(Mode: ControlCapture.Settled, Tick: 40UL)), Token);

        source.Collect();
        Assert.Contains(expectedSubstring: "had completed tick 41", actualString: (await passed).Output);

        // A refused wait arms nothing.
        armed = null;
        var refused = session.ExecuteAsync(Capture(options: new ControlCapture(Mode: ControlCapture.Settled, Tick: 999UL)), Token);

        source.Collect();
        Assert.Contains(expectedSubstring: "was not armed: [world.wait: refused]", actualString: (await refused).Output);
        Assert.Null(@object: armed);
        Assert.StartsWith(expectedStartString: "A presented capture reads the frames", actualString: LocalControlServer.Validate(request: Capture(options: new ControlCapture(Mode: ControlCapture.Presented, Tick: 3UL)))!.Output, comparisonType: StringComparison.Ordinal);
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
