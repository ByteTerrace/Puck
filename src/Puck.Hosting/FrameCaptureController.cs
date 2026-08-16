using Puck.Abstractions.Capture;
using Puck.Abstractions.Presentation;

namespace Puck.Hosting;

/// <summary>
/// Owns one optional frame-capture session for a host. The launcher supplies the exact root surface and its
/// <see cref="FrameContext"/>; this controller owns session cadence, indexing, the frame budget, and isolation of
/// capture-only failures. It has no recording or backend dependency.
/// </summary>
public sealed class FrameCaptureController : IDisposable {
    private sealed class Session(ICaptureSink sink, int frameRate, int maxFrames) {
        public UInt128 CadenceAccumulator;
        public long CapturedFrameCount;
        public Exception? Fault;
        public bool HasRenderTimestamp;
        public ulong LastRenderTicks;

        public readonly int FrameRate = frameRate;
        public readonly int MaxFrames = maxFrames;
        public readonly ICaptureSink Sink = sink;
    }

    private readonly Lock m_sync = new();

    private bool m_disposed;
    private Session? m_session;

    /// <summary>Gets the armed sink, or <see langword="null"/> when idle.</summary>
    public ICaptureSink? CurrentSink {
        get {
            lock (m_sync) {
                return m_session?.Sink;
            }
        }
    }
    /// <summary>Gets the failure that disabled the current session, or <see langword="null"/>.</summary>
    public Exception? Fault {
        get {
            lock (m_sync) {
                return m_session?.Fault;
            }
        }
    }
    /// <summary>Gets whether a sink is armed, including a session whose capture path has faulted and awaits an explicit
    /// disarm so its owner can finalize it.</summary>
    public bool IsArmed {
        get {
            lock (m_sync) {
                return (m_session is not null);
            }
        }
    }
    /// <summary>Gets whether an armed session is able to accept another frame.</summary>
    public bool WantsFrames {
        get {
            lock (m_sync) {
                return (
                    (m_session is { Fault: null } session) &&
                    !BudgetExhausted(session: session)
                );
            }
        }
    }

    private static bool BudgetExhausted(Session session) =>
        ((session.MaxFrames > 0) && (session.CapturedFrameCount >= session.MaxFrames));
    private static bool IsCaptureDue(Session session, ulong renderTicks) {
        if (!session.HasRenderTimestamp) {
            session.HasRenderTimestamp = true;
            session.LastRenderTicks = renderTicks;

            return true;
        }

        var deltaTicks = ((renderTicks >= session.LastRenderTicks)
            ? (renderTicks - session.LastRenderTicks)
            : 0UL
        );

        session.LastRenderTicks = renderTicks;
        session.CadenceAccumulator += (((UInt128)deltaTicks) * ((uint)session.FrameRate));

        if (session.CadenceAccumulator < EngineTicks.PerSecond) {
            return false;
        }

        session.CadenceAccumulator %= EngineTicks.PerSecond;

        return true;
    }
    private static Surface ResolvePixels(Surface surface, IPresentSurfaceReadback? readback) {
        if (
            surface.IsEmpty ||
            surface.IsCpuPixels
        ) {
            return surface;
        }

        if (readback is null) {
            throw new NotSupportedException(message: "The active surface presenter does not support frame readback.");
        }

        var captured = readback.ReadSurface(surface: surface);

        if (captured.IsEmpty) {
            return captured;
        }

        if (!captured.IsCpuPixels) {
            throw new InvalidOperationException(message: "Surface readback returned a non-CPU surface.");
        }

        if (
            (captured.Width != surface.Width) ||
            (captured.Height != surface.Height) ||
            (captured.Format != surface.Format)
        ) {
            throw new InvalidOperationException(message: "Surface readback changed the source extent or pixel format.");
        }

        return captured;
    }

    /// <summary>Arms a new session. Cadence, frame index, and frame budget begin from zero for every arm.</summary>
    /// <param name="sink">The sink that receives captured frames. The controller owns it until
    /// <see cref="Disarm"/> returns it.</param>
    /// <param name="options">The capture cadence and per-session frame budget.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="CaptureOptions.FrameRate"/> is less than one, or
    /// <see cref="CaptureOptions.MaxFrames"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">A session is already armed.</exception>
    public void Arm(ICaptureSink sink, CaptureOptions options) {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: options.FrameRate,
            other: 1
        );
        ArgumentOutOfRangeException.ThrowIfNegative(value: options.MaxFrames);

        lock (m_sync) {
            ObjectDisposedException.ThrowIf(
                condition: m_disposed,
                instance: this
            );

            if (m_session is not null) {
                throw new InvalidOperationException(message: "A frame-capture session is already armed.");
            }

            m_session = new Session(
                frameRate: options.FrameRate,
                maxFrames: options.MaxFrames,
                sink: sink
            );
        }
    }
    /// <summary>Captures the supplied root surface when the armed session's engine-time cadence is due. The method is
    /// intended for the launcher render thread immediately after root production and before presentation.</summary>
    /// <param name="surface">The exact root surface produced for this frame.</param>
    /// <param name="context">The same frame context supplied to the root node.</param>
    /// <param name="readback">The active presenter's optional GPU-surface readback capability.</param>
    public void Capture(Surface surface, in FrameContext context, IPresentSurfaceReadback? readback) {
        lock (m_sync) {
            if (
                (m_session is not { Fault: null } session) ||
                BudgetExhausted(session: session) ||
                !IsCaptureDue(
                session: session,
                renderTicks: context.RenderTicks
            )
            ) {
                return;
            }

            try {
                var captured = ResolvePixels(
                    readback: readback,
                    surface: surface
                );

                if (captured.IsEmpty) {
                    return;
                }

                session.Sink.Consume(frame: new CaptureFrame(
                    FrameIndex: session.CapturedFrameCount,
                    Surface: captured,
                    TimestampTicks: context.ElapsedTicks
                ));
                session.CapturedFrameCount++;
            } catch (Exception exception) {
                session.Fault = exception;

                try {
                    Console.Error.WriteLine(value: $"capture | disabled after error: {exception.Message}");
                } catch (Exception) {
                    // The session is already disabled; there is no second error channel safe for the render thread.
                }
            }
        }
    }
    /// <summary>Disarms and returns the current sink, including one whose capture path faulted, so its owner can
    /// finalize it. Returns <see langword="null"/> when idle.</summary>
    public ICaptureSink? Disarm() {
        lock (m_sync) {
            var sink = m_session?.Sink;

            m_session = null;

            return sink;
        }
    }
    /// <inheritdoc/>
    public void Dispose() {
        ICaptureSink? sink;

        lock (m_sync) {
            if (m_disposed) {
                return;
            }

            m_disposed = true;
            sink = m_session?.Sink;
            m_session = null;
        }

        sink?.Dispose();
    }
}
