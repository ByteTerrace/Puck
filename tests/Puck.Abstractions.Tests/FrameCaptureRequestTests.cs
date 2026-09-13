using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;

namespace Puck.Abstractions.Tests;

public sealed class FrameCaptureRequestTests {
    [Fact]
    public async Task CancellingAWaitDoesNotCancelOrCompleteTheCapture() {
        var request = new FrameCaptureRequest(path: "capture.png");
        using var cancellation = new CancellationTokenSource();
        var wait = request.Completion.WaitAsync(cancellationToken: cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: async () => await wait);
        Assert.False(condition: request.Completion.IsCompleted);
        _ = request.Write(writer: _ => { });
        Assert.True(condition: (await request.Completion).Succeeded);
    }
    [Fact]
    public async Task CompletionFollowsTheWriteAndRemainsBoundToThatRequest() {
        var first = new FrameCaptureRequest(path: "first.png");
        var second = new FrameCaptureRequest(path: "second.png");
        var written = first.Write(writer: path => {
            Assert.Equal(
                actual: path,
                expected: "first.png"
            );
            Assert.False(condition: first.Completion.IsCompleted);
            Assert.False(condition: first.TryFail(error: new IOException(message: "too late to refuse an active write")));
        });

        Assert.True(condition: written.Succeeded);
        Assert.Same(
            written,
            await first.Completion
        );
        Assert.False(condition: second.Completion.IsCompleted);
        Assert.Throws<InvalidOperationException>(testCode: () => first.Write(writer: _ => Assert.Fail(message: "duplicate write")));
    }
    [Fact]
    public async Task DeviceLossCompletesTheRequestAndStillReachesHostRecovery() {
        var request = new FrameCaptureRequest(path: "lost-device.png");
        var failure = new DeviceLostException(
            "readback lost the device",
            reasonCode: -4
        );

        Assert.Same(
            failure,
            Assert.Throws<DeviceLostException>(testCode: () => request.Write(writer: _ => throw failure))
        );
        Assert.True(condition: request.Completion.IsCompleted);
        Assert.Same(
            failure,
            (await request.Completion).Error
        );
        Assert.False(condition: request.TryFail(error: new ObjectDisposedException(objectName: "renderer")));
    }
    [Fact]
    public async Task WriterFailureAndUnservedDisposalAreTerminalResults() {
        var request = new FrameCaptureRequest(path: "failure.png");
        var failure = new IOException(message: "disk full");
        var result = request.Write(writer: _ => throw failure);

        Assert.False(condition: result.Succeeded);
        Assert.Same(
            failure,
            (await request.Completion).Error
        );
        Assert.False(condition: request.TryFail(error: new ObjectDisposedException(objectName: "renderer")));

        var unserved = new FrameCaptureRequest(path: "unserved.png");

        Assert.True(condition: unserved.TryFail(error: new ObjectDisposedException(objectName: "renderer")));
        Assert.IsType<ObjectDisposedException>(@object: (await unserved.Completion).Error);
        Assert.Throws<InvalidOperationException>(testCode: () => unserved.Write(writer: _ => Assert.Fail(message: "disposed write")));
    }
}
