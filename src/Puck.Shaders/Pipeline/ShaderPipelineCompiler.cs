using System.Collections.ObjectModel;

namespace Puck.Shaders;

/// <summary>Validates a shader-pipeline document and compiles its same-frame dependency graph into an immutable plan.
/// This class does not load source, invoke a compiler, or create GPU objects.</summary>
public sealed class ShaderPipelineCompiler {
    private readonly ShaderPipelineLimits m_limits;

    /// <summary>Initializes a planner with the documented default resource limits.</summary>
    public ShaderPipelineCompiler(ShaderPipelineLimits? limits = null) {
        m_limits = limits ?? new ShaderPipelineLimits();
        ValidateLimits(m_limits);
    }

    /// <summary>Compiles a valid definition into an execution plan.</summary>
    /// <exception cref="ShaderPipelineCompilationException">The definition has invalid names, bindings,
    /// initialization, resource declarations, a cycle, or exceeds a plan limit.</exception>
    public ShaderPipelinePlan Compile(ShaderPipelineDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        var diagnostics = new List<ShaderPipelineDiagnostic>();
        ValidateDefinition(definition: definition, diagnostics: diagnostics);

        if (diagnostics.Count != 0) {
            throw new ShaderPipelineCompilationException(diagnostics: diagnostics);
        }

        var resourceByName = definition.Resources.ToDictionary(keySelector: static resource => resource.Name, comparer: StringComparer.Ordinal);
        var writerByResource = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        for (var passIndex = 0; passIndex < definition.Passes.Count; passIndex++) {
            foreach (var output in definition.Passes[passIndex].OutputReferences) {
                writerByResource.Add(key: output.Name, value: passIndex);
            }
        }

        var dependencies = new List<HashSet<int>>(capacity: definition.Passes.Count);

        for (var passIndex = 0; passIndex < definition.Passes.Count; passIndex++) {
            var passDependencies = new HashSet<int>();
            dependencies.Add(item: passDependencies);

            foreach (var input in definition.Passes[passIndex].InputReferences) {
                if (!input.PreviousFrame && writerByResource.TryGetValue(key: input.Name, value: out var writer) && (writer != passIndex)) {
                    passDependencies.Add(item: writer);
                }
            }
        }

        var order = TopologicalOrder(definition: definition, dependencies: dependencies, diagnostics: diagnostics);

        if (diagnostics.Count != 0) {
            throw new ShaderPipelineCompilationException(diagnostics: diagnostics);
        }

        var ordinal = new Dictionary<int, int>();

        for (var index = 0; index < order.Count; index++) {
            ordinal[order[index]] = index;
        }

        var plannedPasses = order.Select((passIndex, index) => new ShaderPipelinePlannedPass(
            Declaration: definition.Passes[passIndex],
            Index: index,
            Dependencies: new ReadOnlyCollection<int>(dependencies[passIndex].OrderBy(value => ordinal[value]).Select(value => ordinal[value]).ToList()),
            Parameters: ShaderPipelineParameterLayout.Resolve(definition.Passes[passIndex])
        )).ToList();

        var plannedResources = resourceByName.Values
            .OrderBy(static resource => resource.Name, StringComparer.Ordinal)
            .Select(resource => new ShaderPipelinePlannedResource(
                Declaration: resource,
                WriterPassIndex: writerByResource.TryGetValue(key: resource.Name, value: out var writer) ? ordinal[writer] : -1
            )).ToList();

        return new ShaderPipelinePlan(
            definition: definition,
            resources: plannedResources,
            passes: plannedPasses,
            outputs: definition.Outputs
        );
    }

    /// <summary>Attempts to compile a definition without throwing for authored validation errors.</summary>
    public bool TryCompile(ShaderPipelineDefinition definition, out ShaderPipelinePlan? plan, out IReadOnlyList<ShaderPipelineDiagnostic> diagnostics) {
        try {
            plan = Compile(definition: definition);
            diagnostics = Array.Empty<ShaderPipelineDiagnostic>();
            return true;
        } catch (ShaderPipelineCompilationException exception) {
            plan = null;
            diagnostics = exception.Diagnostics;
            return false;
        }
    }

