using System.Globalization;
using Puck.Abstractions;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// <c>world.pins</c>: the session's operator pins as one replayable console script, so a restart carries them. A pin is
/// what the operator holds for the session rather than what the document says: the presentation levers
/// (<see cref="WorldSessionLeverSink.Applied"/>: an exposure pin, a render tier), the composition overrides
/// (<see cref="WorldCompositionState"/>: a forced layout, a named or free camera), the state and row values an operator
/// line wrote (<see cref="WorldPinLedger"/>: a held clock, a scrubbed time) and where each joined seat's body stands. A
/// reload keeps every one of them by itself; a new process starts from the document, so the script is how they cross
/// a restart: <c>world.pins save</c> before it, <c>world.pins restore</c> after.
/// </summary>
/// <remarks>Each pin is read from its one live home and written as the line that sets it, so nothing here keeps a list
/// of pin kinds: a new lever, override or pinning verb is carried as soon as its home holds it. A seat's ephemeral body
/// registers (a noclip toggle a rule keeps) have no operator write and are not carried.</remarks>
/// <param name="link">The link levers are submitted through.</param>
/// <param name="levers">The applied presentation levers.</param>
/// <param name="composition">The live composition overrides.</param>
/// <param name="server">The boot world's authority, whose seats' bodies are read.</param>
/// <param name="roster">The joined seats.</param>
/// <param name="source">The boot world's source, which names the default pins file.</param>
/// <param name="ledger">The operator lines that wrote state and rows, or <see langword="null"/> for none.</param>
internal sealed class WorldPinsCommandModule(IServerLink link, WorldSessionLeverSink levers, WorldCompositionState composition, WorldServer server, PlayerRoster roster,
    WorldDefinitionSource source, WorldPinLedger? ledger = null) : ICommandModule {
    private const string Verb = "world.pins";
    private const string LeverVerb = "world.lever";
    private static readonly CommandForms Forms = CommandForms.Of(verb: Verb, "[save|restore [<path>]]");
    private static readonly CommandForms LeverForms = CommandForms.Of(verb: LeverVerb, "<section> <name> <a> <b> <c> <d> [<seat>] [<view>]");

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            name: Verb,
            description: $"Reads, saves or restores the session's operator pins: {Forms.Spelling}. With no argument it answers the pins as console lines that set them again, one a line: each applied presentation lever (world.lever), the composition overrides (view.override layout, camera, or the free camera's orbit), the state cells and document rows an operator line wrote (the latest line for each), and each joined seat's body pose (body.pose). 'save' writes those lines to <path>, by default a file under the user's Puck directory named for the boot world's source, so a World restarted on the same source finds it; 'restore' runs a saved file's lines after this one, in order, as the caller. A reload keeps every pin by itself; the script carries them across a restart. A seat's ephemeral body registers (a noclip toggle a rule keeps) have no operator write and are not carried.",
            forms: Forms,
            handler: (context, args) => {
                if (args.Count == 0) { return new CommandResult(Output: string.Join(separator: '\n', values: Capture())); }
                if (args.Count > 2) { return Forms.Refuse(); }

                var path = ((args.Count == 2) ? Path.GetFullPath(path: args[1].ToString()) : DefaultPath());

                switch (args[0].ToString()) {
                    case "save": {
                        var lines = Capture();

                        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
                        Puck.Confinement.AtomicFile.WriteAllBytesDurably(path, System.Text.Encoding.UTF8.GetBytes(s: string.Join(separator: '\n', values: lines) + "\n"));
                        return new CommandResult(Output: $"[{Verb}: saved {lines.Count} line{((lines.Count == 1) ? string.Empty : "s")} to {PuckPaths.Normalize(path: path)}]");
                    }
                    case "restore": {
                        if (!File.Exists(path: path)) { return CommandResult.Error(output: $"[{Verb}: no pins saved at {PuckPaths.Normalize(path: path)}]"); }
                        if (context.TextSession is not { } session) { return CommandResult.Error(output: $"[{Verb}: restore runs its lines through a console session, and this line has none]"); }

                        var lines = File.ReadAllLines(path: path).Where(predicate: static line => ((line.Trim().Length != 0) && !line.TrimStart().StartsWith(value: '#'))).ToArray();

                        foreach (var line in lines) { session.Enqueue(line: line); }
                        return new CommandResult(Output: $"[{Verb}: restoring {lines.Length} line{((lines.Length == 1) ? string.Empty : "s")} from {PuckPaths.Normalize(path: path)}]");
                    }
                    default:
                        return Forms.Refuse();
                }
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            name: LeverVerb,
            description: $"Submits one presentation lever by its knob name and value lanes: {LeverForms.Spelling}. The form world.pins writes for an applied lever, so a saved session sets its presentation again; each knob's own verb (world.exposure pin, world.shadows, ...) is the form to type. <section> is the document section the knob folds into, <seat> a 0-based local seat or -1 for none, <view> a view name. The knob's setter refuses a value it cannot hold, by name on stderr.",
            forms: LeverForms,
            handler: (context, args) => {
                if ((args.Count is < 6 or > 8) || !Enum.TryParse<WorldSection>(value: args[0].ToString(), ignoreCase: true, result: out var section)) { return LeverForms.Refuse(); }

                var lanes = new double[4];

                for (var lane = 0; (lane < lanes.Length); lane++) {
                    if (!double.TryParse(s: args[(2 + lane)].ToString(), provider: CultureInfo.InvariantCulture, result: out lanes[lane]) || !double.IsFinite(d: lanes[lane])) {
                        return LeverForms.Refuse();
                    }
                }

                var seat = WorldSessionLever.NoSeat;

                if ((args.Count >= 7) && !int.TryParse(s: args[6].ToString(), provider: CultureInfo.InvariantCulture, result: out seat)) { return LeverForms.Refuse(); }
                link.SubmitSessionLever(
                    lever: new WorldSessionLever(A: lanes[0], B: lanes[1], C: lanes[2], D: lanes[3], Name: args[1].ToString(), Section: section, Seat: seat,
                        View: ((args.Count == 8) ? args[7].ToString() : null)),
                    principal: context.Principal
                );
                return new CommandResult(Output: $"[{LeverVerb}: {args[1].ToString()} submitted]");
            },
            routing: CommandRouting.Immediate
        );
    }

    // The pins file a World on this source saves to and restores from unless a line names another.
    private string DefaultPath() {
        var sourcePath = source.SourcePath;

        return Path.Combine(path1: PuckUserDirectory.Resolve(name: "pins"),
            path2: $"{Path.GetFileNameWithoutExtension(path: sourcePath)}-{ContentDigest.HexOfText(text: PuckPaths.Key(path: sourcePath))[..16]}.pins");
    }
    // Every pin, as the line that sets it again: the levers first, so a restored view renders as pinned from its first
    // frame, then the composition, the operator's state and row lines, and the seats' bodies.
    private List<string> Capture() {
        var culture = CultureInfo.InvariantCulture;
        var lines = new List<string>();

        foreach (var lever in levers.Applied) {
            lines.Add(item: string.Create(provider: culture,
                handler: $"{LeverVerb} {lever.Section} {lever.Name} {lever.A:R} {lever.B:R} {lever.C:R} {lever.D:R} {lever.Seat}{((lever.View is { } view) ? $" {view}" : string.Empty)}"));
        }
        if (composition.ActiveLayout is { } layout) { lines.Add(item: $"view.override layout {layout}"); }
        if (composition.OperatorCamera is { } camera) {
            var pivot = camera.Pivot();

            lines.Add(item: string.Create(provider: culture,
                handler: $"view.override camera orbit {pivot.X:R} {pivot.Y:R} {pivot.Z:R} {camera.Distance:R} {float.RadiansToDegrees(radians: camera.YawRadians):R} {float.RadiansToDegrees(radians: camera.PitchRadians):R} {float.RadiansToDegrees(radians: camera.FieldOfViewRadians):R}"));
        } else if (composition.SelectedCamera is { } selected) {
            lines.Add(item: $"view.override camera {selected}");
        }
        if (ledger is not null) { lines.AddRange(collection: ledger.Lines); }
        for (var seat = 0; (seat < server.Population.LocalSeatCount); seat++) {
            if (!roster.IsJoined(slot: seat) || (server.Body(index: seat) is not { } body)) { continue; }

            var position = body.Position;
            var (yaw, pitch, roll) = VectorFunctions.EulerOf(attitude: body.Orientation);

            lines.Add(item: string.Create(provider: culture,
                handler: $"body.pose {position.X:R} {position.Y:R} {position.Z:R} {float.RadiansToDegrees(radians: yaw):R} {float.RadiansToDegrees(radians: pitch):R} {float.RadiansToDegrees(radians: roll):R} {seat}"));
        }
        return lines;
    }
}
