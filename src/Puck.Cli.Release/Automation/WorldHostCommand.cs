using System.CommandLine;
using System.Formats.Tar;
using System.IO.Compression;
using Puck.Assets;

namespace Puck.Cli.Automation;

/// <summary>The <c>world host</c> verbs: build a host release, and the programs a hosting machine runs from the release it
/// installed (its bootstrap, its services and the guest commands a deploying controller sends it). A server is a World
/// configured to host: the machine installs its release from a signed channel through the same installer and update
/// chain a player's machine uses, and systemd runs the installed CLI directly.</summary>
public static class WorldHostCommand {
    private const string Verb = "world host build";
    // The TLS front: Caddy's official Linux release, pinned by the archive's SHA-256. Its SHA-512 matches the checksum
    // file Caddy publishes with the release (caddy_2.11.7_checksums.txt).
    private const string TlsFrontUrl = "https://github.com/caddyserver/caddy/releases/download/v2.11.7/caddy_2.11.7_linux_amd64.tar.gz";
    private const string TlsFrontSha256 = "727b91701a392de6ebc5027509f548bf39979e5216340d0faed8fa5e69c84f8b";

    /// <summary>Creates the <c>world host</c> verb.</summary>
    /// <returns>The verb.</returns>
    public static Command Create() => new(name: "host", description: "Build a host release, and run the programs a hosting machine runs from one: its bootstrap, its services and its guest commands.") {
        Build(), Fetch(), Install(), Serve(), Tls(), Guard(), Probe(), Request(),
    };

