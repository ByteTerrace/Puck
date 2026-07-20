using System.Globalization;
using System.Text;
using System.Text.Json;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The LOOK verb surface — the appearance peer of the kit verbs. <c>world.look.set</c>/<c>.remove</c>/<c>.assign</c>/
/// <c>.tune</c> mold the <see cref="WorldSection.Looks"/> section through the same <see cref="WorldMutation"/> messages
/// the editor drives; <c>world.population.spawn</c> is the read-modify-write sugar over the population defaults'
/// <see cref="WorldSpawnPolicy"/>; <c>world.looks</c> is the Immediate census (one line per look row — name, resolved
/// source, active count — mirroring <c>world.population</c>). A SEPARATE module from <see cref="WorldMutationCommandModule"/>
/// so neither class crosses its analyzer ceilings.
/// </summary>
/// <remarks>The spawn policy sits under <c>world.population.*</c> (not a <c>world.spawns.*</c> family) so the verb family
/// matches the <see cref="WorldSection.Population"/> grant section it mutates. The write verbs route
/// <see cref="CommandRouting.Simulation"/> (the stdin barrier serializes a following <c>world.looks</c> read-after-write);
/// the server prints the loud accept/reject line when the buffered edit applies.</remarks>
internal sealed class WorldLookCommandModule(WorldServer server, WorldPopulation population, IServerLink link) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            name: "world.look.set",
            description: "Upserts a LOOK row (whole-row, keyed by name) from one inline-JSON WorldLook {name, source ($type catalog|creation), scale, motion {gaitAmplitude, replayFrames, secondsPerFrame}}: world.look.set <look-json>. Applies LIVE within the probed render envelope; a full-document revalidation rejects loudly.",
            handler: (context, args) => {
                var raw = RawArgument(context: context, args: in args);

                if (string.IsNullOrWhiteSpace(value: raw)) {
                    return Usage(verb: "world.look.set", form: "<look-json>");
                }

                try {
                    if (JsonSerializer.Deserialize(json: raw, jsonTypeInfo: WorldJsonContext.Default.WorldLook) is not { } look) {
                        return new CommandResult(Output: "[world.look.set: the JSON parsed to null]") { IsError = true };
                    }

                    return Submit(mutation: new WorldMutation.UpsertLook(Principal: WorldPrincipal.Console, Look: look));
                } catch (JsonException exception) {
                    return new CommandResult(Output: $"[world.look.set: {exception.Message.ReplaceLineEndings(replacementText: " ")}]") { IsError = true };
                }
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            name: "world.look.remove",
            description: "Removes a look row by name: world.look.remove <name>. Rejected loudly by full-document revalidation while the look assignment table still names it.",
            handler: (_, args) => {
                if (args.Count != 1) {
                    return Usage(verb: "world.look.remove", form: "<name>");
                }

                return Submit(mutation: new WorldMutation.RemoveLook(Principal: WorldPrincipal.Console, Name: args[0].ToString()));
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            name: "world.look.assign",
            description: "Sets the look→entity assignment policy (the same primitive as world.kit.assign): world.look.assign hash | table <look> [<look>…]. hash keeps the R1 low-discrepancy mapping (a distinct stream from the kit table); table cycles the named looks by index.",
            handler: (_, args) => {
                if (args.Count == 0) {
                    return Usage(verb: "world.look.assign", form: "hash | table <look> [<look>…]");
                }

                if (args.Is(index: 0, value: WorldRowAssignment.HashPolicy)) {
                    return Submit(mutation: new WorldMutation.SetLookAssignment(Principal: WorldPrincipal.Console, Assignment: WorldRowAssignment.Hash));
                }

                if (args.Is(index: 0, value: WorldRowAssignment.TablePolicy)) {
                    if (args.Count < 2) {
                        return new CommandResult(Output: "[world.look.assign: the table policy needs at least one look name]") { IsError = true };
                    }

                    var table = new string[args.Count - 1];

                    for (var index = 1; (index < args.Count); index++) {
                        table[index - 1] = args[index].ToString();
                    }

                    return Submit(mutation: new WorldMutation.SetLookAssignment(Principal: WorldPrincipal.Console, Assignment: new WorldRowAssignment(Policy: WorldRowAssignment.TablePolicy, Table: table)));
                }

                return new CommandResult(Output: $"[world.look.assign: unknown policy '{args[0].ToString()}' — hash | table]") { IsError = true };
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            name: "world.look.tune",
            description: "Console sugar: read-modify-write ONE look field into a whole-row upsert: world.look.tune <name> <field> <value>. Fields (camelCase): scale, gaitAmplitude, secondsPerFrame (numbers); replayFrames (true|false); catalogIndex (an integer 0..127, or - to clear the pin to the index-derived catalog pick — both switch the source to catalog).",
            handler: (_, args) => {
                if (args.Count != 3) {
                    return Usage(verb: "world.look.tune", form: "<name> <field> <value>");
                }

                var name = args[0].ToString();

                if (FindLook(name: name) is not { } look) {
                    return new CommandResult(Output: $"[world.look.tune: no look row named '{name}']") { IsError = true };
                }

                if (WithLookField(look: look, field: args[1].ToString(), value: args[2].ToString(), error: out var error) is not { } tuned) {
                    return new CommandResult(Output: $"[world.look.tune: {error}]") { IsError = true };
                }

                return Submit(mutation: new WorldMutation.UpsertLook(Principal: WorldPrincipal.Console, Look: tuned));
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            name: "world.population.spawn",
            description: "Sets how simulated peers are distributed at spawn (RMW on the population defaults' spawn policy — LIVE for future activations, standing bodies unmoved): world.population.spawn phyllotaxis <radius> | points <jitter> <id> [<id>…]. The <jitter> leads deliberately so the open-ended spawn-point id list stays unambiguous.",
            handler: (_, args) => {
                if (args.Count == 0) {
                    return Usage(verb: "world.population.spawn", form: "phyllotaxis <radius> | points <jitter> <id> [<id>…]");
                }

                if (ParseSpawnPolicy(args: in args, error: out var policyError) is not { } policy) {
                    return new CommandResult(Output: $"[world.population.spawn: {policyError}]") { IsError = true };
                }

                var current = server.Definition.Population;

                return Submit(mutation: new WorldMutation.SetPopulationDefaults(Principal: WorldPrincipal.Console, Population: (current with { SpawnPolicy = policy })));
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            name: "world.looks",
            description: "Reports the LOOK census (Immediate; the stdin barrier makes it read the settled state after any pending mutation): one line per look row — name, resolved source, active entity count. A world with no looks section prints the single implicit 'catalog (index-derived)' row over the whole population.",
            handler: (_, _) => new CommandResult(Output: DescribeLooks())
        );
    }

    // The world.looks census: one row per look, mirroring world.population's per-kit echo.
    private string DescribeLooks() {
        var rows = population.LookRows;
        var counts = population.ActiveLookCounts();
        var builder = new StringBuilder(value: "[world.looks:");

        for (var index = 0; (index < rows.Count); index++) {
            _ = builder.Append(value: $" {rows[index].Name}={DescribeSource(source: rows[index].Source)}:{counts[index]}");
        }

        return builder.Append(value: ']').ToString();
    }

    private static string DescribeSource(WorldLookSource source) => source switch {
        WorldLookSource.Catalog { Index: { } catalogIndex } => $"catalog(index {catalogIndex})",
        WorldLookSource.Catalog => "catalog(index-derived)",
        WorldLookSource.Creation creation => $"creation({creation.CreationId})",
        _ => "unknown",
    };

    // Parse a spawn policy from the verb args: `phyllotaxis <radius>` or `points <jitter> <id> [<id>…]`.
    private static WorldSpawnPolicy? ParseSpawnPolicy(in WireArgs args, out string error) {
        error = string.Empty;

        if (args.Is(index: 0, value: "phyllotaxis")) {
            if ((args.Count != 2) || !float.TryParse(s: args[1], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var radius)) {
                error = "phyllotaxis needs one <radius> number";

                return null;
            }

            return new WorldSpawnPolicy.Phyllotaxis(Radius: radius);
        }

        if (args.Is(index: 0, value: "points")) {
            if ((args.Count < 3) || !float.TryParse(s: args[1], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var jitter)) {
                error = "points needs a <jitter> number then at least one spawn-point id";

                return null;
            }

            var points = new string[args.Count - 2];

            for (var index = 2; (index < args.Count); index++) {
                points[index - 2] = args[index].ToString();
            }

            return new WorldSpawnPolicy.PointCycle(Points: points, Jitter: jitter);
        }

        error = $"unknown distribution '{args[0].ToString()}' — phyllotaxis | points";

        return null;
    }

    // RMW ONE look field (camelCase names matching the JSON), or null with a reason on an unknown field / bad value.
    private static WorldLook? WithLookField(WorldLook look, string field, string value, out string error) {
        error = string.Empty;

        switch (field) {
            case "scale":
                if (!TryFloat(value: value, name: "scale", result: out var scale, error: out error)) {
                    return null;
                }

                return (look with { Scale = scale });
            case "gaitAmplitude":
                if (!TryFloat(value: value, name: "gaitAmplitude", result: out var gait, error: out error)) {
                    return null;
                }

                return (look with { Motion = (look.Motion with { GaitAmplitude = gait }) });
            case "secondsPerFrame":
                if (!TryFloat(value: value, name: "secondsPerFrame", result: out var seconds, error: out error)) {
                    return null;
                }

                return (look with { Motion = (look.Motion with { SecondsPerFrame = seconds }) });
            case "replayFrames":
                bool? flag = value.ToLowerInvariant() switch {
                    "true" => true,
                    "false" => false,
                    _ => null,
                };

                if (flag is not { } resolved) {
                    error = $"bad replayFrames '{value}' — true|false";

                    return null;
                }

                return (look with { Motion = (look.Motion with { ReplayFrames = resolved }) });
            case "catalogIndex":
                // The P3 clear token: '-' clears the pin to the index-derived catalog pick; an integer pins that rig.
                if (value == "-") {
                    return (look with { Source = new WorldLookSource.Catalog(Index: null) });
                }

                if (!int.TryParse(s: value, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var catalogIndex)) {
                    error = $"bad catalogIndex '{value}' — an integer 0..{(WorldPopulation.MaxPopulation - 1)}, or - to clear the pin";

                    return null;
                }

                return (look with { Source = new WorldLookSource.Catalog(Index: catalogIndex) });
            default:
                error = $"unknown field '{field}' — scale|gaitAmplitude|secondsPerFrame|replayFrames|catalogIndex";

                return null;
        }
    }

    private static bool TryFloat(string value, string name, out float result, out string error) {
        if (!float.TryParse(s: value, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out result)) {
            error = $"bad {name} '{value}' — a number";

            return false;
        }

        error = string.Empty;

        return true;
    }

    private WorldLook? FindLook(string name) {
        foreach (var look in server.Definition.Looks) {
            if (string.Equals(a: look.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return look;
            }
        }

        return null;
    }

    private CommandResult Submit(WorldMutation mutation) {
        link.SubmitWorldMutation(mutation: mutation);

        return CommandResult.None;
    }

    private static CommandResult Usage(string verb, string form) => new(Output: $"[{verb}: expected {form}]") {
        IsError = true,
    };

    // The raw argument text after the verb token — reconstructed from the submitted line so inline-JSON quotes survive
    // the console tokenizer (the WorldMutationCommandModule.Row idiom).
    private static string RawArgument(CommandContext context, in WireArgs args) {
        if (context.Text is { } text) {
            var span = text.AsSpan().TrimStart();
            var separator = span.IndexOfAny(value0: ' ', value1: '\t');

            return ((separator < 0) ? string.Empty : span[(separator + 1)..].Trim().ToString());
        }

        return args.Tail(0);
    }
}
