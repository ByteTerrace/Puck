using Puck.Hosting;

namespace Puck.Shaders;

// How far back a standing output may reach. Every instance records its two latest outputs, and its node keeps exactly
// those images through a replacement, so an output standing for a producer's previous frame resolves to the producer's
// previous output, and nothing older exists to resolve to. A chain of standing outputs crossing two previous-frame reads
// (a view standing for the camera's previous frame, read at its own previous frame by a root standing for it) would need
// an output two frames old. The set refuses such a chain when it installs, naming it, rather than letting the chain
// resolve to nothing: an output only resolves through outputs its producers still record. A deeper record would bind the
// ring's depth to the graph's topology (a storage keeps one instance per frame slot, so an image two renders old is
// already being rewritten), which the frame-slot ring does not offer.
public sealed partial class RenderGraphRuntime {
    // Why a set's graphs could chain two previous-frame reads through outputs that stand for their inputs, or null when
    // none can. An edge runs from an instance to the producer of an input its graph's default output may stand for (a
    // package pass that draws nothing publishes the input at its position in the output's place); the set is refused
    // when a path through distinct instances crosses two previous-frame edges. A path returning to an instance ends at
    // that instance's own image, which a node never stands for, so it never resolves through a third frame.
    private static RenderGraphRuntimeRefusal? RefuseStandingChains(RenderGraphInstanceSet set, IReadOnlyList<RenderGraphRuntimeGraph?> graphs, IRenderGraphExternalProducer?[] producers, Binding[][] inputs) {
        var count = graphs.Count;
        var edges = new List<(int Producer, bool PreviousFrame)>[count];

        for (var index = 0; (index < count); index++) {
            edges[index] = [];

            if (graphs[index] is not { } graph) {
                continue;
            }

            var standable = StandableVersions(plan: graph.Pipeline.Plan);

            foreach (var binding in inputs[index]) {
                if (
                    (producers[binding.Producer] is null) &&
                    standable.Contains(item: binding.Version)
                ) {
                    edges[index].Add(item: (binding.Producer, binding.PreviousFrame));
                }
            }
        }

        var path = new List<int>();
        var onPath = new bool[count];

        for (var start = 0; (start < count); start++) {
            if (Walk(
                edges: edges,
                index: start,
                onPath: onPath,
                path: path,
                previousFrames: 0
            ) is { } chain) {
                var names = chain.Select(selector: index => set.Instances[index].Name).ToArray();

                return Refuse(
                    RenderGraphRuntimeRefusalCode.StandingChain,
                    $"Instances {string.Join(separator: " -> ", values: names.Select(selector: static name => $"'{name}'"))} could stand for one another across two previous-frame reads, which would need an output two frames old; only the two latest are recorded. Have one of them draw rather than stand for its input, or read the same frame.",
                    names
                );
            }
        }

        return null;
    }
    // Depth-first over distinct instances: returns the path, ending at the producer, once it crosses two previous-frame
    // edges, or null.
    private static List<int>? Walk(List<(int Producer, bool PreviousFrame)>[] edges, int index, bool[] onPath, List<int> path, int previousFrames) {
        onPath[index] = true;
        path.Add(item: index);

        foreach (var (producer, previousFrame) in edges[index]) {
            if (onPath[producer]) {
                continue;
            }

            var crossed = (previousFrames + (previousFrame ? 1 : 0));

            if (crossed >= 2) {
                path.Add(item: producer);

                return path;
            }
            if (Walk(
                edges: edges,
                index: producer,
                onPath: onPath,
                path: path,
                previousFrames: crossed
            ) is { } chain) {
                return chain;
            }
        }

        path.RemoveAt(index: (path.Count - 1));
        onPath[index] = false;

        return null;
    }
    // The external versions a graph's default output may stand for: following package passes in order, an output at a
    // position stands for the input at that position, or for whatever that input stands for, unless the input is read at
    // its previous frame, for which no output stands.
    private static HashSet<string> StandableVersions(ShaderPipelinePlan plan) {
        var standing = new Dictionary<string, HashSet<string>>(comparer: StringComparer.Ordinal);

        foreach (var pass in plan.Passes) {
            if (pass.Package is not { } step) {
                continue;
            }

            var count = Math.Min(
                val1: step.Inputs.Count,
                val2: step.Outputs.Count
            );

            for (var position = 0; (position < count); position++) {
                var input = step.Inputs[position];

                if (input.PreviousFrame) {
                    continue;
                }

                var versions = new HashSet<string>(comparer: StringComparer.Ordinal);

                if (
                    (plan.FindResource(name: input.Name) is { } resource) &&
                    plan.Storages[resource.Storage].IsExternal &&
                    !plan.Storages[resource.Storage].Declaration.IsHostBuffer
                ) {
                    _ = versions.Add(item: input.Name);
                } else if (standing.TryGetValue(
                    key: input.Name,
                    value: out var inherited
                )) {
                    versions.UnionWith(other: inherited);
                }

                standing[step.Outputs[position].Name] = versions;
            }
        }

        return (standing.TryGetValue(
            key: plan.DefaultOutput,
            value: out var published
        )
            ? published
            : []);
    }
}
