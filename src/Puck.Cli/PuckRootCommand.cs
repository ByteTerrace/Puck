using System.CommandLine;

using Puck.Cli.Analysis;
using Puck.Cli.Affected;
using Puck.Cli.Architecture;
using Puck.Cli.Automation;
using Puck.Cli.Azure;
using Puck.Cli.Baselines;
using Puck.Cli.Branding;
using Puck.Cli.Bench;
using Puck.Cli.Canary;
using Puck.Cli.CartridgeCost;
using Puck.Cli.Counters;
using Puck.Cli.Determinism;
using Puck.Cli.Docs;
using Puck.Cli.Firmware;
using Puck.Cli.FontAtlas;
using Puck.Cli.Format;
using Puck.Cli.Formats;
using Puck.Cli.Gate;
using Puck.Cli.Host;
using Puck.Cli.Landing;
using Puck.Cli.Laws;
using Puck.Cli.Mcp;
using Puck.Cli.NuGet;
using Puck.Cli.Official;
using Puck.Cli.Packaging;
using Puck.Cli.Parity;
using Puck.Cli.PublishRelease;
using Puck.Cli.PullRequest;
using Puck.Cli.Qualification;
using Puck.Cli.Ratchets;
using Puck.Cli.Refusals;
using Puck.Cli.Scan;
using Puck.Cli.Schema;
using Puck.Cli.Search;
using Puck.Cli.Shaders;
using Puck.Cli.WasmStdlib;
using Puck.Cli.WorktreeBase;
using Puck.Cli.WorktreeReport;

namespace Puck.Cli;

/// <summary>
/// The <c>puck</c> command tree: one root composes every verb assembly's verbs, and the process entry point and
/// in-process self-invocations both go through <see cref="InvokeAsync(string[])"/>.
/// </summary>
public static class PuckRootCommand {
    /// <summary>What the gate's verbs read from the verbs composed beside them: the source types behind each generated
    /// schema (<c>puck schema</c>) and the grammar of each verb whose own arguments decide whether a run is GPU work
    /// (<c>puck parity</c>, <c>puck counters</c>).</summary>
    public static GateComposition Gate { get; } = new(
        GpuVerbGrammars: new Dictionary<string, Func<Command>>(comparer: StringComparer.Ordinal) {
            ["counters"] = CountersCommand.Create,
            ["parity"] = ParityCommand.Create,
        },
        SchemaSourceTypes: SchemaCommand.SourceTypesOf
    );

    /// <summary>Creates the root command. <paramref name="clock"/> is the CLI host's one clock: every deadline a verb
    /// puts on a process, a connection, a lease, or a request runs on it.</summary>
    /// <param name="clock">The CLI host's clock; <see cref="TimeProvider.System"/> for a real invocation.</param>
    /// <returns>The root command: its verbs in ordinal name order, its help naming the tool <see cref="CliHelp.ToolName"/>,
    /// and every action guarded by <see cref="CliExit.Guard"/>.</returns>
    public static RootCommand Create(TimeProvider clock) => Create(clock: clock, schemaBootstrap: SchemaBootstrap.IsBootstrap);
    // A bootstrap binary cannot start a consumer of the deliberately absent model table.
    public static RootCommand Create(TimeProvider clock, bool schemaBootstrap) {
        Command[] verbs = (schemaBootstrap ? [SchemaCommand.Create(clock: clock)] : [
            AffectedCommand.Create(composition: Gate),
            ArchitectureCommand.Create(),
            ArtifactsCommand.Create(),
            AzureCommand.Create(clock: clock),
            BaselinesCommand.Create(),
            BenchRunner.Create(clock: clock),
            BrandingCommand.Create(),
            BundleCommand.Create(),
            CanaryCommand.Create(),
            CanaryCeilingsCommand.Create(),
            CartridgeCostCommand.Create(),
            RatchetCommand.CreateCommentSmells(),
            CountersCommand.Create(),
            DeclarationsCommand.Create(),
            DerivationsCommand.Create(),
            DeterminismCommand.Create(),
            DocsCommand.Create(),
            FirmwareCommand.Create(),
            FontAtlasCommand.Create(),
            FormatCommand.Create(),
            FormatsCommand.Create(),
            GateCommand.Create(clock: clock, composition: Gate),
            HostCommand.Create(composition: Gate),
            LandingCommand.Create(),
            LawsCommand.Create(),
            RatchetCommand.CreateLengths(),
            McpCommand.Create(),
            NuGetCommand.Create(),
            OfficialCommand.Create(),
            PackagesCommand.Create(),
            ParityCommand.Create(),
            PublishCommand.Create(),
            PullRequestCommand.Create(),
            QualifyCommand.Create(),
            ReferencesCommand.Create(),
            RefusalsCommand.Create(),
            ScanCommand.Create(),
            SchemaCommand.Create(clock: clock),
            SearchCommand.Create(),
            ShadersCommand.Create(),
            WasmBuildCommand.Create(),
            WasmStdlibCommand.Create(),
            WorktreeBaseCommand.Create(),
            WorktreeReportCommand.Create(clock: clock),
            WorldCommand.Create(clock: clock),
            .. WorldsRoot.Verbs(),
        ]);

        return CliRoot.Compose(
            description: "The Puck developer CLI: every repository operation is a verb here.",
            verbs: verbs
        );
    }
    public static int Invoke(string[] args) => CliRoot.Invoke(
        args: args,
        root: Create(clock: TimeProvider.System)
    );
    public static Task<int> InvokeAsync(string[] args) => CliRoot.InvokeAsync(
        args: args,
        root: Create(clock: TimeProvider.System)
    );
}
