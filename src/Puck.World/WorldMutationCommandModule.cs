using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Puck.Commands;
using Puck.Launcher;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The dev reflection of the world-mutation protocol — the console surface an agent (or a deterministic test) molds a
/// running world through over stdin, reusing the SAME <see cref="WorldMutation"/> messages the editor drives. Every
/// row-valued verb takes ONE inline-JSON argument in the exact wire shape of the document section (no
/// second grammar, parsed through <see cref="WorldJsonContext"/>); a parse error echoes inline and submits
/// nothing. Every mutation verb routes <see cref="CommandRouting.Simulation"/>, so it buffers on the server and the
/// stdin barrier serializes a following <c>world.status</c> read-after-write for free; the server's own loud accept/
/// reject line is printed when the buffered edit applies at the tick boundary. <c>world.status</c> and <c>world.save</c>
/// are Immediate reads of the server's live definition and journal. This is a SEPARATE module from
/// <see cref="WorldCommandModule"/> to keep that class under its analyzer ceilings.
/// </summary>
/// <remarks>JSON arguments must be a single whitespace-free token (compact JSON): the console tokenizer that identifies
/// the verb would otherwise split the object, and the raw line the handler parses is reconstructed from the submitted
/// text. The verbs read that raw line, so quotes survive.</remarks>
internal sealed class WorldMutationCommandModule(WorldServer server, IServerLink link, WorldDefinitionSource definitionSource, WorldRenderSettings renderSettings, WorldScreenBinder screenBinder, Client.WorldAudioDirector audioDirector, PresentPacingControl pacing) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return Row(
            name: "world.kit.set",
            description: "Upserts a locomotion kit row (whole-row, keyed by name) from one inline-JSON WorldKit: world.kit.set <kit-json>. Buffers and applies at the tick boundary; a full-document revalidation rejects loudly.",
            info: WorldJsonContext.Default.WorldKit,
            toMutation: static kit => new WorldMutation.UpsertKit(Principal: WorldPrincipal.Console, Kit: kit)
        );
        yield return Simulation(
            name: "world.kit.remove",
            description: "Removes a kit row by name: world.kit.remove <name>. Rejected loudly if the composed document then names no seat kit or leaves an assignment table dangling.",
            handler: (context, args) => {
                if (args.Length != 1) {
                    return Usage(verb: "world.kit.remove", form: "<name>");
                }

                return Submit(mutation: new WorldMutation.RemoveKit(Principal: WorldPrincipal.Console, Name: args[0]));
            }
        );
        yield return Simulation(
            name: "world.kit.default",
            description: "Sets the default seat kit (by name): world.kit.default <name>. Rejected if the name matches no kit row.",
            handler: (context, args) => {
                if (args.Length != 1) {
                    return Usage(verb: "world.kit.default", form: "<name>");
                }

                return Submit(mutation: new WorldMutation.SetDefaultSeatKit(Principal: WorldPrincipal.Console, Name: args[0]));
            }
        );
        yield return Simulation(
            name: "world.kit.assign",
            description: "Sets the kit→entity assignment policy: world.kit.assign hash | table <kit> [<kit>…]. hash keeps the R1 low-discrepancy mapping; table cycles the named kits by index.",
            handler: (context, args) => {
                if (args.Length == 0) {
                    return Usage(verb: "world.kit.assign", form: "hash | table <kit> [<kit>…]");
                }

                switch (args[0].ToLowerInvariant()) {
                    case WorldRowAssignment.HashPolicy:
                        return Submit(mutation: new WorldMutation.SetKitAssignment(Principal: WorldPrincipal.Console, Assignment: WorldRowAssignment.Hash));
                    case WorldRowAssignment.TablePolicy:
                        if (args.Length < 2) {
                            return new CommandResult(Output: "[world.kit.assign: the table policy needs at least one kit name]") { IsError = true };
                        }

                        return Submit(mutation: new WorldMutation.SetKitAssignment(Principal: WorldPrincipal.Console, Assignment: new WorldRowAssignment(Policy: WorldRowAssignment.TablePolicy, Table: args[1..])));
                    default:
                        return new CommandResult(Output: $"[world.kit.assign: unknown policy '{args[0]}' — hash | table]") { IsError = true };
                }
            }
        );
        yield return Simulation(
            name: "world.kit.tune",
            description: "Console sugar: read-modify-write ONE MotionTuning field of a kit row into a whole-row upsert (the protocol stays coarse): world.kit.tune <name> <field> <value>. Field names are camelCase (moveSpeed, turnSpeed, groundY, jumpSpeed, riseGravity, fallGravity, maxFallSpeed, jumpCutMultiplier, coyoteTime, jumpBufferTime).",
            handler: (context, args) => {
                if (args.Length != 3) {
                    return Usage(verb: "world.kit.tune", form: "<name> <field> <value>");
                }

                if (!float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
                    return new CommandResult(Output: $"[world.kit.tune: bad value '{args[2]}' — a number]") { IsError = true };
                }

                if (FindKit(name: args[0]) is not { } kit) {
                    return new CommandResult(Output: $"[world.kit.tune: no kit row named '{args[0]}']") { IsError = true };
                }

                if (WithTuningField(tuning: kit.Tuning, field: args[1], value: value) is not { } tuned) {
                    return new CommandResult(Output: $"[world.kit.tune: unknown field '{args[1]}' — camelCase MotionTuning fields]") { IsError = true };
                }

                return Submit(mutation: new WorldMutation.UpsertKit(Principal: WorldPrincipal.Console, Kit: (kit with { Tuning = tuned })));
            }
        );
        yield return Row(
            name: "world.screen.set",
            description: "Upserts a diegetic screen (whole-row, keyed by index) from one inline-JSON WorldScreen: world.screen.set <screen-json>. Slab geometry rides the program rebuild; a source change reconciles through the runtime binder where possible.",
            info: WorldJsonContext.Default.WorldScreen,
            toMutation: static screen => new WorldMutation.UpsertScreen(Principal: WorldPrincipal.Console, Screen: screen)
        );
        yield return Simulation(
            name: "world.screen.remove",
            description: "Removes a screen by index: world.screen.remove <index>.",
            handler: (context, args) => {
                if ((args.Length != 1) || !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) {
                    return Usage(verb: "world.screen.remove", form: "<index>");
                }

                return Submit(mutation: new WorldMutation.RemoveScreen(Principal: WorldPrincipal.Console, Index: index));
            }
        );
        yield return Row(
            name: "world.link.set",
            description: "Upserts a cable-link row (whole-row, keyed by name) from one inline-JSON WorldScreenLink: world.link.set <link-json> — {\"name\":\"arcade-pair\",\"screens\":[0,1]}. The durable twin of screen.link; the binder reconciles the declared links live (establishing or reporting the group dormant). Rejected loudly for an undeclared screen, a screen in two links, or fewer than two screens.",
            info: WorldJsonContext.Default.WorldScreenLink,
            toMutation: static link => new WorldMutation.UpsertScreenLink(Principal: WorldPrincipal.Console, Link: link)
        );
        yield return Simulation(
            name: "world.link.remove",
            description: "Removes a cable-link row by name: world.link.remove <name>. Rejected if no row declares that name.",
            handler: (context, args) => {
                if (args.Length != 1) {
                    return Usage(verb: "world.link.remove", form: "<name>");
                }

                return Submit(mutation: new WorldMutation.RemoveScreenLink(Principal: WorldPrincipal.Console, Name: args[0]));
            }
        );
        yield return Row(
            name: "world.camera.set",
            description: "Upserts a placeable camera (whole-row, keyed by name) from one inline-JSON WorldCamera (name, anchor entity|entityLeaf|placement|group|null, offset, rig $type chase|firstPerson|orbit|lookAt|dolly): world.camera.set <camera-json>. Applies LIVE: a pose/aim/FOV/rig edit re-wires the running offscreen view in place, a dimension change recreates it, and every jumbotron filming it updates without a restart.",
            info: WorldJsonContext.Default.WorldCamera,
            toMutation: static camera => new WorldMutation.UpsertCamera(Principal: WorldPrincipal.Console, Camera: camera)
        );
        yield return Simulation(
            name: "world.camera.remove",
            description: "Removes a camera by name: world.camera.remove <name>. Rejected if a View screen still references it; a runtime screen.view of it unbinds and its offscreen render is released live.",
            handler: (context, args) => {
                if (args.Length != 1) {
                    return Usage(verb: "world.camera.remove", form: "<name>");
                }

                return Submit(mutation: new WorldMutation.RemoveCamera(Principal: WorldPrincipal.Console, Name: args[0]));
            }
        );
        yield return Row(
            name: "world.scene.set",
            description: "Replaces the static scene (ground albedos + shape rows) from one inline-JSON WorldScene: world.scene.set <scene-json>. Geometry rebuilds live within the probed render envelope; an over-envelope scene is rejected loudly.",
            info: WorldJsonContext.Default.WorldScene,
            toMutation: static scene => new WorldMutation.SetScene(Principal: WorldPrincipal.Console, Scene: scene)
        );
        yield return Row(
            name: "world.scene.row.set",
            description: "Upserts one static-scene shape row (whole-row, keyed by id) from one inline-JSON WorldSceneRow ($type boulder|slab): world.scene.row.set <row-json>. The editor's per-act scene grain; the typed editor.place/editor.move verbs are its shaped twins.",
            info: WorldJsonContext.Default.WorldSceneRow,
            toMutation: static row => new WorldMutation.UpsertSceneRow(Principal: WorldPrincipal.Console, Row: row)
        );
        yield return Simulation(
            name: "world.scene.row.remove",
            description: "Removes a static-scene shape row by id: world.scene.row.remove <id>. Rejected if no row declares that id.",
            handler: (context, args) => {
                if (args.Length != 1) {
                    return Usage(verb: "world.scene.row.remove", form: "<id>");
                }

                return Submit(mutation: new WorldMutation.RemoveSceneRow(Principal: WorldPrincipal.Console, Id: args[0]));
            }
        );
        yield return Row(
            name: "world.spawns.set",
            description: "Replaces the seat spawn-point list from one inline-JSON WorldSpawnPoint array: world.spawns.set <spawns-json-array>. Order maps slots; takes effect at the next seat activation (no live teleport).",
            info: WorldJsonContext.Default.WorldSpawnPointArray,
            toMutation: static spawns => new WorldMutation.SetSpawns(Principal: WorldPrincipal.Console, Spawns: spawns)
        );
        yield return Row(
            name: "world.motion.set",
            description: "Replaces the world's motion defaults from one inline-JSON WorldMotionDefaults (moveSpeed, turnSpeed, groundY): world.motion.set <json>. Moves the ground plane and the profileless stand-in speeds; jump feel, gravity and the velocity-response table are per-kit — use world.kit.tune. Any other field is rejected by name.",
            info: WorldJsonContext.Default.WorldMotionDefaults,
            toMutation: static motion => new WorldMutation.SetMotion(Principal: WorldPrincipal.Console, Motion: motion)
        );
        yield return Row(
            name: "world.wander.set",
            description: "Replaces the wander tuning from one inline-JSON WanderTuning: world.wander.set <json>. Recompiles the crowd's producer feel (running phase preserved).",
            info: WorldJsonContext.Default.WanderTuning,
            toMutation: static wander => new WorldMutation.SetWander(Principal: WorldPrincipal.Console, Wander: wander)
        );
        yield return Simulation(
            name: "world.population.defaults",
            description: "Sets the census defaults (document-only; the live census stays the world.population verb): world.population.defaults <local> <network>.",
            handler: (context, args) => {
                if ((args.Length != 2) ||
                    !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var local) ||
                    !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var network)) {
                    return Usage(verb: "world.population.defaults", form: "<local> <network>");
                }

                // Preserve the document's live peer-source default (the world.population idle|wander verb owns it) and the
                // spawn policy (world.population.spawn owns it) — this verb only sets the local/network census figures.
                return Submit(mutation: new WorldMutation.SetPopulationDefaults(Principal: WorldPrincipal.Console, Population: (server.Definition.Population with { LocalPlayers = local, NetworkPlayers = network })));
            }
        );
        yield return Row(
            name: "world.render.defaults",
            description: "Replaces the render-lever defaults and quality-preset table from one inline-JSON WorldRenderDefaults: world.render.defaults <json>. Document-only (live render levers stay world.shadows/.ao/.render-scale).",
            info: WorldJsonContext.Default.WorldRenderDefaults,
            toMutation: static render => new WorldMutation.SetRenderDefaults(Principal: WorldPrincipal.Console, Render: render)
        );
        yield return Row(
            name: "world.addon.set",
            description: "Upserts a data-side addon descriptor (whole-row, keyed by name) from one inline-JSON WorldAddonRow: world.addon.set <json>.",
            info: WorldJsonContext.Default.WorldAddonRow,
            toMutation: static addon => new WorldMutation.UpsertAddon(Principal: WorldPrincipal.Console, Addon: addon)
        );
        yield return Simulation(
            name: "world.addon.remove",
            description: "Removes an addon descriptor by name: world.addon.remove <name>.",
            handler: (context, args) => {
                if (args.Length != 1) {
                    return Usage(verb: "world.addon.remove", form: "<name>");
                }

                return Submit(mutation: new WorldMutation.RemoveAddon(Principal: WorldPrincipal.Console, Name: args[0]));
            }
        );
        yield return Row(
            name: "world.bindings.set",
            description: "Upserts a per-world binding overlay (whole-row, keyed by id) from one inline-JSON WorldBindingOverlay: world.bindings.set <overlay-json>. Layered over the engine default beneath every seat's profile bindings; rejected loudly if the composed mapping then fails to compile. Recomposes every seat on apply.",
            info: WorldJsonContext.Default.WorldBindingOverlay,
            toMutation: static overlay => new WorldMutation.UpsertBindingOverlay(Principal: WorldPrincipal.Console, Overlay: overlay)
        );
        yield return Simulation(
            name: "world.bindings.remove",
            description: "Removes a per-world binding overlay by id: world.bindings.remove <id>. Rejected if no overlay declares that id. Recomposes every seat on apply.",
            handler: (context, args) => {
                if (args.Length != 1) {
                    return Usage(verb: "world.bindings.remove", form: "<id>");
                }

                return Submit(mutation: new WorldMutation.RemoveBindingOverlay(Principal: WorldPrincipal.Console, Id: args[0]));
            }
        );
        yield return Row(
            name: "world.creation.set",
            description: "Upserts a creation ASSET row (whole-row, keyed by id) from one inline-JSON WorldCreation {id, document, hash}: world.creation.set <json>. The compose boundary re-canonicalizes the document and REJECTS a hash the pipeline did not itself compute; editor.import <path> is the file-reading twin.",
            info: WorldJsonContext.Default.WorldCreation,
            toMutation: static creation => new WorldMutation.UpsertCreation(Principal: WorldPrincipal.Console, Creation: creation)
        );
        yield return Simulation(
            name: "world.creation.remove",
            description: "Removes a creation asset row by id: world.creation.remove <id>. Rejected loudly while live placements still reference it (no cascade — remove them first).",
            handler: (context, args) => {
                if (args.Length != 1) {
                    return Usage(verb: "world.creation.remove", form: "<id>");
                }

                return Submit(mutation: new WorldMutation.RemoveCreation(Principal: WorldPrincipal.Console, Id: args[0]));
            }
        );
        yield return Row(
            name: "world.placement.set",
            description: "Upserts a placement INSTANCE row (whole-row, keyed by id) from one inline-JSON WorldPlacement {id, creationId, position, yawDegrees, scale, repeat?, mirror?, inhabit?, faceSources?}: world.placement.set <json>. Must name an existing creation row; capacity-checked against the probed render envelope; a framed creation replays its timeline; an inhabited row is a live population body (repeat/mirror reject on animated OR inhabited rows).",
            info: WorldJsonContext.Default.WorldPlacement,
            toMutation: static placement => new WorldMutation.UpsertPlacement(Principal: WorldPrincipal.Console, Placement: placement)
        );
        yield return Simulation(
            name: "world.placement.remove",
            description: "Removes a placement row by id: world.placement.remove <id>.",
            handler: (context, args) => {
                if (args.Length != 1) {
                    return Usage(verb: "world.placement.remove", form: "<id>");
                }

                return Submit(mutation: new WorldMutation.RemovePlacement(Principal: WorldPrincipal.Console, Id: args[0]));
            }
        );
        yield return Row(
            name: "world.authoring.set",
            description: "Replaces the whole editor/authoring policy row from one inline-JSON WorldAuthoringDefaults: world.authoring.set <json>. The candidate radius/cap, sole-editor layout split, and drag-preview deadline apply LIVE (next tick, no restart); the authoring headroom and max-repeat-per-segment feed the frozen render-envelope probe and apply at the NEXT boot (the accept echo narrates the split).",
            info: WorldJsonContext.Default.WorldAuthoringDefaults,
            toMutation: static authoring => new WorldMutation.SetAuthoringDefaults(Principal: WorldPrincipal.Console, Authoring: authoring)
        );
        yield return Simulation(
            name: "world.load",
            description: "Loads a world file and submits it as a whole-document swap (validate → swap → derived rebuild → journal RESET): world.load <path>. A missing/invalid file echoes a loud line and swaps nothing.",
            handler: (context, args) => {
                var path = RawArgument(context: context, args: args);

                if (string.IsNullOrWhiteSpace(value: path)) {
                    return Usage(verb: "world.load", form: "<path>");
                }

                if (!WorldDefinitionLoader.TryLoadFile(path: Path.GetFullPath(path: path), definition: out var loaded, reason: out var reason)) {
                    return new CommandResult(Output: $"[world.load: {reason}]") {
                        IsError = true,
                    };
                }

                link.SubmitDefinition(definition: loaded, principal: WorldPrincipal.Console);

                return CommandResult.None;
            }
        );
        yield return Simulation(
            name: "world.undo",
            description: "Undoes the last n applied mutations (default 1) by replaying the journal minus its tail through the same apply path: world.undo [n]. The journal IS the edit history; replay IS the undo engine.",
            handler: (context, args) => {
                var count = 1;

                if ((args.Length >= 1) && (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || (count < 1))) {
                    return new CommandResult(Output: $"[world.undo: bad count '{args[0]}' — a positive integer]") { IsError = true };
                }

                link.SubmitUndo(count: count, principal: WorldPrincipal.Console);

                return CommandResult.None;
            }
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.save",
            description: "Writes a SESSION SNAPSHOT of the live world to a file in canonical form (stable member order, invariant numbers, LF newlines, one trailing newline) and compacts the journal (the saved definition becomes the new base, dirty → 0): world.save [path]. The snapshot is the live definition (mutations included) with session state folded into its document homes — the live render levers into Render, the live census + peer-source default into Population, and runtime screen inserts into the screens' Machine sources. No argument writes back to the loaded --world file; booted from the baked default with no path is an error.",
            handler: (_, args) => {
                var target = ((args.Length >= 1) ? string.Join(separator: ' ', values: args) : definitionSource.SourcePath);

                if (string.IsNullOrWhiteSpace(value: target)) {
                    return new CommandResult(Output: "[world.save: booted from the baked default — provide a path: world.save <path>]") {
                        IsError = true,
                    };
                }

                try {
                    var snapshot = WorldSessionCapture.Capture(definition: server.Definition, render: renderSettings, population: server.Population, binder: screenBinder, audio: audioDirector, pacing: pacing);
                    var bytes = WorldDefinitionSerialization.Save(definition: snapshot, path: target);

                    server.Compact();

                    return new CommandResult(Output: $"[world.save: {target} ({bytes} bytes)]");
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)) {
                    return new CommandResult(Output: $"[world.save: could not write {target} ({exception.Message})]") {
                        IsError = true,
                    };
                }
            }
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.status",
            description: "Reports the live world definition and journal state (Immediate; the stdin barrier makes it read the settled state after any pending mutation): source path (or baked default), schema, kit/screen/camera counts, a cheap session-drift hint (which live session dimensions — render/population/screens — differ from the document's defaults, none when a save would reproduce the file), and dirty = journal length. Session drift is separate from dirty: a saved-bytes-only world.save leaves the in-memory definition unchanged, so session drift honestly persists past a save.",
            handler: (_, _) => {
                var definition = server.Definition;
                var source = (definitionSource.SourcePath ?? "baked default");
                var dirty = server.JournalLength;
                var drift = WorldSessionCapture.DescribeDrift(definition: definition, render: renderSettings, population: server.Population, binder: screenBinder, audio: audioDirector, pacing: pacing);

                return new CommandResult(Output: $"[world.status: source {source} schema {definition.Schema} kits {definition.Kits.Count} screens {definition.Screens.Count} cameras {definition.Cameras.Count} creations {definition.Creations.Count} placements {definition.Placements.Count} session-drift {drift} dirty {dirty} undoable {dirty}]");
            }
        );
    }

    // A row-valued mutation verb: parse ONE inline-JSON argument (the document-section wire shape) from the raw line and
    // submit the composed mutation. A parse error echoes inline and submits nothing.
    private CommandDefinition Row<T>(string name, string description, JsonTypeInfo<T> info, Func<T, WorldMutation> toMutation) {
        return CommandDefinition.WithTrailingArgs(
            name: name,
            description: description,
            handler: (context, args) => {
                var raw = RawArgument(context: context, args: args);

                if (!TryParseJson(json: raw, info: info, value: out var value, error: out var error)) {
                    return new CommandResult(Output: $"[{name}: {error}]") { IsError = true };
                }

                return Submit(mutation: toMutation(arg: value));
            },
            routing: CommandRouting.Simulation
        );
    }

    // A non-JSON mutation verb — a thin Simulation-routed wrapper so every mutation verb shares the routing discipline.
    private static CommandDefinition Simulation(string name, string description, Func<CommandContext, string[], CommandResult> handler) {
        return CommandDefinition.WithTrailingArgs(name: name, description: description, handler: handler, routing: CommandRouting.Simulation);
    }

    // Buffer a mutation over the link and return a quiet ack — the server prints the loud accept/reject line when the
    // buffered edit applies at the tick boundary, and the barrier guarantees a following world.status sees the result.
    private CommandResult Submit(WorldMutation mutation) {
        link.SubmitWorldMutation(mutation: mutation);

        return CommandResult.None;
    }

    private static CommandResult Usage(string verb, string form) {
        return new CommandResult(Output: $"[{verb}: expected {form}]") {
            IsError = true,
        };
    }

    // The raw argument text after the verb token — reconstructed from the submitted line so inline-JSON quotes survive
    // the console tokenizer. A Simulation dispatch always carries the raw text; the split-args join is a defensive fallback.
    private static string RawArgument(CommandContext context, string[] args) {
        if (context.Text is { } text) {
            var span = text.AsSpan().TrimStart();
            var separator = span.IndexOfAny(value0: ' ', value1: '\t');

            return ((separator < 0) ? string.Empty : span[(separator + 1)..].Trim().ToString());
        }

        return string.Join(separator: ' ', values: args);
    }

    private static bool TryParseJson<T>(string json, JsonTypeInfo<T> info, out T value, out string error) {
        value = default!;

        if (string.IsNullOrWhiteSpace(value: json)) {
            error = "expected a compact inline-JSON argument";

            return false;
        }

        try {
            if (JsonSerializer.Deserialize(json: json, jsonTypeInfo: info) is not { } parsed) {
                error = "the JSON parsed to null";

                return false;
            }

            value = parsed;
            error = string.Empty;

            return true;
        } catch (JsonException exception) {
            error = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }
    }

    private WorldKit? FindKit(string name) {
        foreach (var kit in server.Definition.Kits) {
            if (string.Equals(a: kit.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return kit;
            }
        }

        return null;
    }

    // Read-modify-write ONE MotionTuning field for the world.kit.tune sugar (camelCase names matching the JSON), or null
    // when the field name is unknown.
    private static MotionTuning? WithTuningField(MotionTuning tuning, string field, float value) {
        return field switch {
            "moveSpeed" => (tuning with { MoveSpeed = value }),
            "turnSpeed" => (tuning with { TurnSpeed = value }),
            "groundY" => (tuning with { GroundY = value }),
            "jumpSpeed" => (tuning with { JumpSpeed = value }),
            "riseGravity" => (tuning with { RiseGravity = value }),
            "fallGravity" => (tuning with { FallGravity = value }),
            "maxFallSpeed" => (tuning with { MaxFallSpeed = value }),
            "jumpCutMultiplier" => (tuning with { JumpCutMultiplier = value }),
            "coyoteTime" => (tuning with { CoyoteTime = value }),
            "jumpBufferTime" => (tuning with { JumpBufferTime = value }),
            _ => (MotionTuning?)null,
        };
    }
}
