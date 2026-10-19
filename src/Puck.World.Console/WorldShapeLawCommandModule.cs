using Puck.Commands;
using Puck.World.Authoring;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// <c>creation.shape &lt;prototype&gt; [&lt;shape-name&gt;...]</c>: the per-shape law read-back of a creation the live
/// document authors (<see cref="CreationShapeLaws"/>). Each shape's line names what it lowers to, whether that field is
/// the distance, a bound (with its gain and step scale) or an approximation, whether the fixed-point evaluator
/// interprets it, and what deterministic contact makes of it on a solid placement. Reads the document through the
/// acting principal's view and changes nothing; it needs no renderer, so it answers on a headless host too. The
/// offline twin is <c>puck creation shape</c>.
/// </summary>
/// <param name="authority">The seam that resolves the invocation's row and its read view.</param>
public sealed class WorldShapeLawCommandModule(IWorldConsoleAuthority authority) : ICommandModule {
    private const string Verb = "creation.shape";

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: Verb,
            description: "Reads back each shape's law in a creation the live document authors: creation.shape <prototype> [<shape-name>...]. A summary line counts the shapes whose lowered field is exact, a bound or an approximation, and those the fixed-point evaluator or deterministic contact refuse; then one line a shape (all of them, or the named ones): index, name, authored type/profile/lift, scale, facets, lowered= the instructions the renderer emits for the shape alone (its domain folds aside), distance=exact | bound gain=<g> (the field is at least g times the distance, so |grad d| lies in [g, 1]: a gauge or a non-uniform Scale) | approximate (a warp, field op, path or sweep certifies no gain), step=<1/L> when a warp or blend clamps the march, cpu=interprets | refuses (the fixed-point evaluator's reason), contact=admitted | omitted (detail, sweep) | refused (why) for a solid placement, and contact-omits= the render instructions contact drops. Refuses by name a prototype the document does not author and a shape name it does not hold, changing nothing. Needs no renderer. The offline twin is puck creation shape.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return CommandResult.Usage(form: "<prototype> [<shape-name>...]", verb: Verb);
                }
                if (!authority.TryResolveReadView(context: context, error: out var error, verb: Verb, view: out var view)) {
                    return error;
                }

                var prototype = args[0].ToString();
                var creations = view.Definition.Creations;

                if (WorldDefinitionRows.FindCreation(creations: creations, id: prototype) is not { } creation) {
                    return CommandResult.Error(output: $"[{Verb}: no prototype named '{prototype}' — prototypes: {string.Join(separator: ", ", values: creations.Select(selector: static candidate => candidate.Id.Value))}]");
                }

                var document = creation.EngineDocument;
                var shapes = (document.Shapes ?? []);
                var selected = new List<int>();

                for (var index = 1; (index < args.Count); index++) {
                    var name = args[index].ToString();
                    var found = -1;

                    for (var shape = 0; (shape < shapes.Count); shape++) {
                        if (string.Equals(a: shapes[shape].Name?.Value, b: name, comparisonType: StringComparison.Ordinal)) {
                            found = shape;
                            break;
                        }
                    }
                    if (found < 0) {
                        return CommandResult.Error(output: $"[{Verb}: {prototype} has no shape named '{name}' — shapes: {string.Join(separator: ", ", values: shapes.Select(selector: static (shape, at) => (shape.Name?.Value ?? $"#{at}")))}]");
                    }
                    selected.Add(item: found);
                }

                var laws = ((selected.Count == 0)
                    ? CreationShapeLaws.Of(document: document)
                    : [.. selected.Select(selector: index => CreationShapeLaws.Of(document: document, index: index))]);

                return new CommandResult(Output: CreationShapeLaws.Describe(laws: laws, prototype: prototype, shapeCount: shapes.Count, verb: Verb));
            },
            routing: CommandRouting.Immediate
        );
    }
}
