using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The contact/solidity verb surface — the dev reflection of Arc 1's <see cref="WorldMutation.SetCollision"/> and the
/// solidity facets, molded over stdin through the SAME mutation messages the editor drives. Every write verb routes
/// <see cref="CommandRouting.Simulation"/> (buffers, applies at the tick boundary, the stdin barrier serializes a
/// following read); <c>world.contacts</c> is an <see cref="CommandRouting.Immediate"/> read of the live definition and
/// the body table. A SEPARATE module from <see cref="WorldMutationCommandModule"/> to keep every class under its
/// analyzer ceilings.
/// </summary>
internal sealed class WorldCollisionCommandModule(WorldServer server, IServerLink link) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return Row(
            name: "world.collision",
            description: "Replaces the whole contact-solver tuning from one inline-JSON WorldCollision {enabled, provider, contactSkin, maxIterations, maxSlopeDegrees, gradientProbe}: world.collision <json>. Applies LIVE (the collider set rebuilds next tick).",
            info: WorldJsonContext.Default.WorldCollision,
            toMutation: static collision => new WorldMutation.SetCollision(Principal: WorldPrincipal.Console, Collision: collision)
        );
        yield return Simulation(
            name: "world.collision.on",
            description: "Turns collision ON (RMW over the collision section's enabled flag): world.collision.on.",
            handler: (_, _) => SubmitCollision(edit: static collision => (collision with { Enabled = true }))
        );
        yield return Simulation(
            name: "world.collision.off",
            description: "Turns collision OFF (RMW): world.collision.off. Bodies keep their flat ground plane.",
            handler: (_, _) => SubmitCollision(edit: static collision => (collision with { Enabled = false }))
        );
        yield return Simulation(
            name: "world.collision.skin",
            description: "Sets the contact skin (RMW): world.collision.skin <value>. The signed gap the solver keeps between a body and every surface.",
            handler: (_, args) => Scalar(verb: "world.collision.skin", args: args, edit: static (collision, value) => (collision with { ContactSkin = value }))
        );
        yield return Simulation(
            name: "world.collision.slope",
            description: "Sets the walkable-slope limit in degrees (RMW): world.collision.slope <degrees>. The steepest surface a body still stands on; must be in (0, 90).",
            handler: (_, args) => Scalar(verb: "world.collision.slope", args: args, edit: static (collision, value) => (collision with { MaxSlopeDegrees = value }))
        );
        yield return Simulation(
            name: "world.collision.gradient",
            description: "Sets the field-provider gradient probe step (RMW): world.collision.gradient <value> | -. '-' restores the evaluator default (0). Meaningful only under provider 'field'.",
            handler: (_, args) => {
                if ((args.Length == 1) && string.Equals(a: args[0], b: "-", comparisonType: StringComparison.Ordinal)) {
                    return SubmitCollision(edit: static collision => (collision with { GradientProbe = 0f }));
                }

                return Scalar(verb: "world.collision.gradient", args: args, edit: static (collision, value) => (collision with { GradientProbe = value }));
            }
        );
        yield return Simulation(
            name: "world.collision.provider",
            description: "Sets the contact provider (RMW): world.collision.provider <analytic|field>. analytic derives convex colliders from the document's solid rows; field compiles them into an SDF (Arc 2).",
            handler: (_, args) => {
                if (args.Length != 1) {
                    return Usage(verb: "world.collision.provider", form: "<analytic|field>");
                }

                if (!Enum.TryParse<WorldContactProvider>(value: args[0], ignoreCase: true, result: out var provider)) {
                    return new CommandResult(Output: $"[world.collision.provider: unknown provider '{args[0]}' — analytic | field]");
                }

                return SubmitCollision(edit: collision => (collision with { Provider = provider }));
            }
        );
        yield return Simulation(
            name: "world.kit.collider",
            description: "Sets a kit's body VOLUME (RMW → UpsertKit): world.kit.collider <name> <radius> <height> | <name> none. A vertical capsule; height must be at least twice the radius. 'none' removes the volume.",
            handler: (_, args) => {
                if ((args.Length == 2) && string.Equals(a: args[1], b: "none", comparisonType: StringComparison.Ordinal)) {
                    return EditKit(verb: "world.kit.collider", name: args[0], edit: static kit => (kit with { Collider = null }));
                }

                if ((args.Length != 3) ||
                    !float.TryParse(s: args[1], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var radius) ||
                    !float.TryParse(s: args[2], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var height)) {
                    return Usage(verb: "world.kit.collider", form: "<name> <radius> <height> | <name> none");
                }

                return EditKit(verb: "world.kit.collider", name: args[0], edit: kit => (kit with { Collider = new WorldCollider(Radius: radius, Height: height) }));
            }
        );
        yield return Simulation(
            name: "world.kit.model",
            description: "Sets a kit's motion model (RMW → UpsertKit): world.kit.model <name> <grounded|free>.",
            handler: (_, args) => {
                if ((args.Length != 2) || !Enum.TryParse<MotionModel>(value: args[1], ignoreCase: true, result: out var model)) {
                    return Usage(verb: "world.kit.model", form: "<name> <grounded|free>");
                }

                return EditKit(verb: "world.kit.model", name: args[0], edit: kit => (kit with { Model = model }));
            }
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.kit.response",
            description: "Sets a kit's velocity-response table (RMW → UpsertKit) from one inline-JSON MotionResponse array: world.kit.response <name> <json-array> | <name> none. Rows evaluate in order, first match wins; 'none' clears the table (instant snap).",
            handler: (context, args) => {
                if ((args.Length == 2) && string.Equals(a: args[1], b: "none", comparisonType: StringComparison.Ordinal)) {
                    return EditKit(verb: "world.kit.response", name: args[0], edit: static kit => (kit with { Tuning = (kit.Tuning with { Response = null }) }));
                }

                if (args.Length < 2) {
                    return Usage(verb: "world.kit.response", form: "<name> <json-array> | <name> none");
                }

                var raw = RawArgument(context: context, args: args);
                var separator = raw.AsSpan().IndexOfAny(value0: ' ', value1: '\t');

                if (separator < 0) {
                    return Usage(verb: "world.kit.response", form: "<name> <json-array> | <name> none");
                }

                var name = raw[..separator];
                var json = raw[(separator + 1)..].Trim();

                if (!TryParseJson(json: json, info: WorldJsonContext.Default.MotionResponseArray, value: out var rows, error: out var error)) {
                    return new CommandResult(Output: $"[world.kit.response: {error}]");
                }

                return EditKit(verb: "world.kit.response", name: name, edit: kit => (kit with { Tuning = (kit.Tuning with { Response = rows }) }));
            },
            routing: CommandRouting.Simulation
        );
        yield return Simulation(
            name: "world.scene.solid",
            description: "Marks a static-scene row SOLID or decorative (RMW → UpsertSceneRow): world.scene.solid <row-id> <margin> | <row-id> off. Solidity is DATA — the facet is the switch; 'off' drops it.",
            handler: (_, args) => {
                if ((args.Length == 2) && string.Equals(a: args[1], b: "off", comparisonType: StringComparison.Ordinal)) {
                    return EditSceneRow(id: args[0], edit: static row => (row with { Solid = null }));
                }

                if ((args.Length != 2) || !float.TryParse(s: args[1], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var margin)) {
                    return Usage(verb: "world.scene.solid", form: "<row-id> <margin> | <row-id> off");
                }

                return EditSceneRow(id: args[0], edit: row => (row with { Solid = new WorldSolid(Margin: margin) }));
            }
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.contacts",
            description: "Reports the solidity state (Immediate read): world.contacts prints the solid-row census (spheres + boxes); world.contacts <body-index> prints that 1-based body's grounded flag, planar speed, and resolved contact count.",
            handler: (_, args) => {
                if (args.Length == 0) {
                    return Census();
                }

                if (!int.TryParse(s: args[0], style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, out var index) || (index < 1) || (index > WorldPopulation.MaxPopulation)) {
                    return new CommandResult(Output: $"[world.contacts: bad body index '{args[0]}' — 1..{WorldPopulation.MaxPopulation}]");
                }

                if (server.Population.EntryBody(index: (index - 1)) is not { } body) {
                    return new CommandResult(Output: $"[world.contacts: body {index} is inactive]");
                }

                return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.contacts: p{index} grounded={(body.Grounded ? "true" : "false")} planarSpeed={body.PlanarSpeed:0.00} resolved={body.ContactCount}]"));
            }
        );
    }

    // The solid-row census: count the spheres (solid boulders) and boxes (solid slabs + solid screens) the analytic
    // provider would derive from the live definition.
    private CommandResult Census() {
        var spheres = 0;
        var boxes = 0;

        foreach (var row in server.Definition.Scene.Rows) {
            if (row.Solid is null) {
                continue;
            }

            switch (row) {
                case WorldSceneRow.Boulder:
                    spheres++;

                    break;
                case WorldSceneRow.Slab:
                    boxes++;

                    break;
            }
        }

        foreach (var screen in server.Definition.Screens) {
            if (screen.Solid is not null) {
                boxes++;
            }
        }

        var enabled = ((server.Definition.Collision ?? WorldCollision.None).Enabled ? "on" : "off");

        return new CommandResult(Output: $"[world.contacts: collision {enabled} — {spheres + boxes} solid rows ({spheres} spheres, {boxes} boxes)]");
    }

    // RMW one collision-section field into a whole-section SetCollision (the protocol stays coarse).
    private CommandResult SubmitCollision(Func<WorldCollision, WorldCollision> edit) {
        var current = (server.Definition.Collision ?? WorldCollision.None);

        return Submit(mutation: new WorldMutation.SetCollision(Principal: WorldPrincipal.Console, Collision: edit(arg: current)));
    }

    private CommandResult Scalar(string verb, string[] args, Func<WorldCollision, float, WorldCollision> edit) {
        if ((args.Length != 1) || !float.TryParse(s: args[0], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, out var value)) {
            return Usage(verb: verb, form: "<value>");
        }

        var current = (server.Definition.Collision ?? WorldCollision.None);

        return Submit(mutation: new WorldMutation.SetCollision(Principal: WorldPrincipal.Console, Collision: edit(arg1: current, arg2: value)));
    }

    // RMW a whole kit row (found by name) into an UpsertKit.
    private CommandResult EditKit(string verb, string name, Func<WorldKit, WorldKit> edit) {
        if (FindKit(name: name) is not { } kit) {
            return new CommandResult(Output: $"[{verb}: no kit row named '{name}']");
        }

        return Submit(mutation: new WorldMutation.UpsertKit(Principal: WorldPrincipal.Console, Kit: edit(arg: kit)));
    }

    // RMW a whole scene row (found by id) into an UpsertSceneRow.
    private CommandResult EditSceneRow(string id, Func<WorldSceneRow, WorldSceneRow> edit) {
        foreach (var row in server.Definition.Scene.Rows) {
            if (string.Equals(a: row.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return Submit(mutation: new WorldMutation.UpsertSceneRow(Principal: WorldPrincipal.Console, Row: edit(arg: row)));
            }
        }

        return new CommandResult(Output: $"[world.scene.solid: no scene row with id '{id}']");
    }

    private WorldKit? FindKit(string name) {
        foreach (var kit in server.Definition.Kits) {
            if (string.Equals(a: kit.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return kit;
            }
        }

        return null;
    }

    // A row-valued mutation verb: parse ONE inline-JSON argument from the raw line and submit the composed mutation.
    private CommandDefinition Row<T>(string name, string description, JsonTypeInfo<T> info, Func<T, WorldMutation> toMutation) {
        return CommandDefinition.WithTrailingArgs(
            name: name,
            description: description,
            handler: (context, args) => {
                var raw = RawArgument(context: context, args: args);

                if (!TryParseJson(json: raw, info: info, value: out var value, error: out var error)) {
                    return new CommandResult(Output: $"[{name}: {error}]");
                }

                return Submit(mutation: toMutation(arg: value));
            },
            routing: CommandRouting.Simulation
        );
    }

    private static CommandDefinition Simulation(string name, string description, Func<CommandContext, string[], CommandResult> handler) {
        return CommandDefinition.WithTrailingArgs(name: name, description: description, handler: handler, routing: CommandRouting.Simulation);
    }

    private CommandResult Submit(WorldMutation mutation) {
        link.SubmitWorldMutation(mutation: mutation);

        return CommandResult.None;
    }

    private static CommandResult Usage(string verb, string form) {
        return new CommandResult(Output: $"[{verb}: expected {form}]") {
            IsError = true,
        };
    }

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
}
