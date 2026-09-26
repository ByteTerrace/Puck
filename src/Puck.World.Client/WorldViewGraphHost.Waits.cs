using System.Diagnostics;
using System.Globalization;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Shaders;

namespace Puck.World.Client;

/// <summary>A point in one pipeline instance's lifecycle that <c>pipeline.wait</c> holds a console session for.</summary>
public enum WorldPipelinePhase : byte {
    /// <summary>The latest requested compilation has completed, successfully or not.</summary>
    Compiled = 1,
    /// <summary>The latest compiled candidate is the installed, allocated graph.</summary>
    Installed = 2,
    /// <summary>The installed graph has submitted at least the requested number of frames since its last reset.</summary>
    Submitted = 3,
    /// <summary>The most recently requested capture has been written or has failed, and its report has been made.</summary>
    Captured = 4,
    /// <summary>The instance renders at the given output extent from here on: the host has requested the extent and the
    /// graph at that extent has installed. A resize is built off the frame thread from the instance's next frame, paused
    /// or running; a paused instance installs it without rendering, so its next step renders at that extent.</summary>
    Resized = 5,
    /// <summary>The work counts of at least the requested number of submissions since the instance's last reset (or
    /// since boot, when it was never reset) have completed on the GPU and are what <c>pipeline.inspect</c> shows.</summary>
    Counted = 6,
}
public sealed partial class WorldViewGraphHost {
    /// <summary>The longest deadline, in seconds, one <c>pipeline.wait</c> may hold a session for.</summary>
    public const int MaxWaitSeconds = 600;

    internal enum WaitState : byte {
        Pending,
        Reached,
        Failed,
        Unsupported,
    }

    public sealed partial class Entry {
        private long m_waitDeadline;
        private (uint Width, uint Height) m_waitExtent;
        private WorldPipelinePhase? m_waitPhase;
        private int m_waitSeconds;
        private ulong m_waitSubmissions;
        private Exception? m_waitSwapErrorAtArm;

        // Grown once, on the first counted poll, to the graph's pass count; polling reuses it.
        private readonly GpuWorkSample m_workSample = new();

        /// <summary>Gets whether a <c>pipeline.wait</c> is armed on this instance and has not yet been reported.</summary>
        public bool IsWaiting => m_waitPhase.HasValue;

        internal void ArmWait(WorldPipelinePhase phase, ulong submissions, (uint Width, uint Height) extent, int seconds) {
            m_waitPhase = phase;
            m_waitSubmissions = submissions;
            m_waitExtent = extent;
            m_waitSeconds = seconds;
            m_waitSwapErrorAtArm = Node.LastSwapError;
            m_waitDeadline = checked((Stopwatch.GetTimestamp() + (Stopwatch.Frequency * seconds)));
        }
        internal void ClearWait() {
            m_waitPhase = null;
            m_waitSwapErrorAtArm = null;
        }
        internal void CompleteCapture(string? error) {
            Capture = null;
            LastCaptureError = error;
        }

