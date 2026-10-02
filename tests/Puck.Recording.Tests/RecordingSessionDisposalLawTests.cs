using Puck.Abstractions.Capture;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Recording;
using Puck.Recording.Document;
using Puck.Recording.Session;
using Puck.Testing;
using Xunit;

namespace Puck.Recording.Tests;

/// <summary>
/// A recording session's disposal returns only after its encode thread has left the encoder and the output file is
/// closed: a caller that deletes the recording's directory next never races a thread still writing to it.
/// </summary>
public sealed class RecordingSessionDisposalLawTests {
    // An encoder whose frame encode parks until released, so the encode thread is observably inside it.
    private sealed class ParkedEncoder : IVideoEncoder, IVideoEncoderFactory {
        public string CodecId => "V_MPEG4/ISO/AVC";
        public ReadOnlyMemory<byte> CodecPrivate => ReadOnlyMemory<byte>.Empty;

        public TaskCompletionSource Entered { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(initialState: false);

        public volatile bool Disposed;

        public IVideoEncoder? Create(IReadOnlyList<string> codecLadder, int width, int height, int frameRate, int bitrateKilobitsPerSecond, out string reason) {
            reason = string.Empty;

            return this;
        }
        public void Dispose() => Disposed = true;
        public IReadOnlyList<RecordedPacket> Drain() => [];
        public IReadOnlyList<RecordedPacket> EncodeFrame(ReadOnlySpan<byte> pixels, GpuPixelFormat format, int width, int height, long timestampNanoseconds) {
            Entered.TrySetResult();
            Release.Wait();

            return [];
        }
    }

    [Fact]
    public async Task DisposalWaitsForTheEncodeThreadToLeaveTheEncoderAndClosesTheOutput() {
        using var directory = new TemporaryDirectory(prefix: "puck-recording-disposal-");
        var encoder = new ParkedEncoder();
        var output = directory.PathOf(name: "capture.mkv");

        try {
            Assert.True(
                condition: RecordingSession.TryCreate(
                    options: new RecordingSessionOptions {
                        Clock = new RecordingSessionClock(),
                        Document = (RecordingDocument.CreateDefault() with { Audio = null, Output = output }),
                        SourceHeight = 2,
                        SourceWidth = 2,
                        VideoEncoderFactory = encoder,
                    },
                    reason: out var reason,
                    session: out var session
                ),
                userMessage: reason
            );

            using (session) {
                session!.Consume(frame: new CaptureFrame(
                    FrameIndex: 0L,
                    Surface: Surface.CpuPixels(
                        format: GpuPixelFormat.R8G8B8A8Unorm,
                        height: 2U,
                        pixels: new byte[16],
                        width: 2U
                    ),
                    TimestampTicks: 0UL
                ));
                await encoder.Entered.Task.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);

                var disposal = Task.Run(
                    action: session.Dispose,
                    cancellationToken: CancellationToken.None
                );

                // Control: the encode thread is parked inside the encoder, so a disposal that does not wait for it would
                // have returned by now.
                Assert.NotSame(
                    actual: await Task.WhenAny(
                        task1: disposal,
                        task2: Task.Delay(
                            cancellationToken: TestContext.Current.CancellationToken,
                            delay: TimeSpan.FromMilliseconds(value: 300)
                        )
                    ),
                    expected: disposal
                );
                Assert.False(condition: encoder.Disposed);
                encoder.Release.Set();
                await disposal.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
            }

            Assert.True(condition: encoder.Disposed);
            // The output is closed: nothing holds it open, so it can be deleted at once.
            File.Delete(path: output);
        } finally {
            encoder.Release.Set();
        }
    }
}
