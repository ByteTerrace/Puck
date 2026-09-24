using System.Globalization;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Shaders;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Live shader-pipeline authoring. Document mutations use the normal authority path;
/// presentation controls operate on accepted instances and never compile on the command thread.</summary>
internal sealed class WorldPipelineCommandModule(WorldServer server, IServerLink link, WorldDeferredVerbEchoes echoes,
    WorldPipelineRuntime? pipelines = null, WorldRenderProbe? renderProbe = null) : ICommandModule {
    // Long enough for a cold cross-backend compile of a small pipeline; a script names a longer bound explicitly.
    private const int DefaultWaitSeconds = 30;

    // Reopens the inspection record the node closed and ends it with the device's memory profile and the residency
    // the selector chooses for the instance's parameter region; a host without a device reports the default profile.
    private void AppendResidency(StringBuilder builder, ShaderPipelinePlan plan) {
        var profile = (renderProbe?.Device?.MemoryProfile ?? default);
        var bytes = plan.ParameterBytes;

        builder.Length--;
        builder.Append(handler: $"\n{ConsoleRecord.ContinuationIndent}memory:");
        profile.AppendFields(builder: builder);
        builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $"\n{ConsoleRecord.ContinuationIndent}residency: parameters={bytes} bytes policy={GpuResidency.Name(policy: GpuResidency.Select(
                byteCount: bytes,
                profile: profile
            ))}]"
        );
    }
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
    // One line per overridden field: the committed value and the preview, where "-" is no value of its own and
    // "default" is a previewed pass that leaves the field at the source's default.
    private static string DescribeOverrides(WorldViewPipeline row, WorldPipelineRuntime.Entry? entry) {
        entry?.Synchronize();

        var pending = (entry?.PendingOverrides ?? new Dictionary<string, JsonElement>());
        var committed = (row.Overrides ?? new Dictionary<string, JsonElement>());
        var result = new StringBuilder(value: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[pipeline.overrides: {row.Name} revision={WorldDefinitionFingerprint.ComputePipeline(pipeline: row)} installed={(entry?.InstalledSource?.SourceIdentity ?? "none")}"
        ));
        var fields = new SortedSet<(string Pass, string Field)>();

        foreach (var (pass, config) in committed.Concat(second: pending)) {
            foreach (var field in config.EnumerateObject()) {
                fields.Add(item: (pass, field.Name));
            }
        }
        foreach (var (pass, field) in fields) {
            var committedValue = ((committed.TryGetValue(key: pass, value: out var committedPass) && committedPass.TryGetProperty(propertyName: field, value: out var committedField))
                ? committedField.GetRawText()
                : "-");
            var pendingValue = (pending.TryGetValue(key: pass, value: out var pendingPass)
                ? (pendingPass.TryGetProperty(propertyName: field, value: out var pendingField)
                    ? pendingField.GetRawText()
                    : "default")
                : "-");

            result.Append(value: $"\n  {pass}.{field} committed={committedValue} pending={pendingValue}");
        }
        result.Append(value: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"\n  timeScale committed={row.TimeScale:0.###} pending={((entry is null) ? "-" : entry.ClockScale.ToString(format: "0.###", provider: CultureInfo.InvariantCulture))}"
        ));
        result.Append(value: $"\n  output committed={(row.Output ?? "-")} pending={(entry?.SelectedOutput ?? "-")}");

        return result.Append(value: ']').ToString();
    }
    private static CommandResult Missing(string verb, string name) =>
        CommandResult.Error(output: $"[{verb}: '{name}' has no rendered pipeline instance]");

    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            name: "pipeline.load",
            description: "pipeline.load <name> <source> [camera] — author a pipeline JSON, a one-off shader source, or a shader package directory, relative to the world document. Accepted rows compile in the background; pipeline.status reports the result.",
            bindability: CommandBindability.Unbindable,
            routing: CommandRouting.Simulation,
            handler: (context, args) => {
                if (args.Count is < 2 or > 3) {
                    return CommandResult.Usage(
                    form: "<name> <source> [camera]",
                    verb: "pipeline.load"
                );
                }
                return link.Submit(
                    mutation: new WorldMutation.UpsertViewPipeline(
                        Principal: context.Principal,
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
                if (args.Count > 1) {
                    return CommandResult.Usage(
                    form: "[name]",
                    verb: "pipeline.reload"
                );
                }
                if (pipelines is null) { return CommandResult.Error(output: "[pipeline.reload: requires a rendered host]"); }
                pipelines.Reconcile(rows: server.Definition.Views.Pipelines);
                if (args.Count == 1) {
                    var name = args[0].ToString();

                    if (FindEntry(name: name) is not { } entry) {
                        return Missing(
                        name: name,
                        verb: "pipeline.reload"
                    );
                    }
                    pipelines.QueueCompile(
                        name: name,
                        source: entry.Source
                    );
                    return new CommandResult($"[pipeline.reload: {name} queued]");
                }
                foreach (var (name, entry) in pipelines.Entries) {
                    pipelines.QueueCompile(
                    name: name,
                    source: entry.Source
                );
                }
                return new CommandResult($"[pipeline.reload: {pipelines.Entries.Count} queued]");
            }
        );
        yield return Immediate(
            "pipeline.watch",
            "pipeline.watch <name> [on|off] — watch the pipeline document, shaders and includes; compile changes after a quiet period.",
            (_, args) => {
                if (args.Count is < 1 or > 2) {
                    return CommandResult.Usage(
                    form: "<name> [on|off]",
                    verb: "pipeline.watch"
                );
                }
                var name = args[0].ToString();

                if (FindEntry(name: name) is not { } entry) {
                    return Missing(
                    name: name,
                    verb: "pipeline.watch"
                );
                }
                if (args.Count == 2) {
                    if (args.Is(
                        index: 1,
                        value: "on"
                    )) {
                        entry.Watch(path: Path.GetFullPath(
                        entry.Source,
                        pipelines!.DocumentDirectory
                    ));
                    } else if (args.Is(
                        index: 1,
                        value: "off"
                    )) { entry.Unwatch(); } else {
                        return CommandResult.Usage(
                        form: "<name> [on|off]",
                        verb: "pipeline.watch"
                    );
                    }
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
                if (args.Count is < 1 or > 3) {
                    return CommandResult.Usage(
                    form: "<name> [pause|resume|set <seconds>|scale <rate>]",
                    verb: "pipeline.time"
                );
                }
                var name = args[0].ToString();

                if (FindEntry(name: name) is not { } entry) {
                    return Missing(
                    name: name,
                    verb: "pipeline.time"
                );
                }
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
                    )) { entry.ClockScale = value; } else {
                        return CommandResult.Usage(
                        form: "<name> set <seconds>|scale <rate>",
                        verb: "pipeline.time"
                    );
                    }
                } else if (args.Count != 1) {
                    return CommandResult.Usage(
                    form: "<name> [pause|resume|set <seconds>|scale <rate>]",
                    verb: "pipeline.time"
                );
                }
                return new CommandResult($"[pipeline.time: {name} {Clock(entry: entry)}]");
            }
        );
        yield return Immediate(
            "pipeline.step",
            "pipeline.step <name> — pause and advance exactly one pipeline frame by 1/60 second.",
            (_, args) => {
                if (args.Count != 1) {
                    return CommandResult.Usage(
                    form: "<name>",
                    verb: "pipeline.step"
                );
                }
                var name = args[0].ToString();

                if (FindEntry(name: name) is not { } entry) {
                    return Missing(
                    name: name,
                    verb: "pipeline.step"
                );
                }
                entry.Step();
                return new CommandResult($"[pipeline.step: {name} queued]");
            }
        );
        yield return Immediate(
            "pipeline.reset",
            "pipeline.reset <name> — reset time and feedback resources to their declared initial values.",
            (_, args) => {
                if (args.Count != 1) {
                    return CommandResult.Usage(
                    form: "<name>",
                    verb: "pipeline.reset"
                );
                }
                var name = args[0].ToString();

                if (FindEntry(name: name) is not { } entry) {
                    return Missing(
                    name: name,
                    verb: "pipeline.reset"
                );
                }
                entry.Reset();
                return new CommandResult($"[pipeline.reset: {name}]");
            }
        );
        yield return Immediate(
            "pipeline.sentinels",
            "pipeline.sentinels <name> on|off — write each frame-block word's echo sentinel in place of the frame values and config, so an echo pass shows whether every member reads back exactly.",
            (_, args) => {
                if (
                    (args.Count != 2) ||
                    !(args.Is(
                        index: 1,
                        value: "on"
                    ) || args.Is(
                        index: 1,
                        value: "off"
                    ))
                ) {
                    return CommandResult.Usage(
                    form: "<name> on|off",
                    verb: "pipeline.sentinels"
                );
                }
                var name = args[0].ToString();

                if (FindEntry(name: name) is not { } entry) {
                    return Missing(
                    name: name,
                    verb: "pipeline.sentinels"
                );
                }
                entry.Node.Sentinels = args.Is(
                    index: 1,
                    value: "on"
                );
                return new CommandResult($"[pipeline.sentinels: {name} {(entry.Node.Sentinels ? "on" : "off")}]");
            }
        );
        yield return Immediate(
            "pipeline.output",
            "pipeline.output <name> <output> — preview a named image version as the instance's output; pipeline.commit makes it authored.",
            (_, args) => {
                if (args.Count != 2) {
                    return CommandResult.Usage(
                    form: "<name> <output>",
                    verb: "pipeline.output"
                );
                }
                var name = args[0].ToString();

                if (FindEntry(name: name) is not { } entry) {
                    return Missing(
                    name: name,
                    verb: "pipeline.output"
                );
                }
                if (!entry.TrySelectOutput(
                    output: args[1].ToString(),
                    reason: out var reason
                )) {
                    return CommandResult.Error(output: $"[pipeline.output: {reason}]");
                }
                return new CommandResult($"[pipeline.output: {name} {args[1].ToString()} pending]");
            }
        );
        yield return Immediate(
            "pipeline.set",
            "pipeline.set <name> <pass> <json> — preview parameter overrides for a pass: each field replaces the pass's current value, null returns a field to the source's default, and the result binds through the pass's config schema. pipeline.commit makes the preview authored; pipeline.overrides shows it beside the committed values.",
            (context, args) => {
                if (args.Count < 3) {
                    return CommandResult.Usage(
                    form: "<name> <pass> <json>",
                    verb: "pipeline.set"
                );
                }
                var name = args[0].ToString();

                if (FindEntry(name: name) is not { } entry) {
                    return Missing(
                    name: name,
                    verb: "pipeline.set"
                );
                }
                var pass = args[1].ToString();

                try {
                    using var document = JsonDocument.Parse(WorldCommandArguments.RawAfter(
                        args: in args,
                        context: context,
                        tokens: 3
                    ));

                    if (!entry.TrySetOverride(
                        change: document.RootElement,
                        pass: pass,
                        reason: out var reason
                    )) {
                        return CommandResult.Error(output: $"[pipeline.set: {reason}]");
                    }
                    return new CommandResult($"[pipeline.set: {name} {pass} pending]");
                } catch (JsonException exception) {
                    return CommandResult.Error(output: $"[pipeline.set: {exception.Message}]");
                }
            }
        );
        yield return CommandDefinition.WithWireArgs(
            name: "pipeline.commit",
            description: "pipeline.commit <name> — commit the instance's previewed overrides, time scale and output into its views.pipelines row. The commit names the row revision and the installed source it was previewed against, and is refused by name when either has moved; world.save then writes it.",
            bindability: CommandBindability.Unbindable,
            routing: CommandRouting.Simulation,
            handler: (context, args) => {
                if (args.Count != 1) {
                    return CommandResult.Usage(
                    form: "<name>",
                    verb: "pipeline.commit"
                );
                }
                if (pipelines is null) { return CommandResult.Error(output: "[pipeline.commit: requires a rendered host]"); }
                var name = args[0].ToString();

                if (FindEntry(name: name) is not { } entry) {
                    return Missing(
                    name: name,
                    verb: "pipeline.commit"
                );
                }
                if (!entry.TryPrepareCommit(
                    commit: out var commit,
                    principal: context.Principal,
                    reason: out var reason
                )) {
                    return CommandResult.Error(output: $"[pipeline.commit: {name} {reason}]");
                }
                return link.Submit(
                    echoes: echoes,
                    mutation: commit,
                    verb: "pipeline.commit"
                );
            }
        );
        yield return Immediate(
            "pipeline.overrides",
            "pipeline.overrides <name> — show the instance's committed overrides, time scale and output beside its uncommitted preview, the row revision the preview is based on, and the source identity of the installed graph.",
            (_, args) => {
                if (args.Count != 1) {
                    return CommandResult.Usage(
                    form: "<name>",
                    verb: "pipeline.overrides"
                );
                }
                var name = args[0].ToString();

                if (WorldDefinitionRows.FindPipeline(
                    name: name,
                    pipelines: server.Definition.Views.Pipelines
                ) is not { } row) {
                    return CommandResult.Error(output: $"[pipeline.overrides: no views.pipelines row named '{name}']");
                }
                return new CommandResult(DescribeOverrides(
                    entry: FindEntry(name: name),
                    row: row
                ));
            }
        );
        yield return Immediate(
            "pipeline.capture",
            "pipeline.capture <name> <path> — capture the selected output on the next produced frame, including a paused frame.",
            (_, args) => {
                if (args.Count != 2) {
                    return CommandResult.Usage(
                    form: "<name> <path>",
                    verb: "pipeline.capture"
                );
                }
                var name = args[0].ToString();

                if (FindEntry(name: name) is not { } entry) {
                    return Missing(
                    name: name,
                    verb: "pipeline.capture"
                );
                }
                if (!entry.Node.IsReady) { return CommandResult.Error(output: $"[pipeline.capture: {name} has no active graph]"); }
                if (entry.Node.PendingCapturePath is { } pending) {
                    return CommandResult.Error(output: $"[pipeline.capture: a capture of {pending} is still pending]");
                }
                try {
                    var path = Path.GetFullPath(path: args[1].ToString());

                    if (Path.GetDirectoryName(path: path) is { Length: > 0 } directory) { Directory.CreateDirectory(path: directory); }
                    entry.RequestCapture(request: new FrameCaptureRequest(path: path));
                    return new CommandResult($"[pipeline.capture: {name} pending {path}]");
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)) {
                    return CommandResult.Error(output: $"[pipeline.capture: {exception.Message}]");
                }
            }
        );
        yield return Immediate(
            "pipeline.wait",
            $"pipeline.wait <name> compiled|installed|captured [seconds] | pipeline.wait <name> submitted|counted <frames> [seconds] | pipeline.wait <name> resized <width> <height> [seconds] — hold only this session until the instance's latest compilation completes, its compiled candidate is installed, it has submitted that many frames since its last reset (or since boot, never reset), the work counts of that many submissions since its last reset (or boot) have completed and pipeline.inspect shows them, its latest capture is written, or the host has requested that output extent and the instance has installed its graph at that extent. A reload, a row edit or a resize is not a step: a paused instance (pipeline.time pause, or time scale zero) builds and installs it too, reaching installed and resized, and keeps showing its last image until a step, resume or reset renders the new graph. The wait lasts at most seconds (default {DefaultWaitSeconds}, 1..{WorldPipelineRuntime.MaxWaitSeconds}) of presentation time. Exactly one '[pipeline: <name> wait <phase> …]' outcome follows on stderr: reached, failed, unsupported (a shader tool is absent), or timed out.",
            (context, args) => {
                const string Form = "<name> compiled|installed|captured [seconds] | <name> submitted|counted <frames> [seconds] | <name> resized <width> <height> [seconds]";

                if (args.Count is < 2 or > 5) {
                    return CommandResult.Usage(
                    form: Form,
                    verb: "pipeline.wait"
                );
                }
                if (pipelines is null) { return CommandResult.Error(output: "[pipeline.wait: requires a rendered host]"); }
                var name = args[0].ToString();

                if (FindEntry(name: name) is not { } entry) {
                    return Missing(
                    name: name,
                    verb: "pipeline.wait"
                );
                }
                WorldPipelinePhase phase;
                var submissions = 0UL;
                var extent = default((uint Width, uint Height));
                var next = 2;

                if (args.Is(
                    index: 1,
                    value: "compiled"
                )) { phase = WorldPipelinePhase.Compiled; } else if (args.Is(
                    index: 1,
                    value: "installed"
                )) { phase = WorldPipelinePhase.Installed; } else if (args.Is(
                    index: 1,
                    value: "captured"
                )) { phase = WorldPipelinePhase.Captured; } else if (
                    args.Is(
                    index: 1,
                    value: "submitted"
                ) &&
                    (args.Count >= 3) &&
                    args.TryUnsignedDigits(
                    index: 2,
                    value: out submissions
                ) &&
                    (submissions > 0)
                ) {
                    phase = WorldPipelinePhase.Submitted;
                    next = 3;
                } else if (
                    args.Is(
                    index: 1,
                    value: "counted"
                ) &&
                    (args.Count >= 3) &&
                    args.TryUnsignedDigits(
                    index: 2,
                    value: out submissions
                ) &&
                    (submissions is > 0 and <= long.MaxValue)
                ) {
                    phase = WorldPipelinePhase.Counted;
                    next = 3;
                } else if (
                    args.Is(
                    index: 1,
                    value: "resized"
                ) &&
                    (args.Count >= 4) &&
                    args.TryUnsignedDigits(
                    index: 2,
                    value: out var width
                ) &&
                    args.TryUnsignedDigits(
                    index: 3,
                    value: out var height
                ) &&
                    (width is > 0 and <= uint.MaxValue) &&
                    (height is > 0 and <= uint.MaxValue)
                ) {
                    phase = WorldPipelinePhase.Resized;
                    extent = (((uint)width), ((uint)height));
                    next = 4;
                } else {
                    return CommandResult.Usage(
                        form: Form,
                        verb: "pipeline.wait"
                    );
                }
                var seconds = DefaultWaitSeconds;

                if (args.Count > next) {
                    if (
                        (args.Count != (next + 1)) ||
                        !args.TryUnsignedDigits(
                        index: next,
                        value: out var authored
                    ) ||
                        (authored is 0 or > WorldPipelineRuntime.MaxWaitSeconds)
                    ) {
                        return CommandResult.Error(output: $"[pipeline.wait: seconds must be a whole number in 1..{WorldPipelineRuntime.MaxWaitSeconds}]");
                    }
                    seconds = ((int)authored);
                }
                if (context.TextSession is not { } session) { return CommandResult.Error(output: "[pipeline.wait: requires an originating text session]"); }
                if (entry.IsWaiting) { return CommandResult.Error(output: $"[pipeline.wait: {name} already has a wait armed]"); }
                session.HoldWhile(hold: pipelines.ArmWait(
                    extent: extent,
                    name: name,
                    phase: phase,
                    seconds: seconds,
                    submissions: submissions
                ));
                return new CommandResult(string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"[pipeline.wait: {name} {args[1].ToString()}{phase switch {
                        WorldPipelinePhase.Submitted or WorldPipelinePhase.Counted => $" {submissions}",
                        WorldPipelinePhase.Resized => $" {extent.Width} {extent.Height}",
                        _ => string.Empty,
                    }} armed for at most {seconds}s]"
                ));
            }
        );
        yield return Immediate(
            "pipeline.inspect",
            "pipeline.inspect <name> — show the bytes the instance owns beside the active graph's steady-state bytes, the peak a reload of it would reach and the budget, then its ordered passes, typed resources and named outputs, the GPU work each pass recorded in the newest completed submission, the device's memory profile, and the residency policy the selector chooses for the instance's parameter region.",
            (_, args) => {
                if (args.Count != 1) {
                    return CommandResult.Usage(
                    form: "<name>",
                    verb: "pipeline.inspect"
                );
                }
                var name = args[0].ToString();

                if (FindEntry(name: name) is not { } entry) {
                    return Missing(
                    name: name,
                    verb: "pipeline.inspect"
                );
                }
                var result = new StringBuilder();

                if (
                    (entry.Node.Plan is not { } plan) ||
                    !entry.Node.TryAppendInspection(
                        builder: result,
                        name: name
                    )
                ) {
                    return CommandResult.Error(output: $"[pipeline.inspect: {name} has no active graph]");
                }

                AppendResidency(
                    builder: result,
                    plan: plan
                );

                return new CommandResult(result.ToString());
            }
        );
        yield return Immediate(
            "pipeline.budget",
            $"pipeline.budget <name> [<bytes>|device] — show the instance's memory budget in bytes: the device's (1/{ShaderPipelineMemoryBudget.DeviceLocalShare} of its device-local memory, or {ShaderPipelineMemoryBudget.UnreportedBytes} when it reports none), a cap that only lowers it, and the installed graph's owned, steady-state and reload-peak bytes. A byte count sets the cap; device clears it. A replacement whose peak does not fit is refused by name before it allocates, and the installed graph keeps running; lowering the cap never frees it.",
            (_, args) => {
                if (args.Count is < 1 or > 2) {
                    return CommandResult.Usage(
                    form: "<name> [<bytes>|device]",
                    verb: "pipeline.budget"
                );
                }
                var name = args[0].ToString();

                if (FindEntry(name: name) is not { } entry) {
                    return Missing(
                    name: name,
                    verb: "pipeline.budget"
                );
                }
                if (args.Count == 2) {
                    if (args.Is(
                        index: 1,
                        value: "device"
                    )) {
                        entry.Node.BudgetCapBytes = null;
                    } else if (
                        args.TryULong(
                        index: 1,
                        value: out var cap
                    ) &&
                        (cap != 0UL)
                    ) {
                        entry.Node.BudgetCapBytes = cap;
                    } else {
                        return CommandResult.Usage(
                        form: "<name> [<bytes>|device]",
                        verb: "pipeline.budget"
                    );
                    }
                }
                var node = entry.Node;
                var account = node.InstalledAccount;

                return new CommandResult(string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"[pipeline.budget: {name} budget={node.BudgetBytes} device={node.DeviceBudgetBytes} cap={((node.BudgetCapBytes is { } bytes) ? bytes.ToString(provider: CultureInfo.InvariantCulture) : "none")} owned={node.OwnedBytes} steady={account.SteadyBytes} peak={account.PeakBytes}]"
                ));
            }
        );
        yield return Immediate(
            "pipeline.status",
            "pipeline.status — show authored sources, pending compilation, last results, watches, clocks and each instance's work counters.",
            (_, args) => {
                if (args.Count != 0) {
                    return CommandResult.Usage(
                    form: string.Empty,
                    verb: "pipeline.status"
                );
                }
                var result = new StringBuilder(value: $"[pipeline.status: {server.Definition.Views.Pipelines.Count} row(s)");

                foreach (var row in server.Definition.Views.Pipelines) {
                    result.Append(handler: $"\n  {row.Name} source={row.Source}");
                    if (FindEntry(name: row.Name) is not { } entry) { result.Append(value: " unrendered"); continue; }
                    result.Append(handler: $" {(entry.IsCompiling
                        ? "compiling"
                        : "idle")} ready={entry.Node.IsReady.ToString().ToLowerInvariant()} frames={entry.Node.FrameCounter} {Clock(entry: entry)} watch={((entry.WatchPath is null)
                        ? "off"
                        : "on")} changes={entry.SourceChangeCount} extent={entry.Node.Extent.Width}x{entry.Node.Extent.Height}{((entry.Node.RequestedExtent == entry.Node.Extent)
                        ? string.Empty
                        : $" requested={entry.Node.RequestedExtent.Width}x{entry.Node.RequestedExtent.Height}")}");
                    result.Append(handler: $"\n    {(entry.LastCompile?.Message ?? "no completed compilation")}");
                    GpuWorkReport.AppendLifetime(
                        builder: result.Append(value: "\n    "),
                        source: entry.Node
                    ).Length--;
                    if (entry.Node.LastSwapError is { } error) { result.Append(handler: $"\n    GPU candidate refused: {error.Message}"); }
                }
                return new CommandResult(result.Append(value: ']').ToString());
            }
        );
    }
}
