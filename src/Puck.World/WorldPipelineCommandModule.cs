using System.Globalization;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Live shader-pipeline authoring. Document mutations use the normal authority path;
/// presentation controls operate on accepted instances and never compile on the command thread.</summary>
internal sealed class WorldPipelineCommandModule(WorldServer server, IServerLink link, WorldDeferredVerbEchoes echoes,
    WorldPipelineRuntime? pipelines = null) : ICommandModule {
    private static string Clock(WorldPipelineRuntime.Entry entry) => string.Create(
        CultureInfo.InvariantCulture,
        $"paused={entry.ClockPaused.ToString().ToLowerInvariant()} seconds={entry.ClockSeconds:0.###} scale={entry.ClockScale:0.###}"
    );
    private WorldPipelineRuntime.Entry? FindEntry(string name) {
        pipelines?.Reconcile(rows: server.Definition.Views.Pipelines);
        return (((pipelines is not null) && pipelines.TryGet(
            entry: out var entry,
            name: name
        ))
            ? entry
            : null
        );
    }
    private static CommandDefinition Immediate(string name, string description, Func<CommandContext, WireArgs, CommandResult> handler) =>
        CommandDefinition.WithWireArgs(
            name: name,
            description: description,
            handler: handler,
            bindability: CommandBindability.Unbindable,
            routing: CommandRouting.Immediate
        );
    private static CommandResult Missing(string verb, string name) =>
        CommandResult.Error(output: $"[{verb}: '{name}' has no rendered pipeline instance]");

    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            name: "pipeline.load",
            description: "pipeline.load <name> <source> [camera] — author a pipeline JSON or one-off shader source, relative to the world document. Accepted rows compile in the background; pipeline.status reports the result.",
            bindability: CommandBindability.Unbindable,
            routing: CommandRouting.Simulation,
            handler: (context, args) => {
                if (args.Count is < 2 or > 3) { return CommandResult.Usage(
                    form: "<name> <source> [camera]",
                    verb: "pipeline.load"
                ); }
                return link.Submit(
                    mutation: new WorldMutation.UpsertViewPipeline(
                        Principal: context.ActingPrincipal(),
                        Pipeline: new WorldViewPipeline(
                            Name: args[0].ToString(),
                            Source: args[1].ToString(),
                            Camera: ((args.Count == 3)
                    ? args[2].ToString()
                    : null)
                        )
                    ),
                    echoes: echoes,
                    verb: "pipeline.load"
                );
            }
        );
        yield return Immediate(
            "pipeline.reload",
            "pipeline.reload [name] — compile a complete candidate in the background; keep the last good pipeline on errors.",
            (_, args) => {
            if (args.Count > 1) { return CommandResult.Usage(
                form: "[name]",
                verb: "pipeline.reload"
            ); }
            if (pipelines is null) { return CommandResult.Error(output: "[pipeline.reload: requires a rendered host]"); }
            pipelines.Reconcile(rows: server.Definition.Views.Pipelines);
            if (args.Count == 1) {
                var name = args[0].ToString();

                if (FindEntry(name: name) is not { } entry) { return Missing(
                    name: name,
                    verb: "pipeline.reload"
                ); }
                pipelines.QueueCompile(
                    name: name,
                    source: entry.Source
                );
                return new CommandResult($"[pipeline.reload: {name} queued]");
            }
            foreach (var (name, entry) in pipelines.Entries) { pipelines.QueueCompile(
                name: name,
                source: entry.Source
            ); }
            return new CommandResult($"[pipeline.reload: {pipelines.Entries.Count} queued]");
        }
        );
        yield return Immediate(
            "pipeline.watch",
            "pipeline.watch <name> [on|off] — watch the pipeline document, shaders and includes; compile changes after a quiet period.",
            (_, args) => {
            if (args.Count is < 1 or > 2) { return CommandResult.Usage(
                form: "<name> [on|off]",
                verb: "pipeline.watch"
            ); }
            var name = args[0].ToString();

            if (FindEntry(name: name) is not { } entry) { return Missing(
                name: name,
                verb: "pipeline.watch"
            ); }
            if (args.Count == 2) {
                if (args.Is(
                    index: 1,
                    value: "on"
                )) { entry.Watch(path: Path.GetFullPath(
                    entry.Source,
                    pipelines!.DocumentDirectory
                )); } else if (args.Is(
                    index: 1,
                    value: "off"
                )) { entry.Unwatch(); } else { return CommandResult.Usage(
                    form: "<name> [on|off]",
                    verb: "pipeline.watch"
                ); }
            }
            return new CommandResult($"[pipeline.watch: {name} {((entry.WatchPath is null)
                ? "off"
                : "on")}]");
        }
        );
        yield return Immediate(
            "pipeline.time",
            "pipeline.time <name> [pause|resume|set <seconds>|scale <rate>] — control the presentation clock and feedback advancement.",
            (_, args) => {
            if (args.Count is < 1 or > 3) { return CommandResult.Usage(
                form: "<name> [pause|resume|set <seconds>|scale <rate>]",
                verb: "pipeline.time"
            ); }
            var name = args[0].ToString();

            if (FindEntry(name: name) is not { } entry) { return Missing(
                name: name,
                verb: "pipeline.time"
            ); }
            if (
                (args.Count == 2) &&
                args.Is(
                index: 1,
                value: "pause"
            )
            ) { entry.ClockPaused = true; } else if (
                (args.Count == 2) &&
                args.Is(
                index: 1,
                value: "resume"
            )
            ) { entry.ClockPaused = false; } else if (
                (args.Count == 3) &&
                args.TryFloat(
                index: 2,
                value: out var value
            ) &&
                float.IsFinite(f: value) &&
                (value >= 0)
            ) {
                if (args.Is(
                    index: 1,
                    value: "set"
                )) { entry.Reset(); entry.ClockSeconds = value; } else if (args.Is(
                    index: 1,
                    value: "scale"
                )) { entry.ClockScale = value; } else { return CommandResult.Usage(
                    form: "<name> set <seconds>|scale <rate>",
                    verb: "pipeline.time"
                ); }
            } else if (args.Count != 1) { return CommandResult.Usage(
                form: "<name> [pause|resume|set <seconds>|scale <rate>]",
                verb: "pipeline.time"
            ); }
            return new CommandResult($"[pipeline.time: {name} {Clock(entry: entry)}]");
        }
        );
        yield return Immediate(
            "pipeline.step",
            "pipeline.step <name> — pause and advance exactly one pipeline frame by 1/60 second.",
            (_, args) => {
            if (args.Count != 1) { return CommandResult.Usage(
                form: "<name>",
                verb: "pipeline.step"
            ); }
            var name = args[0].ToString();

            if (FindEntry(name: name) is not { } entry) { return Missing(
                name: name,
                verb: "pipeline.step"
            ); }
            entry.Step();
            return new CommandResult($"[pipeline.step: {name} queued]");
        }
        );
        yield return Immediate(
            "pipeline.reset",
            "pipeline.reset <name> — reset time and feedback resources to their declared initial values.",
            (_, args) => {
            if (args.Count != 1) { return CommandResult.Usage(
                form: "<name>",
                verb: "pipeline.reset"
            ); }
            var name = args[0].ToString();

            if (FindEntry(name: name) is not { } entry) { return Missing(
                name: name,
                verb: "pipeline.reset"
            ); }
            entry.Reset();
            return new CommandResult($"[pipeline.reset: {name}]");
        }
        );
        yield return Immediate(
            "pipeline.output",
            "pipeline.output <name> <output> — display a named output of the pipeline.",
            (_, args) => {
            if (args.Count != 2) { return CommandResult.Usage(
                form: "<name> <output>",
                verb: "pipeline.output"
            ); }
            var name = args[0].ToString();

            if (FindEntry(name: name) is not { } entry) { return Missing(
                name: name,
                verb: "pipeline.output"
            ); }
            try { entry.Node.SelectOutput(name: args[1].ToString()); } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) { return CommandResult.Error(output: $"[pipeline.output: {exception.Message}]"); }
            return new CommandResult($"[pipeline.output: {name} {args[1].ToString()}]");
        }
        );
        yield return Immediate(
            "pipeline.set",
            "pipeline.set <name> <pass> <json> — replace a pass's live parameters using its schema; omitted fields take their declared defaults.",
            (context, args) => {
            if (args.Count < 3) { return CommandResult.Usage(
                form: "<name> <pass> <json>",
                verb: "pipeline.set"
            ); }
            var name = args[0].ToString();

            if (FindEntry(name: name) is not { } entry) { return Missing(
                name: name,
                verb: "pipeline.set"
            ); }
            var pass = args[1].ToString();

            try {
                using var document = JsonDocument.Parse(WorldCommandArguments.RawAfter(
                    args: in args,
                    context: context,
                    tokens: 3
                ));

                if (!entry.Node.TrySetConfig(
                    pass,
                    document.RootElement,
                    out var reason
                )) {
                    return CommandResult.Error(output: $"[pipeline.set: {reason}]");
                }
                return new CommandResult($"[pipeline.set: {name} {pass} updated]");
            } catch (JsonException exception) {
                return CommandResult.Error(output: $"[pipeline.set: {exception.Message}]");
            }
        }
        );
        yield return Immediate(
            "pipeline.capture",
            "pipeline.capture <name> <path> — capture the selected output on the next produced frame, including a paused frame.",
            (_, args) => {
            if (args.Count != 2) { return CommandResult.Usage(
                form: "<name> <path>",
                verb: "pipeline.capture"
            ); }
            var name = args[0].ToString();

            if (FindEntry(name: name) is not { } entry) { return Missing(
                name: name,
                verb: "pipeline.capture"
            ); }
            if (!entry.Node.IsReady) { return CommandResult.Error(output: $"[pipeline.capture: {name} has no active graph]"); }
            if (entry.Node.PendingCapturePath is { } pending) {
                return CommandResult.Error(output: $"[pipeline.capture: a capture of {pending} is still pending]");
            }
            try {
                var path = Path.GetFullPath(path: args[1].ToString());

                if (Path.GetDirectoryName(path: path) is { Length: > 0 } directory) { Directory.CreateDirectory(path: directory); }
                var request = new FrameCaptureRequest(path: path);

                entry.Node.RequestCapture(request: request);
                entry.Capture = request;
                return new CommandResult($"[pipeline.capture: {name} pending {path}]");
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)) {
                return CommandResult.Error(output: $"[pipeline.capture: {exception.Message}]");
            }
        }
        );
        yield return Immediate(
            "pipeline.inspect",
            "pipeline.inspect <name> — show ordered passes, typed resources and named outputs of the active graph.",
            (_, args) => {
            if (args.Count != 1) { return CommandResult.Usage(
                form: "<name>",
                verb: "pipeline.inspect"
            ); }
            var name = args[0].ToString();

            if (FindEntry(name: name) is not { } entry) { return Missing(
                name: name,
                verb: "pipeline.inspect"
            ); }
            if (entry.Node.Plan is not { } plan) { return CommandResult.Error(output: $"[pipeline.inspect: {name} has no active graph]"); }
            var result = new StringBuilder(value: $"[pipeline.inspect: {name}; allocated={entry.Node.AllocationBytes} bytes; budget={entry.Node.AllocationBudgetBytes} bytes");

            foreach (var pass in plan.Passes) {
                result.Append(handler: $"\n  {pass.Name}: {pass.Declaration.Kind}; reads={string.Join(
                    separator: ",",
                    values: pass.Declaration.InputReferences.Select(selector: input => (input.Name + (input.PreviousFrame
                    ? "@previous"
                    : string.Empty)))
                )}; writes={string.Join(
                    separator: ",",
                    values: pass.Declaration.OutputReferences.Select(selector: output => output.Name)
                )}");
            }
            foreach (var resource in plan.Resources) {
                result.Append(handler: $"\n  resource {resource.Name}: {resource.Declaration.Kind} {resource.Declaration.Format}; history={resource.Declaration.History}; initialization={resource.Declaration.Initialization}; lifetime={resource.FirstUsePassIndex}..{resource.LastUsePassIndex}");
            }
            foreach (var resource in entry.Node.ResourceStatus) {
                result.Append(handler: $"\n  allocated {resource.Name}: {resource.Width}x{resource.Height}; bytes={resource.AllocationBytes}; external={resource.External}");
            }
            foreach (var pass in entry.Node.PassStatus) {
                result.Append(handler: $"\n  GPU {pass.Name}: {((pass.LastGpuMilliseconds is { } milliseconds)
                    ? (milliseconds.ToString(
                        format: "0.###",
                        provider: CultureInfo.InvariantCulture
                    ) + " ms")
                    : "timing unavailable")}");
            }
            foreach (var output in plan.Outputs) { result.Append(handler: $"\n  output {output.Name} -> {output.Resource.Name}"); }
            return new CommandResult(result.Append(value: ']').ToString());
        }
        );
        yield return Immediate(
            "pipeline.status",
            "pipeline.status — show authored sources, pending compilation, last results, watches and clocks.",
            (_, args) => {
            if (args.Count != 0) { return CommandResult.Usage(
                form: string.Empty,
                verb: "pipeline.status"
            ); }
            var result = new StringBuilder(value: $"[pipeline.status: {server.Definition.Views.Pipelines.Count} row(s)");

            foreach (var row in server.Definition.Views.Pipelines) {
                result.Append(handler: $"\n  {row.Name} source={row.Source}");
                if (FindEntry(name: row.Name) is not { } entry) { result.Append(value: " unrendered"); continue; }
                result.Append(handler: $" {(entry.IsCompiling
                    ? "compiling"
                    : "idle")} ready={entry.Node.IsReady.ToString().ToLowerInvariant()} frames={entry.Node.FrameCounter} {Clock(entry: entry)} watch={((entry.WatchPath is null)
                    ? "off"
                    : "on")} changes={entry.SourceChangeCount}");
                result.Append(handler: $"\n    {(entry.LastCompile?.Message ?? "no completed compilation")}");
                if (entry.Node.LastSwapError is { } error) { result.Append(handler: $"\n    GPU candidate refused: {error.Message}"); }
            }
            return new CommandResult(result.Append(value: ']').ToString());
        }
        );
    }
}
