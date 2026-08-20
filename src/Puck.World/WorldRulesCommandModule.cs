using Puck.Commands;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>
/// The <c>rules</c> section's READ-BACK: <c>world.rules</c> reads the live compiled set. A rule is the SAME
/// <see cref="ActionPredicate"/>/<see cref="ActionEffect"/>/<see cref="ActionTriggerMode"/> primitive a kit's per-body
/// actions already use, one level up — so there is no second predicate or effect vocabulary to learn here, only a
/// narrower admissible subset (see <see cref="WorldRule"/>). A rule row is authored through the general
/// <see cref="WorldRowCommandModule"/> (<c>world.row.set</c>/<c>world.row.remove rules ...</c>) — it takes the SAME ordinary
/// <see cref="WorldCapability.Mutate"/> hold over <see cref="WorldSection.Rules"/> there. A rule's own EFFECTS are a
/// different question entirely: they act as <see cref="WorldPrincipal.World"/>, which the admission predicate exempts
/// structurally — so the authority that matters over a rule is the authority to WRITE it. <c>world.why world
/// &lt;capability&gt; &lt;subject&gt;</c> answers for the firing side.
/// </summary>
internal sealed class WorldRulesCommandModule(IServerLink link) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "world.rules",
            description: "Reads every live compiled rule back: its name, its mode, its gate-held latch (latch=held when the gate held at the last evaluation — an edge rule will not fire again until it lets go; latch=open when it did not, so the next tick the gate holds is a crossing), the gate's OWN PREDICATES spelled out (an 'all' gate prints its conjuncts, never a type name), and its effects.",
            valueKind: CommandValueKind.Digital,
            handler: _ => {
                var result = default(CommandResult);

                link.Query(
                    query: new WorldQuery.Rules(),
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
