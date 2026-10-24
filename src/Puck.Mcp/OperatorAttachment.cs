using System.Collections.Concurrent;
using System.Globalization;

using Puck.Hosting;
using Puck.Networking;

namespace Puck.Mcp;

/// <summary>
/// The Operator adapter's World attachments, opened on demand. A call that names no World runs on the current
/// attachment: the pinned capability file's World, or, when <paramref name="target"/> is a directory, the newest
/// running World there that answers, kept until its attachment closes. A call that names a World runs on exactly that
/// World's own attachment, opened when there is none, and never falls back to another; naming a World does not change
/// the current one. A closed attachment is dropped, and the next call attaches again; the call that found it closed is
/// reported, never replayed. Every attachment it opens runs its connect, handshake and call deadlines on
/// <paramref name="clock"/>.
/// </summary>
internal sealed class OperatorAttachment(string target, TimeProvider clock) : IDisposable {
    private const string CapabilityPattern = "puck-control-*.json";

    private readonly ConcurrentDictionary<string, LocalControlClient> m_clients = new(comparer: StringComparer.Ordinal);
    private readonly SemaphoreSlim m_gate = new(initialCount: 1, maxCount: 1);

    private volatile string? m_current;
    private bool m_disposed;

    /// <summary>Gets whether the adapter follows a directory of attachment files rather than one pinned World.</summary>
    public bool Follows => Directory.Exists(path: target);
    /// <summary>Gets the capability file or directory the adapter attaches through.</summary>
    public string Target => target;