    /// <summary>Convenience static entry point for callers that do not need custom limits.</summary>
    public static ShaderPipelinePlan Plan(ShaderPipelineDefinition definition) => new ShaderPipelineCompiler().Compile(definition: definition);

    private static void ValidateLimits(ShaderPipelineLimits limits) {
        if ((limits.MaxResources <= 0) || (limits.MaxPasses <= 0) || (limits.MaxInputsPerPass <= 0) || (limits.MaxOutputsPerPass <= 0)) {
            throw new ArgumentOutOfRangeException(nameof(limits), "Shader pipeline limits must be positive.");
        }
    }

    private void ValidateDefinition(ShaderPipelineDefinition definition, List<ShaderPipelineDiagnostic> diagnostics) {
        if (!string.Equals(a: definition.Schema, b: ShaderPipelineSchemas.Pipeline, comparisonType: StringComparison.Ordinal)) {
            Add(diagnostics, "SHADERPIPE_SCHEMA", $"Pipeline '{definition.Name}' declares schema '{definition.Schema}'; expected '{ShaderPipelineSchemas.Pipeline}'.", definition.Name);
        }
        if (string.IsNullOrWhiteSpace(value: definition.Name)) {
            Add(diagnostics, "SHADERPIPE_NAME", "Pipeline name must not be empty.", definition.Name);
        }
        ValidateConfig(config: definition.Config, owner: "pipeline", diagnostics: diagnostics);

        if (definition.Resources.Count > m_limits.MaxResources) {
            Add(diagnostics, "SHADERPIPE_LIMIT_RESOURCES", $"Pipeline declares {definition.Resources.Count} resources; the limit is {m_limits.MaxResources}.", definition.Name);
        }
        if (definition.Passes.Count > m_limits.MaxPasses) {
            Add(diagnostics, "SHADERPIPE_LIMIT_PASSES", $"Pipeline declares {definition.Passes.Count} passes; the limit is {m_limits.MaxPasses}.", definition.Name);
        }

        var resources = new Dictionary<string, ShaderPipelineResource>(StringComparer.Ordinal);

        foreach (var resource in definition.Resources) {
            if (!resources.TryAdd(key: resource.Name, value: resource)) {
                Add(diagnostics, "SHADERPIPE_DUPLICATE_RESOURCE", $"Resource '{resource.Name}' is declared more than once.", resource.Name);
                continue;
            }
            ValidateResource(resource: resource, diagnostics: diagnostics);
        }

        var passNames = new HashSet<string>(StringComparer.Ordinal);
        var writers = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pass in definition.Passes) {
            if (!passNames.Add(item: pass.Name)) {
                Add(diagnostics, "SHADERPIPE_DUPLICATE_PASS", $"Pass '{pass.Name}' is declared more than once.", pass.Name);
            }
            if (string.IsNullOrWhiteSpace(value: pass.Name) || string.IsNullOrWhiteSpace(value: pass.Source) || string.IsNullOrWhiteSpace(value: pass.EntryPoint)) {
                Add(diagnostics, "SHADERPIPE_PASS_SHAPE", $"Pass '{pass.Name}' requires a name, shader source, and entry point.", pass.Name);
            }
            ValidateConfig(config: pass.Config, owner: $"pass '{pass.Name}'", diagnostics: diagnostics);
            if (!Enum.IsDefined(pass.Kind)) {
                Add(diagnostics, "SHADERPIPE_PASS_KIND", $"Pass '{pass.Name}' has an unsupported pass kind '{pass.Kind}'.", pass.Name);
            }
            if ((pass.Kind == ShaderPipelinePassKind.Fullscreen) && (pass.OutputReferences.Count != 1)) {
                Add(diagnostics, "SHADERPIPE_UNSUPPORTED_MRT", $"Fullscreen pass '{pass.Name}' declares {pass.OutputReferences.Count} outputs; the current runtime supports exactly one color target per fullscreen pass.", pass.Name);
            }
            if ((pass.Kind == ShaderPipelinePassKind.Compute) && ((pass.GroupSizeX == 0) || (pass.GroupSizeY == 0) || (pass.GroupSizeZ == 0))) {
                Add(diagnostics, "SHADERPIPE_WORKGROUP", $"Compute pass '{pass.Name}' requires non-zero GroupSizeX, GroupSizeY, and GroupSizeZ.", pass.Name);
            }
            if (pass.InputReferences.Count > m_limits.MaxInputsPerPass) {
                Add(diagnostics, "SHADERPIPE_LIMIT_INPUTS", $"Pass '{pass.Name}' declares {pass.InputReferences.Count} inputs; the limit is {m_limits.MaxInputsPerPass}.", pass.Name);
            }
            if (pass.OutputReferences.Count > m_limits.MaxOutputsPerPass) {
                Add(diagnostics, "SHADERPIPE_LIMIT_OUTPUTS", $"Pass '{pass.Name}' declares {pass.OutputReferences.Count} outputs; the limit is {m_limits.MaxOutputsPerPass}.", pass.Name);
            }

            var bindings = new HashSet<string>(StringComparer.Ordinal);
            var bindingNumbers = new HashSet<uint>();
            var outputs = pass.OutputReferences.Select(static output => output.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var input in pass.InputReferences) {
                if (!bindings.Add(item: input.Name)) {
                    Add(diagnostics, "SHADERPIPE_DUPLICATE_BINDING", $"Pass '{pass.Name}' binds resource '{input.Name}' more than once.", pass.Name);
                }
                if (input.Binding is { } binding && !bindingNumbers.Add(item: binding)) {
                    Add(diagnostics, "SHADERPIPE_DUPLICATE_BINDING", $"Pass '{pass.Name}' uses descriptor binding {binding} more than once.", pass.Name);
                }
                if (!input.PreviousFrame && outputs.Contains(input.Name)) {
                    Add(diagnostics, "SHADERPIPE_SAME_PASS_FEEDBACK", $"Pass '{pass.Name}' reads and writes resource '{input.Name}' in the same frame; mark the input previousFrame for explicit feedback.", input.Name);
                }
                if (!resources.TryGetValue(key: input.Name, value: out var resource)) {
                    Add(diagnostics, "SHADERPIPE_UNKNOWN_RESOURCE", $"Pass '{pass.Name}' reads undeclared resource '{input.Name}'.", input.Name);
                } else if ((pass.Kind == ShaderPipelinePassKind.Fullscreen) && (resource.Kind == ShaderPipelineResourceKind.Buffer)) {
                    Add(diagnostics, "SHADERPIPE_UNSUPPORTED_FULLSCREEN_BUFFER", $"Fullscreen pass '{pass.Name}' reads buffer '{input.Name}'; the current graphics binding contract supports sampled images only.", input.Name);
                } else if (input.PreviousFrame && (!resource.History || !resource.Persistent)) {
                    Add(diagnostics, "SHADERPIPE_FEEDBACK_DECLARATION", $"Pass '{pass.Name}' reads '{input.Name}' from the previous frame, but that resource is not declared persistent history.", input.Name);
                }
            }

            foreach (var output in pass.OutputReferences) {
                if (!resources.ContainsKey(key: output.Name)) {
                    Add(diagnostics, "SHADERPIPE_UNKNOWN_RESOURCE", $"Pass '{pass.Name}' writes undeclared resource '{output.Name}'.", output.Name);
                } else if ((pass.Kind == ShaderPipelinePassKind.Fullscreen) && (resources[output.Name].Kind != ShaderPipelineResourceKind.Image)) {
                    Add(diagnostics, "SHADERPIPE_UNSUPPORTED_FULLSCREEN_OUTPUT", $"Fullscreen pass '{pass.Name}' writes '{output.Name}', which is not an image color target.", output.Name);
                } else if (resources[output.Name].IsExternal) {
                    Add(diagnostics, "SHADERPIPE_EXTERNAL_WRITE", $"Pass '{pass.Name}' writes external resource '{output.Name}'; external resources are host inputs and cannot have a pass writer.", output.Name);
                }
                if (!writers.TryAdd(key: output.Name, value: pass.Name)) {
                    Add(diagnostics, "SHADERPIPE_SINGLE_WRITER", $"Resource '{output.Name}' is written by both pass '{writers[output.Name]}' and pass '{pass.Name}'.", output.Name);
                }
                if (output.PreviousFrame) {
                    Add(diagnostics, "SHADERPIPE_FEEDBACK_OUTPUT", $"Pass '{pass.Name}' output '{output.Name}' cannot be marked previousFrame; feedback is an input property.", output.Name);
                }
            }
        }

