using System.CommandLine;

using Puck.Mcp;
using Puck.Networking;

namespace Puck.Cli.Mcp;

// The `puck mcp` verb: two exclusive hosting shapes, one process each — a local stdio operator attachment, or the
// OAuth-protected HTTP extension composed over an owned World silo. The command validator is what guarantees a
// complete pair reaches RunAsync, so the branch there reads a non-null path without re-checking its partner.
internal static class McpCommand {
    private static string ResolveAttachmentPath(string? attachmentPath) {
        if (!string.IsNullOrEmpty(value: attachmentPath) && !string.Equals(a: attachmentPath, b: "latest", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return attachmentPath;
        }

        var tempDirectory = Path.GetTempPath();
        var files = Directory.GetFiles(
            path: tempDirectory,
            searchPattern: "puck-control-*.json"
        );

        if (files.Length == 0) {
            throw new FileNotFoundException(message: $"No active Puck control attachment found in '{tempDirectory}'. Start Puck and run 'world.control start' in the console first.");
        }

        var candidates = files
            .Select(selector: path => new FileInfo(fileName: path))
            .OrderByDescending(keySelector: file => file.LastWriteTimeUtc);

        foreach (var file in candidates) {
            try {
                _ = LocalEndpointCapability.ReadDescriptor(path: file.FullName);

                return file.FullName;
            } catch (Exception error) when ((error is UnauthorizedAccessException or IOException or InvalidDataException)) { }
        }

        throw new FileNotFoundException(message: $"No readable Puck control attachment found in '{tempDirectory}'. Start Puck and run 'world.control start' in the console first.");
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

            var resolvedAttachmentPath = ResolveAttachmentPath(attachmentPath: attachmentPath);

            await OperatorMcpServer
                .RunAsync(
                attachmentPath: resolvedAttachmentPath,
                cancellationToken: stop.Token,
                input: Console.OpenStandardInput(),
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
            Description = "The attachment file printed by `world.control start` in a running World's console, or `latest` to attach to the most recent active World. When omitted with --profile operator, defaults to `latest`.",
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
            description: """
            Optional Puck Console/MCP hosting over local stdio or OAuth-protected HTTP. Both target MCP 2026-07-28.

              puck mcp --profile operator [--attach <attachment file|latest>]    local stdio, one attachment
              puck mcp --silo <silo.json> --http <configuration.json>           hosted HTTP over an owned World silo

            The two shapes are exclusive; options never mix across them.
            """,
            name: "mcp"
        ) { attachOption, httpOption, profileOption, siloOption };

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