    /// <summary>Closes every attachment. Does not stop any World.</summary>
    public void Dispose() {
        m_disposed = true;
        m_current = null;
        foreach (var id in m_clients.Keys) {
            if (m_clients.TryRemove(key: id, value: out var client)) {
                client.Dispose();
            }
        }
    }
    /// <summary>Lists the running Worlds that publish an Operator attachment file this adapter can address: every live
    /// one in the directory it follows, or the one it is pinned to. A file whose host process is gone is left out.
    /// Never waits for a call in flight.</summary>
    /// <returns>Each World with whether it is the current one, newest first.</returns>
    public IReadOnlyList<(LocalEndpointInfo Info, bool Current)> List() {
        var current = m_current;

        return [.. Discover().Select(selector: entry => (entry.Info, (string.Equals(a: entry.Info.Id, b: current, comparisonType: StringComparison.Ordinal) && m_clients.TryGetValue(key: entry.Info.Id, value: out var client) && !client.IsClosed)))];
    }
    /// <summary>Runs one call on a live attachment, opening one when there is none or the host has hung up. Calls are
    /// serialized, so a call that closes its attachment has dropped it before the next call attaches.</summary>
    /// <param name="world">The World the call names, by attachment id or process id; <see langword="null"/> runs it on
    /// the current attachment.</param>
    /// <param name="call">The operation to run on the attachment.</param>
    /// <param name="refuse">Builds the result when the World cannot be attached; nothing is dispatched.</param>
    /// <param name="cancellationToken">Cancels waiting, attaching and the call.</param>
    /// <returns>The call's result, or the refusal.</returns>
    public async Task<TResult> RunAsync<TResult>(string? world, Func<LocalControlClient, CancellationToken, Task<TResult>> call, Func<string, TResult> refuse, CancellationToken cancellationToken) {
        await m_gate.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        try {
            var (client, refusal) = await AttachAsync(world: world, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (client is null) {
                return refuse(refusal!);
            }

            try {
                return await call(client, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            } finally {
                if (client.IsClosed) {
                    Forget(client: client);
                }
            }
        } finally {
            m_gate.Release();
        }
    }

    private static bool IsAttachFailure(Exception error, CancellationToken cancellationToken) =>
        ((error is IOException or InvalidDataException or UnauthorizedAccessException or System.Net.Sockets.SocketException or System.Text.Json.JsonException) ||
        ((error is OperationCanceledException) && !cancellationToken.IsCancellationRequested));
    private static bool Names(LocalEndpointInfo info, string world) =>
        (string.Equals(a: info.Id, b: world, comparisonType: StringComparison.OrdinalIgnoreCase) ||
        (int.TryParse(s: world, style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out var processId) && (processId == info.ProcessId)));

    private IEnumerable<(string Path, LocalEndpointInfo Info)> Discover() {
        if (!Directory.Exists(path: target)) {
            LocalEndpointInfo? pinned = null;

            try {
                pinned = LocalEndpointCapability.ReadDescriptor(path: target).Info;
            } catch (Exception error) when ((error is UnauthorizedAccessException or IOException or InvalidDataException or System.Text.Json.JsonException)) { }

            if (pinned?.IsHostRunning() == true) {
                yield return (target, pinned);
            }

            yield break;
        }

        var files = Directory.GetFiles(
            path: target,
            searchPattern: CapabilityPattern
        );

        // Newest first. A descriptor whose host process is gone, or that cannot be read, is never offered; a World
        // that exited without removing its file would otherwise be tried on every call.
        foreach (var file in files.Select(selector: path => new FileInfo(fileName: path)).OrderByDescending(keySelector: file => file.LastWriteTimeUtc).ThenBy(keySelector: file => file.FullName, comparer: StringComparer.Ordinal)) {
            LocalEndpointInfo? info = null;

            try {
                info = LocalEndpointCapability.ReadDescriptor(path: file.FullName).Info;
            } catch (Exception error) when ((error is UnauthorizedAccessException or IOException or InvalidDataException or System.Text.Json.JsonException)) { }

            if (info?.IsHostRunning() == true) {
                yield return (file.FullName, info);
            }
        }
    }
    private void Forget(LocalControlClient client) {
        _ = m_clients.TryRemove(item: new KeyValuePair<string, LocalControlClient>(key: client.Endpoint.Id, value: client));
        _ = Interlocked.CompareExchange(comparand: client.Endpoint.Id, location1: ref m_current, value: null);
    }
    private async Task<LocalControlClient> OpenAsync(string path, CancellationToken cancellationToken) {
        var client = await LocalControlClient.ConnectAsync(
            attachmentPath: path,
            cancellationToken: cancellationToken,
            clock: clock
        ).ConfigureAwait(continueOnCapturedContext: false);

        m_clients[client.Endpoint.Id] = client;

        return client;
    }
    private async Task<(LocalControlClient? Client, string? Refusal)> AttachAsync(string? world, CancellationToken cancellationToken) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        foreach (var open in m_clients.Values) {
            if (open.CloseIfHostGone()) {
                Forget(client: open);
            }
        }

        var pinned = !Directory.Exists(path: target);

        if (world is not null) {
            return await AttachNamedAsync(world: world, pinned: pinned, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        if ((m_current is { } currentId) && m_clients.TryGetValue(key: currentId, value: out var current)) {
            return (current, null);
        }

        if (pinned) {
            try {
                var client = await OpenAsync(path: target, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

                m_current = client.Endpoint.Id;

                return (client, null);
            } catch (Exception error) when (IsAttachFailure(cancellationToken: cancellationToken, error: error)) {
                return (null, $"No World is attached at {target} ({error.Message}). Run `world.control start` in that World, or run the adapter without --attach to follow the newest World.");
            }
        }

        foreach (var (path, _) in Discover()) {
            try {
                var client = await OpenAsync(path: path, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

                m_current = client.Endpoint.Id;

                return (client, null);
            } catch (Exception error) when (IsAttachFailure(cancellationToken: cancellationToken, error: error)) { }
        }

        return (null, $"No running World accepts an Operator attachment (searched {Path.Combine(path1: target, path2: CapabilityPattern)}). Start World and run `world.control start`; the next call attaches.");
    }
    private async Task<(LocalControlClient? Client, string? Refusal)> AttachNamedAsync(string world, bool pinned, CancellationToken cancellationToken) {
        var running = Discover().Where(predicate: entry => Names(info: entry.Info, world: world)).ToArray();

        if (running.Length == 0) {
            var scope = (pinned
                ? $" This adapter is pinned to {target} by --attach and serves no other World."
                : "");

            return (null, $"No running World matches '{world}'; nothing was dispatched and no other World was tried. puck_worlds lists the Worlds that accept an Operator attachment.{scope}");
        }
        if (running.Length > 1) {
            return (null, $"'{world}' matches {running.Length} running Worlds (attachment ids {string.Join(separator: ", ", values: running.Select(selector: entry => entry.Info.Id))}); name one by attachment id. Nothing was dispatched.");
        }

        var (path, info) = running[0];

        if (m_clients.TryGetValue(key: info.Id, value: out var open)) {
            return (open, null);
        }

        try {
            return (await OpenAsync(path: path, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false), null);
        } catch (Exception error) when (IsAttachFailure(cancellationToken: cancellationToken, error: error)) {
            return (null, $"World {info.Id} (process {info.ProcessId}) did not accept an Operator attachment ({error.Message}); nothing was dispatched and no other World was tried.");
        }
    }
}