        foreach (var resource in definition.Resources) {
            var hasWriter = writers.ContainsKey(key: resource.Name);
            var hasInitialContents = (resource.Initialization != ShaderPipelineInitialization.Undefined);

            if (!hasWriter && !hasInitialContents) {
                Add(diagnostics, "SHADERPIPE_UNINITIALIZED_RESOURCE", $"Resource '{resource.Name}' has no writer or initialization.", resource.Name);
            }
            if (resource.History && !resource.Persistent) {
                Add(diagnostics, "SHADERPIPE_HISTORY_PERSISTENCE", $"Resource '{resource.Name}' declares history but is not persistent.", resource.Name);
            }
        }

        var outputNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var output in definition.Outputs) {
            if (!outputNames.Add(item: output.Name)) {
                Add(diagnostics, "SHADERPIPE_DUPLICATE_OUTPUT", $"Pipeline output '{output.Name}' is declared more than once.", output.Name);
            }
            if (!resources.ContainsKey(key: output.Resource.Name)) {
                Add(diagnostics, "SHADERPIPE_UNKNOWN_RESOURCE", $"Pipeline output '{output.Name}' references undeclared resource '{output.Resource.Name}'.", output.Resource.Name);
            }
            if (output.Resource.PreviousFrame) {
                Add(diagnostics, "SHADERPIPE_FEEDBACK_OUTPUT", $"Pipeline output '{output.Name}' cannot expose a previous-frame resource reference.", output.Name);
            }
        }
    }

    private static void ValidateResource(ShaderPipelineResource resource, List<ShaderPipelineDiagnostic> diagnostics) {
        if (string.IsNullOrWhiteSpace(value: resource.Name)) {
            Add(diagnostics, "SHADERPIPE_RESOURCE_NAME", "Resource name must not be empty.", resource.Name);
        }
        if ((resource.Kind is ShaderPipelineResourceKind.Image or ShaderPipelineResourceKind.Depth) && string.IsNullOrWhiteSpace(value: resource.Format)) {
            Add(diagnostics, "SHADERPIPE_RESOURCE_FORMAT", $"Resource '{resource.Name}' requires a format.", resource.Name);
        }
        if ((resource.Kind is ShaderPipelineResourceKind.Image or ShaderPipelineResourceKind.Depth) &&
            ((resource.Dimensions is null) || !double.IsFinite(resource.Dimensions.Width) || !double.IsFinite(resource.Dimensions.Height) ||
             (resource.Dimensions.Width <= 0) || (resource.Dimensions.Height <= 0))) {
            Add(diagnostics, "SHADERPIPE_RESOURCE_DIMENSIONS", $"Image resource '{resource.Name}' requires non-zero dimensions.", resource.Name);
        }
        if ((resource.Kind == ShaderPipelineResourceKind.Buffer) && (resource.SizeBytes is null or 0)) {
            Add(diagnostics, "SHADERPIPE_BUFFER_SIZE", $"Buffer resource '{resource.Name}' requires a non-zero sizeBytes.", resource.Name);
        }
        if (resource.ElementType is not null && resource.StrideBytes is not null && resource.StrideBytes < resource.ElementType.Value.SizeBytes()) {
            Add(diagnostics, "SHADERPIPE_BUFFER_STRIDE", $"Buffer resource '{resource.Name}' strideBytes {resource.StrideBytes} is smaller than its element type size.", resource.Name);
        }
        if (!Enum.IsDefined(value: resource.Kind)) {
            Add(diagnostics, "SHADERPIPE_RESOURCE_KIND", $"Resource '{resource.Name}' has an unknown resource kind.", resource.Name);
        } else if (resource.Kind == ShaderPipelineResourceKind.Depth) {
            Add(diagnostics, "SHADERPIPE_UNSUPPORTED_DEPTH", $"Resource '{resource.Name}' is a depth attachment; depth resources are reserved in the schema but the current runtime has no depth attachment or depth sampling contract.", resource.Name);
        }
    }

    private static void ValidateConfig(IReadOnlyDictionary<string, ShaderConfigField>? config, string owner, List<ShaderPipelineDiagnostic> diagnostics) {
        if (config is null) {
            return;
        }
        try {
            ShaderConfigBinding.ValidateSchema(schema: config, ownerName: owner);
        } catch (InvalidDataException exception) {
            Add(diagnostics, "SHADERPIPE_CONFIG_SCHEMA", exception.Message, owner);
        }
    }

    private static List<int> TopologicalOrder(ShaderPipelineDefinition definition, IReadOnlyList<HashSet<int>> dependencies, List<ShaderPipelineDiagnostic> diagnostics) {
        var remaining = dependencies.Select(static set => set.Count).ToArray();
        var dependents = Enumerable.Range(start: 0, count: definition.Passes.Count).Select(static _ => new List<int>()).ToArray();

        for (var pass = 0; pass < dependencies.Count; pass++) {
            foreach (var dependency in dependencies[pass]) {
                dependents[dependency].Add(item: pass);
            }
        }

        var ready = new SortedSet<int>(dependencies.Select((set, index) => (set, index)).Where(static pair => pair.set.Count == 0).Select(static pair => pair.index));
        var order = new List<int>(capacity: definition.Passes.Count);

        while (ready.Count != 0) {
            var pass = ready.Min;
            ready.Remove(pass);
            order.Add(item: pass);

            foreach (var dependent in dependents[pass]) {
                if (--remaining[dependent] == 0) {
                    ready.Add(item: dependent);
                }
            }
        }

        if (order.Count == definition.Passes.Count) {
            return order;
        }

        var cycle = FindCycle(definition: definition, dependencies: dependencies);
        Add(diagnostics, "SHADERPIPE_CYCLE", $"Same-frame pass dependency cycle: {string.Join(separator: " -> ", values: cycle)}.", cycle.FirstOrDefault());
        return order;
    }

    private static IReadOnlyList<string> FindCycle(ShaderPipelineDefinition definition, IReadOnlyList<HashSet<int>> dependencies) {
        var state = new int[dependencies.Count];
        var stack = new List<int>();

        for (var start = 0; start < dependencies.Count; start++) {
            if (state[start] == 0 && Visit(start)) {
                var first = stack.IndexOf(stack[^1]);
                return stack.Skip(Math.Max(0, first)).Select(index => definition.Passes[index].Name).ToArray();
            }
        }

        return definition.Passes.Select(static pass => pass.Name).ToArray();

        bool Visit(int pass) {
            state[pass] = 1;
            stack.Add(item: pass);

            foreach (var dependency in dependencies[pass].OrderBy(static index => index)) {
                if (state[dependency] == 1) {
                    stack.Add(item: dependency);
                    return true;
                }
                if ((state[dependency] == 0) && Visit(dependency)) {
                    return true;
                }
            }

            stack.RemoveAt(index: stack.Count - 1);
            state[pass] = 2;
            return false;
        }
    }

    private static void Add(List<ShaderPipelineDiagnostic> diagnostics, string code, string message, string? name) =>
        diagnostics.Add(item: new ShaderPipelineDiagnostic(Code: code, Message: message, Name: name));
}


