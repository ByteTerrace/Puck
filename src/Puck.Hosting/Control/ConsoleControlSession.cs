using System.Security.AccessControl;
using Puck.Networking;
using Puck.Abstractions.Presentation;
using Puck.Commands;

namespace Puck.Hosting;

/// <summary>Ordered text ingress with a host-selected principal and command guard; capture is supplied separately by the trusted host.</summary>
public sealed class ConsoleControlSession : IControlSession {
    private readonly Lock m_gate = new();
    private readonly CancellationTokenSource m_lifetime = new();

    private bool m_busy;
    private bool m_disposed;

    private readonly TextCommandSession m_session;
    private readonly Func<string, FrameCaptureRequest> m_capture;

    private TaskCompletionSource<CommandResult>? m_result;

    /// <summary>Creates a dedicated Console session. The capture delegate runs only on the ordinary command pump.</summary>
    /// <param name="source">The running host's existing text source.</param>
    /// <param name="capture">Arms the outermost render target or throws when unavailable.</param>
    /// <param name="slot">The host's immutable Console routing slot.</param>
    /// <param name="scope">Optional host scope entered by the ordinary command pump.</param>
    /// <param name="principal">The fixed acting principal; null uses the Console principal.</param>
    /// <param name="authorize">Optional command-metadata predicate forwarded to the dedicated text session.</param>
    public ConsoleControlSession(TextCommandSource source, Func<string, FrameCaptureRequest> capture, int slot = 0, Func<IDisposable>? scope = null, CommandPrincipal? principal = null, Func<CommandMetadata, bool>? authorize = null) {
        m_capture = capture;
        m_session = source.CreateSession(principal ?? CommandPrincipal.Console, slot: slot, scope: scope, authorize: authorize, onResult: (_, result) => Volatile.Read(location: ref m_result)?.TrySetResult(result: result));
    }

    /// <inheritdoc/>
    public async Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) {
        CancellationTokenSource deadline;

        lock (m_gate) {
            ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
            if (m_busy) { return new(0, "refused", "Console session busy; await the active operation.", true); }
            if (LocalControlServer.Validate(request: request) is { } refusal) { return refusal; }
            deadline = CancellationTokenSource.CreateLinkedTokenSource(token1: cancellationToken, token2: m_lifetime.Token);
            deadline.CancelAfter(millisecondsDelay: request.TimeoutMilliseconds);
            m_busy = true;
        }
        cancellationToken = deadline.Token;
        using var close = cancellationToken.Register(callback: static state => ((TextCommandSession)state!).Dispose(), state: m_session);

        try {
            if (request.Operation == "capture") { return await CaptureAsync(id: request.Id, token: cancellationToken).ConfigureAwait(continueOnCapturedContext: false); }
            var completion = new TaskCompletionSource<CommandResult>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

            Volatile.Write(location: ref m_result, value: completion);
            m_session.Enqueue(line: request.Command!);
            var result = await completion.Task.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            return new(request.Id, (result.IsError ? "refused" : (string.IsNullOrEmpty(value: result.Output) ? "submitted" : "completed")), result.Output, result.IsError, result.ClearTranscript);
        } catch (Exception error) when ((error is IOException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)) {
            return new(request.Id, "refused", error.Message, true);
        } finally {
            Volatile.Write(location: ref m_result, value: null);
            close.Dispose();
            deadline.Dispose();
            lock (m_gate) {
                m_busy = false;
                if (m_disposed) { m_lifetime.Dispose(); }
            }
        }
    }

    private async Task<ControlResponse> CaptureAsync(long id, CancellationToken token) {
        var directory = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-frame-{Guid.NewGuid():N}");

        CreateArtifactDirectory(path: directory);
        var path = Path.Combine(path1: directory, path2: "frame.png");
        FrameCaptureRequest? capture = null;

        try {
            // The engine barrier orders this behind this session's prior simulation submissions and waits.
            capture = await m_session.InvokeAsync(() => m_capture(path), token).ConfigureAwait(continueOnCapturedContext: false);
            var result = await capture.Completion.WaitAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);

            if (!result.Succeeded) { return new(id, "refused", $"Capture failed: {result.Error?.Message}", true); }
            await using var file = new FileStream(access: FileAccess.Read, bufferSize: 4096, mode: FileMode.Open, options: FileOptions.Asynchronous, path: path, share: FileShare.Read);

            if (file.Length is <= 0 or > ControlLimits.ImageBytes) { return new(id, "refused", "Completed PNG exceeds the 16 MiB image limit or is empty.", true); }
            var png = new byte[((int)file.Length)];

            await file.ReadExactlyAsync(buffer: png, cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
            return new(id, "completed", "Next composed frame; completion does not certify an earlier mutation or an exact simulation tick.", Png: png);
        } finally {
            if ((capture is not null) && !capture.Completion.IsCompleted) { _ = CleanAfterCaptureAsync(capture: capture, directory: directory); } else { Clean(directory: directory); }
        }
    }
    private static async Task CleanAfterCaptureAsync(FrameCaptureRequest capture, string directory) {
        await capture.Completion.ConfigureAwait(continueOnCapturedContext: false);
        Clean(directory: directory);
    }
    private static void CreateArtifactDirectory(string path) {
        if (OperatingSystem.IsWindows()) {
            var security = LocalUserAccess.Create<DirectorySecurity>();

            new DirectoryInfo(path: path).Create(security);
        } else {
            Directory.CreateDirectory(path: path, unixCreateMode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
    private static void Clean(string directory) {
        try { File.Delete(path: Path.Combine(path1: directory, path2: "frame.png")); Directory.Delete(path: directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
}
