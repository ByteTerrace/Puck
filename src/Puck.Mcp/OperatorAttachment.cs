using Puck.Hosting;
using Puck.Networking;

namespace Puck.Mcp;

/// <summary>
/// The Operator adapter's current World attachment, opened on demand. A closed attachment is dropped, and the next
/// tool call attaches again: to the pinned capability file, or, when <paramref name="target"/> is a directory, to the
/// newest World there that answers. The call that found the attachment closed is reported, never replayed. Every
/// attachment it opens runs its connect, handshake and call deadlines on <paramref name="clock"/>.
/// </summary>
internal sealed class OperatorAttachment(string target, TimeProvider clock) : IDisposable {
    private const string CapabilityPattern = "puck-control-*.json";

    private readonly SemaphoreSlim m_gate = new(initialCount: 1, maxCount: 1);

    private LocalControlClient? m_client;
    private bool m_disposed;

    /// <summary>Closes the current attachment. Does not stop any World.</summary>
    public void Dispose() {
        m_disposed = true;
        Interlocked.Exchange(location1: ref m_client, value: null)?.Dispose();
    }
    /// <summary>Runs one call on a live attachment, opening one when there is none or the host has hung up. Calls are
    /// serialized, so a call that closes its attachment has dropped it before the next call attaches.</summary>
    /// <param name="call">The operation to run on the attachment.</param>
    /// <param name="refuse">Builds the result when no World can be attached; nothing is dispatched.</param>
    /// <param name="cancellationToken">Cancels waiting, attaching and the call.</param>
    /// <returns>The call's result, or the refusal.</returns>
    public async Task<TResult> RunAsync<TResult>(Func<LocalControlClient, CancellationToken, Task<TResult>> call, Func<string, TResult> refuse, CancellationToken cancellationToken) {
        await m_gate.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        try {
            var (client, refusal) = await AttachAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (client is null) {
                return refuse(refusal!);
            }

            try {
                return await call(client, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            } finally {
                if (client.IsClosed) {
                    _ = Interlocked.CompareExchange(comparand: client, location1: ref m_client, value: null);
                }
            }
        } finally {
            m_gate.Release();
        }
    }

    private static bool IsAttachFailure(Exception error, CancellationToken cancellationToken) =>
        ((error is IOException or InvalidDataException or UnauthorizedAccessException or System.Net.Sockets.SocketException or System.Text.Json.JsonException) ||
        ((error is OperationCanceledException) && !cancellationToken.IsCancellationRequested));
    private static IEnumerable<string> LatestCandidates(string directory) {
        var files = Directory.GetFiles(
            path: directory,
            searchPattern: CapabilityPattern
        );

        // Newest first; a World that exited without removing its file refuses the connection and is skipped.
        foreach (var file in files.Select(selector: path => new FileInfo(fileName: path)).OrderByDescending(keySelector: file => file.LastWriteTimeUtc)) {
            var readable = false;

            try {
                _ = LocalEndpointCapability.ReadDescriptor(path: file.FullName);
                readable = true;
            } catch (Exception error) when ((error is UnauthorizedAccessException or IOException or InvalidDataException or System.Text.Json.JsonException)) { }

            if (readable) {
                yield return file.FullName;
            }
        }
    }
    private async Task<(LocalControlClient? Client, string? Refusal)> AttachAsync(CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        if (m_client is { } current) {
            if (!current.CloseIfHostGone()) {
                return (current, null);
            }

            m_client = null;
        }

        if (!Directory.Exists(path: target)) {
            try {
                m_client = await LocalControlClient.ConnectAsync(
                    attachmentPath: target,
                    cancellationToken: cancellationToken,
                    clock: clock
                ).ConfigureAwait(continueOnCapturedContext: false);

                return (m_client, null);
            } catch (Exception error) when (IsAttachFailure(cancellationToken: cancellationToken, error: error)) {
                return (null, $"No World is attached at {target} ({error.Message}). Run `world.control start` in that World, or run the adapter without --attach to follow the newest World.");
            }
        }

        foreach (var candidate in LatestCandidates(directory: target)) {
            try {
                m_client = await LocalControlClient.ConnectAsync(
                    attachmentPath: candidate,
                    cancellationToken: cancellationToken,
                    clock: clock
                ).ConfigureAwait(continueOnCapturedContext: false);

                return (m_client, null);
            } catch (Exception error) when (IsAttachFailure(cancellationToken: cancellationToken, error: error)) { }
        }

        return (null, $"No running World accepts an Operator attachment (searched {Path.Combine(path1: target, path2: CapabilityPattern)}). Start World and run `world.control start`; the next call attaches.");
    }
}
