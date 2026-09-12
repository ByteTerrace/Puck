using System.Globalization;
using System.Text;
using System.Text.Json;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Live shader-pipeline authoring. Document mutations use the normal authority path;
/// presentation controls operate on accepted instances and never compile on the command thread.</summary>
internal sealed class WorldPipelineCommandModule(WorldServer server, IServerLink link, WorldDeferredVerbEchoes echoes,
    WorldPipelineRuntime? pipelines = null) : ICommandModule {
    private static CommandDefinition Immediate(string name, string description, Func<CommandContext, WireArgs, CommandResult> handler) =>
        CommandDefinition.WithWireArgs(name: name, description: description, handler: handler,
            bindability: CommandBindability.Unbindable, routing: CommandRouting.Immediate);

    private WorldPipelineRuntime.Entry? FindEntry(string name) {
        pipelines?.Reconcile(server.Definition.Views.Pipelines);
        return pipelines is not null && pipelines.TryGet(name, out var entry) ? entry : null;
    }
    private static CommandResult Missing(string verb, string name) =>
        CommandResult.Error($"[{verb}: '{name}' has no rendered pipeline instance]");
    private static string Clock(WorldPipelineRuntime.Entry entry) => string.Create(CultureInfo.InvariantCulture,
        $"paused={entry.ClockPaused.ToString().ToLowerInvariant()} seconds={entry.ClockSeconds:0.###} scale={entry.ClockScale:0.###}");

    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            name: "pipeline.load",
            description: "pipeline.load <name> <source> [camera] — author a pipeline JSON or one-off shader source, relative to the world document. Accepted rows compile in the background; pipeline.status reports the result.",
            bindability: CommandBindability.Unbindable, routing: CommandRouting.Simulation,
            handler: (context, args) => {
                if (args.Count is < 2 or > 3) { return CommandResult.Usage("pipeline.load", "<name> <source> [camera]"); }
                return link.Submit(mutation: new WorldMutation.UpsertViewPipeline(
                    Principal: context.ActingPrincipal(),
                    Pipeline: new WorldViewPipeline(Name: args[0].ToString(), Source: args[1].ToString(),
                        Camera: args.Count == 3 ? args[2].ToString() : null)),
                    echoes: echoes, verb: "pipeline.load");
            });
        yield return Immediate("pipeline.reload", "pipeline.reload [name] — compile a complete candidate in the background; keep the last good pipeline on errors.", (_, args) => {
            if (args.Count > 1) { return CommandResult.Usage("pipeline.reload", "[name]"); }
            if (pipelines is null) { return CommandResult.Error("[pipeline.reload: requires a rendered host]"); }
            pipelines.Reconcile(server.Definition.Views.Pipelines);
            if (args.Count == 1) {
                var name = args[0].ToString();
                if (FindEntry(name) is not { } entry) { return Missing("pipeline.reload", name); }
                pipelines.QueueCompile(name, entry.Source);
                return new CommandResult($"[pipeline.reload: {name} queued]");
            }
            foreach (var (name, entry) in pipelines.Entries) { pipelines.QueueCompile(name, entry.Source); }
            return new CommandResult($"[pipeline.reload: {pipelines.Entries.Count} queued]");
        });
        yield return Immediate("pipeline.watch", "pipeline.watch <name> [on|off] — watch the pipeline document, shaders and includes; compile changes after a quiet period.", (_, args) => {
            if (args.Count is < 1 or > 2) { return CommandResult.Usage("pipeline.watch", "<name> [on|off]"); }
            var name = args[0].ToString();
            if (FindEntry(name) is not { } entry) { return Missing("pipeline.watch", name); }
            if (args.Count == 2) {
                if (args.Is(1, "on")) { entry.Watch(Path.GetFullPath(entry.Source, pipelines!.DocumentDirectory)); }
                else if (args.Is(1, "off")) { entry.Unwatch(); }
                else { return CommandResult.Usage("pipeline.watch", "<name> [on|off]"); }
            }
            return new CommandResult($"[pipeline.watch: {name} {(entry.WatchPath is null ? "off" : "on")}]");
        });
        yield return Immediate("pipeline.time", "pipeline.time <name> [pause|resume|set <seconds>|scale <rate>] — control the presentation clock and feedback advancement.", (_, args) => {
            if (args.Count is < 1 or > 3) { return CommandResult.Usage("pipeline.time", "<name> [pause|resume|set <seconds>|scale <rate>]"); }
            var name = args[0].ToString();
            if (FindEntry(name) is not { } entry) { return Missing("pipeline.time", name); }
            if (args.Count == 2 && args.Is(1, "pause")) { entry.ClockPaused = true; }
            else if (args.Count == 2 && args.Is(1, "resume")) { entry.ClockPaused = false; }
            else if (args.Count == 3 && args.TryFloat(2, out var value) && float.IsFinite(value) && value >= 0) {
                if (args.Is(1, "set")) { entry.Reset(); entry.ClockSeconds = value; }
                else if (args.Is(1, "scale")) { entry.ClockScale = value; }
                else { return CommandResult.Usage("pipeline.time", "<name> set <seconds>|scale <rate>"); }
            } else if (args.Count != 1) { return CommandResult.Usage("pipeline.time", "<name> [pause|resume|set <seconds>|scale <rate>]"); }
            return new CommandResult($"[pipeline.time: {name} {Clock(entry)}]");
        });
        yield return Immediate("pipeline.step", "pipeline.step <name> — pause and advance exactly one pipeline frame by 1/60 second.", (_, args) => {
            if (args.Count != 1) { return CommandResult.Usage("pipeline.step", "<name>"); }
            var name = args[0].ToString();
            if (FindEntry(name) is not { } entry) { return Missing("pipeline.step", name); }
            entry.Step();
            return new CommandResult($"[pipeline.step: {name} queued]");
        });
        yield return Immediate("pipeline.reset", "pipeline.reset <name> — reset time and feedback resources to their declared initial values.", (_, args) => {
            if (args.Count != 1) { return CommandResult.Usage("pipeline.reset", "<name>"); }
            var name = args[0].ToString();
            if (FindEntry(name) is not { } entry) { return Missing("pipeline.reset", name); }
            entry.Reset();
            return new CommandResult($"[pipeline.reset: {name}]");
        });
        yield return Immediate("pipeline.output", "pipeline.output <name> <output> — display a named output of the pipeline.", (_, args) => {
            if (args.Count != 2) { return CommandResult.Usage("pipeline.output", "<name> <output>"); }
            var name = args[0].ToString();
            if (FindEntry(name) is not { } entry) { return Missing("pipeline.output", name); }
            try { entry.Node.SelectOutput(args[1].ToString()); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return CommandResult.Error($"[pipeline.output: {exception.Message}]"); }
            return new CommandResult($"[pipeline.output: {name} {args[1].ToString()}]");
        });
        yield return Immediate("pipeline.set", "pipeline.set <name> <pass> <json> — replace a pass's live parameters using its schema; omitted fields take their declared defaults.", (context, args) => {
            if (args.Count < 3) { return CommandResult.Usage("pipeline.set", "<name> <pass> <json>"); }
            var name = args[0].ToString();
            if (FindEntry(name) is not { } entry) { return Missing("pipeline.set", name); }
            var pass = args[1].ToString();
            try {
                using var document = JsonDocument.Parse(WorldCommandArguments.RawAfter(args: in args, context: context, tokens: 3));
                if (!entry.Node.TrySetConfig(pass, document.RootElement, out var reason)) {
                    return CommandResult.Error($"[pipeline.set: {reason}]");
                }
                return new CommandResult($"[pipeline.set: {name} {pass} updated]");
            } catch (JsonException exception) {
                return CommandResult.Error($"[pipeline.set: {exception.Message}]");
            }
        });
        yield return Immediate("pipeline.inspect", "pipeline.inspect <name> — show ordered passes, typed resources and named outputs of the active graph.", (_, args) => {
            if (args.Count != 1) { return CommandResult.Usage("pipeline.inspect", "<name>"); }
            var name = args[0].ToString();
            if (FindEntry(name) is not { } entry) { return Missing("pipeline.inspect", name); }
            if (entry.Node.Plan is not { } plan) { return CommandResult.Error($"[pipeline.inspect: {name} has no active graph]"); }
            var result = new StringBuilder($"[pipeline.inspect: {name}");
            foreach (var pass in plan.Passes) {
                result.Append($"\n  {pass.Name}: {pass.Declaration.Kind}; reads={string.Join(",", pass.Declaration.InputReferences.Select(input => input.Name + (input.PreviousFrame ? "@previous" : string.Empty)))}; writes={string.Join(",", pass.Declaration.OutputReferences.Select(output => output.Name))}");
            }
            foreach (var resource in plan.Resources) {
                result.Append($"\n  resource {resource.Name}: {resource.Declaration.Kind} {resource.Declaration.Format}; history={resource.Declaration.History}; initialization={resource.Declaration.Initialization}");
            }
            foreach (var output in plan.Outputs) { result.Append($"\n  output {output.Name} -> {output.Resource.Name}"); }
            return new CommandResult(result.Append(']').ToString());
        });
        yield return Immediate("pipeline.status", "pipeline.status — show authored sources, pending compilation, last results, watches and clocks.", (_, args) => {
            if (args.Count != 0) { return CommandResult.Usage("pipeline.status", string.Empty); }
            var result = new StringBuilder($"[pipeline.status: {server.Definition.Views.Pipelines.Count} row(s)");
            foreach (var row in server.Definition.Views.Pipelines) {
                result.Append($"\n  {row.Name} source={row.Source}");
                if (FindEntry(row.Name) is not { } entry) { result.Append(" unrendered"); continue; }
                result.Append($" {(entry.IsCompiling ? "compiling" : "idle")} {Clock(entry)} watch={(entry.WatchPath is null ? "off" : "on")} changes={entry.SourceChangeCount}");
                result.Append($"\n    {entry.LastCompile?.Message ?? "no completed compilation"}");
                if (entry.Node.LastSwapError is { } error) { result.Append($"\n    GPU candidate refused: {error.Message}"); }
            }
            return new CommandResult(result.Append(']').ToString());
        });
    }
}
