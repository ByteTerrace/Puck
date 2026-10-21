using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Presentation;

/// <summary>The terminal outcome of one render capture. Success means the writer returned after closing the PNG;
/// it does not promise durable storage or that another process cannot subsequently change the file.</summary>
/// <param name="Path">The requested output path.</param>
/// <param name="Error">The capture failure, or null on success.</param>
/// <param name="Tick">The tick of the state the served image shows, the tick its server says the image was rendered at;
/// <see langword="null"/> when the server names none or no frame served the request.</param>
/// <param name="Frame">The serving node's ordinal of the frame that rendered the served image, counted as its completed
/// frames were when that frame's render finished; <see langword="null"/> when the server counts no frames or no frame
/// served the request.</param>
public sealed record FrameCaptureResult(string Path, Exception? Error, ulong? Tick = null, ulong? Frame = null) {
    /// <summary>Gets whether the PNG write completed successfully.</summary>
    public bool Succeeded => (Error is null);
    /// <summary>Gets a presented capture's frames in the order the display showed them, each with its PNG and its
    /// difference from the frame before it; empty for a settled capture and for one that failed.</summary>
    public IReadOnlyList<PresentedFrame> Presented { get; init; } = [];
    /// <summary>Gets the region a presented capture measured its differences over, or <see langword="null"/> for the
    /// whole frame and for a settled capture.</summary>
    public FrameCaptureRegion? Region { get; init; }
}
/// <summary>One process-local capture request, forwarded unchanged through a render chain. Arming and serving
/// happen on the render host's pump. Workers may await <see cref="Completion"/>; cancelling that wait does not
/// cancel an accepted capture or make its output path available for reuse.</summary>
/// <remarks>A settled request (<see cref="FrameCaptureMode.Settled"/>) is served once, by the node that rendered the
/// frame. A presented request (<see cref="Presented"/>) is served by the render-graph runtime, one frame each
/// presentation, until it holds <see cref="Frames"/>; it encodes each frame's PNG and measures its difference from the
/// frame before it off the render pump, and completes once every frame's file is written.</remarks>
public sealed class FrameCaptureRequest {
    /// <summary>The most frames one presented capture reads.</summary>
    public const int MaxPresentedFrames = 120;

    /// <summary>The presented frames whose encodes may wait at once; taking one more waits for a slot, which bounds the
    /// pixels a burst holds to this many frames.</summary>
    public const int MaxPendingFrames = 16;

    private readonly TaskCompletionSource<FrameCaptureResult> m_completion = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Task<PresentedFrame>> m_frames = [];
    private readonly SemaphoreSlim m_pending = new(initialCount: MaxPendingFrames, maxCount: MaxPendingFrames);

    private int m_claimed;
    private long m_lastPresentation = -1;
    private (byte[] Rgba, int Width, int Height)? m_previous;

