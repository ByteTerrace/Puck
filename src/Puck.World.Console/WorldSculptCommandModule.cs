using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Puck.Commands;
using Puck.World.Authoring.Sculpting;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The in-engine twin of <c>puck creation sculpt</c>: <c>creation.sculpts</c> lists the registry,
/// <c>creation.sculpt &lt;name&gt;</c> applies a sculpt's <see cref="SculptPatch"/> against the live definition
/// through the ordinary document mutation door. Every distinct row the patch touched (<see cref="SculptPatch.TouchedRows"/>
/// — several member-level edits to one row become one upsert) is composed through the same section table
/// <c>world.row.set</c>/<c>world.row.remove</c> compose through (<see cref="WorldRowCommandModule.TryComposeEditedRow"/>/
/// <see cref="WorldRowCommandModule.TryComposeRemove"/>), stamped with the principal the issuing ingress stamped
/// (<c>context.ActingPrincipal()</c> — never a principal this module constructs, and never a re-dispatched text line,
/// which would stamp the shared injection sink's Console identity over whoever actually issued the verb), and
/// submitted over the link as ordinary <see cref="WorldMutation"/> rows — so the whole-document revalidation, the
/// per-section <c>Mutate</c> grant check, the tick-boundary apply, the replay tape's recorded mutation entries, and
/// the deferred <c>[creation.sculpt: …]</c> verdict echo every other row mutation crosses govern a sculpt's edits
/// identically. Composition is all-or-nothing: a row that fails to compose, or that already has an edit buffered in
/// this tick window (the shared <see cref="WorldRowStepWindowGuard"/>), refuses the whole sculpt by name before
/// anything is submitted. There is deliberately no <c>--write</c> flag: writing to disk is <c>world.save</c>.
/// </summary>
public sealed class WorldSculptCommandModule(IWorldConsoleAuthority authority, IServerLink link, WorldDeferredVerbEchoes echoes, WorldRowStepWindowGuard stepGuard) : ICommandModule {
    // The handful of sections a sculpt's SculptPatch (JsonNode-only, no Schema reference — see CreationBuilder's
    // remarks) addresses by their raw document member path, which occasionally differs from the console's own
    // dotted row-verb vocabulary (world.row.set's own path table, Puck.World.WorldRowCommandModule). Identity for
    // every path not listed here.
    private static readonly IReadOnlyDictionary<string, string> s_pathToVerb = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
        ["prototypes"] = "creations",
        ["state.world"] = "state",
        ["looks.rows"] = "looks",
    };

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "creation.sculpts",
            description: "Lists the registered creation sculpts by name and description: creation.sculpts.",
            handler: (context, args) => {
                if (CommandResult.RequireNoArguments(
                    args: args,
                    verb: "creation.sculpts"
                ) is { } refusal) {
                    return refusal;
                }

                if (CreationSculptRegistry.All.Count == 0) {
                    return new CommandResult(Output: "[creation.sculpts: none registered]");
                }

                var echo = CommandEcho.Open(verb: "creation.sculpts");

                foreach (var sculpt in CreationSculptRegistry.All) {
                    echo = echo.Field(
                        key: sculpt.Name,
                        value: sculpt.Description
                    );
                }

                return new CommandResult(Output: echo.Close());
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "creation.sculpt",
            description: "Applies a registered sculpt's patch to the LIVE world document: creation.sculpt <name>. Echoes the patch's plan (every op's path and its inserted/replaced/set/removed/unset verdict against a working copy) and then one line per distinct row it composed and submitted — the same section table, Mutate grant check, whole-document revalidation and tick-boundary apply every world.row.set/.remove crosses, under the issuing principal; the apply verdict arrives deferred as [creation.sculpt: …] like any buffered mutation's. All-or-nothing: a row that fails to compose, or that already has an edit buffered this tick window, refuses the whole sculpt by name and submits nothing. No --write: writing to disk stays world.save.",
            handler: (context, args) => {
                if (args.Count != 1) {
                    return CommandResult.Usage(
                        form: "<name>",
                        verb: "creation.sculpt"
                    );
                }

                var name = args[0].ToString();

                if (!CreationSculptRegistry.TryGet(
                    name: name,
                    sculpt: out var sculpt
                )) {
                    var known = string.Join(separator: ", ", values: CreationSculptRegistry.All.Select(selector: static s => s.Name));

                    return CommandResult.Error(output: $"[creation.sculpt: unknown sculpt '{name}' — {(known.Length > 0 ? known : "none registered")}]");
                }

                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "creation.sculpt"
                )) {
                    return error;
                }

                var principal = context.ActingPrincipal();
                var document = JsonSerializer.SerializeToNode(
                    inputType: typeof(WorldDefinition),
                    options: WorldJsonContext.Default.Options,
                    value: server.Definition
                )!.AsObject();
                var working = document.DeepClone().AsObject();
                IReadOnlyList<SculptPatchResult> results;

                try {
                    results = sculpt.Sculpt(context: new SculptContext(Document: document)).Apply(document: working);
                } catch (Exception exception) when (exception is InvalidOperationException or FormatException or ArgumentException or JsonException) {
                    return CommandResult.Error(output: $"[creation.sculpt: {name}: patch fault — {exception.Message.ReplaceLineEndings(replacementText: " ")}]");
                }

                var plan = CommandEcho.Open(verb: "creation.sculpt").Field(
                    key: "planned",
                    value: name
                );

                foreach (var result in results) {
                    plan = plan.Field(
                        key: result.Path,
                        value: result.Verdict
                    );
                }

                // Compose every row first (all-or-nothing): a refusal here names the row and submits nothing.
                var touched = SculptPatch.TouchedRows(results: results);
                var composed = new List<(string Identity, WorldMutation Mutation)>(capacity: touched.Count);
                var window = server.NextInputTick;

                foreach (var row in touched) {
                    var verb = s_pathToVerb.GetValueOrDefault(
                        key: row.Section,
                        defaultValue: row.Section
                    );
                    var identity = WorldRowCommandModule.RowIdentityOf(
                        key: row.KeyValue,
                        path: verb
                    );

                    if (stepGuard.IsClaimed(
                        rowIdentity: identity,
                        window: window
                    )) {
                        return CommandResult.Error(output: $"[creation.sculpt: {name}: row '{identity}' already has an edit buffered this tick — fence with world.wait; nothing submitted]");
                    }

                    WorldMutation? mutation;
                    string reason;

                    if (row.Removed) {
                        if (row.KeyValue is null) {
                            return CommandResult.Error(output: $"[creation.sculpt: {name}: {verb}: keyless (set only) — a sculpt cannot remove a whole section; nothing submitted]");
                        }

                        if (!WorldRowCommandModule.TryComposeRemove(
                            error: out reason,
                            key: row.KeyValue,
                            mutation: out mutation,
                            path: verb,
                            principal: principal,
                            server: server
                        )) {
                            return CommandResult.Error(output: $"[creation.sculpt: {name}: {identity}: {reason}; nothing submitted]");
                        }
                    } else {
                        var value = ResolveRowValue(
                            document: working,
                            keyField: row.KeyField,
                            keyValue: row.KeyValue,
                            section: row.Section
                        );

                        if (value is null) {
                            return CommandResult.Error(output: $"[creation.sculpt: {name}: {identity}: the patched document carries no such row; nothing submitted]");
                        }

                        if (!WorldRowCommandModule.TryComposeEditedRow(
                            error: out reason,
                            mutation: out mutation,
                            path: verb,
                            principal: principal,
                            row: value,
                            server: server
                        )) {
                            return CommandResult.Error(output: $"[creation.sculpt: {name}: {identity}: {reason}; nothing submitted]");
                        }
                    }

                    composed.Add(item: (identity, mutation!));
                }

                var output = new StringBuilder(value: plan.Close());

                foreach (var (identity, mutation) in composed) {
                    _ = link.Submit(
                        echoes: echoes,
                        mutation: mutation,
                        verb: "creation.sculpt"
                    );
                    stepGuard.Claim(rowIdentity: identity);
                    _ = output.Append(value: '\n').Append(value: $"[creation.sculpt: {name}: {identity} {(IsRemove(mutation: mutation) ? "remove" : "set")} submitted as {principal.Describe()}]");
                }

                return new CommandResult(Output: output.ToString());
            },
            routing: CommandRouting.Simulation
        );
    }

    private static bool IsRemove(WorldMutation mutation) => mutation.GetType().Name.StartsWith(
        comparisonType: StringComparison.Ordinal,
        value: "Remove"
    );

    // A keyless section's row is the section value itself; a keyed section's row is the array element whose
    // KeyField equals KeyValue — resolved against the patched tree so every op that touched this row (however many)
    // is folded into the one value resubmitted.
    private static JsonNode? ResolveRowValue(JsonObject document, string section, string? keyField, string? keyValue) {
        JsonNode? current = document;

        foreach (var segment in section.Split(separator: '.')) {
            if (current is not JsonObject obj) {
                return null;
            }

            current = obj[segment];
        }

        if (keyField is null) {
            return current;
        }

        if (current is not JsonArray array) {
            return null;
        }

        foreach (var element in array) {
            if (
                (element is JsonObject row) &&
                (row[keyField] is { } keyNode) &&
                string.Equals(
                    a: (keyNode.ToJsonString().Trim(trimChar: '"')),
                    b: keyValue,
                    comparisonType: StringComparison.Ordinal
                )
            ) {
                return element;
            }
        }

        return null;
    }
}
