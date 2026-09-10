using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.Shaders.Study;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>study.*</c> console verb surface — load/reload/watch/time/status for a compiled <c>views.studies</c> row
/// (see <c>WorldViewStudy</c>). CORE-registered (windowed AND headless) for command-vocabulary parity with
/// <see cref="WorldViewCommandModule"/>: a shipped world that commits a <c>study.*</c> verb on a wheel ring needs the
/// same verb names under a headless boot, or the document refuses once its vocabulary composes. <see cref="WorldServer"/>,
/// <see cref="IServerLink"/>, and <see cref="WorldDeferredVerbEchoes"/> are core, so <c>study.load</c> genuinely
/// upserts the <c>views.studies</c> row headless too; <see cref="WorldRenderProbe"/>/<see cref="WorldStudyRuntime"/>
/// are OPTIONAL (default <see langword="null"/>, registered together by whichever render-tree factory built the
/// document's studies) and every other verb refuses by name at use when they are absent, mirroring
/// <c>world.view.pointer</c>'s posture toward <c>WorldCursorFeed</c>.
/// </summary>
/// <remarks>
/// Every verb here runs on the window-pump thread — the thread that also produces frames and owns the console
/// writers — so compiling, a <see cref="StudyPassNode.Swap(StudyProgram)"/>, and a stderr echo inside a handler are
/// all safe. <c>study.watch</c> involves no other thread either: the frame presenter polls the watched source's
/// write stamp once per produced frame (<see cref="WorldStudyRuntime.PumpWatches"/>) and runs the reload there once
/// the stamp has held still. A study named for the first time by
/// <c>study.load</c> gets a node from <see cref="WorldStudyRuntime.CreateNode"/> and registers on the engine node
/// (<see cref="Puck.SdfVm.SdfEngineNode.RegisterChild"/>) right there, so it is a live child as soon as a layout slot
/// names it — the row upsert lands at the tick boundary independently.
/// </remarks>
internal sealed class WorldStudyCommandModule(WorldServer server, IServerLink link, WorldDeferredVerbEchoes echoes, WorldRenderProbe? renderProbe = null, WorldStudyRuntime? studies = null) : ICommandModule {
    private static WorldViewStudy? FindStudy(WorldServer server, string name) {
        var rows = server.Definition.Views.Studies;

        for (var index = 0; (index < rows.Count); index++) {
            if (string.Equals(
                a: rows[index].Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return rows[index];
            }
        }

        return null;
    }
    private static string ResolveSourcePath(string documentDirectory, string source) =>
        (Path.IsPathRooted(path: source)
            ? source
            : Path.Combine(path1: documentDirectory, path2: source)
        );
    // The runtime entry for `name`, creating and registering one (runtime + engine node) for a study named for the
    // first time by a runtime study.load — so a layout slot naming it next frame finds a live child. Refuses by name
    // when the host has no render tree to register into.
    private (WorldStudyRuntime.Entry? Entry, string? Refusal) EnsureEntry(string name) {
        if (studies is not { } runtime) {
            return (null, "requires a windowed boot");
        }

        if (runtime.TryGet(
            entry: out var entry,
            name: name
        )) {
            return (entry, null);
        }

        if (
            (runtime.CreateNode is not { } createNode) ||
            (renderProbe?.Node is not { } engineNode)
        ) {
            return (null, $"'{name}' cannot register: no render tree to host it");
        }

        var node = createNode(name);

        runtime.Register(
            name: name,
            node: node
        );
        engineNode.RegisterChild(
            name: name,
            node: node
        );

        return (runtime.Entries[name], null);
    }
    // The one compile path study.load, study.reload, and the debounced study.watch callback (TriggerReload) all
    // run — resolves `source` relative to the runtime's boot-captured document directory, records the outcome on the
    // entry, and echoes every error diagnostic as <file>:<line>: <message>. Safe on any thread; the SWAP is the
    // caller's, and pump-thread only (see this type's remarks).
    private (StudyProgram? Program, string Message) Compile(WorldStudyRuntime.Entry entry, string name, string source) {
        var runtime = studies!;
        var resolvedPath = ResolveSourcePath(documentDirectory: runtime.DocumentDirectory, source: source);
        string sourceText;

        try {
            sourceText = File.ReadAllText(path: resolvedPath);
        } catch (IOException exception) {
            return (null, $"source '{resolvedPath}' could not be read: {exception.Message}");
        }

        StudyProgram program;

        try {
            program = runtime.Compiler.Compile(name: name, sourcePath: resolvedPath, sourceText: sourceText);
        } catch (StudyToolMissingException exception) {
            return (null, exception.Message);
        }

        entry.LastCompile = program;

        if (!program.IsError) {
            return (program, $"compiled ok, hash={program.SourceHash}");
        }

        var builder = new StringBuilder();

        foreach (var diagnostic in program.Diagnostics) {
            if (diagnostic.IsError) {
                if (builder.Length > 0) {
                    _ = builder.Append(value: " | ");
                }

                _ = builder.Append(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"{resolvedPath}:{diagnostic.Line}: {diagnostic.Message}"
                );
            }
        }

        // No swap follows a failed compile (empty bytecode), so the last good pipeline keeps rendering — never a
        // black pane on a typo.
        return (null, builder.ToString());
    }
    // study.reload's path: the row's CURRENT source (a live edit since boot is picked up), compiled and swapped
    // inline — pump thread.
    private (bool Ok, string Message) CompileAndSwap(string name) {
        if (FindStudy(
            name: name,
            server: server
        ) is not { } study) {
            return (false, $"'{name}' names no views.studies row");
        }

        var (entry, refusal) = EnsureEntry(name: name);

        if (entry is null) {
            return (false, refusal!);
        }

        var (program, message) = Compile(
            entry: entry,
            name: name,
            source: study.Source
        );

        if (program is null) {
            return (false, message);
        }

        entry.Node.Swap(program: program);

        return (true, message);
    }
    // The reload WorldStudyRuntime.PumpWatches runs on the pump thread once a watched source's change has gone quiet
    // — the same compile+swap study.reload does, echoed on stderr.
    private void TriggerReload(string name) {
        var (ok, message) = CompileAndSwap(name: name);

        Console.Error.WriteLine(value: (ok
            ? $"[study.watch: '{name}' reloaded — {message}]"
            : $"[study.watch: '{name}' reload failed — {message}]"
        ));
    }
    private CommandResult? RequireWindowed(string verb) {
        return (((renderProbe is null) || (studies is null))
            ? CommandResult.Error(output: $"[{verb}: requires a windowed boot]")
            : null
        );
    }
    private static string FormatClock(WorldStudyRuntime.Entry entry) =>
        string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"paused={entry.ClockPaused.ToString().ToLowerInvariant()} seconds={entry.ClockSeconds:0.###} scale={entry.ClockScale:0.###}"
        );
    private static string FormatCompile(WorldStudyRuntime.Entry entry) {
        if (entry.LastCompile is not { } compile) {
            return "uncompiled";
        }

        if (!compile.IsError) {
            return $"ok (hash={compile.SourceHash})";
        }

        foreach (var diagnostic in compile.Diagnostics) {
            if (diagnostic.IsError) {
                return string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"failed ({compile.SourcePath}:{diagnostic.Line}: {diagnostic.Message})"
                );
            }
        }

