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

    /// <summary>A world staged for a leg: a delta over the world's entry. For a remote authority and its client on one
    /// machine it names the pair's namespace, admits one remote player for each seat and pins the pair's own federation
    /// key, which both processes boot with one key file; the authority listens on a free loopback port. For a leg on
    /// several seats it seats that many local players at boot. The delta stands beside the entry, since a delta resolves
    /// the documents its basis references (adjacencies, references) beside itself, and disposal removes it.</summary>
    /// <param name="Document">The staged delta the leg's processes boot.</param>
    /// <param name="KeyFile">The federation key both processes of a pair sign with, or empty for a leg with no
    /// pair.</param>
    /// <param name="Endpoint">The loopback endpoint a pair's authority listens on and its client connects to, or empty
    /// for a leg with no pair.</param>
    private sealed record RemotePair(string Document, string KeyFile, string Endpoint) : IDisposable {
        /// <summary>Removes the staged delta.</summary>
        public void Dispose() {
            try {
                File.Delete(path: Document);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                Console.Error.WriteLine(value: $"{Verb}: the remote pair's staged document {PuckPaths.Normalize(path: Document)} could not be removed: {exception.Message}");
            }
        }

        /// <summary>Stages a leg's world from a world: its package folder, <c>.puck</c> source or document.</summary>
        /// <param name="directory">The directory the key is written to.</param>
        /// <param name="world">The world.</param>
        /// <param name="seats">The local seats the staged world seats at boot; one stages none beyond the world's
        /// own.</param>
        /// <param name="pair">Whether the leg plays against a listening authority started on this machine.</param>
        /// <returns>The staged world, whose disposal removes the delta it wrote beside the world's entry.</returns>
        /// <exception cref="InvalidOperationException">The world's package names no entry this verb can stage over.</exception>
        public static RemotePair Stage(string directory, string world, int seats = 1, bool pair = true) {
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
            // A leg on several seats seats them all at boot: a World started with no devices joins none on demand.
            var seated = ((seats > 1)
                ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"localSeats: {seats}, seatActivation [{string.Join(separator: ", ", values: Enumerable.Repeat(element: "Eager", count: seats))}]")
                : string.Empty);
            var source = new StringBuilder()
                .Append(provider: CultureInfo.InvariantCulture, handler: $"// The benchmark's staged world: {(pair ? "the world as its authority and a client on one machine" : "the world")}{((seats > 1) ? $", with {seats} seats" : string.Empty)}.\nbasis: {JsonValue.Create(value: basis).ToJsonString()}\n");

            if (pair) {
                _ = source
                    .Append(provider: CultureInfo.InvariantCulture, handler: $"host {{ authorityNamespace: \"{PairNamespace}\" }}\n")
                    .Append(provider: CultureInfo.InvariantCulture, handler: $"bodies {{ networkPlayers: {seats}{((seated.Length > 0) ? $", {seated}" : string.Empty)} }}\n")
                    .Append(value: "admission [\n")
                    .Append(value: "  { admits: Session, disclosure: Replica, domain: \"*\", grants [{ capability: Observe, subject: \"all\" }], mode: FederatedAuthority }\n")
                    .Append(value: "  { admits: Visitor, disclosure: Replica, domain: \"*\", grants [{ budget: 8, capability: Drive, exclusive: true }, { budget: 64, capability: Observe }], mode: FederatedAuthority }\n")
                    .Append(provider: CultureInfo.InvariantCulture, handler: $"  {{ domain: \"{identity.Domain}\", subject: \"{PairNamespace}\", mode: SignsDirectly, algorithm: \"ecdsa-p256-sha256\", publicKey: \"{identity.PublicKeyBase64}\", grants [] }}\n")
                    .Append(value: "]\n");
                File.WriteAllBytes(path: key, bytes: identity.Pkcs8);
            } else {
                _ = source.Append(provider: CultureInfo.InvariantCulture, handler: $"bodies {{ {seated} }}\n");
            }
            File.WriteAllText(path: document, contents: source.ToString(), encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return new RemotePair(Document: document, Endpoint: (pair ? endpoint : string.Empty), KeyFile: (pair ? key : string.Empty));
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
