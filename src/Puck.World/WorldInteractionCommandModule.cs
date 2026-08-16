using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The generalized property-interaction READ-BACK surface. <c>world.properties</c> reads the <c>properties</c>
/// registry vocabulary (a carrier's own tags ride the ORDINARY <c>world.state.cell.set</c>/<c>.remove</c>/
/// <c>world.state</c> verb family already, since a property is backed by a keyed <c>int</c> state row of the same
/// name — see <see cref="WorldPropertyRegistrySection"/>'s remarks); <c>world.interactions</c> reads the live
/// compiled <c>interactions</c> table back. Both sections are authored through the general
/// <see cref="WorldRowCommandModule"/>. A property is the ONE <see cref="WorldMutation.SetProperty"/> kind (ordinal
/// 10, distinguished by its own <c>Remove</c> flag), which is why it carries the SPECIAL bare-name grammar —
/// <c>world.row.set</c>/<c>world.row.remove properties.names &lt;name&gt;</c>; an interaction is the ordinary
/// Upsert/Remove pair (ordinals 54/55) over
/// <c>world.row.set</c>/<c>world.row.remove interactions.interactions</c>.
/// </summary>
/// <remarks>An interaction's own EFFECTS are a different question entirely from authoring it: they act as
/// <see cref="WorldPrincipal.World"/>, exempted structurally by the admission predicate, on the SAME terms a world
/// rule's effects already are (see <see cref="WorldRulesCommandModule"/>'s own remarks) — the authority that matters
/// over authoring is the ordinary <see cref="WorldCapability.Mutate"/> hold over
/// <see cref="WorldSection.Properties"/>/<see cref="WorldSection.Interactions"/> the row verb already checks.</remarks>
internal sealed class WorldInteractionCommandModule(IServerLink link) : ICommandModule {
    private static CommandResult Usage(string verb, string form) => CommandResult.Error(output: $"[{verb}: expected {form}]");

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.properties",
            description: "Reads the property registry back (Immediate): with no argument, the declared vocabulary; with a 0-based body index, which of those registered properties are currently ON for that carrier (a nonzero cell at key=<bodyIndex> in the property's own row): world.properties [bodyIndex].",
            handler: (_, args) => {
                if (args.Count > 1) {
                    return Usage(
                        form: "[bodyIndex]",
                        verb: "world.properties"
                    );
                }

                int? bodyIndex = null;

                if (args.Count == 1) {
                    if (!int.TryParse(
                        s: args[0].ToString(),
                        style: System.Globalization.NumberStyles.Integer,
                        provider: System.Globalization.CultureInfo.InvariantCulture,
                        result: out var parsed
                    )) {
                        return CommandResult.Error(output: $"[world.properties: '{args[0].ToString()}' is not an integer]");
                    }

                    bodyIndex = parsed;
                }

                var result = default(CommandResult);

                link.Query(
                    query: new WorldQuery.Properties(BodyIndex: bodyIndex),
                    completion: answer => {
                        result = new CommandResult(Output: answer.Text) { IsError = answer.Refused };
                    }
                );

                return result;
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "world.interactions",
            description: "Reads every live compiled interaction back (Immediate): its name, its mode, its gate-held latch (latch=held means the co-occurrence held at the last evaluation — the 'an interaction fired' echo, on the SAME terms world.rules gives a rule), the co-occurrence gate's own resolved predicates, and its effects.",
            valueKind: CommandValueKind.Digital,
            handler: _ => {
                var result = default(CommandResult);

                link.Query(
                    query: new WorldQuery.Interactions(),
                    completion: answer => {
                        result = new CommandResult(Output: answer.Text) { IsError = answer.Refused };
                    }
                );

                return result;
            },
            routing: CommandRouting.Immediate
        );
    }
}
