namespace Puck.Shaders;

// Versioned resources: each declared resource is one version with one writer, and a version naming `from` forwards its
// predecessor. A forwarding chain shares one storage; the forward orders every reader of the predecessor before the
// successor's writer, and the planner refuses any chain whose storage could not hold both versions' contents in turn.
public sealed partial class ShaderPipelineCompiler {
    // The version forwarding each predecessor. A predecessor named twice keeps its first successor here; the second is
    // refused by ValidateForwards.
    private static Dictionary<string, string> Successors(ShaderPipelineDefinition definition) {
        var successors = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var resource in definition.Resources) {
            if (resource.From is { } predecessor) {
                successors.TryAdd(
                    key: predecessor,
                    value: resource.Name
                );
            }
        }

        return successors;
    }
    // The first version of the chain a version belongs to, stopping at an unknown name or a forwarding loop.
    private static ShaderPipelineResource ChainRoot(string name, IReadOnlyDictionary<string, ShaderPipelineResource> resources) {
        var current = resources[name];
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal) { name };

        while (
            (current.From is { } predecessor) &&
            resources.TryGetValue(
                key: predecessor,
                value: out var previous
            ) &&
            seen.Add(item: predecessor)
        ) {
            current = previous;
        }

        return current;
    }
    private static void ValidateForwards(ShaderPipelineDefinition definition, IReadOnlyDictionary<string, ShaderPipelineResource> resources, IReadOnlyDictionary<string, string> successors, IReadOnlySet<string> outputs, List<ShaderPipelineDiagnostic> diagnostics) {
        var writers = new Dictionary<string, ShaderPipelinePass>(comparer: StringComparer.Ordinal);

        foreach (var pass in definition.Passes) {
            foreach (var output in pass.OutputReferences) {
                writers.TryAdd(
                    key: output.Name,
                    value: pass
                );
            }
        }

        foreach (var successor in definition.Resources) {
            if (successor.From is not { } name) {
                continue;
            }
            if (!resources.TryGetValue(
                key: name,
                value: out var predecessor
            )) {
                Add(
                    diagnostics,
                    "SHADERPIPE_UNKNOWN_RESOURCE",
                    $"Version '{successor.Name}' forwards undeclared version '{name}'.",
                    name
                );
                continue;
            }
            if (!string.Equals(
                a: successors[name],
                b: successor.Name,
                comparisonType: StringComparison.Ordinal
            )) {
                Add(
                    diagnostics,
                    "SHADERPIPE_FORWARD_BRANCH",
                    $"Version '{name}' is forwarded by both '{successors[name]}' and '{successor.Name}'; a version has at most one successor.",
                    name
                );
            }
            if (predecessor.Kind != successor.Kind) {
                Add(
                    diagnostics,
                    "SHADERPIPE_FORWARD_KIND",
                    $"Version '{successor.Name}' is a {successor.Kind} but forwards {predecessor.Kind} '{name}'.",
                    successor.Name
                );
            }
            if (!string.Equals(
                a: predecessor.Format,
                b: successor.Format,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                Add(
                    diagnostics,
                    "SHADERPIPE_FORWARD_FORMAT",
                    $"Version '{successor.Name}' has format '{successor.Format}' but forwards '{name}' of format '{predecessor.Format}'.",
                    successor.Name
                );
            }
            if (
                (predecessor.Dimensions != successor.Dimensions) ||
                (predecessor.SizeBytes != successor.SizeBytes)
            ) {
                Add(
                    diagnostics,
                    "SHADERPIPE_FORWARD_EXTENT",
                    $"Version '{successor.Name}' declares a different extent from '{name}', which it forwards; a forward continues one storage.",
                    successor.Name
                );
            }
            if (predecessor.Samples != successor.Samples) {
                Add(
                    diagnostics,
                    "SHADERPIPE_FORWARD_SAMPLES",
                    $"Version '{successor.Name}' has {successor.Samples} samples but forwards '{name}' with {predecessor.Samples}.",
                    successor.Name
                );
            }
            if (successor.Initialization != ShaderPipelineInitialization.Undefined) {
                Add(
                    diagnostics,
                    "SHADERPIPE_FORWARD_INITIALIZATION",
                    $"Version '{successor.Name}' forwards '{name}' and cannot declare an initialization; its contents are its predecessor's.",
                    successor.Name
                );
            }
            if (outputs.Contains(item: name)) {
                Add(
                    diagnostics,
                    "SHADERPIPE_FORWARD_PUBLIC",
                    $"Version '{successor.Name}' forwards public output '{name}', which would overwrite it before publication.",
                    name
                );
            }
            if (predecessor.History) {
                Add(
                    diagnostics,
                    "SHADERPIPE_FORWARD_HISTORY",
                    $"Version '{successor.Name}' forwards history '{name}', which would overwrite what the next frame reads; declare history on the last version instead.",
                    name
                );
            }
            if (predecessor.IsExternal) {
                Add(
                    diagnostics,
                    "SHADERPIPE_EXTERNAL_WRITE",
                    $"Version '{successor.Name}' forwards external resource '{name}'; external resources are host inputs and cannot be overwritten.",
                    name
                );
            } else if (!writers.ContainsKey(key: name)) {
                Add(
                    diagnostics,
                    "SHADERPIPE_FORWARD_UNWRITTEN",
                    $"Version '{successor.Name}' forwards '{name}', which no pass writes; forwarding it would destroy its initialization after the first frame.",
                    name
                );
            }
        }

        foreach (var resource in definition.Resources) {
            var chain = new List<string> { resource.Name };
            var current = resource;

            while (
                (current.From is { } predecessor) &&
                resources.TryGetValue(
                    key: predecessor,
                    value: out var previous
                )
            ) {
                if (string.Equals(
                    a: predecessor,
                    b: resource.Name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    chain.Add(item: predecessor);
                    Add(
                        diagnostics,
                        "SHADERPIPE_FORWARD_CYCLE",
                        $"Forwarding loop: {string.Join(separator: " <- ", values: chain)}.",
                        resource.Name
                    );
                    break;
                }
                if (chain.Contains(item: predecessor)) {
                    break;
                }
                chain.Add(item: predecessor);
                current = previous;
            }
        }

        // A pass that samples a version while writing its successor reads contents its own writes destroy.
        foreach (var pass in definition.Passes) {
            foreach (var input in pass.InputReferences) {
                if (
                    !input.PreviousFrame &&
                    successors.TryGetValue(
                        key: input.Name,
                        value: out var successor
                    ) &&
                    pass.OutputReferences.Any(predicate: output => string.Equals(
                        a: output.Name,
                        b: successor,
                        comparisonType: StringComparison.Ordinal
                    ))
                ) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_DISCARDED_READ",
                        $"Pass '{pass.Name}' samples '{input.Name}' while writing '{successor}', which overwrites it.",
                        input.Name
                    );
                }
            }
        }
    }
    // A forward is a consuming edge: the successor's writer runs after the predecessor's writer and after every pass that
    // samples the predecessor in the same frame.
    private static void AddForwardDependencies(ShaderPipelineDefinition definition, List<HashSet<int>> dependencies, IReadOnlyDictionary<string, int> writerByResource) {
        foreach (var successor in definition.Resources) {
            if (
                (successor.From is not { } predecessor) ||
                !writerByResource.TryGetValue(
                    key: successor.Name,
                    value: out var overwriter
                )
            ) {
                continue;
            }
            if (writerByResource.TryGetValue(
                key: predecessor,
                value: out var writer
            )) {
                dependencies[overwriter].Add(item: writer);
            }
            for (var passIndex = 0; (passIndex < definition.Passes.Count); passIndex++) {
                if (
                    (passIndex != overwriter) &&
                    definition.Passes[passIndex].InputReferences.Any(predicate: input => (!input.PreviousFrame && string.Equals(
                        a: input.Name,
                        b: predecessor,
                        comparisonType: StringComparison.Ordinal
                    )))
                ) {
                    dependencies[overwriter].Add(item: passIndex);
                }
            }
        }
    }
}
