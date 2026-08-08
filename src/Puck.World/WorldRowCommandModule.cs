using System.Globalization;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The GENERAL document-row verb pair — <c>world.row.set</c>/<c>world.row.remove</c> — the ONE door every
/// document section is authored through: a dotted DOCUMENT MEMBER PATH (the document's own
/// camelCase JSON names, e.g. <c>kits</c>, <c>hud.panels</c>, <c>views.seatRig</c>) selects which
/// <see cref="WorldMutation"/> a row's inline JSON composes into, closing over the same section table
/// <c>puck schema</c> documents for payload shapes. An unknown path is refused BY NAME, enumerating every admissible
/// sibling — never a silent no-op. This is the type-ERASED generalization of
/// <see cref="WorldCommandDefinition.Row{T}"/>: one section entry closes over a <see cref="JsonTypeInfo{T}"/> and a
/// mutation factory, so the verb pair itself never grows a case per section.
/// </summary>
/// <remarks>
/// <para>ONE grammar exception: <c>properties.names</c> carries a BARE-NAME token, never JSON — the registry section
/// is a name and a toggle, so <c>world.row.set properties.names &lt;name&gt;</c> / <c>world.row.remove properties.names
/// &lt;name&gt;</c> both submit <see cref="WorldMutation.SetProperty"/>, distinguished only by its own
/// <see cref="WorldMutation.SetProperty.Remove"/> flag.</para>
/// <para>This module implements NO schema validation and issues NO schema-pointer refusals — a parse failure echoes
/// <see cref="WorldJsonPayload"/>'s own message inline and submits nothing; every semantic check (unknown id, capacity,
/// cross-row reference) still runs where it always has, at whole-document revalidation when the buffered mutation
/// applies at the tick boundary. <c>puck schema</c> is DOCUMENTATION for a payload's shape, never a gate this verb
/// consults.</para>
/// <para>Every mutation here carries the identity its ingress door stamped (see <see cref="WorldPrincipalMapping"/>) —
/// Console for a typed line — and that identity is not a formality: <see cref="WorldServer"/>'s per-section
/// <see cref="WorldCapability.Mutate"/> grant check applies to EVERY submitted mutation regardless of which module
/// produced it, so revoking a principal's grant over a section refuses that principal's writes here exactly like any
/// other's.</para>
/// <para><c>world.assign kits|looks r1 | cycle &lt;name&gt;…</c> lives here too — ONE row→entity
/// assignment-sequence primitive over both tables, the target deciding only which <see cref="WorldMutation"/> kind
/// wraps the built <see cref="WorldRowAssignment"/> and what additive offset the r1 sequence takes.</para>
/// <para><c>world.kits</c> is a plain census read-back, not a row verb — the kits section's only listing.</para>
/// </remarks>
internal sealed class WorldRowCommandModule(WorldServer server, IServerLink link) : ICommandModule {
    private const string PropertiesNamesPath = "properties.names";

