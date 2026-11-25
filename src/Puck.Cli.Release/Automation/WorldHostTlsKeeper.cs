using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Storage;

namespace Puck.Cli.Automation;

/// <summary>Keeps the MCP front's TLS state (certificate, key and ACME account) in the world owner's private store,
/// <c>puck world host tls</c>. The front keeps that state in its data directory on the machine's own disk, so without
/// the keeper every fresh machine would register a new ACME account and issue a new certificate for the same name, and
/// Let's Encrypt allows only five new certificates per exact set of names in seven days.
/// <para>The invariant: only the holder of the issuance lease lets the front start without a fresh certificate in hand.
/// Startup is the one door: systemd starts the front only after this keeper reports READY, and it reports READY only
/// when the restored or local certificate is outside the front's renewal window, or once it holds the lease. A second
/// machine that finds no fresh certificate waits for the holder to store one (or for its lease to lapse), then restores
/// it, so a name issues once however many machines start. Afterwards the keeper stores whichever certificate is newer,
/// so a renewal reaches the store and a machine that sees another's renewal loads it instead of renewing.</para>
/// <para>The lease is a store document written only by compare-and-swap on its version token, holding its holder and
/// expiry, so it works over any store the provider supplies. Its expiry is judged on each machine's clock, which bounds
/// the guarantee by their skew; a lease is far longer than the skew of synchronized hosts.</para></summary>
[System.Runtime.Versioning.SupportedOSPlatform(platformName: "linux")]
public static class WorldHostTlsKeeper {
    private const string Schema = "puck.world.tls-state.v1";
    private const string LeaseSchema = "puck.world.tls-lease.v1";
    // The front's certificate and ACME account trees; locks, OCSP staples and the instance id stay per machine.
    private static readonly string[] Trees = ["certificates", "acme"];
    private static readonly TimeSpan LeaseLifetime = TimeSpan.FromSeconds(seconds: 60);
    private static readonly TimeSpan Round = TimeSpan.FromSeconds(seconds: 20);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(seconds: 10);
    private static readonly TimeSpan SyncPeriod = TimeSpan.FromMinutes(minutes: 10);
    // How long a holder keeps the lease while the front has not yet stored a fresh certificate; the front keeps retrying
    // on its own afterwards, and another machine may take its turn.
    private static readonly TimeSpan HoldLimit = TimeSpan.FromMinutes(minutes: 15);

    private sealed record State([property: JsonPropertyName("schema")] string Schema, [property: JsonPropertyName("host")] string Host,
        [property: JsonPropertyName("files")] SortedDictionary<string, string> Files);
    private sealed record Lease([property: JsonPropertyName("schema")] string Schema, [property: JsonPropertyName("holder")] Guid Holder,
        [property: JsonPropertyName("expires")] long Expires);
    private readonly record struct Span(DateTimeOffset NotBefore, DateTimeOffset NotAfter);

