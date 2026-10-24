using System.CommandLine;

using Puck.Mcp;

namespace Puck.Cli.Mcp;

// The `puck mcp` verb: two exclusive hosting shapes, one process each — a local stdio operator attachment, or the
// OAuth-protected HTTP extension composed over an owned World silo. The command validator is what guarantees a
// complete pair reaches RunAsync, so the branch there reads a non-null path without re-checking its partner.
public static class McpCommand {
    // The World build of a checkout's current sources, from the same content-keyed store `puck test` and `puck canary`
    // use: an unchanged source state reuses its build, a changed one is built once (into the store, never over the
    // checkout's bin/Release, which a running World may hold open), and a concurrent build of one state is shared. The
    // build blocks, so it runs on its own thread; a cancelled start abandons it and releases whatever it finishes with.
    private static async Task<OperatorWorldBuild> ResolveBuildAsync(string checkout, TimeSpan budget, CancellationToken cancellationToken) {
        var logs = RunDirectory.CreatePath(prefix: "puck-mcp-build-");
        var build = Task.Run(function: () => {
            var resolved = WorldArtifactBuild.TryResolve(
                artifact: out var artifact,
                error: out var error,
                logDirectory: logs,
                repositoryRoot: checkout,
                timeout: budget,
                verb: "mcp"
            );

            if (resolved) {
                RunDirectory.TryDelete(path: logs);
            }

            return (resolved
                ? new OperatorWorldBuild(Error: null, Lease: artifact, Path: artifact!.Path)
                : new OperatorWorldBuild(Error: error, Lease: null, Path: null));
        }, cancellationToken: CancellationToken.None);

        try {
            return await build.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException) {
            _ = build.ContinueWith(
                continuationAction: static (Task<OperatorWorldBuild> finished) => finished.Result.Lease?.Dispose(),
                cancellationToken: CancellationToken.None,
                continuationOptions: TaskContinuationOptions.OnlyOnRanToCompletion,
                scheduler: TaskScheduler.Default
            );

            throw;
        }
    }
    private static async Task<int> RunAsync(string? attachmentPath, string? configurationPath, string? siloPath, CancellationToken cancellationToken) {
        // The root already turns Ctrl+C and SIGTERM into this token with no deadline; this handler adds Ctrl+Break,
        // so every console interrupt is a graceful drain rather than a tear-down mid-session.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);
        ConsoleCancelEventHandler cancel = (_, eventArgs) => {
            eventArgs.Cancel = true;

            stop.Cancel();
        };

        Console.CancelKeyPress += cancel;

        try {
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
                built: Puck.World.WorldSchema.SourceRevision,
                cancellationToken: stop.Token
            ).ConfigureAwait(continueOnCapturedContext: false) is { } warning) {
                Console.Error.WriteLine(value: warning);
            }

            // A World the adapter starts boots from this checkout or one of its worktrees, and a windowed one holds the
            // machine's shared GPU lease, the one `puck lease gpu` takes.
            var checkout = RepositoryPaths.FindRoot();
            var launch = ((checkout is null)
                ? null
                : new OperatorWorldLaunch(
                    AcquireGpuLease: async (holder, wait, token) => await CliLease.AcquireAsync(
                        cancellationToken: token,
                        clock: TimeProvider.System,
                        holder: holder,
                        name: "gpu",
                        root: await CliLease.RootAsync(cancellationToken: token, checkout: checkout).ConfigureAwait(continueOnCapturedContext: false),
                        wait: wait
                    ).ConfigureAwait(continueOnCapturedContext: false),
                    Checkout: checkout,
                    ResolveBuild: ResolveBuildAsync
                ));

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
        var command = new Command(
            description: "Host the Puck Console over MCP, on local stdio or OAuth-protected HTTP.",
            name: "mcp"
        ) { attachOption, httpOption, profileOption, siloOption };

        command.Detail(detail: """
            Both shapes target MCP 2026-07-28; local stdio also answers the initialize handshake of earlier revisions.

              puck mcp --profile operator [--attach <attachment file|latest>]    local stdio, one attachment
              puck mcp --silo <silo.json> --http <configuration.json>           hosted HTTP over an owned World silo

            The two shapes are exclusive; options never mix across them.
            """);

        profileOption.AcceptOnlyFromAmong(values: ["operator"]);
        command.Validators.Add(item: result => {
            var attachmentPath = result.GetValue(option: attachOption);
            var configurationPath = result.GetValue(option: httpOption);
            var profile = result.GetValue(option: profileOption);
            var siloPath = result.GetValue(option: siloOption);
            var stdio = ((attachmentPath is not null) || (profile is not null));
            var hosted = ((configurationPath is not null) || (siloPath is not null));

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
            siloPath: parseResult.GetValue(option: siloOption)
        ));

        return command;
    }
}