    private static Command Build() {
        var package = new Argument<string>(name: "package") { Description = "The primary world's asset package folder (worlds/debug)." };
        var output = CliOptions.Output(description: "The host release folder; it must be missing or empty.", required: true);
        var extensions = new Option<string[]>(name: "--extension") { AllowMultipleArgumentsPerToken = true, Description = "A world extension project installed into extensions/<name>/, such as src/Puck.World.Azure for a deployment's store, sign-in and telemetry. Repeatable." };
        var tlsFront = new Option<bool>(name: "--tls-front") { Description = "Carry the TLS front (Caddy, pinned by its archive's digest) at tls/caddy, for a deployment that exposes the hosted MCP front." };
        var command = new Command(name: "build", description: "Assemble a host release folder for this machine's runtime: the CLI (which carries the silo) published self-contained, its launcher, the composed hosted worlds and the named world extensions. `puck publish --channel server` turns it into a release.") {
            package, output, extensions, tlsFront,
        };

        command.SetAction(action: parse => RunBuild(
            extensions: parse.GetValue(option: extensions) ?? [],
            output: Path.GetFullPath(path: parse.GetRequiredValue(option: output)),
            package: Path.GetFullPath(path: parse.GetRequiredValue(argument: package)),
            tlsFront: parse.GetValue(option: tlsFront)
        ));
        return command;
    }
    private static int RunBuild(string package, string output, IReadOnlyList<string> extensions, bool tlsFront) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repository)) { return 2; }
        if (!Directory.Exists(path: package)) { return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: package), why: "is not an asset package folder."); }
        if (Directory.Exists(path: output) && Directory.EnumerateFileSystemEntries(path: output).Any()) {
            return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: output), why: "is not empty.");
        }
        if (tlsFront && (System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier != "linux-x64")) {
            return CliExit.Refuse(verb: Verb, what: "--tls-front", why: "carries the linux-x64 front, and this machine's runtime is another.");
        }
        _ = Directory.CreateDirectory(path: output);
        try {
            Console.WriteLine(value: $"{Verb}: publishing the CLI self-contained for {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}.");
            _ = CliProcess.RunCheckedAsync(
                arguments: ["publish", CliOptions.NoNodeReuse, "-m:2", "--disable-build-servers", Path.Combine(path1: repository, path2: "src/Puck.Cli/Puck.Cli.csproj"), "-c", "Release", "--nologo",
                    "-p:PuckSelfContained=true", "-p:DebugType=none", "-p:PublishDocumentationFile=false", "-p:PublishReferencesDocumentationFiles=false", "-o", output],
                fileName: "dotnet",
                timeout: WorldPlayerCommand.BuildTimeout,
                workingDirectory: repository
            ).GetAwaiter().GetResult();
            WorldPlayerCommand.PublishLauncher(output: output, repository: repository, verb: Verb);
            foreach (var extension in extensions) { _ = WorldPlayerCommand.PublishExtension(output: output, project: extension, repository: repository, verb: Verb); }

            // The asset package is the directory the composed worlds join: machine content paths resolve beside it.
            var worlds = Path.Combine(path1: output, path2: WorldHostLayout.WorldsDirectory);

            Console.WriteLine(value: $"{Verb}: copying {CliPaths.ToDisplay(fullPath: package)} into {WorldHostLayout.WorldsDirectory}/ and composing its hosted worlds there.");
            if (CliFiles.CopyDirectory(destination: worlds, intoEmpty: true, source: package) != 0) { throw new InvalidOperationException(message: "copying the asset package failed."); }
            if (WorldPrepareCommand.Create().Parse(args: [package, "--output", worlds]).Invoke() != 0) {
                throw new InvalidOperationException(message: "composing the hosted worlds failed; its errors are above.");
            }
            if (tlsFront && OperatingSystem.IsLinux()) { FetchTlsFront(output: output); }
        } catch (Exception exception) when (exception is InvalidOperationException or IOException or HttpRequestException or InvalidDataException) {
            Console.Error.WriteLine(value: $"{Verb}: {exception.Message}");
            return 1;
        }
        Console.WriteLine(value: $"{Verb}: {CliPaths.ToDisplay(fullPath: output)} holds the host release.");
        return 0;
    }
    [System.Runtime.Versioning.SupportedOSPlatform(platformName: "linux")]
    private static void FetchTlsFront(string output) {
        Console.WriteLine(value: $"{Verb}: fetching the TLS front from {TlsFrontUrl}.");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(minutes: 5) };
        var archive = Puck.Abstractions.Retry.RetryAsync(action: () => client.GetByteArrayAsync(requestUri: TlsFrontUrl), attempts: 6, delay: TimeSpan.FromSeconds(seconds: 10),
            retryable: static exception => exception is HttpRequestException or TaskCanceledException).GetAwaiter().GetResult();

        if (ContentPin.Compute(content: archive).Hex != TlsFrontSha256) { throw new InvalidDataException(message: $"{TlsFrontUrl} does not match its pinned digest."); }
        using var tar = new TarReader(archiveStream: new GZipStream(mode: CompressionMode.Decompress, stream: new MemoryStream(buffer: archive)));

        while (tar.GetNextEntry() is { } entry) {
            if (entry.Name != "caddy") { continue; }
            var path = Path.Combine(path1: output, path2: WorldHostLayout.TlsFrontExecutable);

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
            entry.ExtractToFile(destinationFileName: path, overwrite: false);
            File.SetUnixFileMode(path: path, mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            return;
        }
        throw new InvalidDataException(message: $"{TlsFrontUrl} holds no caddy executable.");
    }
    private static Command Fetch() {
        var tree = new Argument<string>(name: "tree") { Description = "The signed release tree (`puck channel push` signed its server channel)." };
        var to = new Option<string>(name: "--to") { Description = "The install root.", Required = true };
        var source = new Option<string?>(name: "--source") { Description = "The address the tree is published at; absent, the tree itself." };
        var command = new Command(name: "fetch", description: "Install a signed host release into an install root exactly as a hosting machine does: the installer the release carries, fetched by its pin, installs the release's signed manifest by its pin. Prints the installed version's directory.") { tree, to, source };

        command.SetAction(action: async (parse, token) => {
            var root = Path.GetFullPath(path: parse.GetRequiredValue(argument: tree));

            try {
                var release = WorldHostRelease.FromTree(source: (parse.GetValue(option: source) ?? root), tree: root);

                Console.Out.WriteLine(value: await release.InstallAsync(cancellationToken: token, installRoot: parse.GetRequiredValue(option: to)).ConfigureAwait(continueOnCapturedContext: false));
                return 0;
            } catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or HttpRequestException or UnauthorizedAccessException) {
                Console.Error.WriteLine(value: $"world host fetch: {exception.Message}");
                return 1;
            }
        });
        return command;
    }
    private static Command Install() {
        var command = new Command(name: "install", description: $"A hosting machine's bootstrap, run as root from the version its installer installed under {WorldHostLayout.InstallRoot}, with the protected bootstrap (base64 JSON) on standard input: read the retained inputs it pins, check the boot guard under the release lock, write the configuration and credentials, start the sandboxed units and wait for private health (and the TLS front). Exit 0 the candidate is ready, 1 refused or not ready.");

        command.SetAction(action: async (parse, token) => {
            if (!OperatingSystem.IsLinux()) { return CliExit.Refuse(verb: "world host install", what: "this machine", why: "is not Linux; a hosting machine runs its units under systemd."); }
            try {
                var encoded = (await Console.In.ReadToEndAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false)).Trim();

                return await WorldHostInstall.RunAsync(bootstrap: WorldHostBootstrap.Decode(value: encoded), cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception exception) when (exception is not OperationCanceledException) {
                Console.Error.WriteLine(value: $"world host install refused: {exception.Message}");
                return 1;
            }
        });
        return command;
    }
    private static Command Serve() {
        var silo = new Option<string>(name: "--silo") { Description = "The silo document.", Required = true };
        var mcp = new Option<string?>(name: "--mcp") { Description = "The remote MCP configuration, for a host that exposes the MCP front." };
        var extensions = new Option<string?>(name: "--extensions-dir") { Description = "The one extensions directory to search; absent, the release's own." };
        var command = new Command(name: "serve", description: "Run the silo a hosting machine serves, with its installed world extensions; its unit runs this.") { silo, mcp, extensions };

        command.SetAction(action: (parse, token) => Puck.World.Silo.WorldSiloApplication.RunAsync(cancellationToken: token, args: [
            "--silo", parse.GetRequiredValue(option: silo),
            .. ((parse.GetValue(option: mcp) is { } http) ? (string[])["--mcp", http] : []),
            .. ((parse.GetValue(option: extensions) is { } directory) ? (string[])["--extensions-dir", directory] : []),
        ]));
        return command;
    }
    private static Command Tls() {
        var machine = new Option<string>(name: "--machine") { Description = "The machine record.", Required = true };
        var command = new Command(name: "tls", description: "Keep the MCP front's TLS state in the owner's store, and report readiness to systemd once the front may start; its unit runs this.") { machine };

        command.SetAction(action: async (parse, token) => {
            if (!OperatingSystem.IsLinux()) { return CliExit.Refuse(verb: "world host tls", what: "this machine", why: "is not Linux."); }
            try {
                await WorldHostTlsKeeper.RunAsync(cancellationToken: token, machinePath: parse.GetRequiredValue(option: machine)).ConfigureAwait(continueOnCapturedContext: false);
            } catch (OperationCanceledException) { }
            return 0;
        });
        return command;
    }
    private static Command Guard() {
        var request = new Argument<string>(name: "request") { Description = "The base64 JSON guard request." };
        var command = new Command(name: "guard", description: $"Refuse a delayed effect whose release operation is no longer current; the caller holds {WorldHostLayout.ReleaseLock}. Exit 0 the effect may run, 1 refused.") { request };

        command.SetAction(action: async (parse, token) => {
            try {
                return await WorldHostGuest.GuardAsync(cancellationToken: token, request: parse.GetRequiredValue(argument: request)).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception exception) when (exception is not OperationCanceledException) {
                Console.Error.WriteLine(value: $"world release guest guard refused: {exception.Message}");
                return 1;
            }
        });
        return command;
    }
    private static Command Probe() {
        var command = new Command(name: "probe", description: "Print the machine's engine, whether its silo runs, its release status and its private health as one JSON object.");

        command.SetAction(action: (parse, token) => WorldHostGuest.ProbeAsync(cancellationToken: token));
        return command;
    }
    private static Command Request() {
        var method = new Argument<string>(name: "method") { Description = "The HTTP method." };
        var path = new Argument<string>(name: "path") { Description = "The release-control route." };
        var timeout = CliOptions.Count(defaultValue: 30, description: "Seconds the request may take, 1 to 3600.", maximum: 3600, minimum: 1, name: "--timeout");
        var command = new Command(name: "request", description: "Send one release-control request to the silo on loopback and print its body; a refusal's body goes to standard error. Exit 0 success, 1 failure.") { method, path, timeout };

        command.SetAction(action: (parse, token) => WorldHostGuest.RequestAsync(cancellationToken: token, method: parse.GetRequiredValue(argument: method), path: parse.GetRequiredValue(argument: path), timeoutSeconds: parse.GetValue(option: timeout)));
        return command;
    }
}