    /// <summary>Runs the keeper until the process is stopped.</summary>
    /// <param name="machinePath">The machine record's path (the keeper unit's credential).</param>
    /// <param name="cancellationToken">Stops the keeper.</param>
    /// <returns>A task that completes when the keeper is stopped.</returns>
    /// <exception cref="InvalidDataException">The machine record carries no MCP front.</exception>
    public static async Task RunAsync(string machinePath, CancellationToken cancellationToken) {
        var machine = WorldHostMachine.Read(path: machinePath);
        var mcp = (machine.Inputs.Mcp ?? throw new InvalidDataException(message: "the machine record carries no MCP front"));
        var store = WorldHostStore.Open(owner: machine.Owner, store: machine.Store);
        var keeper = new Keeper(Data: Path.Combine(path1: WorldHostLayout.TlsStateDirectory, path2: "caddy"), Host: mcp.Host.ToLowerInvariant(), Store: store,
            StateKey: new ObjectBlobAddress(ObjectId: machine.Owner, Key: (mcp.TlsState + "state.json")), LockKey: new ObjectBlobAddress(ObjectId: machine.Owner, Key: (mcp.TlsState + "lock")));
        var lease = await keeper.StartAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        var held = DateTimeOffset.UtcNow;
        var synced = DateTimeOffset.MinValue;

        Notify(message: "READY=1");
        while (!cancellationToken.IsCancellationRequested) {
            try {
                if (lease is { } holder) {
                    lease = await keeper.HoldAsync(held: held, lease: holder, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                } else if ((DateTimeOffset.UtcNow - synced) > SyncPeriod) {
                    _ = await keeper.SyncAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                    synced = DateTimeOffset.UtcNow;
                }
            } catch (Exception exception) when (exception is not OperationCanceledException) {
                // The keeper never takes the front down: a failed round is retried on the next.
                Log(message: $"TLS state round failed: {exception.Message}");
            }
            await Task.Delay(cancellationToken: cancellationToken, delay: Round).ConfigureAwait(continueOnCapturedContext: false);
        }
    }

    private sealed record Keeper(string Data, string Host, WorldHostStore Store, ObjectBlobAddress StateKey, ObjectBlobAddress LockKey) {
        // Waits until the front may start. Returns the issuance lease's holder id when this machine must issue.
        public async Task<Guid?> StartAsync(CancellationToken cancellationToken) {
            var waiting = false;

            while (true) {
                try {
                    if (Fresh(span: Leaf(files: LocalFiles()))) { return null; }
                    if (Fresh(span: await SyncAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false))) { return null; }
                    if (await AcquireAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false) is { } lease) {
                        // Another machine may have stored its certificate between the read and the lease.
                        if (Fresh(span: await SyncAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false))) {
                            await ReleaseAsync(cancellationToken: cancellationToken, lease: lease).ConfigureAwait(continueOnCapturedContext: false);
                            return null;
                        }
                        Log(message: "holding the TLS issuance lease; the front may issue");
                        return lease;
                    }
                    if (!waiting) {
                        Log(message: "another machine holds the TLS issuance lease; waiting for its certificate");
                        waiting = true;
                    }
                } catch (Exception exception) when (exception is not OperationCanceledException) {
                    Log(message: $"TLS state unavailable, retrying: {exception.Message}");
                }
                await Task.Delay(cancellationToken: cancellationToken, delay: Wait).ConfigureAwait(continueOnCapturedContext: false);
            }
        }
        // Advances one round while this machine holds the lease taken at held; returns the lease, or null once the issued
        // certificate is stored or the lease is given up.
        public async Task<Guid?> HoldAsync(Guid lease, DateTimeOffset held, CancellationToken cancellationToken) {
            if (Fresh(span: Leaf(files: LocalFiles()))) {
                _ = await SyncAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                await ReleaseAsync(cancellationToken: cancellationToken, lease: lease).ConfigureAwait(continueOnCapturedContext: false);
                Log(message: "stored the issued certificate and released the TLS issuance lease");
                return null;
            }
            if (((DateTimeOffset.UtcNow - held) > HoldLimit) || !await RenewAsync(cancellationToken: cancellationToken, lease: lease).ConfigureAwait(continueOnCapturedContext: false)) {
                await ReleaseAsync(cancellationToken: cancellationToken, lease: lease).ConfigureAwait(continueOnCapturedContext: false);
                Log(message: "gave up the TLS issuance lease before the front stored a certificate");
                return null;
            }
            return lease;
        }
        // Stores the local certificate when it is newer than the stored one, and restores the stored one when it is newer.
        // Returns the local certificate's validity afterwards.
        public async Task<Span?> SyncAsync(CancellationToken cancellationToken) {
            var (files, version) = await StoredAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            var mine = LocalFiles();
            var theirs = Leaf(files: files);
            var local = Leaf(files: mine);

            if (Newer(left: local, right: theirs)) {
                var body = JsonSerializer.SerializeToUtf8Bytes(value: new State(Files: new SortedDictionary<string, string>(dictionary: mine.ToDictionary(keySelector: static pair => pair.Key, elementSelector: static pair => Convert.ToBase64String(inArray: pair.Value)), comparer: StringComparer.Ordinal), Host: Host, Schema: Schema));
                var written = await Store.Blobs.WriteAsync(address: StateKey, cancellationToken: cancellationToken, content: body, ifMatchVersion: version,
                    mode: ((version is null) ? ObjectBlobWriteMode.CreateOnly : ObjectBlobWriteMode.Overwrite), target: Store.Target).ConfigureAwait(continueOnCapturedContext: false);

                if (written.Succeeded) { Log(message: "stored the local TLS state"); }
                return local;
            }
            // A stored account is restored even without a newer certificate, so this machine never registers another.
            if (Newer(left: theirs, right: local) || (HasAccount(files: files) && !HasAccount(files: mine))) {
                Restore(files: files);
                Log(message: "restored the stored TLS state");
                return Leaf(files: LocalFiles());
            }
            return local;
        }
        private async Task<(Dictionary<string, byte[]> Files, string? Version)> StoredAsync(CancellationToken cancellationToken) {
            if (await Store.Blobs.ReadAsync(address: StateKey, cancellationToken: cancellationToken, target: Store.Target).ConfigureAwait(continueOnCapturedContext: false) is not { } found) { return ([], null); }
            var state = (JsonSerializer.Deserialize<State>(utf8Json: found.Content.Span) ?? throw new InvalidDataException(message: "the stored TLS state is empty"));

            if ((state.Schema != Schema) || (state.Host != Host)) { throw new InvalidDataException(message: "the stored TLS state belongs to another host or schema"); }
            return (state.Files.ToDictionary(keySelector: static pair => pair.Key, elementSelector: static pair => Convert.FromBase64String(s: pair.Value), comparer: StringComparer.Ordinal), found.VersionToken);
        }
        private void Restore(Dictionary<string, byte[]> files) {
            foreach (var (name, content) in files) {
                var parts = name.Split(separator: '/');

                if (!Trees.Contains(value: parts[0]) || parts.Any(predicate: static part => (part.Length == 0) || part.StartsWith(value: '.'))) {
                    throw new InvalidDataException(message: "the stored TLS state names a path outside the front's trees");
                }
                var path = Path.Combine(paths: [Data, .. parts]);

                _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!, unixCreateMode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                if (File.Exists(path: path) && File.ReadAllBytes(path: path).AsSpan().SequenceEqual(other: content)) { continue; }
                // Each file is replaced whole, so the front never reads one half written.
                var temporary = Path.Combine(path1: Path.GetDirectoryName(path: path)!, path2: $".{Path.GetFileName(path: path)}.puck");

                File.WriteAllBytes(path: temporary, bytes: content);
                File.SetUnixFileMode(path: temporary, mode: UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(destFileName: path, overwrite: true, sourceFileName: temporary);
            }
        }
        private Dictionary<string, byte[]> LocalFiles() {
            var files = new Dictionary<string, byte[]>(comparer: StringComparer.Ordinal);

            foreach (var tree in Trees) {
                var root = Path.Combine(path1: Data, path2: tree);

                if (!Directory.Exists(path: root)) { continue; }
                foreach (var path in Directory.EnumerateFiles(path: root, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
                    if (Path.GetFileName(path: path).StartsWith(value: '.')) { continue; }
                    files[Path.GetRelativePath(path: path, relativeTo: Data).Replace(newChar: '/', oldChar: '\\')] = File.ReadAllBytes(path: path);
                }
            }
            return files;
        }
        // The front's file storage names a certificate's directory and files after the lowercase host name.
        private Span? Leaf(Dictionary<string, byte[]> files) {
            var suffix = $"/{Host}/{Host}.crt";
            Span? newest = null;

            foreach (var (name, content) in files) {
                if (!name.StartsWith(value: "certificates/", comparisonType: StringComparison.Ordinal) || !name.EndsWith(value: suffix, comparisonType: StringComparison.Ordinal)) { continue; }
                using var certificate = X509Certificate2.CreateFromPem(certPem: Encoding.ASCII.GetString(bytes: content));
                var span = new Span(NotAfter: certificate.NotAfter.ToUniversalTime(), NotBefore: certificate.NotBefore.ToUniversalTime());

                if ((newest is null) || (span.NotAfter > newest.Value.NotAfter)) { newest = span; }
            }
            return newest;
        }
        // The lease document is created once and afterwards only replaced over the version this machine read.
        private async Task<Guid?> AcquireAsync(CancellationToken cancellationToken) {
            var found = await Store.Blobs.ReadAsync(address: LockKey, cancellationToken: cancellationToken, target: Store.Target).ConfigureAwait(continueOnCapturedContext: false);

            if ((found is { } current) && (JsonSerializer.Deserialize<Lease>(utf8Json: current.Content.Span) is { } lease) && (lease.Expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds())) { return null; }
            var holder = Guid.NewGuid();

            return ((await WriteLeaseAsync(cancellationToken: cancellationToken, expires: (DateTimeOffset.UtcNow + LeaseLifetime), holder: holder, version: found?.VersionToken).ConfigureAwait(continueOnCapturedContext: false)) ? holder : null);
        }
        private async Task<bool> RenewAsync(Guid lease, CancellationToken cancellationToken) {
            if (await HeldVersionAsync(cancellationToken: cancellationToken, lease: lease).ConfigureAwait(continueOnCapturedContext: false) is not { } version) { return false; }
            return await WriteLeaseAsync(cancellationToken: cancellationToken, expires: (DateTimeOffset.UtcNow + LeaseLifetime), holder: lease, version: version).ConfigureAwait(continueOnCapturedContext: false);
        }
        private async Task ReleaseAsync(Guid lease, CancellationToken cancellationToken) {
            if (await HeldVersionAsync(cancellationToken: cancellationToken, lease: lease).ConfigureAwait(continueOnCapturedContext: false) is { } version) {
                _ = await WriteLeaseAsync(cancellationToken: cancellationToken, expires: DateTimeOffset.UnixEpoch, holder: lease, version: version).ConfigureAwait(continueOnCapturedContext: false);
            }
        }
        // Returns the lease document's version when this machine still holds it.
        private async Task<string?> HeldVersionAsync(Guid lease, CancellationToken cancellationToken) {
            var found = await Store.Blobs.ReadAsync(address: LockKey, cancellationToken: cancellationToken, target: Store.Target).ConfigureAwait(continueOnCapturedContext: false);

            return (((found is { } current) && (JsonSerializer.Deserialize<Lease>(utf8Json: current.Content.Span)?.Holder == lease)) ? current.VersionToken : null);
        }
        private async Task<bool> WriteLeaseAsync(Guid holder, DateTimeOffset expires, string? version, CancellationToken cancellationToken) {
            var body = JsonSerializer.SerializeToUtf8Bytes(value: new Lease(Expires: expires.ToUnixTimeSeconds(), Holder: holder, Schema: LeaseSchema));
            var written = await Store.Blobs.WriteAsync(address: LockKey, cancellationToken: cancellationToken, content: body, ifMatchVersion: version,
                mode: ((version is null) ? ObjectBlobWriteMode.CreateOnly : ObjectBlobWriteMode.Overwrite), target: Store.Target).ConfigureAwait(continueOnCapturedContext: false);

            return written.Succeeded;
        }
    }

    // The front's default renewal window is the last third of a certificate's lifetime.
    private static bool Fresh(Span? span) => ((span is { } value) && ((value.NotAfter - DateTimeOffset.UtcNow) > ((value.NotAfter - value.NotBefore) / 3)));
    private static bool Newer(Span? left, Span? right) => ((left is { } value) && ((right is null) || (value.NotAfter > right.Value.NotAfter)));
    private static bool HasAccount(Dictionary<string, byte[]> files) => files.Keys.Any(predicate: static name => name.StartsWith(value: "acme/", comparisonType: StringComparison.Ordinal));
    private static void Log(string message) => Console.Out.WriteLine(value: message);
    // systemd's readiness notification: one datagram to the socket the manager names, an abstract one when it starts with @.
    private static void Notify(string message) {
        if (Environment.GetEnvironmentVariable(variable: "NOTIFY_SOCKET") is not { Length: > 0 } path) { return; }
        using var socket = new Socket(addressFamily: AddressFamily.Unix, protocolType: ProtocolType.Unspecified, socketType: SocketType.Dgram);

        socket.Connect(remoteEP: new UnixDomainSocketEndPoint(path: (path.StartsWith(value: '@') ? ('\0' + path[1..]) : path)));
        _ = socket.Send(buffer: Encoding.ASCII.GetBytes(s: message));
    }
}