        return "failed";
    }
    private CommandResult DescribeStatus() {
        var rows = server.Definition.Views.Studies;
        var builder = new StringBuilder(value: $"[study.status: {rows.Count} row(s)");

        for (var index = 0; (index < rows.Count); index++) {
            var study = rows[index];
            var compileText = "uncompiled";
            var clockText = "paused=false seconds=0 scale=1";
            var changeCount = 0;
            var watching = false;

            if (
                (studies is { } runtime) &&
                runtime.TryGet(
                entry: out var entry,
                name: study.Name
            )
            ) {
                compileText = FormatCompile(entry: entry);
                clockText = FormatClock(entry: entry);
                changeCount = entry.SourceChangeCount;
                watching = (entry.WatchPath is not null);
            }

            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $" | {study.Name} source={study.Source} camera={study.Camera ?? "-"} timeScale={study.TimeScale:0.###} compile={compileText} watch={(watching
                    ? "on"
                    : "off")} changes={changeCount} clock=[{clockText}]"
            );
        }

        return new CommandResult(Output: builder.Append(value: ']').ToString());
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "study.load",
            description: "Upserts a views.studies row, compiles it, and swaps it in: study.load <name> <path> [camera]. <path> resolves relative to the document's own directory; <camera> is the authored camera feeding iCameraPos/iCameraTarget/iCameraUp/iCameraFov under PUCK_STUDY, omitted for the study's own iMouse orbit. The row buffers and applies at the tick boundary like every world.row.set-shaped mutation (a full-document revalidation rejects loudly: a slot already claiming the name, an invalid camera reference); windowed, the study is compiled from <path> right away — a new name registers as a live child, an existing one swaps — with the outcome (or every diagnostic as <file>:<line>: <message>) on stderr. Headless, only the row upserts.",
            routing: CommandRouting.Simulation,
            handler: (context, args) => {
                if (args.Count is < 2 or > 3) {
                    return CommandResult.Usage(
                        form: "<name> <path> [camera]",
                        verb: "study.load"
                    );
                }

                var name = args[0].ToString();
                var path = args[1].ToString();
                var camera = ((args.Count == 3) ? args[2].ToString() : null);
                var result = link.Submit(
                    mutation: new WorldMutation.UpsertViewStudy(
                        Principal: context.ActingPrincipal(),
                        Study: new WorldViewStudy(
                            Name: name,
                            Source: path,
                            Camera: camera,
                            TimeScale: 1f
                        )
                    ),
                    echoes: echoes,
                    verb: "study.load"
                );

                // Windowed: compile from the given path now (the row itself lands at the tick boundary) and make
                // the study a live child, so a layout slot naming it shows it as soon as the row applies.
                if (studies is not null) {
                    var (entry, refusal) = EnsureEntry(name: name);

                    if (entry is null) {
                        Console.Error.WriteLine(value: $"[study.load: '{name}' {refusal}]");
                    } else {
                        var (program, message) = Compile(
                            entry: entry,
                            name: name,
                            source: path
                        );

                        if (program is not null) {
                            entry.Node.Swap(program: program);
                        }

                        Console.Error.WriteLine(value: $"[study.load: '{name}' {message}]");
                    }
                }

                return result;
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "study.reload",
            description: "Recompiles a study from disk: study.reload [name] — every views.studies row when name is omitted. Immediate; a compile failure echoes every diagnostic as <file>:<line>: <message> and keeps the last good pipeline rendering — never a black pane on a typo.",
            routing: CommandRouting.Immediate,
            handler: (_, args) => {
                if (RequireWindowed(verb: "study.reload") is { } refusal) {
                    return refusal;
                }

                if (args.Count > 1) {
                    return CommandResult.Usage(
                        form: "[name]",
                        verb: "study.reload"
                    );
                }

                if (args.Count == 1) {
                    var name = args[0].ToString();
                    var (ok, message) = CompileAndSwap(name: name);

                    return (ok
                        ? new CommandResult(Output: $"[study.reload: '{name}' {message}]")
                        : CommandResult.Error(output: $"[study.reload: '{name}' {message}]")
                    );
                }

                var rows = server.Definition.Views.Studies;

                if (rows.Count == 0) {
                    return CommandResult.Error(output: "[study.reload: no authored views.studies rows]");
                }

                var builder = new StringBuilder(value: "[study.reload:");
                var anyFailed = false;

                for (var index = 0; (index < rows.Count); index++) {
                    var (ok, message) = CompileAndSwap(name: rows[index].Name);

                    anyFailed = (anyFailed || !ok);
                    builder.Append(value: $" {rows[index].Name}={(ok ? "ok" : "failed")}");
                }

                return (anyFailed
                    ? CommandResult.Error(output: builder.Append(value: ']').ToString())
                    : new CommandResult(Output: builder.Append(value: ']').ToString())
                );
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "study.watch",
            description: "Live source-change watch for one study: study.watch <name> on|off — no argument echoes the current on/off state. 'on' polls the row's source file's write stamp once per produced frame; once the stamp has moved and held still for 150 ms the frame recompiles and swaps the pipeline the same way study.reload does, echoing the outcome on stderr. 'off' stops polling. Immediate; presentation-only session state, never simulation state.",
            routing: CommandRouting.Immediate,
            handler: (_, args) => {
                if (RequireWindowed(verb: "study.watch") is { } refusal) {
                    return refusal;
                }

                if (args.Count is < 1 or > 2) {
                    return CommandResult.Usage(
                        form: "<name> [on|off]",
                        verb: "study.watch"
                    );
                }

                var name = args[0].ToString();

                if (FindStudy(
                    name: name,
                    server: server
                ) is not { } study) {
                    return CommandResult.Error(output: $"[study.watch: '{name}' names no views.studies row]");
                }

                var (registeredEntry, registrationRefusal) = EnsureEntry(name: name);

                if (registeredEntry is null) {
                    return CommandResult.Error(output: $"[study.watch: '{name}' {registrationRefusal}]");
                }

                if (args.Count == 1) {
                    return new CommandResult(Output: $"[study.watch: {name} {((registeredEntry.WatchPath is not null)
                        ? "on"
                        : "off")}]");
                }

                if (args.Is(
                    index: 1,
                    value: "on"
                )) {
                    if (registeredEntry.WatchPath is null) {
                        var resolved = ResolveSourcePath(documentDirectory: studies!.DocumentDirectory, source: study.Source);

                        if (!File.Exists(path: resolved)) {
                            return CommandResult.Error(output: $"[study.watch: '{name}' source does not exist: {resolved}]");
                        }

                        studies!.ReloadFromWatch ??= TriggerReload;
                        registeredEntry.Watch(path: resolved);
                    }

                    return new CommandResult(Output: $"[study.watch: {name} on]");
                }

                if (args.Is(
                    index: 1,
                    value: "off"
                )) {
                    registeredEntry.Unwatch();

                    return new CommandResult(Output: $"[study.watch: {name} off]");
                }

                return CommandResult.Error(output: $"[study.watch: unknown state '{args[1].ToString()}' — on|off]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "study.time",
            description: "The study clock, presentation only (never simulation state): study.time <name> pause|resume|set <seconds>|scale <x> — no sub-command echoes the current paused/seconds/scale triple. 'pause'/'resume' hold or release iTime's accumulation; 'set <seconds>' jumps the clock; 'scale <x>' multiplies the accumulation rate (0 = frozen, the same effect as pause but read back separately from the authored views.studies row's own timeScale). Immediate.",
            routing: CommandRouting.Immediate,
            handler: (_, args) => {
                if (RequireWindowed(verb: "study.time") is { } refusal) {
                    return refusal;
                }

                if (args.Count < 1) {
                    return CommandResult.Usage(
                        form: "<name> [pause|resume|set <seconds>|scale <x>]",
                        verb: "study.time"
                    );
                }

                var name = args[0].ToString();

                if (FindStudy(
                    name: name,
                    server: server
                ) is null) {
                    return CommandResult.Error(output: $"[study.time: '{name}' names no views.studies row]");
                }

                var (entry, registrationRefusal) = EnsureEntry(name: name);

                if (entry is null) {
                    return CommandResult.Error(output: $"[study.time: '{name}' {registrationRefusal}]");
                }

                if (args.Count == 1) {
                    return new CommandResult(Output: $"[study.time: {name} {FormatClock(entry: entry)}]");
                }

                if (args.Is(
                    index: 1,
                    value: "pause"
                )) {
                    entry.ClockPaused = true;

                    return new CommandResult(Output: $"[study.time: {name} {FormatClock(entry: entry)}]");
                }

                if (args.Is(
                    index: 1,
                    value: "resume"
                )) {
                    entry.ClockPaused = false;

                    return new CommandResult(Output: $"[study.time: {name} {FormatClock(entry: entry)}]");
                }

                if (args.Is(
                    index: 1,
                    value: "set"
                )) {
                    if (
                        (args.Count != 3) ||
                        !args.TryFloat(
                        index: 2,
                        value: out var seconds
                    ) ||
                        !float.IsFinite(f: seconds)
                    ) {
                        return CommandResult.Usage(
                            form: "<name> set <seconds>",
                            verb: "study.time"
                        );
                    }

                    entry.ClockSeconds = seconds;

                    return new CommandResult(Output: $"[study.time: {name} {FormatClock(entry: entry)}]");
                }

                if (args.Is(
                    index: 1,
                    value: "scale"
                )) {
                    if (
                        (args.Count != 3) ||
                        !args.TryFloat(
                        index: 2,
                        value: out var scale
                    ) ||
                        !float.IsFinite(f: scale) ||
                        (scale < 0f)
                    ) {
                        return CommandResult.Usage(
                            form: "<name> scale <x>",
                            verb: "study.time"
                        );
                    }

                    entry.ClockScale = scale;

                    return new CommandResult(Output: $"[study.time: {name} {FormatClock(entry: entry)}]");
                }

                return CommandResult.Error(output: $"[study.time: unknown sub-command '{args[1].ToString()}' — pause|resume|set <seconds>|scale <x>]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "study.status",
            description: "Per-study read-back: study.status — for every authored views.studies row, its source, camera, timeScale, last compile outcome (with the first diagnostic when it failed), watch state, the count of source-change events the watcher has seen, and clock. A query (always echoes).",
            routing: CommandRouting.Immediate,
            handler: (_, args) => {
                if (RequireWindowed(verb: "study.status") is { } refusal) {
                    return refusal;
                }

                return ((CommandResult.RequireNoArguments(
                    args: args,
                    verb: "study.status"
                ) is { } usageRefusal)
                    ? usageRefusal
                    : DescribeStatus()
                );
            }
        );
    }
}
