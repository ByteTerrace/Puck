using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldViewCommandModule {
    private const string IsolateForm = "<prototype> <shape-name>... | off";

    // Sets, clears or echoes the stamp pool's shape isolation. Names resolve against the live document so a refusal
    // names what is missing; the emitters resolve them again at each rebuild against the document they then emit.
    private CommandResult Isolate(WireArgs args) {
        var isolation = stamps.Isolation;

        if (args.Count == 0) {
            return new CommandResult(Output: ((isolation.Current is { } current)
                ? Describe(selection: current, verdict: null)
                : "[view.isolate: off]"));
        }
        if ((args.Count == 1) && string.Equals(a: args[0].ToString(), b: "off", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            var was = isolation.Current;

            isolation.Set(selection: null);

            return new CommandResult(Output: ((was is null)
                ? "[view.isolate: off]"
                : $"[view.isolate: off — {was.Prototype} renders whole]"));
        }
        if (args.Count < 2) {
            return CommandResult.Usage(form: IsolateForm, verb: "view.isolate");
        }

        var prototype = args[0].ToString();
        var creations = client.Definition.Creations;

        if (WorldDefinitionRows.FindCreation(creations: creations, id: prototype) is not { } creation) {
            return CommandResult.Error(output: $"[view.isolate: no prototype named '{prototype}' — prototypes: {NamesOf(names: creations.Select(selector: static candidate => candidate.Id.Value))}]");
        }

        var names = new string[(args.Count - 1)];

        for (var index = 0; (index < names.Length); index++) {
            names[index] = args[(index + 1)].ToString();
        }

        var matched = new bool[names.Length];
        var hidden = WorldShapeIsolation.Resolve(matched: matched, names: names, shapes: (creation.EngineDocument.Shapes ?? []));
        var unmatched = names.Where(predicate: (_, index) => !matched[index]).ToArray();

        if (unmatched.Length > 0) {
            return CommandResult.Error(output: $"[view.isolate: {prototype} has no shape matching {string.Join(separator: ", ", values: unmatched.Select(selector: static name => $"'{name}'"))} — nothing changed]");
        }

        var selection = new WorldShapeIsolation.Selection(Names: names, Prototype: prototype);

        isolation.Set(selection: selection);

        return new CommandResult(Output: Describe(selection: selection, verdict: hidden));
    }
    // The isolation as its echo prints it: the prototype, how many of its shapes are kept and hidden, and the names.
    private string Describe(WorldShapeIsolation.Selection selection, bool[]? verdict) {
        var hidden = (verdict ?? ((WorldDefinitionRows.FindCreation(creations: client.Definition.Creations, id: selection.Prototype) is { } creation)
            ? WorldShapeIsolation.Resolve(matched: null, names: selection.Names, shapes: (creation.EngineDocument.Shapes ?? []))
            : null));

        if (hidden is null) {
            return $"[view.isolate: {selection.Prototype} (absent from the live document) names={string.Join(separator: ",", values: selection.Names)}]";
        }

        var hiddenCount = hidden.Count(predicate: static shape => shape);

        return $"[view.isolate: {selection.Prototype} kept={(hidden.Length - hiddenCount)} hidden={hiddenCount} names={string.Join(separator: ",", values: selection.Names)}]";
    }
}