        // Detail is assigned only from strings that already exist, so polling a pending wait allocates nothing; the
        // pending description is built once, by Describe, when the deadline passes.
        private WaitState EvaluateCompiled(out string? detail) {
            detail = null;
            if (IsCompiling) { return WaitState.Pending; }
            if (LastCompile is not { } result) { detail = "no compilation was requested"; return WaitState.Failed; }
            detail = result.Message;
            return result.Status switch {
                ShaderPipelineLoadStatus.Compiled => WaitState.Reached,
                ShaderPipelineLoadStatus.Retry => WaitState.Pending,
                ShaderPipelineLoadStatus.Unsupported => WaitState.Unsupported,
                _ => WaitState.Failed,
            };
        }
        private WaitState EvaluateInstalled(out string? detail) {
            var compiled = EvaluateCompiled(detail: out detail);

            if (compiled != WaitState.Reached) { return compiled; }
            if (
                (Node.LastSwapError is { } error) &&
                !ReferenceEquals(
                objA: error,
                objB: m_waitSwapErrorAtArm
            )
            ) {
                detail = error.Message;
                return WaitState.Failed;
            }
            if (Node.IsReady && ReferenceEquals(
                objA: Node.Plan,
                objB: LastCompile!.Pipeline!.Plan
            )) {
                return WaitState.Reached;
            }

            return ((Refusal is { } refused)
                ? Refused(
                    detail: out detail,
                    refusal: refused
                )
                : WaitState.Pending);
        }
        // A refused instance (Refusal) reaches no phase past its compilation until a new compilation starts, so a wait on
        // it fails naming the refusal, and a capture it holds is withdrawn, since no frame will serve it.
        private WaitState Refused(string refusal, out string? detail) {
            if (Capture is { } pending) {
                _ = pending.TryFail(error: new InvalidOperationException(message: refusal));
                CompleteCapture(error: refusal);
            }

            detail = refusal;

            return ((LastCompile?.Status == ShaderPipelineLoadStatus.Unsupported)
                ? WaitState.Unsupported
                : WaitState.Failed);
        }
        // An instance builds a resize off the frame thread while it keeps presenting the old extent, paused or running, so
        // it has reached the phase only once the graph at the new extent has installed; a refusal since the wait was armed
        // fails it.
        private WaitState EvaluateResized(out string? detail) {
            detail = null;
            if (Refusal is { } refused) {
                return Refused(
                    detail: out detail,
                    refusal: refused
                );
            }
            if (Node.RequestedExtent != m_waitExtent) { return WaitState.Pending; }
            if (Node.Extent == m_waitExtent) { return WaitState.Reached; }
            if (
                (Node.LastSwapError is { } error) &&
                !ReferenceEquals(
                objA: error,
                objB: m_waitSwapErrorAtArm
            )
            ) {
                detail = error.Message;
                return WaitState.Failed;
            }
            return WaitState.Pending;
        }