    /// <summary>Creates an unserved settled request (<see cref="FrameCaptureMode.Settled"/>). The caller owns directory
    /// creation and path policy.</summary>
    /// <param name="path">The PNG path.</param>
    /// <param name="converge">The number of rendered samples to capture; zero captures one render with no extra
    /// samples. Either way the capture holds presentation from the frame it is armed in until it is served: the
    /// renderer keeps rendering that held frame, with no presentation time passing, and serves a render of it.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="converge"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or white space.</exception>
    public FrameCaptureRequest(string path, int converge = 0) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: converge);
        Converge = converge;
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        Frames = 1;
    }

    private FrameCaptureRequest(string path, int frames, FrameCaptureRegion? region) : this(path: path) {
        ArgumentOutOfRangeException.ThrowIfLessThan(other: 1, value: frames);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(other: MaxPresentedFrames, value: frames);
        Frames = frames;
        Mode = FrameCaptureMode.Presented;
        Region = region;
    }

    /// <summary>Creates an unserved presented request (<see cref="FrameCaptureMode.Presented"/>): the frames the display
    /// presents once it is armed, each read back as composed for the display, holding and settling nothing. The caller
    /// owns directory creation and path policy.</summary>
    /// <param name="path">The PNG path of a one-frame capture, and the name a burst's frames are numbered from
    /// (<see cref="PathOf"/>).</param>
    /// <param name="frames">The consecutive presented frames to read, 1 through <see cref="MaxPresentedFrames"/>.</param>
    /// <param name="region">The region each frame's difference from the one before it is measured over, or
    /// <see langword="null"/> for the whole frame.</param>
    /// <returns>The request.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frames"/> is outside 1 through
    /// <see cref="MaxPresentedFrames"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or white space.</exception>
    public static FrameCaptureRequest Presented(string path, int frames = 1, FrameCaptureRegion? region = null) => new(
        frames: frames,
        path: path,
        region: region
    );

    /// <summary>Gets this request's terminal result. Failures are result data, so fire-and-forget console requests
    /// do not leave unobserved task exceptions. Continuations never run inline on the render pump.</summary>
    public Task<FrameCaptureResult> Completion => m_completion.Task;
    /// <summary>Gets the requested PNG path.</summary>
    public string Path { get; }
    /// <summary>Gets the number of frozen samples requested, or zero for an ordinary capture.</summary>
    public int Converge { get; }
    /// <summary>Gets whether the request is past serving: a writer or a refusal has claimed it, or a presented request has
    /// taken its last frame, though its encodes may still be writing until <see cref="Completion"/> completes.</summary>
    public bool Claimed => (Volatile.Read(location: ref m_claimed) != 0);
    /// <summary>Gets the frames the capture reads: one for a settled capture.</summary>
    public int Frames { get; }
    /// <summary>Gets how the capture reads the render chain's frames.</summary>
    public FrameCaptureMode Mode { get; }
    /// <summary>Gets the region a presented capture measures its frame-to-frame differences over, or
    /// <see langword="null"/> for the whole frame.</summary>
    public FrameCaptureRegion? Region { get; }

    /// <summary>Returns the PNG path of one of the capture's frames: <see cref="Path"/> itself for a one-frame capture,
    /// otherwise <see cref="Path"/> with the frame's three-digit index before its extension (frame 2 of
    /// <c>flicker.png</c> is <c>flicker-002.png</c>).</summary>
    /// <param name="index">The frame's position in the capture, from zero.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the capture's frames.</exception>
    public string PathOf(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(other: Frames, value: index);

        if (Frames == 1) {
            return Path;
        }

        var extension = System.IO.Path.GetExtension(path: Path);
        var stem = Path[..(Path.Length - extension.Length)];

        return string.Create(
            provider: System.Globalization.CultureInfo.InvariantCulture,
            handler: $"{stem}-{index:000}{extension}"
        );
    }
    /// <summary>Takes one presented frame (<see cref="FrameCaptureMode.Presented"/>): queues the encode of its PNG and the
    /// measure of its difference from the frame taken before it on the thread pool, and once it holds
    /// <see cref="Frames"/> frames completes when every encode has finished. A presentation already taken is ignored.
    /// The caller waits for an encode to finish only while <see cref="MaxPendingFrames"/> frames wait for theirs.</summary>
    /// <param name="rgba">The frame's pixels, four bytes a pixel, rows top to bottom; the request owns the array.</param>
    /// <param name="width">The frame's width, in pixels.</param>
    /// <param name="height">The frame's height, in pixels.</param>
    /// <param name="presentation">The render chain's index of the presentation the frame was shown on.</param>
    /// <param name="rendered">The presentation the shown image was rendered on.</param>
    /// <param name="tick">The simulation tick the shown image was rendered from, or <see langword="null"/>.</param>
    /// <param name="encode">Writes one PNG (path, pixels, width, height) and closes it before returning; it runs on the
    /// thread pool, and a throw fails the capture.</param>
    /// <returns><see langword="true"/> once the request takes no more frames: it has taken its last, or it was refused or
    /// withdrawn.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rgba"/> or <paramref name="encode"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The request is settled.</exception>
    public bool Present(byte[] rgba, int width, int height, long presentation, long rendered, ulong? tick, Action<string, byte[], int, int> encode) {
        ArgumentNullException.ThrowIfNull(argument: rgba);
        ArgumentNullException.ThrowIfNull(argument: encode);

        if (Mode != FrameCaptureMode.Presented) {
            throw new InvalidOperationException(message: "A settled capture is served by a frame's writer, not by presented frames.");
        }
        if (Claimed) {
            return true;
        }
        if (presentation == m_lastPresentation) {
            return false;
        }

        m_lastPresentation = presentation;

        var index = m_frames.Count;
        var path = PathOf(index: index);
        var previous = (((m_previous is { } earlier) && (earlier.Width == width) && (earlier.Height == height))
            ? earlier.Rgba
            : null);
        var region = Region;

        m_pending.Wait();
        m_previous = (rgba, width, height);
        m_frames.Add(item: Task.Run(function: () => {
            try {
                var delta = ((previous is null)
                    ? null
                    : PresentedFrameDelta.Of(current: rgba, height: height, previous: previous, region: region, width: width));

                encode(arg1: path, arg2: rgba, arg3: width, arg4: height);

                return new PresentedFrame(
                    Delta: delta,
                    Height: height,
                    Index: index,
                    Path: path,
                    Presentation: presentation,
                    Rendered: rendered,
                    Tick: tick,
                    Width: width
                );
            } finally {
                _ = m_pending.Release();
            }
        }));

        if (m_frames.Count < Frames) {
            return false;
        }
        if (Interlocked.CompareExchange(
            comparand: 0,
            location1: ref m_claimed,
            value: 1
        ) != 0) {
            return true;
        }

        m_previous = null;
        _ = Task.WhenAll(tasks: m_frames).ContinueWith(
            continuationAction: static (_, state) => ((FrameCaptureRequest)state!).CompletePresented(),
            cancellationToken: CancellationToken.None,
            continuationOptions: TaskContinuationOptions.None,
            scheduler: TaskScheduler.Default,
            state: this
        );

        return true;
    }

    /// <summary>Refuses an unserved request: a renderer being disposed, or the requester withdrawing a capture it
    /// will no longer wait for, which a <see cref="CaptureRequestSlot"/> still holding it then drops. A write that
    /// has started retains ownership of its outcome.</summary>
    /// <param name="error">The reason no capture will be written.</param>
    /// <returns>Whether this call completed the request.</returns>
    public bool TryFail(Exception error) {
        ArgumentNullException.ThrowIfNull(error);
        if (Interlocked.CompareExchange(
            comparand: 0,
            location1: ref m_claimed,
            value: 1
        ) != 0) {
            return false;
        }

        m_completion.SetResult(result: new FrameCaptureResult(
            Path,
            error
        ));
        return true;
    }
    /// <summary>Serves this request once, completing only after the synchronous readback and PNG writer returns.
    /// A writer exception becomes a failed result. Device loss also propagates to the host's recovery loop;
    /// ordinary capture failures do not interrupt rendering.</summary>
    /// <param name="writer">The readback and PNG writer. It must close the file before returning.</param>
    /// <param name="tick">The tick of the state the image the writer reads was rendered from, which the result records
    /// (<see cref="FrameCaptureResult.Tick"/>): the tick of the frame that rendered it, however many frames before it is
    /// served; <see langword="null"/> when the server names none.</param>
    /// <param name="frame">The serving node's ordinal of the frame that rendered that image
    /// (<see cref="FrameCaptureResult.Frame"/>); <see langword="null"/> when the server counts none.</param>
    /// <returns>The terminal result, including any writer exception.</returns>
    /// <exception cref="InvalidOperationException">Another owner already claimed or refused this request, or the request
    /// is presented.</exception>
    /// <exception cref="DeviceLostException">Readback lost the graphics device. Completion contains the same
    /// failure before this signal is rethrown for host recovery.</exception>
    public FrameCaptureResult Write(Action<string> writer, ulong? tick = null, ulong? frame = null) {
        ArgumentNullException.ThrowIfNull(writer);
        if (Mode != FrameCaptureMode.Settled) {
            throw new InvalidOperationException(message: "A presented capture is served by the render-graph runtime, one presented frame at a time.");
        }
        if (Interlocked.CompareExchange(
            comparand: 0,
            location1: ref m_claimed,
            value: 1
        ) != 0) {
            throw new InvalidOperationException(message: "The capture request has already been served or refused.");
        }

        Exception? error = null;

        try {
            writer(Path);
        } catch (DeviceLostException exception) {
            m_completion.SetResult(result: new FrameCaptureResult(
                Path,
                exception,
                tick,
                frame
            ));
            throw;
        } catch (Exception exception) {
            error = exception;
        }

        var result = new FrameCaptureResult(
            Path,
            error,
            tick,
            frame
        );

        m_completion.SetResult(result: result);
        return result;
    }

    // Completes a presented capture once every frame's encode has finished: its frames in order, or its first failure.
    private void CompletePresented() {
        var frames = new List<PresentedFrame>(capacity: m_frames.Count);
        Exception? error = null;

        foreach (var frame in m_frames) {
            if (frame.IsCompletedSuccessfully) {
                frames.Add(item: frame.Result);
            } else {
                error ??= (frame.Exception?.InnerException ?? new InvalidOperationException(message: "A presented frame's encode did not complete."));
            }
        }

        m_completion.SetResult(result: new FrameCaptureResult(
            Error: error,
            Frame: ((frames.Count > 0) ? ((ulong)frames[0].Rendered) : null),
            Path: Path,
            Tick: ((frames.Count > 0) ? frames[0].Tick : null)
        ) {
            Presented = ((error is null) ? frames : []),
            Region = Region,
        });
    }
}
