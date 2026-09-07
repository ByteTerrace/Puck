using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;

namespace Puck.Abstractions.Tests;

public sealed class FrameCaptureRequestTests {
    [Fact]
    public async Task DeviceLossCompletesTheRequestAndStillReachesHostRecovery() {
        var request = new FrameCaptureRequest("lost-device.png");
        var failure = new DeviceLostException("readback lost the device", reasonCode: -4);
        Assert.Same(failure, Assert.Throws<DeviceLostException>(() => request.Write(_ => throw failure)));
        Assert.True(request.Completion.IsCompleted);
        Assert.Same(failure, (await request.Completion).Error);
        Assert.False(request.TryFail(new ObjectDisposedException("renderer")));
    }

    [Fact]
    public async Task CompletionFollowsTheWriteAndRemainsBoundToThatRequest() {
        var first = new FrameCaptureRequest("first.png");
        var second = new FrameCaptureRequest("second.png");
        var written = first.Write(path => {
            Assert.Equal("first.png", path);
            Assert.False(first.Completion.IsCompleted);
            Assert.False(first.TryFail(new IOException("too late to refuse an active write")));
        });
        Assert.True(written.Succeeded);
        Assert.Same(written, await first.Completion);
        Assert.False(second.Completion.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => first.Write(_ => Assert.Fail("duplicate write")));
    }

    [Fact]
    public async Task WriterFailureAndUnservedDisposalAreTerminalResults() {
        var request = new FrameCaptureRequest("failure.png");
        var failure = new IOException("disk full");
        var result = request.Write(_ => throw failure);
        Assert.False(result.Succeeded);
        Assert.Same(failure, (await request.Completion).Error);
        Assert.False(request.TryFail(new ObjectDisposedException("renderer")));

        var unserved = new FrameCaptureRequest("unserved.png");
        Assert.True(unserved.TryFail(new ObjectDisposedException("renderer")));
        Assert.IsType<ObjectDisposedException>((await unserved.Completion).Error);
        Assert.Throws<InvalidOperationException>(() => unserved.Write(_ => Assert.Fail("disposed write")));
    }

    [Fact]
    public async Task CancellingAWaitDoesNotCancelOrCompleteTheCapture() {
        var request = new FrameCaptureRequest("capture.png");
        using var cancellation = new CancellationTokenSource();
        var wait = request.Completion.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);
        Assert.False(request.Completion.IsCompleted);
        _ = request.Write(_ => { });
        Assert.True((await request.Completion).Succeeded);
    }
}