        internal string Describe() => m_waitPhase switch {
            WorldPipelinePhase.Compiled => "compilation pending",
            WorldPipelinePhase.Installed => (IsCompiling
                ? "compilation pending"
                : "candidate not installed"),
            WorldPipelinePhase.Submitted => string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"frames={Node.FrameCounter} ready={(Node.IsReady ? "true" : "false")}"
            ),
            WorldPipelinePhase.Counted => (Node.TryReadCompleted(sample: m_workSample)
                ? string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"counted={(m_workSample.Submission - Node.ResetSubmission)} submission={m_workSample.Submission}"
                )
                : string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"frames={Node.FrameCounter} none completed"
                )),
            WorldPipelinePhase.Captured => "capture pending",
            WorldPipelinePhase.Resized => string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"requested={Node.RequestedExtent.Width}x{Node.RequestedExtent.Height} extent={Node.Extent.Width}x{Node.Extent.Height}"
            ),
            _ => "no wait armed",
        };
        internal string PhaseToken() => m_waitPhase switch {
            WorldPipelinePhase.Submitted => string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"submitted {m_waitSubmissions}"
            ),
            WorldPipelinePhase.Counted => string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"counted {m_waitSubmissions}"
            ),
            WorldPipelinePhase.Resized => string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"resized {m_waitExtent.Width} {m_waitExtent.Height}"
            ),
            _ => (m_waitPhase?.ToString().ToLowerInvariant() ?? "none"),
        };

        internal int WaitSeconds => m_waitSeconds;

        internal bool WaitExpired(long now) => (now >= m_waitDeadline);

        private WaitState EvaluateWait(out string? detail) {
            detail = null;
            switch (m_waitPhase) {
                case WorldPipelinePhase.Compiled:
                    return EvaluateCompiled(detail: out detail);
                case WorldPipelinePhase.Installed:
                    return EvaluateInstalled(detail: out detail);
                case WorldPipelinePhase.Submitted:
                    if (
                        Node.IsReady &&
                        (Node.FrameCounter >= m_waitSubmissions)
                    ) { return WaitState.Reached; }
                    var installed = EvaluateInstalled(detail: out detail);

                    return ((installed is WaitState.Failed or WaitState.Unsupported)
                        ? installed
                        : WaitState.Pending
                    );
                case WorldPipelinePhase.Counted:
                    if (
                        Node.TryReadCompleted(sample: m_workSample) &&
                        ((m_workSample.Submission - Node.ResetSubmission) >= ((long)m_waitSubmissions))
                    ) { return WaitState.Reached; }
                    var counting = EvaluateInstalled(detail: out detail);

                    return ((counting is WaitState.Failed or WaitState.Unsupported)
                        ? counting
                        : WaitState.Pending
                    );
                case WorldPipelinePhase.Resized:
                    return EvaluateResized(detail: out detail);
                case WorldPipelinePhase.Captured:
                    if (Capture is not null) {
                        return ((Refusal is { } refused)
                            ? Refused(
                                detail: out detail,
                                refusal: refused
                            )
                            : WaitState.Pending);
                    }
                    if (CapturesRequested == 0) { detail = "no capture was requested"; return WaitState.Failed; }
                    if (LastCaptureError is { } error) { detail = error; return WaitState.Failed; }
                    return WaitState.Reached;
                default:
                    return WaitState.Reached;
            }
        }

        internal bool TryResolveWait(out WaitState state, out string? detail) {
            state = EvaluateWait(detail: out detail);
            return (state != WaitState.Pending);
        }

        /// <summary>Arms a console capture of the selected output and records it for <see cref="WorldPipelinePhase.Captured"/>.</summary>
        /// <param name="request">The request the render node serves on its next produced or held frame.</param>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
        public void RequestCapture(FrameCaptureRequest request) {
            ArgumentNullException.ThrowIfNull(request);
            Node.RequestCapture(request: request);
            Capture = request;
            CapturesRequested++;
        }
    }

    /// <summary>Arms a lifecycle wait on a registered instance and returns the hold predicate for the issuing console
    /// session. The predicate runs on the command pump; it reports exactly one outcome through <see cref="Report"/> —
    /// <c>wait &lt;phase&gt; reached</c>, <c>failed: …</c>, <c>unsupported: …</c>, or <c>timed out after Ns: …</c> — and
    /// then releases the session. The deadline is a liveness bound in presentation time, never a measurement.</summary>
    /// <param name="name">The registered instance name.</param>
    /// <param name="phase">The lifecycle point to wait for.</param>
    /// <param name="submissions">The submitted-frame count <see cref="WorldPipelinePhase.Submitted"/> waits for, or the
    /// completed-submission count <see cref="WorldPipelinePhase.Counted"/> waits for, each since the last reset; ignored
    /// by the other phases.</param>
    /// <param name="extent">The output extent, in pixels, <see cref="WorldPipelinePhase.Resized"/> waits for; ignored by
    /// the other phases.</param>
    /// <param name="seconds">The deadline in whole seconds, 1 to <see cref="MaxWaitSeconds"/>.</param>
    /// <returns>The predicate that holds the session while the wait is unresolved.</returns>
    /// <exception cref="KeyNotFoundException"><paramref name="name"/> is not registered.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="seconds"/> is outside 1 to <see cref="MaxWaitSeconds"/>.</exception>
    /// <exception cref="InvalidOperationException">A wait is already armed on the instance.</exception>
    public Func<bool> ArmWait(string name, WorldPipelinePhase phase, ulong submissions, int seconds, (uint Width, uint Height) extent = default) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1,
            value: seconds
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: MaxWaitSeconds,
            value: seconds
        );
        var entry = m_entries[name];

        if (entry.IsWaiting) { throw new InvalidOperationException(message: $"'{name}' already has a wait armed."); }
        entry.ArmWait(
            extent: extent,
            phase: phase,
            seconds: seconds,
            submissions: submissions
        );
        return () => PollWait(
            entry: entry,
            name: name
        );
    }

    private bool PollWait(string name, Entry entry) {
        if (!entry.IsWaiting) { return false; }
        string outcome;

        if (
            !m_entries.TryGetValue(
            key: name,
            value: out var live
        ) ||
            !ReferenceEquals(
            objA: live,
            objB: entry
        )
        ) {
            outcome = "failed: the instance was removed";
        } else if (entry.TryResolveWait(
            detail: out var detail,
            state: out var state
        )) {
            outcome = state switch {
                WaitState.Reached => "reached",
                WaitState.Unsupported => $"unsupported: {detail}",
                _ => $"failed: {detail}",
            };
        } else if (entry.WaitExpired(now: Stopwatch.GetTimestamp())) {
            outcome = string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"timed out after {entry.WaitSeconds}s: {entry.Describe()}"
            );
        } else {
            return true;
        }
        Report?.Invoke(
            name,
            $"wait {entry.PhaseToken()} {outcome}"
        );
        entry.ClearWait();
        return false;
    }
}
