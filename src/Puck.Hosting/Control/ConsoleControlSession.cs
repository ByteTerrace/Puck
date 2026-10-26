using System.Security.AccessControl;
using Puck.Networking;
using Puck.Abstractions.Presentation;
using Puck.Commands;

namespace Puck.Hosting;

/// <summary>Ordered text ingress with a host-selected principal and command guard; capture is supplied separately by the trusted host.</summary>
public sealed class ConsoleControlSession : IControlSession {
    private readonly Lock m_gate = new();
    private readonly CancellationTokenSource m_lifetime = new();

    private readonly Action<FrameCaptureRequest> m_capture;
    private readonly TimeProvider m_clock;
    private readonly TextCommandSession m_session;

    private bool m_busy;
    private bool m_disposed;
    private TaskCompletionSource<CommandResult>? m_result;
    private Action<CommandResult>? m_scriptResult;

    /// <summary>Creates a dedicated Console session. The capture delegate runs only on the ordinary command pump.</summary>
    /// <param name="source">The running host's existing text source.</param>
    /// <param name="capture">Arms a capture request the session made, settled or presented, on the outermost render
    /// target, or throws when unavailable.</param>
    /// <param name="slot">The host's immutable Console routing slot.</param>
    /// <param name="scope">Optional host scope entered by the ordinary command pump.</param>
    /// <param name="principal">The fixed acting principal; null uses the Console principal.</param>
    /// <param name="authorize">Optional command-metadata predicate forwarded to the dedicated text session.</param>
    /// <param name="clock">Drives each request's deadline; <see langword="null"/> is <see cref="TimeProvider.System"/>.</param>
    public ConsoleControlSession(TextCommandSource source, Action<FrameCaptureRequest> capture, int slot = 0, Func<IDisposable>? scope = null, Principal? principal = null, Func<CommandMetadata, bool>? authorize = null, TimeProvider? clock = null) {
        m_capture = capture;
        m_clock = (clock ?? TimeProvider.System);
        m_session = source.CreateSession(
            (principal ?? Principal.Console),
            slot: slot,
            scope: scope,
            authorize: authorize,
            // A scripted ingress like stdin: its simulation lines land in the next step that takes input, whatever the
            // wall-clock capture stamp would say on a host running behind.
            dueNextTick: true,
            // The settled result, never the submit-time one: a simulation-routed line answers nothing until its tick
            // applies, an edit answers nothing until the authority decides it, and a line that holds the session
            // answers when its hold ends.
            onSettled: (_, result) => {
                if (Volatile.Read(location: ref m_scriptResult) is { } scriptResult) {
                    scriptResult(result);
                } else {
                    Volatile.Read(location: ref m_result)?.TrySetResult(result: result);
                }
            }
        );
    }

    /// <summary>The longest margin <see cref="AnswerBudget"/> keeps back from a call's deadline.</summary>
    public static readonly TimeSpan AnswerMargin = TimeSpan.FromSeconds(seconds: 2);

    /// <summary>Returns the time a call's line may take before it must answer: the call's deadline less a tenth of it,
    /// at most <see cref="AnswerMargin"/>, left for the answer to reach the pump's next drain and travel back. A hold
    /// the line arms ends at this point with an answer, so the caller's own deadline does not close the attachment.</summary>
    /// <param name="timeout">The call's deadline; not negative.</param>
    /// <returns>The answer budget.</returns>
    public static TimeSpan AnswerBudget(TimeSpan timeout) => (timeout - TimeSpan.FromTicks(value: Math.Min(
        val1: (timeout.Ticks / 10),
        val2: AnswerMargin.Ticks
    )));

    private async Task<ControlResponse> CaptureAsync(long id, ControlCapture? options, CancellationToken token) {
        var presented = (options?.Mode == ControlCapture.Presented);
        var burst = (options is { IsBurst: true });
        var directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: (burst ? $"puck-burst-{Guid.NewGuid():N}" : $"puck-frame-{Guid.NewGuid():N}")
        );

