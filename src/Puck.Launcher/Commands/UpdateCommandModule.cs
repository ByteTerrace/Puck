using Puck.Commands;
using Puck.Launcher.Release;

namespace Puck.Launcher.Commands;

/// <summary>
/// The self-update console surface every <c>AddSelfUpdate</c> registration contributes — <c>update.status</c>,
/// <c>update.check</c>, and <c>update.apply</c>, matching this repository's own storage-catalog console module's
/// shape: every verb runs INLINE on the frame loop and visibly stalls the session for its duration, including the
/// staging download <c>update.apply</c> triggers underneath it.
/// </summary>
internal sealed class UpdateCommandModule(UpdateService service) : ICommandModule {
    private readonly UpdateService m_service = service;

    private static string Word(UpdateCheckOutcome outcome) => outcome switch {
        UpdateCheckOutcome.Available => "available",
        UpdateCheckOutcome.OutsideRollout => "outside-rollout",
        UpdateCheckOutcome.Refused => "refused",
        _ => "up-to-date",
    };

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "update.status",
            description: "Reports the resolved self-update configuration (Immediate): app, channel, cache root, installed version, check interval, and keep-N-versions.",
            handler: (_, args) => {
                if (args.Count > 0) {
                    return CommandResult.Error(output: $"[update.status: unrecognized '{args[0]}' — expected no arguments]");
                }

                var options = m_service.Options;
                var interval = ((options.CheckInterval is { } checkInterval)
                    ? $"{checkInterval.TotalSeconds:0}s"
                    : "manual only"
                );

                return new CommandResult(Output: $"[update.status: app {options.App} channel {options.Channel} cacheRoot {options.CacheRoot} installedVersion {options.InstalledVersion} checkInterval {interval} keepVersions {options.KeepVersions}]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "update.check",
            description: "Fetches and verifies the tracked channel's current release manifest and reports what would change — signature, sequence-replay, revocation, minimumSupported, version-monotonicity, and rollout-bucket checks, in that order. Downloads nothing (Immediate, network I/O — visibly stalls the session for its duration, like storage.push/.pull).",
            handler: (_, args) => {
                if (args.Count > 0) {
                    return CommandResult.Error(output: $"[update.check: unrecognized '{args[0]}' — expected no arguments]");
                }

                var result = m_service.CheckAsync(now: DateTimeOffset.UtcNow, cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                var line = $"[update.check: {Word(outcome: result.Outcome)} — {result.Detail}]";

                return (result.Outcome switch {
                    UpdateCheckOutcome.Refused => CommandResult.Error(output: line),
                    _ => new CommandResult(Output: line),
                });
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "update.apply",
            description: "Re-checks the tracked channel and, only when a verified in-rollout newer version is found, stages and applies it — writing state-generation and atomically swapping the install's 'current' pointer the stub reads at its NEXT launch. Refuses without touching anything on disk when the check does not report available. Immediate, network I/O — visibly stalls the session for its duration, like storage.push/.pull.",
            handler: (_, args) => {
                if (args.Count > 0) {
                    return CommandResult.Error(output: $"[update.apply: unrecognized '{args[0]}' — expected no arguments]");
                }

                var result = m_service.ApplyAsync(now: DateTimeOffset.UtcNow, cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                var line = $"[update.apply: {(result.Applied ? "applied" : "refused")} — {result.Detail}]";

                return (result.Applied ? new CommandResult(Output: line) : CommandResult.Error(output: line));
            }
        );
    }
}
