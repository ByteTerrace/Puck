using System.Numerics;
using Puck.Commands;
using static Puck.Demo.CommandArgs;

namespace Puck.Demo.Creator;

/// <summary>
/// THE RIG's console verbs (<c>creator.chain</c>/<c>creator.pole</c>/<c>creator.kind</c>/<c>creator.gait</c>) — split
/// into a static helper class (rather than more <see cref="CreatorCommandModule"/> instance methods) purely to keep
/// that class's own member count under the analyzer's class-coupling ceiling; this is still the SAME logical verb
/// surface, reached through the module's <see cref="CreatorCommandModule.GetCommands"/>. Every handler reuses the
/// module's <c>WithScene</c>/<c>WithSceneArgs</c>/<c>Plain</c>/<c>WithArgs</c> wrappers (internal, not private, for
/// exactly this reuse) so the availability guard (the overworld root + creator mode both up) stays in ONE place.
/// </summary>
internal static class CreatorRigCommands {
    /// <summary>Builds the RIG verbs.</summary>
    /// <param name="module">The owning command module (supplies the console-wiring wrappers and scene access).</param>
    /// <returns>The RIG console verbs.</returns>
    public static IEnumerable<CommandDefinition> GetCommands(CreatorCommandModule module) {
        ArgumentNullException.ThrowIfNull(module);

        yield return module.WithArgs(
            description: "Defines a chain from shapes (root→tip): creator.chain <name> <shapeIdOrName...> [limb|spine] — captures their CURRENT positions as rest.",
            handler: module.WithSceneArgs(handler: static (scene, args) => {
                if (args.Length < 3) {
                    return "[creator.chain: usage — creator.chain <name> <shapeId1> <shapeId2> [more...] [limb|spine]]";
                }

                var name = args[0];
                var last = args[^1];
                var hasKind = string.Equals(a: last, b: CreatorChainState.KindLimb, comparisonType: StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a: last, b: CreatorChainState.KindSpine, comparisonType: StringComparison.OrdinalIgnoreCase);
                var kind = (hasKind ? last : null);
                var shapeTokens = args[1..(hasKind ? (args.Length - 1) : args.Length)];

                return ((scene.DefineChain(name: name, shapeIdsOrNames: shapeTokens, kind: kind) is { } defined)
                    ? $"[creator.chain: #{defined.Id} '{defined.Name}' ({defined.Kind}, {defined.ShapeIds.Count} shapes)]"
                    : "[creator.chain: needs at least 2 resolvable shape ids/names]");
            }),
            name: "creator.chain"
        );
        yield return CreatorCommandModule.Plain(
            description: "Lists the defined chains (id, name, kind, member shapes).",
            handler: module.WithScene(handler: static scene => ((scene.Chains.Count > 0)
                ? $"[creator.chain.list: {string.Join(separator: "; ", values: scene.Chains.Select(selector: static chain => $"#{chain.Id} '{chain.Name}' {chain.Kind} [{string.Join(separator: ",", values: chain.ShapeIds)}]"))}]"
                : "[creator.chain.list: none defined yet — creator.chain <name> <ids...> defines one]")),
            name: "creator.chain.list"
        );
        yield return module.WithArgs(
            description: "Deletes a chain by id or name: creator.chain.del <idOrName>.",
            handler: module.WithSceneArgs(handler: static (scene, args) => (((args.Length > 0) && scene.DeleteChain(idOrName: args[0]))
                ? "[creator.chain.del: deleted]"
                : "[creator.chain.del: give a valid chain id or name]")),
            name: "creator.chain.del"
        );
        yield return module.WithArgs(
            description: "Sets a chain's pole (bend-direction hint): creator.pole <idOrName> <x> <y> <z>.",
            handler: module.WithSceneArgs(handler: static (scene, args) => {
                if ((args.Length < 4) || !TryParseFloats(args: args, count: 3, start: 1, values: out var xyz)) {
                    return "[creator.pole: usage — creator.pole <idOrName> <x> <y> <z>]";
                }

                return (scene.SetPole(idOrName: args[0], pole: new Vector3(xyz[0], xyz[1], xyz[2]))
                    ? $"[creator.pole: {CreatorCommandModule.Describe(vector: new Vector3(xyz[0], xyz[1], xyz[2]))}]"
                    : $"[creator.pole: no chain matches '{args[0]}']");
            }),
            name: "creator.pole"
        );
        yield return module.WithArgs(
            description: "Sets a chain's kind: creator.kind <idOrName> <limb|spine> (\"limb\" needs exactly 3 shapes, else it demotes to \"spine\").",
            handler: module.WithSceneArgs(handler: static (scene, args) => (((args.Length > 1) && (scene.SetKind(idOrName: args[0], kind: args[1]) is { } kind))
                ? $"[creator.kind: {kind}]"
                : "[creator.kind: usage — creator.kind <idOrName> <limb|spine>]")),
            name: "creator.kind"
        );
        yield return module.WithArgs(
            description: "Sweeps every chain named with this PREFIX through a walk-cycle gait, recording frames 1..N (the bake's walk-pair convention): creator.gait <prefix> <frames> [stride].",
            handler: module.WithSceneArgs(handler: static (scene, args) => {
                if (args.Length < 2) {
                    return "[creator.gait: usage — creator.gait <chainPrefix> <frames> [stride]]";
                }

                if (!TryParseInt(text: args[1], value: out var frames)) {
                    return "[creator.gait: give a frame count]";
                }

                var stride = (((args.Length > 2) && TryParseFloat(text: args[2], value: out var parsedStride)) ? parsedStride : 0.4f);
                var recorded = scene.Gait(prefix: args[0], frameCount: frames, stride: stride);

                return ((recorded > 0)
                    ? $"[creator.gait: recorded {recorded} frame(s) for chains prefixed '{args[0]}']"
                    : $"[creator.gait: no chain named starting with '{args[0]}']");
            }),
            name: "creator.gait"
        );
    }
}
