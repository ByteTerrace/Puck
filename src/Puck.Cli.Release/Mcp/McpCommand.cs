using System.CommandLine;
using Puck.Mcp;

namespace Puck.Cli.Mcp;

// The `puck mcp` verb: two exclusive hosting shapes, one process each — a local stdio operator attachment, or the
// OAuth-protected HTTP extension composed over an owned World silo. The command validator is what guarantees a
// complete pair reaches RunAsync, so the branch there reads a non-null path without re-checking its partner.
public static class McpCommand {
    // Starts one World through the CLI's one World launch (WorldLaunch). The build comes from the same content-keyed
    // store `puck test` and `puck canary` use: an unchanged source state reuses its build, a changed one is built once (into
    // the store, never over the checkout's bin/Release, which a running World may hold open), and a concurrent build of one
    // state is shared; a World that opens a device on Vulkan or a backend its document picks builds with SPIR-V. The build
    // blocks, so it runs on its own thread; a cancelled start abandons it and releases whatever it finishes with. The build
    // comes before the GPU lease, so a long build never holds the machine's GPU.
    private static async Task<OperatorWorldStart> StartWorldAsync(OperatorWorldRequest world, Action<string> stage, CancellationToken cancellationToken) {
        var shape = (world.Windowed ? WorldShape.Windowed : WorldShape.Headless);
        var logs = RunDirectory.CreatePath(prefix: "puck-mcp-build-");

        stage(obj: "building");

        var resolving = Task.Run(function: () => {
            var resolved = WorldLaunch.TryResolve(build: out var build, error: out var error, logDirectory: logs, named: null, repositoryRoot: world.Checkout,
                spirv: WorldLaunch.NeedsSpirv(backend: world.Backend, shape: shape), verb: "mcp");

            if (resolved) {
                RunDirectory.TryDelete(path: logs);
            }

            return (Build: build, Error: error);
        }, cancellationToken: CancellationToken.None);
        WorldBuild? build;
        string error;

        try {
            (build, error) = await resolving.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException) {
            _ = resolving.ContinueWith(
                continuationAction: static finished => finished.Result.Build?.Dispose(),
                cancellationToken: CancellationToken.None,
                continuationOptions: TaskContinuationOptions.OnlyOnRanToCompletion,
                scheduler: TaskScheduler.Default
            );

            throw;
        }

        if (build is null) {
            return new OperatorWorldStart(Build: null, Lease: null, Process: null, Refusal: $"Refused: no Release build of {world.Checkout}'s current sources could be made, so no World was started: {error}");
        }

        WorldLaunch launch;

        try {
            launch = await WorldLaunch.AdmitAsync(cancellationToken: cancellationToken, request: new WorldLaunchRequest(Artifact: build.Path, Step: "puck_world_start") {
                Arguments = [
                    "--control",
                    .. (world.Paused ? ["--paused"] : Array.Empty<string>()),
                    .. (world.Lockstep ? ["--lockstep"] : Array.Empty<string>()),
                    .. ((world.CacheDirectory is { } cache) ? ["--cache-dir", cache] : Array.Empty<string>()),
                    .. ((world.CaptureTool is { } tool) ? ["--capture-tool", tool] : Array.Empty<string>()),
                ],
                Backend = world.Backend,
                Checkout = world.Checkout,
                // The World publishes its attachment file in its temporary directory, which must be the one the adapter follows.
                Environment = new Dictionary<string, string?>(comparer: StringComparer.Ordinal) { ["TEMP"] = world.TemporaryDirectory, ["TMP"] = world.TemporaryDirectory, ["TMPDIR"] = world.TemporaryDirectory },
                Holder = world.Holder,
                Lane = world.Lane,
                OwnerDevices = world.OwnerPlay,
                Shape = shape,
                StateDirectory = world.StateDirectory,
                WorkingDirectory = world.Checkout,
                World = world.Document,
            }, stage: stage).ConfigureAwait(continueOnCapturedContext: false);
        } catch (TimeoutException exception) {
            build.Dispose();

            return new OperatorWorldStart(Build: null, Lease: null, Process: null, Refusal: $"Refused: the GPU lease was not granted, so no windowed World was started ({exception.Message}). Retry the start, or start headless.");
        } catch (HostAdmissionRefusedException) {
            build.Dispose();

            return new OperatorWorldStart(Build: null, Lease: null, Process: null, Refusal: $"Refused: the host had no memory, commit or disk headroom{(world.Windowed ? " (or its GPU stayed busy)" : string.Empty)} within the admission's bound, so no World was started. Retry the start when `puck host load` reports CAPACITY.");
        } catch {
            build.Dispose();

            throw;
        }

        try {
            var process = launch.StartRedirected(gpu: out var gpu, readiness: out var readiness);

            return new OperatorWorldStart(Build: build.Path, Lease: new Leases(build: build, gpu: gpu), Process: process, Refusal: null) { Observe = readiness.Observe };
        } catch (Exception exception) when ((exception is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)) {
            build.Dispose();

            return new OperatorWorldStart(Build: null, Lease: null, Process: null, Refusal: $"Refused: the World could not be launched ({exception.Message}).");
        } finally {
            launch.Dispose();
        }
    }
    // The build and GPU leases a started World runs under, released together once it has exited.
    private sealed class Leases(WorldBuild build, IDisposable? gpu) : IDisposable {
        public void Dispose() {
            try {
                gpu?.Dispose();
            } finally {
                build.Dispose();
            }
        }
    }
    private static async Task<int> RunAsync(string? attachmentPath, string? configurationPath, string? siloPath, string? installRoot, string? buildSource, string? checkoutPath, bool worker, bool prepareInstall, CancellationToken cancellationToken) {
        // The root already turns Ctrl+C and SIGTERM into this token with no deadline; this handler adds Ctrl+Break,
        // so every console interrupt is a graceful drain rather than a tear-down mid-session.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);
        ConsoleCancelEventHandler cancel = (_, eventArgs) => {
            eventArgs.Cancel = true;

            stop.Cancel();
        };

        Console.CancelKeyPress += cancel;

        try {
            if ((installRoot is not null) && (buildSource is not null)) {
                if (prepareInstall) {
                    _ = await LocalAdapterInstall.ActivateAsync(build: buildSource, install: installRoot, token: stop.Token).ConfigureAwait(continueOnCapturedContext: false);
                    return 0;
                }
                if (!worker) {
                    // The stub counts each launch of a pending version; a front that started proves it as a worker
                    // does, or a client's probe-and-reconnect reaches the attempt ceiling and rolls a good build back.
                    await LocalAdapterInstall.HealthyAsync(install: installRoot, cancellationToken: stop.Token).ConfigureAwait(continueOnCapturedContext: false);
                    await OperatorMcpFront.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(),
                        Path.Combine(path1: installRoot, path2: (OperatingSystem.IsWindows() ? "Puck.Launcher.Stub.exe" : "Puck.Launcher.Stub")),
                        ["mcp", "--profile", "operator", "--worker", "--install-root", installRoot, "--build-source", buildSource,
                            "--checkout", (checkoutPath ?? RepositoryPaths.FindRoot()!), "--attach", (attachmentPath ?? "latest")],
                        token => LocalAdapterInstall.ActivateAsync(build: buildSource, install: installRoot, token: token), stop.Token).ConfigureAwait(continueOnCapturedContext: false);
                    return 0;
                }
                await LocalAdapterInstall.HealthyAsync(install: installRoot, cancellationToken: stop.Token).ConfigureAwait(continueOnCapturedContext: false);
            }
            if (siloPath is not null) {
                return await Puck.World.Silo.WorldSiloApplication
                    .RunAsync(
                    args: ["--silo", siloPath, "--mcp", configurationPath!],
                    cancellationToken: stop.Token
                )
                    .ConfigureAwait(continueOnCapturedContext: false);
            }

            // An MCP client launches the installed tool, which can lag the checkout it serves; the warning reaches the
            // client's server log before the first call does.
            if (await CliRevision.StaleWarningAsync(
                built: CliRevision.Built,
                cancellationToken: stop.Token
            ).ConfigureAwait(continueOnCapturedContext: false) is { } warning) {
                Console.Error.WriteLine(value: warning);
            }

            // A World the adapter starts boots from this checkout or one of its worktrees through the CLI's one World
            // launch: a windowed one holds the machine's shared GPU lease, the one `puck lease gpu` takes, and every start
            // waits for the host admission `puck lease` and the gate wait on.
            var checkout = (checkoutPath ?? RepositoryPaths.FindRoot());
            var launch = ((checkout is null) ? null : new OperatorWorldLaunch(Checkout: checkout, Start: StartWorldAsync));

            // Worlds publish their capability files in the user's temporary directory, so following the newest World
            // is following that directory.
            await OperatorMcpServer
                .RunAsync(
                target: (((attachmentPath is null) || string.Equals(a: attachmentPath, b: "latest", comparisonType: StringComparison.OrdinalIgnoreCase))
                    ? Path.GetTempPath()
                    : attachmentPath),
                cancellationToken: stop.Token,
                input: Console.OpenStandardInput(),
                launch: launch,
                output: Console.OpenStandardOutput()
            )
                .ConfigureAwait(continueOnCapturedContext: false);

            return 0;
        } catch (Exception error) when ((error is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or OperationCanceledException or ArgumentException or InvalidOperationException)) {
            Console.Error.WriteLine(value: $"puck mcp: {error.Message}");

            return 1;
        } finally {
            Console.CancelKeyPress -= cancel;
        }
    }

    public static Command Create() {
        var attachOption = new Option<string?>(name: "--attach") {
            Description = "The attachment file printed by `world.control start` in a running World's console, or `latest` to follow the newest running World across restarts. When omitted with --profile operator, defaults to `latest`. Each tool call attaches on demand, so the adapter may start before any World.",
        };
        var httpOption = new Option<string?>(name: "--http") {
            Description = "The remote MCP deployment configuration — listener, OAuth protected-resource settings, and grants — monitored for grant changes while hosting. Requires --silo.",
        };
        var profileOption = new Option<string?>(name: "--profile") {
            Description = "The local stdio profile. `operator` serves one attachment over this process's standard input and output.",
        };
        var siloOption = new Option<string?>(name: "--silo") {
            Description = "The silo document (puck.silo.configuration.v1) to run, forwarded to Puck.World.Silo unchanged; the MCP extension composes over that host. Requires --http.",
        };
        var installOption = new Option<string?>("--install-root") { Description = "Local launcher install root for the persistent operator session." };
        var buildOption = new Option<string?>("--build-source") { Description = "Completed local CLI build tracked through the development release channel." };
        var checkoutOption = new Option<string?>("--checkout") { Description = "Checkout served by an installed operator adapter." };
        var workerOption = new Option<bool>("--worker") { Description = "Serve one installed backend behind the persistent operator session." };
        var prepareOption = new Option<bool>("--prepare-install") { Description = "Stage and activate a completed local build, then exit." };
        var command = new Command(
            description: "Host the Puck Console over MCP, on local stdio or OAuth-protected HTTP.",
            name: "mcp"
        ) { attachOption, httpOption, profileOption, siloOption, installOption, buildOption, checkoutOption, workerOption, prepareOption };

        command.Detail(detail: """
            Both shapes target MCP 2026-07-28; local stdio also answers the initialize handshake of earlier revisions.

              puck mcp --profile operator [--attach <attachment file|latest>]    local stdio, one attachment
              puck mcp --silo <silo.json> --http <configuration.json>           hosted HTTP over an owned World silo

            The two shapes are exclusive; options never mix across them.
            """);

        profileOption.AcceptOnlyFromAmong(values: ["operator"]);
        command.Validators.Add(item: result => {
            if (((result.GetValue(option: installOption) is null) != (result.GetValue(option: buildOption) is null)) ||
                ((result.GetValue(option: workerOption) || result.GetValue(option: prepareOption)) && (result.GetValue(option: installOption) is null))) {
                result.AddError(errorMessage: "--install-root and --build-source are paired; --worker and --prepare-install require them.");
            }
            var attachmentPath = result.GetValue(option: attachOption);
            var configurationPath = result.GetValue(option: httpOption);
            var profile = result.GetValue(option: profileOption);
            var siloPath = result.GetValue(option: siloOption);
            var stdio = ((attachmentPath is not null) || (profile is not null));
            var hosted = ((configurationPath is not null) || (siloPath is not null));

            if (((result.GetValue(option: installOption) is not null) || (result.GetValue(option: checkoutOption) is not null)) &&
                ((profile != "operator") || hosted)) {
                result.AddError(errorMessage: "Installed adapter options require --profile operator and cannot mix with --silo/--http.");
            }

            if (
                stdio &&
                hosted
            ) {
                result.AddError(errorMessage: "--profile/--attach and --silo/--http are exclusive: local stdio and hosted HTTP are separate processes.");
            } else if (
                stdio &&
                (profile is null)
            ) {
                result.AddError(errorMessage: "Local stdio hosting needs --profile operator.");
            } else if (
                hosted &&
                ((configurationPath is null) || (siloPath is null))
            ) {
                result.AddError(errorMessage: "Hosted HTTP needs both --silo <silo.json> and --http <configuration.json>.");
            } else if (
                !stdio &&
                !hosted
            ) {
                result.AddError(errorMessage: "puck mcp hosts either --profile operator [--attach <attachment file|latest>] or --silo <silo.json> --http <configuration.json>.");
            }
        });
        command.SetAction(action: (parseResult, cancellationToken) => RunAsync(
            attachmentPath: parseResult.GetValue(option: attachOption),
            cancellationToken: cancellationToken,
            configurationPath: parseResult.GetValue(option: httpOption),
            siloPath: parseResult.GetValue(option: siloOption),
            installRoot: parseResult.GetValue(option: installOption),
            buildSource: parseResult.GetValue(option: buildOption),
            checkoutPath: parseResult.GetValue(option: checkoutOption),
            worker: parseResult.GetValue(option: workerOption),
            prepareInstall: parseResult.GetValue(option: prepareOption)
        ));

        return command;
    }
}