        CreateArtifactDirectory(path: directory);
        var path = Path.Combine(
            path1: directory,
            path2: "frame.png"
        );
        var request = (presented
            ? FrameCaptureRequest.Presented(
                frames: options!.Frames,
                path: path,
                region: ((options.Region is [var x, var y, var width, var height]) ? new FrameCaptureRegion(Height: height, Width: width, X: x, Y: y) : null)
            )
            : new FrameCaptureRequest(path: path));
        FrameCaptureRequest? capture = null;
        var pinned = options?.Tick;
        TaskCompletionSource<CommandResult>? waited = null;

        try {
            // A pinned capture holds this session until its tick completes; the capture behind the hold then arms in
            // the drain before the next tick steps, so the still shows exactly that tick.
            if (pinned is { } due) {
                waited = new TaskCompletionSource<CommandResult>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(
                    location: ref m_result,
                    value: waited
                );
                m_session.Enqueue(line: string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"world.wait until {due}"));
            }
            // The engine barrier orders this behind this session's prior simulation submissions and waits.
            capture = await m_session.InvokeAsync(
                () => {
                    // The wait settles on the pump before this runs, so a refused one is seen here.
                    if (waited?.Task is { IsCompleted: true, Result: { IsError: true } refusal }) {
                        throw new InvalidOperationException(message: $"The capture pinned to tick {pinned} was not armed: {refusal.Output}");
                    }

                    m_capture(request);

                    // A pin the world had already passed arms a still of a later tick: withdrawn, never answered as the pin.
                    if ((pinned is { } at) && (request.StateTick is { } held) && (held != at)) {
                        _ = request.TryFail(error: new InvalidOperationException(message: $"the world had completed tick {held} when the capture pinned to tick {at} armed; pin a later tick"));
                    }

                    return request;
                },
                token
            ).ConfigureAwait(continueOnCapturedContext: false);
            var result = await capture.Completion.WaitAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);

            if (!result.Succeeded) {
                return new(
                id,
                "refused",
                $"Capture failed: {result.Error?.Message}",
                true
            );
            }
            // A burst's frames stay on this host for the caller to read; its answer is their report.
            if (burst) {
                return new(
                    id,
                    "completed",
                    PresentedFrameReport.Format(frames: result.Presented, region: result.Region)
                );
            }
            await using var file = new FileStream(
                access: FileAccess.Read,
                bufferSize: 4096,
                mode: FileMode.Open,
                options: FileOptions.Asynchronous,
                path: path,
                share: FileShare.Read
            );

            if (file.Length is <= 0 or > ControlLimits.ImageBytes) {
                return new(
                id,
                "refused",
                "Completed PNG exceeds the 16 MiB image limit or is empty.",
                true
            );
            }
            var png = new byte[((int)file.Length)];

            await file.ReadExactlyAsync(
                buffer: png,
                cancellationToken: token
            ).ConfigureAwait(continueOnCapturedContext: false);
            return new(
                id,
                "completed",
                (presented
                    ? $"Next presented frame, read as the display showed it; completion does not certify an earlier mutation or an exact simulation tick.\n{PresentedFrameReport.Format(frames: result.Presented, region: result.Region)}"
                    : ((request.StateStamp is { } stamp)
                        ? $"Settled still held at {stamp}; the frame shows tick {(result.Tick?.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")}."
                        : "Next composed frame; completion does not certify an earlier mutation or an exact simulation tick.")),
                Png: png
            );
        } finally {
            // A presented capture holds no clock budget to end it, so one this call stops waiting for is withdrawn.
            if (presented) {
                _ = capture?.TryFail(error: new OperationCanceledException(message: "The control call stopped waiting for the presented capture."));
            }
            // A landed burst's frames are its answer's subject, so they stay for the caller to read.
            var kept = (burst && (capture is { Completion.IsCompletedSuccessfully: true }) && capture.Completion.Result.Succeeded);

            if (
                !kept &&
                (capture is not null) &&
                !capture.Completion.IsCompleted
            ) {
                _ = CleanAfterCaptureAsync(
                capture: capture,
                directory: directory
            );
            } else if (!kept) { Clean(directory: directory); }
        }
    }
    private static void Clean(string directory) {
        try {
            Directory.Delete(path: directory, recursive: true);
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static async Task CleanAfterCaptureAsync(FrameCaptureRequest capture, string directory) {
        await capture.Completion.ConfigureAwait(continueOnCapturedContext: false);
        Clean(directory: directory);
    }
    private static void CreateArtifactDirectory(string path) {
        if (OperatingSystem.IsWindows()) {
            var security = LocalUserAccess.Create<DirectorySecurity>();

            new DirectoryInfo(path: path).Create(directorySecurity: security);
        } else {
            Directory.CreateDirectory(
                path: path,
                unixCreateMode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        lock (m_gate) {
            if (m_disposed) { return; }
            m_disposed = true;
            m_lifetime.Cancel();
            m_session.Dispose();
            if (!m_busy) { m_lifetime.Dispose(); }
        }
    }
    /// <inheritdoc/>
    public async Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) {
        OperationDeadline deadline;

        lock (m_gate) {
            ObjectDisposedException.ThrowIf(
                condition: m_disposed,
                instance: this
            );
            if (m_busy) {
                return new(
                0,
                "refused",
                "Console session busy; await the active operation.",
                true
            );
            }
            if (LocalControlServer.Validate(request: request) is { } refusal) { return refusal; }
            deadline = new(
                caller: cancellationToken,
                lifetime: m_lifetime.Token,
                timeout: TimeSpan.FromMilliseconds(value: request.TimeoutMilliseconds),
                timeProvider: m_clock
            );
            m_busy = true;
        }
        cancellationToken = deadline.Token;
        using var close = cancellationToken.Register(
            callback: static state => ((TextCommandSession)state!).Dispose(),
            state: m_session
        );

        m_session.AnswerDeadline = new TextAnswerDeadline(
            budget: AnswerBudget(timeout: TimeSpan.FromMilliseconds(value: request.TimeoutMilliseconds)),
            clock: m_clock
        );

        try {
            if (request.Operation == "capture") {
                return await CaptureAsync(
                id: request.Id,
                options: request.Capture,
                token: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
            }
            var lines = request.ReadCommands();
            var completion = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
            List<ControlCommandResult> results = [];
            var remaining = ControlLimits.OutputCharacters;

            Volatile.Write(location: ref m_scriptResult, value: result => {
                var line = lines[results.Count];
                var bounded = new ControlResponse(request.Id,
                    (result.IsError ? "refused" : (string.IsNullOrEmpty(value: result.Output) ? "submitted" : "completed")),
                    result.Output, result.IsError, result.ClearTranscript)
                    .BoundedTo(maximumCharacters: (remaining - (256 * ((lines.Length - results.Count) - 1))));

                remaining -= bounded.Output.Length;
                results.Add(item: new(line.Line, line.Command, bounded.Status, bounded.Output, bounded.IsError, bounded.ClearTranscript, bounded.Truncated));
                if (results.Count == lines.Length) { completion.TrySetResult(); }
            });
            // Queue the entire script on the pump. No tick can observe just a prefix arriving from the worker.
            if (lines.Length == 1) {
                m_session.Enqueue(line: lines[0].Command);
            } else {
                await m_session.InvokeAsync(() => {
                    foreach (var line in lines) { m_session.Enqueue(line: line.Command); }
                    return true;
                }, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }
            await completion.Task.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            var failed = results.Any(predicate: static result => result.IsError);
            var multiline = (request.Command!.IndexOfAny(anyOf: ['\r', '\n']) >= 0);
            var output = (multiline ? string.Empty : results[0].Output);

            return new ControlResponse(
                request.Id,
                (failed ? "refused" : (results.Any(predicate: static result => !string.IsNullOrEmpty(value: result.Output)) ? "completed" : "submitted")),
                output,
                failed,
                results.Any(predicate: static result => result.ClearTranscript),
                Truncated: results.Any(predicate: static result => result.Truncated),
                Commands: (multiline ? [.. results] : null)
            ).Bounded();
        } catch (Exception error) when ((error is IOException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)) {
            return new(
                request.Id,
                "refused",
                error.Message,
                true
            );
        } finally {
            m_session.AnswerDeadline = null;
            Volatile.Write(location: ref m_scriptResult, value: null);
            Volatile.Write(
                location: ref m_result,
                value: null
            );
            close.Dispose();
            deadline.Dispose();
            lock (m_gate) {
                m_busy = false;
                if (m_disposed) { m_lifetime.Dispose(); }
            }
        }
    }
}
