using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions;
using Puck.Cli.Canary;
using Puck.Cli.Sweep;
using Puck.World;

namespace Puck.Cli.Bench;

public static partial class BenchmarkCommand {
    // The namespace the staged pair's authority and client share: the client names it as the authority it expects at the
    // endpoint, and the authority's admission pins the pair's key under it.
    private const string PairNamespace = "benchmark-pair";

    /// <summary>A remote authority and its client on one machine, staged from one world: a delta over the world's entry that
    /// names the pair's namespace, admits remote players and pins the pair's own federation key, which both processes boot
    /// with one key file. The authority listens on a free loopback port. The delta stands beside the entry, since a
    /// delta resolves the documents its basis references (adjacencies, references) beside itself, and disposal removes
    /// it.</summary>
    /// <param name="Document">The staged delta both processes boot.</param>
    /// <param name="KeyFile">The federation key both processes sign with.</param>
    /// <param name="Endpoint">The loopback endpoint the authority listens on and the client connects to.</param>
    private sealed record RemotePair(string Document, string KeyFile, string Endpoint) : IDisposable {
        /// <summary>Removes the staged delta.</summary>
        public void Dispose() {
            try {
                File.Delete(path: Document);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                Console.Error.WriteLine(value: $"{Verb}: the remote pair's staged document {PuckPaths.Normalize(path: Document)} could not be removed: {exception.Message}");
            }
        }

        /// <summary>Stages a pair from a world: its package folder, <c>.puck</c> source or document.</summary>
        /// <param name="directory">The directory the key is written to.</param>
        /// <param name="world">The world.</param>
        /// <returns>The staged pair, whose disposal removes the delta it wrote beside the world's entry.</returns>
        /// <exception cref="InvalidOperationException">The world's package names no entry this verb can stage over.</exception>
        public static RemotePair Stage(string directory, string world) {
            _ = Directory.CreateDirectory(path: directory);

            var entry = world;

            if (Directory.Exists(path: world) && !WorldPackageManifest.TryResolveEntry(entryPath: out entry, manifest: out _, path: world, reason: out var reason)) {
                throw new InvalidOperationException(message: $"the remote pair stages over the world's entry, and the package {PuckPaths.Normalize(path: world)} names none: {reason}");
            }

            var identity = CanaryCommand.GenerateFederationIdentity();
            var endpoint = string.Create(provider: CultureInfo.InvariantCulture, handler: $"127.0.0.1:{CanaryCommand.GetFreeLoopbackPort()}");
            var key = Path.Combine(path1: directory, path2: "federation.key");
            var full = Path.GetFullPath(path: entry);
            var basis = Path.GetFileName(path: full);

            if (basis.EndsWith(value: WorldDocumentName.SourceSuffix, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                basis = basis[..^WorldDocumentName.SourceSuffix.Length];
            }

            var document = Path.Combine(path1: Path.GetDirectoryName(path: full)!, path2: $".benchmark-pair-{Guid.NewGuid():N}{WorldDocumentName.SourceSuffix}");
            var source = new StringBuilder()
                .Append(provider: CultureInfo.InvariantCulture, handler: $"// The benchmark's remote pair: the world as its authority and a client on one machine.\nbasis: {JsonValue.Create(value: basis).ToJsonString()}\n")
                .Append(provider: CultureInfo.InvariantCulture, handler: $"host {{ authorityNamespace: \"{PairNamespace}\" }}\n")
                .Append(value: "bodies { networkPlayers: 1 }\n")
                .Append(value: "admission [\n")
                .Append(value: "  { disclosure: Replica, domain: \"*\", grants [{ budget: 8, capability: Drive, exclusive: true }, { budget: 64, capability: Observe }], mode: FederatedAuthority }\n")
                .Append(provider: CultureInfo.InvariantCulture, handler: $"  {{ domain: \"{identity.Domain}\", subject: \"{PairNamespace}\", mode: SignsDirectly, algorithm: \"ecdsa-p256-sha256\", publicKey: \"{identity.PublicKeyBase64}\", grants [] }}\n")
                .Append(value: "]\n");

            File.WriteAllBytes(path: key, bytes: identity.Pkcs8);
            File.WriteAllText(path: document, contents: source.ToString(), encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return new RemotePair(Document: document, Endpoint: endpoint, KeyFile: key);
        }
    }

    // Boots the pair's authority, headless and listening, and waits until it has bound its endpoint.
    private static SweepWorld BootAuthority(WorldBuild build, SuiteBudget budget, string directory, RemotePair pair, int timeout) {
        _ = Directory.CreateDirectory(path: directory);

        var request = new WorldLaunchRequest(Verb, build.Path) {
            Arguments = ["--listen", pair.Endpoint, "--federation-key-file", pair.KeyFile],
            ExitAfterSeconds = WorldLaunch.Backstop(TimeSpan.FromSeconds(Allowance(timeout))),
            Shape = WorldShape.Headless,
            StateDirectory = Path.Combine(directory, "state"),
            World = pair.Document,
        };

        if (!SweepWorld.TryBoot(request, directory, BootTimeout, budget, out var authority, out var error)) {
            throw new InvalidOperationException($"the remote authority did not boot: {error}; its log is in {PuckPaths.Normalize(path: directory)}");
        }

        return authority!;
    }

    // Waits until the seat's authority route names a remote authority and its latest delivery holds the seat's body, so a
    // route armed after it plays against that authority; refuses, naming the last topology the World read, once the wait
    // passes its bound.
    private static void WaitForRemote(SweepWorld? authority, SuiteBudget budget, SweepWorld live, string clientLog, Action<string> print) {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = string.Empty;

        while (clock.Elapsed < RemoteTimeout) {
            if (budget.Expired) {
                throw new TimeoutException($"{budget.Reason}; the seat's body never reached the remote authority");
            }
            // A transfer the remote authority refused never turns the seat remote, so its refusal ends the wait at once.
            if (RefusedTransfer(log: clientLog) is { } refused) {
                throw new InvalidOperationException($"the remote authority refused the seat's transfer: {refused}");
            }

            var topology = live.Run("world.benchmark topology", 30);

            if (!topology.Ok) {
                throw new InvalidOperationException($"the World cannot read its seat's topology: {topology.Output}");
            }

            last = topology.Output;
            if (last.Contains("\"kind\":\"remote\"", StringComparison.Ordinal) && last.Contains("\"bodyDelivered\":true", StringComparison.Ordinal)) {
                print($"{Verb}: {last}\n");

                return;
            }
            _ = budget.Token.WaitHandle.WaitOne(PollInterval);
        }

        var listen = authority?.Run("world.rate", 10).Output;

        throw new InvalidOperationException($"the seat's body was not handed to the remote authority within {RemoteTimeout.TotalSeconds:0} s: the World's last topology was {last}{((listen is null) ? string.Empty : $"; the authority answers world.rate with {listen}")}");
    }

    // The first transfer refusal the client's standard error names, or null.
    private static string? RefusedTransfer(string log) {
        if (!File.Exists(path: log)) { return null; }

        using var stream = new FileStream(path: log, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream: stream);

        while (reader.ReadLine() is { } line) {
            if (line.StartsWith(value: "[world.transfer: ", comparisonType: StringComparison.Ordinal) && line.Contains(value: "refused reservation", comparisonType: StringComparison.Ordinal)) {
                return ((line.Length <= 600) ? line : (line[..600] + "..."));
            }
        }

        return null;
    }
}