    private readonly IReadOnlyDictionary<string, RowSection> m_sections = BuildSections(server: server);

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.row.set",
            description: "Upserts ANY document row or section by its dotted MEMBER PATH — the document's own camelCase JSON names (see puck schema for payload shapes): world.row.set <path> <json>. Keyed sections (kits, cameras, screens, speakers, placements, creations, tunes, patches, links, looks, addons, bindingOverlays, state, rules, hud.panels, views.layouts, groups.kinds, interactions.interactions) upsert one row addressed by its own key; keyless sections (motion, render, audio, authoring, collision, host, inputHold, hud.defaults, spawnPoints, views.seatRig, playerDefaults.seatLook) replace the whole row. ONE grammar exception: properties.names takes a BARE NAME token, not JSON — world.row.set properties.names <name> declares it idempotently. An unknown path is refused by name, naming every admissible sibling. Buffers and applies at the tick boundary like every WorldMutation; a full-document revalidation rejects loudly. This verb performs NO schema validation of its own — a JSON parse failure echoes inline and submits nothing; every semantic check still runs at apply.",
            handler: (context, args) => HandleSet(context: context, args: args),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.row.remove",
            description: "Removes ONE row from a KEYED document section by its dotted MEMBER PATH and key: world.row.remove <path> <key>. Keyed sections are the same set world.row.set upserts into (kits, cameras, screens [key is the integer index], speakers, placements, creations, tunes, patches, links, looks, addons, bindingOverlays, state, rules, hud.panels, views.layouts, groups.kinds, interactions.interactions), plus properties.names (BARE NAME token — world.row.remove properties.names <name>). A KEYLESS path (motion, render, audio, authoring, collision, host, inputHold, hud.defaults, spawnPoints, views.seatRig, playerDefaults.seatLook) has no remove — it is refused by name. An unknown path is refused by name, naming every admissible sibling. Buffers and applies at the tick boundary; rejected loudly if no row carries that key.",
            handler: (context, args) => HandleRemove(context: context, args: args),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.kits",
            description: "Reports the kit census (Immediate): one segment per declared kit row — name, body motion program, the motion model arm it compiles (grounded|vehicle|swim), and that arm's key movement scalars. The kits section's own read-back (world.row.set kits/world.row.remove kits has no listing of its own otherwise).",
            handler: (_, args) => ((args.Count != 0)
                ? CommandResult.Error(output: "[world.kits: no arguments — reports the kit census]")
                : new CommandResult(Output: DescribeKits()))
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.assign",
            description: "Sets a row→entity assignment sequence, keyed by which table it targets: world.assign kits|looks r1 | cycle <name> [<name>…]. r1 selects from every row of that table; cycle walks the named row view by index.",
            handler: (context, args) => HandleAssign(context: context, args: args),
            routing: CommandRouting.Simulation
        );
    }

    private CommandResult HandleAssign(CommandContext context, WireArgs args) {
        if (args.Count < 2) {
            return Usage(verb: "world.assign", form: "kits|looks r1 | cycle <name> [<name>…]");
        }

        var principal = context.ActingPrincipal();
        var target = args[0].ToString();

        return target switch {
            "kits" => BuildAssignment(args: args, r1Offset: 1, toMutation: static (principal, assignment) => new WorldMutation.SetKitAssignment(Principal: principal, Assignment: assignment), principal: principal, verb: "world.assign kits"),
            "looks" => BuildAssignment(args: args, r1Offset: 129, toMutation: static (principal, assignment) => new WorldMutation.SetLookAssignment(Principal: principal, Assignment: assignment), principal: principal, verb: "world.assign looks"),
            _ => CommandResult.Error(output: $"[world.assign: unknown target '{target}' — kits|looks]"),
        };
    }

    // The one r1/cycle assignment-sequence builder both kits and looks reduce to — they differ only in which
    // WorldMutation kind wraps the built WorldRowAssignment and r1's own additive offset (the two sequences must not
    // land on the same index for the same tick, so each table keeps its own authored offset).
    private CommandResult BuildAssignment(in WireArgs args, int r1Offset, Func<WorldPrincipal, WorldRowAssignment, WorldMutation> toMutation, WorldPrincipal principal, string verb) {
        if (args.Is(index: 1, value: WorldSequence.R1)) {
            return Submit(mutation: toMutation(principal, new WorldRowAssignment(Sequence: new WorldSequence(Name: WorldSequence.R1, Offset: r1Offset, Step: 0f), Rows: [])));
        }

        if (args.Is(index: 1, value: "cycle")) {
            if (args.Count < 3) {
                return CommandResult.Error(output: $"[{verb}: cycle needs at least one name]");
            }

            return Submit(mutation: toMutation(principal, new WorldRowAssignment(Sequence: new WorldSequence(Name: WorldSequence.Index, Offset: 0, Step: 0f), Rows: TailTokens(args: args, start: 2))));
        }

        return CommandResult.Error(output: $"[{verb}: unknown sequence '{args[1].ToString()}' — r1 | cycle]");
    }

    // Materializes the trailing tokens from <paramref name="start"/> onward as an array — the ONE place world.assign
    // needs each row name as its own string, rather than the joined free-text tail RawAfter gives.
    private static string[] TailTokens(in WireArgs args, int start) {
        var count = args.Count;

        if (start >= count) {
            return [];
        }

        var tokens = new string[(count - start)];

        for (var index = start; (index < count); index++) {
            tokens[(index - start)] = args[index].ToString();
        }

        return tokens;
    }
    private CommandResult HandleSet(CommandContext context, WireArgs args) {
        if (args.Count < 1) {
            return Usage(verb: "world.row.set", form: "<path> <json>");
        }

        var path = args[0].ToString();
        var principal = context.ActingPrincipal();

        if (string.Equals(a: path, b: PropertiesNamesPath, comparisonType: StringComparison.Ordinal)) {
            if (args.Count != 2) {
                return Usage(verb: "world.row.set", form: $"{PropertiesNamesPath} <name>");
            }

            return Submit(mutation: new WorldMutation.SetProperty(Principal: principal, Name: args[1].ToString(), Remove: false));
        }

        if (!m_sections.TryGetValue(key: path, value: out var section)) {
            return UnknownPath(verb: "world.row.set", path: path);
        }

        var raw = WorldCommandArguments.RawAfter(context: context, args: in args, tokens: 2);

        if (string.IsNullOrWhiteSpace(value: raw)) {
            return Usage(verb: "world.row.set", form: $"{path} <json>");
        }

        var outcome = section.Upsert(principal, raw);

        return ((outcome.Error is { } error) ? CommandResult.Error(output: $"[world.row.set: {path}: {error}]") : Submit(mutation: outcome.Mutation!));
    }
    private CommandResult HandleRemove(CommandContext context, WireArgs args) {
        if (args.Count != 2) {
            return Usage(verb: "world.row.remove", form: "<path> <key>");
        }

        var path = args[0].ToString();
        var key = args[1].ToString();
        var principal = context.ActingPrincipal();

        if (string.Equals(a: path, b: PropertiesNamesPath, comparisonType: StringComparison.Ordinal)) {
            return Submit(mutation: new WorldMutation.SetProperty(Principal: principal, Name: key, Remove: true));
        }

        if (!m_sections.TryGetValue(key: path, value: out var section)) {
            return UnknownPath(verb: "world.row.remove", path: path);
        }

        if (section.Remove is not { } remove) {
            return CommandResult.Error(output: $"[world.row.remove: {path}: keyless (set only) — no remove]");
        }

        var outcome = remove(principal, key);

        return ((outcome.Error is { } error) ? CommandResult.Error(output: $"[world.row.remove: {path}: {error}]") : Submit(mutation: outcome.Mutation!));
    }

    // Every admissible path, sections plus the one bare-name exception, sorted for a stable, greppable refusal.
    private CommandResult UnknownPath(string verb, string path) {
        var admissible = string.Join(separator: ", ", values: m_sections.Keys.Append(element: PropertiesNamesPath).OrderBy(keySelector: static name => name, comparer: StringComparer.Ordinal));

        return CommandResult.Error(output: $"[{verb}: unknown path '{path}' — {admissible}]");
    }
    private CommandResult Submit(WorldMutation mutation) {
        link.SubmitWorldMutation(mutation: mutation);

        return CommandResult.None;
    }
    private static CommandResult Usage(string verb, string form) {
        return CommandResult.Error(output: $"[{verb}: expected {form}]");
    }

    // world.kits: name, program, arm, and the arm's key scalars — the census this section never had.
    private string DescribeKits() {
        var kits = server.Definition.Kits;

        if (kits.Count == 0) {
            return "[world.kits: none declared]";
        }

        var builder = new StringBuilder(value: "[world.kits:");

        for (var index = 0; (index < kits.Count); index++) {
            var kit = kits[index];

            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"{((index == 0) ? " " : " | ")}{kit.Name} program={kit.BodyMotionProgram} {DescribeMotionArm(motion: kit.Motion)}");
        }

        return builder.Append(value: ']').ToString();
    }
    private static string DescribeMotionArm(WorldMotionModel? motion) => motion switch {
        WorldMotionModel.Grounded grounded => string.Create(provider: CultureInfo.InvariantCulture, handler: $"arm=grounded moveSpeed={grounded.MoveSpeed:0.###} turnSpeed={grounded.TurnSpeed:0.###} riseGravity={grounded.RiseGravity:0.###} fallGravity={grounded.FallGravity:0.###} maxFallSpeed={grounded.MaxFallSpeed:0.###}"),
        WorldMotionModel.Vehicle vehicle => string.Create(provider: CultureInfo.InvariantCulture, handler: $"arm=vehicle topSpeed={vehicle.TopSpeed:0.###} accel={vehicle.Accel:0.###} grip={vehicle.Grip:0.###} boostMultiplier={vehicle.BoostMultiplier:0.###}"),
        WorldMotionModel.Swim swim => string.Create(provider: CultureInfo.InvariantCulture, handler: $"arm=swim thrustSpeed={swim.ThrustSpeed:0.###} buoyancy={swim.Buoyancy:0.###} maxRiseSpeed={swim.MaxRiseSpeed:0.###} maxSinkSpeed={swim.MaxSinkSpeed:0.###}"),
        _ => "arm=(none)",
    };

    // The section table — the one thing that legitimately stays as data (CLAUDE.md: a table over these rows is
    // vocabulary, not logic to duplicate per section). Each entry closes over a JsonTypeInfo<T> and a
    // Func<WorldPrincipal, T, WorldMutation> factory; Remove is null for a keyless (set-only) section.
    private static IReadOnlyDictionary<string, RowSection> BuildSections(WorldServer server) => new Dictionary<string, RowSection>(comparer: StringComparer.Ordinal) {
        // Keyed sections: set + remove.
        ["kits"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldKit, toMutation: static (principal, kit) => new WorldMutation.UpsertKit(Principal: principal, Kit: kit)), Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveKit(Principal: principal, Name: name))),
        ["cameras"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldCamera, toMutation: static (principal, camera) => new WorldMutation.UpsertCamera(Principal: principal, Camera: camera)), Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveCamera(Principal: principal, Name: name))),
        ["screens"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldScreen, toMutation: static (principal, screen) => new WorldMutation.UpsertScreen(Principal: principal, Screen: screen)), Remove: RemoveByIndex(remove: static (principal, index) => new WorldMutation.RemoveScreen(Principal: principal, Index: index))),
        ["speakers"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldSpeaker, toMutation: static (principal, speaker) => new WorldMutation.UpsertSpeaker(Principal: principal, Speaker: speaker)), Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveSpeaker(Principal: principal, Name: name))),
        ["placements"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldPlacement, toMutation: static (principal, placement) => new WorldMutation.UpsertPlacement(Principal: principal, Placement: placement)), Remove: RemoveByName(remove: static (principal, id) => new WorldMutation.RemovePlacement(Principal: principal, Id: id))),
        ["creations"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldCreation, toMutation: static (principal, creation) => new WorldMutation.UpsertCreation(Principal: principal, Creation: creation)), Remove: RemoveByName(remove: static (principal, id) => new WorldMutation.RemoveCreation(Principal: principal, Id: id))),
        ["tunes"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldTune, toMutation: static (principal, tune) => new WorldMutation.UpsertTune(Principal: principal, Tune: tune)), Remove: RemoveByName(remove: static (principal, id) => new WorldMutation.RemoveTune(Principal: principal, Id: id))),
        ["patches"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldPatch, toMutation: static (principal, patch) => new WorldMutation.UpsertPatch(Principal: principal, Patch: patch)), Remove: RemoveByName(remove: static (principal, id) => new WorldMutation.RemovePatch(Principal: principal, Id: id))),
        ["links"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldScreenLink, toMutation: static (principal, screenLink) => new WorldMutation.UpsertScreenLink(Principal: principal, Link: screenLink)), Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveScreenLink(Principal: principal, Name: name))),
        ["looks"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldLook, toMutation: static (principal, look) => new WorldMutation.UpsertLook(Principal: principal, Look: look)), Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveLook(Principal: principal, Name: name))),
        ["addons"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldAddonRow, toMutation: static (principal, addon) => new WorldMutation.UpsertAddon(Principal: principal, Addon: addon)), Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveAddon(Principal: principal, Name: name))),
        ["bindingOverlays"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldBindingOverlay, toMutation: static (principal, overlay) => new WorldMutation.UpsertBindingOverlay(Principal: principal, Overlay: overlay)), Remove: RemoveByName(remove: static (principal, id) => new WorldMutation.RemoveBindingOverlay(Principal: principal, Id: id))),
        ["state"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldStateRow, toMutation: static (principal, row) => new WorldMutation.UpsertStateRow(Principal: principal, Row: row)), Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveStateRow(Principal: principal, Name: name))),
        ["rules"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldRule, toMutation: static (principal, rule) => new WorldMutation.UpsertWorldRule(Principal: principal, Rule: rule)), Remove: RemoveByCellName(remove: static (principal, name) => new WorldMutation.RemoveWorldRule(Principal: principal, Name: name))),
        ["hud.panels"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldHudPanel, toMutation: static (principal, panel) => new WorldMutation.UpsertHudPanel(Principal: principal, Panel: panel)), Remove: RemoveByName(remove: static (principal, id) => new WorldMutation.RemoveHudPanel(Principal: principal, Id: id))),
        ["views.layouts"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldViewLayout, toMutation: static (principal, layout) => new WorldMutation.UpsertViewLayout(Principal: principal, Layout: layout)), Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveViewLayout(Principal: principal, Name: name))),
        ["groups.kinds"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldGroupKind, toMutation: static (principal, kind) => new WorldMutation.UpsertGroupKind(Principal: principal, Kind: kind)), Remove: RemoveByName(remove: static (principal, name) => new WorldMutation.RemoveGroupKind(Principal: principal, Name: name))),
        ["interactions.interactions"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldInteraction, toMutation: static (principal, interaction) => new WorldMutation.UpsertInteraction(Principal: principal, Interaction: interaction)), Remove: RemoveByCellName(remove: static (principal, name) => new WorldMutation.RemoveInteraction(Principal: principal, Name: name))),

        // Keyless sections: set only.
        ["motion"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldMotionDefaults, toMutation: static (principal, motion) => new WorldMutation.SetMotion(Principal: principal, Motion: motion)), Remove: null),
        ["render"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldRenderDefaults, toMutation: static (principal, render) => new WorldMutation.SetRenderDefaults(Principal: principal, Render: render)), Remove: null),
        ["audio"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldAudioDefaults, toMutation: static (principal, audio) => new WorldMutation.SetAudioDefaults(Principal: principal, Audio: audio)), Remove: null),
        ["authoring"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldAuthoringDefaults, toMutation: static (principal, authoring) => new WorldMutation.SetAuthoringDefaults(Principal: principal, Authoring: authoring)), Remove: null),
        ["collision"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldCollision, toMutation: static (principal, collision) => new WorldMutation.SetCollision(Principal: principal, Collision: collision)), Remove: null),
        ["host"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldHostDefaults, toMutation: static (principal, host) => new WorldMutation.SetHostDefaults(Principal: principal, Host: host)), Remove: null),
        ["inputHold"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldInputHoldSettings, toMutation: static (principal, settings) => new WorldMutation.SetInputHold(Principal: principal, Settings: settings)), Remove: null),
        ["hud.defaults"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldHudDefaults, toMutation: static (principal, defaults) => new WorldMutation.SetHudDefaults(Principal: principal, Defaults: defaults)), Remove: null),
        ["spawnPoints"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldSpawnPointArray, toMutation: static (principal, spawns) => new WorldMutation.SetSpawns(Principal: principal, Spawns: spawns)), Remove: null),
        // The two views sub-rows RMW the CURRENT Views row (SetViewDefaults carries the whole section) — the same
        // read-modify-write world.row.set views.seatRig/world.view.look performed before folding into this table.
        ["views.seatRig"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldCameraRig, toMutation: (principal, rig) => new WorldMutation.SetViewDefaults(Principal: principal, Views: (server.Definition.Views with { SeatRig = rig }))), Remove: null),
        ["playerDefaults.seatLook"] = new RowSection(Upsert: Upsert(info: WorldJsonContext.Default.WorldSeatLook, toMutation: (principal, look) => new WorldMutation.SetPlayerDefaults(Principal: principal, Defaults: (server.Definition.PlayerDefaults with { SeatLook = look }))), Remove: null),
    };

    // Type-erased upsert factory: parses <paramref name="info"/>'s shape from the raw JSON tail and hands the parsed
    // value to <paramref name="toMutation"/> — the ONE generic seam the whole section table closes over.
    private static Func<WorldPrincipal, string, RowOutcome> Upsert<T>(JsonTypeInfo<T> info, Func<WorldPrincipal, T, WorldMutation> toMutation) {
        return (principal, raw) => {
            if (!WorldJsonPayload.TryParse(json: raw, info: info, value: out var value, error: out var error)) {
                return RowOutcome.Fail(error: error);
            }

            return RowOutcome.Ok(mutation: toMutation(arg1: principal, arg2: value));
        };
    }
    private static Func<WorldPrincipal, string, RowOutcome> RemoveByName(Func<WorldPrincipal, string, WorldMutation> remove) {
        return (principal, key) => RowOutcome.Ok(mutation: remove(arg1: principal, arg2: key));
    }

    // screens is the one section keyed by an integer index rather than a string name.
    private static Func<WorldPrincipal, string, RowOutcome> RemoveByIndex(Func<WorldPrincipal, int, WorldMutation> remove) {
        return (principal, key) => {
            if (!int.TryParse(s: key, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var index)) {
                return RowOutcome.Fail(error: $"bad index '{key}' — an integer");
            }

            return RowOutcome.Ok(mutation: remove(arg1: principal, arg2: index));
        };
    }

    // rules and interactions key their Remove mutation by the validated WorldCellName type rather than a plain
    // string.
    private static Func<WorldPrincipal, string, RowOutcome> RemoveByCellName(Func<WorldPrincipal, WorldCellName, WorldMutation> remove) {
        return (principal, key) => {
            if (!WorldCellName.TryParse(candidate: key, name: out var name, reason: out var reason)) {
                return RowOutcome.Fail(error: $"'{key}' {reason}");
            }

            return RowOutcome.Ok(mutation: remove(arg1: principal, arg2: name));
        };
    }

    // One entry of the section table: a path's upsert (always present) and remove (null for a keyless section).
    private sealed record RowSection(Func<WorldPrincipal, string, RowOutcome> Upsert, Func<WorldPrincipal, string, RowOutcome>? Remove);

    // A parsed-and-built mutation, or the reason building one failed — the ONE outcome shape every section entry
    // returns, so the two verb handlers stay generic over which section answered.
    private readonly record struct RowOutcome(WorldMutation? Mutation, string? Error) {
        public static RowOutcome Ok(WorldMutation mutation) => new(Mutation: mutation, Error: null);
        public static RowOutcome Fail(string error) => new(Mutation: null, Error: error);
    }
}
