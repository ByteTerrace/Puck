using Puck.Abstractions.Gpu;
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

        definition = definition with { Passes = definition.Passes.Select(ResolveBindings).ToArray() };
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

        // Liveness follows both current- and previous-frame reads. A history writer is needed for future
        // frames even when no current-frame pass consumes its result. External writes are not admitted.
        var livePasses = new HashSet<int>();
        var liveResources = definition.Outputs.Select(static output => output.Resource.Name).ToHashSet(StringComparer.Ordinal);
        var pendingResources = new Stack<string>(liveResources);
        while (pendingResources.TryPop(out var resourceName)) {
            if (!writerByResource.TryGetValue(resourceName, out var writer) || !livePasses.Add(writer)) { continue; }
            var pass = definition.Passes[writer];
            foreach (var output in pass.OutputReferences) { liveResources.Add(output.Name); }
            foreach (var input in pass.InputReferences) {
                if (liveResources.Add(input.Name)) { pendingResources.Push(input.Name); }
            }
        }
        order.RemoveAll(pass => !livePasses.Contains(pass));
        if (order.Count == 0) {
            Add(diagnostics, "SHADERPIPE_NO_LIVE_PASSES", "Pipeline outputs have no executable writer pass; initialized-only graphs are not supported.", definition.Name);
            throw new ShaderPipelineCompilationException(diagnostics);
        }
        var ordinal = new Dictionary<int, int>();

        for (var index = 0; index < order.Count; index++) {
            ordinal[order[index]] = index;
        }

        var plannedPasses = new List<ShaderPipelinePlannedPass>(order.Count);
        for (var index = 0; index < order.Count; index++) {
            var passIndex = order[index];
            var parameters = ShaderPipelineParameterLayout.Resolve(definition.Passes[passIndex]);
            var configBytes = parameters.SizeBytes - ShaderPipelineParameterLayout.FramePrefixBytes;
            if (configBytes > m_limits.MaxConfigConstantBytes) {
                Add(diagnostics, "SHADERPIPE_PUSH_CONSTANT_LIMIT", $"Pass '{definition.Passes[passIndex].Name}' config block is {configBytes} bytes; the portable limit is {m_limits.MaxConfigConstantBytes} bytes after the {ShaderPipelineParameterLayout.FramePrefixBytes}-byte frame prefix.", definition.Passes[passIndex].Name);
            }
            plannedPasses.Add(new ShaderPipelinePlannedPass(
                Declaration: definition.Passes[passIndex],
                Index: index,
                Dependencies: new ReadOnlyCollection<int>(dependencies[passIndex].OrderBy(value => ordinal[value]).Select(value => ordinal[value]).ToList()),
                Parameters: parameters));
        }
        if (diagnostics.Count != 0) {
            throw new ShaderPipelineCompilationException(diagnostics);
        }
        var firstUse = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastUse = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pass in plannedPasses) {
            foreach (var reference in pass.Declaration.InputReferences.Concat(pass.Declaration.OutputReferences)) {
                firstUse.TryAdd(reference.Name, pass.Index);
                lastUse[reference.Name] = pass.Index;
            }
        }
        // Public outputs remain live through publication; history and persistent resources also outlive passes.
        foreach (var output in definition.Outputs) { lastUse[output.Resource.Name] = plannedPasses.Count; }
        var plannedResources = resourceByName.Values.Where(resource => liveResources.Contains(resource.Name))
            .OrderBy(static resource => resource.Name, StringComparer.Ordinal)
            .Select(resource => new ShaderPipelinePlannedResource(
                Declaration: resource,
                WriterPassIndex: writerByResource.TryGetValue(key: resource.Name, value: out var writer) ? ordinal[writer] : -1,
                FirstUsePassIndex: firstUse.GetValueOrDefault(resource.Name, -1),
                LastUsePassIndex: resource.Persistent ? plannedPasses.Count : lastUse.GetValueOrDefault(resource.Name, -1)
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

    // Assign omitted descriptors once, before making the immutable plan. Explicit slots are reserved first,
    // so a late explicit binding never collides with an earlier implicit one. Compute outputs lead inputs;
    // fullscreen outputs are attachments, not descriptors.
    private static ShaderPipelinePass ResolveBindings(ShaderPipelinePass pass) {
        var used = pass.InputReferences.Concat(pass.Kind == ShaderPipelinePassKind.Compute ? pass.OutputReferences : [])
            .Where(static reference => reference.Binding.HasValue).Select(static reference => reference.Binding!.Value).ToHashSet();
        uint next = 0;
        ResourceReference Resolve(ResourceReference reference) {
            if (reference.Binding.HasValue) { return reference; }
            while (used.Contains(next)) { next = checked(next + 1); }
            used.Add(next);
            return reference with { Binding = next };
        }
        var outputs = pass.Kind == ShaderPipelinePassKind.Compute
            ? pass.OutputReferences.Select(Resolve).ToArray()
            : pass.OutputReferences.ToArray();
        return pass with { Outputs = outputs, Inputs = pass.InputReferences.Select(Resolve).ToArray() };
    }
    private static void ValidateLimits(ShaderPipelineLimits limits) {
        if ((limits.MaxResources <= 0) || (limits.MaxPasses <= 0) || (limits.MaxInputsPerPass <= 0) || (limits.MaxOutputsPerPass <= 0) ||
            (limits.MaxConfigConstantBytes == 0) || (limits.MaxComputeWorkGroupSizeX == 0) || (limits.MaxComputeWorkGroupSizeY == 0) || (limits.MaxComputeWorkGroupSizeZ == 0) ||
            (limits.MaxComputeWorkGroupInvocations == 0)) {
            throw new ArgumentOutOfRangeException(nameof(limits), "Shader pipeline limits must be positive.");
        }
    }

    private static bool ExceedsInvocationLimit(uint x, uint y, uint z, uint limit) {
        if (x == 0 || y == 0 || z == 0) { return false; }
        var xy = (ulong)x * y;
        return (xy > limit) || (z > (limit / xy));
    }
    private void ValidateDefinition(ShaderPipelineDefinition definition, List<ShaderPipelineDiagnostic> diagnostics) {
        if (definition.Resources is null || definition.Passes is null || definition.Outputs is null ||
            definition.Resources.Any(static resource => resource is null || string.IsNullOrWhiteSpace(resource.Name)) ||
            definition.Passes.Any(static pass => pass is null || string.IsNullOrWhiteSpace(pass.Name) ||
                pass.InputReferences.Concat(pass.OutputReferences).Any(static reference => reference is null || string.IsNullOrWhiteSpace(reference.Name))) ||
            definition.Outputs.Any(static output => output is null || string.IsNullOrWhiteSpace(output.Name) || output.Resource is null || string.IsNullOrWhiteSpace(output.Resource.Name))) {
            Add(diagnostics, "SHADERPIPE_DOCUMENT_SHAPE", "Pipeline resources, passes, outputs and resource references must be non-null and named.", definition.Name);
            return;
        }
        if (!string.Equals(a: definition.Schema, b: ShaderPipelineSchemas.Pipeline, comparisonType: StringComparison.Ordinal)) {
            Add(diagnostics, "SHADERPIPE_SCHEMA", $"Pipeline '{definition.Name}' declares schema '{definition.Schema}'; expected '{ShaderPipelineSchemas.Pipeline}'.", definition.Name);
        }
        if (string.IsNullOrWhiteSpace(value: definition.Name)) {
            Add(diagnostics, "SHADERPIPE_NAME", "Pipeline name must not be empty.", definition.Name);
        }
        ValidateConfig(config: definition.Config, owner: "pipeline", diagnostics: diagnostics);
        if (definition.Config is { Count: > 0 }) {
            Add(diagnostics, "SHADERPIPE_GLOBAL_CONFIG_UNSUPPORTED", "Pipeline-level config is not consumed by per-pass runtime parameter blocks; declare config on each pass.", definition.Name);
        }

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
            if (pass.OutputReferences.Count == 0) {
                Add(diagnostics, "SHADERPIPE_PASS_OUTPUTS", "Pass " + pass.Name + " declares no outputs and cannot participate in the executable graph.", pass.Name);
            }
            if (!Enum.IsDefined(pass.Kind)) {
                Add(diagnostics, "SHADERPIPE_PASS_KIND", $"Pass '{pass.Name}' has an unsupported pass kind '{pass.Kind}'.", pass.Name);
            }
            if ((pass.Kind == ShaderPipelinePassKind.Fullscreen) && (pass.OutputReferences.Count != 1)) {
                Add(diagnostics, "SHADERPIPE_UNSUPPORTED_MRT", $"Fullscreen pass '{pass.Name}' declares {pass.OutputReferences.Count} outputs; the current runtime supports exactly one color target per fullscreen pass.", pass.Name);
            }
            if ((pass.Kind == ShaderPipelinePassKind.Compute) && ((pass.GroupSizeX == 0) || (pass.GroupSizeY == 0) || (pass.GroupSizeZ == 0))) {
                Add(diagnostics, "SHADERPIPE_WORKGROUP", $"Compute pass '{pass.Name}' requires non-zero GroupSizeX, GroupSizeY, and GroupSizeZ.", pass.Name);
            }
            if (pass.Kind == ShaderPipelinePassKind.Compute) {
                if (pass.GroupSizeX > m_limits.MaxComputeWorkGroupSizeX || pass.GroupSizeY > m_limits.MaxComputeWorkGroupSizeY || pass.GroupSizeZ > m_limits.MaxComputeWorkGroupSizeZ) {
                    Add(diagnostics, "SHADERPIPE_WORKGROUP_LIMIT", $"Compute pass '{pass.Name}' workgroup dimensions {pass.GroupSizeX}x{pass.GroupSizeY}x{pass.GroupSizeZ} exceed the portable limits {m_limits.MaxComputeWorkGroupSizeX}x{m_limits.MaxComputeWorkGroupSizeY}x{m_limits.MaxComputeWorkGroupSizeZ}.", pass.Name);
                }
                if (ExceedsInvocationLimit(pass.GroupSizeX, pass.GroupSizeY, pass.GroupSizeZ, m_limits.MaxComputeWorkGroupInvocations)) {
                    Add(diagnostics, "SHADERPIPE_WORKGROUP_INVOCATIONS", $"Compute pass '{pass.Name}' workgroup {pass.GroupSizeX}x{pass.GroupSizeY}x{pass.GroupSizeZ} exceeds the portable limit of {m_limits.MaxComputeWorkGroupInvocations} invocations.", pass.Name);
                }
            }
            if (pass.InputReferences.Count > m_limits.MaxInputsPerPass) {
                Add(diagnostics, "SHADERPIPE_LIMIT_INPUTS", $"Pass '{pass.Name}' declares {pass.InputReferences.Count} inputs; the limit is {m_limits.MaxInputsPerPass}.", pass.Name);
            }
            if (pass.OutputReferences.Count > m_limits.MaxOutputsPerPass) {
                Add(diagnostics, "SHADERPIPE_LIMIT_OUTPUTS", $"Pass '{pass.Name}' declares {pass.OutputReferences.Count} outputs; the limit is {m_limits.MaxOutputsPerPass}.", pass.Name);
            }

            if (pass.Kind == ShaderPipelinePassKind.Fullscreen) {
                for (var inputIndex = 0; inputIndex < pass.InputReferences.Count; inputIndex++) {
                    if (pass.InputReferences[inputIndex].Binding is { } binding && binding != (uint)inputIndex) {
                        Add(diagnostics, "SHADERPIPE_FULLSCREEN_BINDING", $"Fullscreen pass '{pass.Name}' inputs must use consecutive descriptor bindings in input order, starting at zero.", pass.Name);
                    }
                }
                if (pass.OutputReferences.Any(static output => output.Binding.HasValue)) {
                    Add(diagnostics, "SHADERPIPE_FULLSCREEN_ATTACHMENT_BINDING", $"Fullscreen pass '{pass.Name}' color output is an attachment and cannot declare a descriptor binding.", pass.Name);
                }
            }
            var bindings = new HashSet<(string Name, bool PreviousFrame)>();
            var bindingNumbers = new HashSet<uint>();
            var outputs = pass.OutputReferences.Select(static output => output.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var input in pass.InputReferences) {
                if (!bindings.Add(item: (input.Name, input.PreviousFrame))) {
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
                } else if (input.PreviousFrame && resource.IsExternal) {
                    Add(diagnostics, "SHADERPIPE_EXTERNAL_HISTORY", "Previous-frame reads from external resources are unsupported because host bindings have no history slots.", input.Name);
                } else if (input.PreviousFrame && (!resource.History || !resource.Persistent || resource.Initialization == ShaderPipelineInitialization.Undefined)) {
                    Add(diagnostics, "SHADERPIPE_FEEDBACK_DECLARATION", $"Pass '{pass.Name}' reads '{input.Name}' from the previous frame, but that resource requires persistent history and explicit initialization.", input.Name);
                }
            }

            if (pass.Language == ShaderSourceLanguage.ShadertoyGlsl &&
                (pass.Kind != ShaderPipelinePassKind.Compute || pass.OutputReferences.Count != 1 ||
                 pass.OutputReferences[0].Binding is not (null or 0) || pass.InputReferences.Any(static input => input.Binding == 0) ||
                 pass.InputReferences.Concat(pass.OutputReferences).Any(reference => resources.TryGetValue(reference.Name, out var resource) && resource.Kind != ShaderPipelineResourceKind.Image))) {
                Add(diagnostics, "SHADERPIPE_SHADERTOY_BINDINGS", $"Shadertoy pass '{pass.Name}' requires one image output at binding 0 and image inputs at other bindings.", pass.Name);
            }
            if (!Enum.IsDefined(pass.Language)) {
                Add(diagnostics, "SHADERPIPE_SOURCE_LANGUAGE", $"Pass '{pass.Name}' has an unsupported shader source language.", pass.Name);
            }
            foreach (var output in pass.OutputReferences) {
                if (pass.Kind == ShaderPipelinePassKind.Compute && output.Binding is { } binding && !bindingNumbers.Add(binding)) {
                    Add(diagnostics, "SHADERPIPE_DUPLICATE_BINDING", $"Pass '{pass.Name}' uses descriptor binding {binding} more than once.", pass.Name);
                }
                if (!resources.ContainsKey(key: output.Name)) {
                    Add(diagnostics, "SHADERPIPE_UNKNOWN_RESOURCE", $"Pass '{pass.Name}' writes undeclared resource '{output.Name}'.", output.Name);
                } else if ((pass.Kind == ShaderPipelinePassKind.Fullscreen) && (resources[output.Name].Kind != ShaderPipelineResourceKind.Image)) {
                    Add(diagnostics, "SHADERPIPE_UNSUPPORTED_FULLSCREEN_OUTPUT", $"Fullscreen pass '{pass.Name}' writes '{output.Name}', which is not an image color target.", output.Name);
                } else if (resources[output.Name].IsExternal) {
                    Add(diagnostics, "SHADERPIPE_EXTERNAL_WRITE", $"Pass '{pass.Name}' writes external resource '{output.Name}'; external resources are host inputs and cannot have a pass writer.", output.Name);
                }
                if (resources.TryGetValue(key: output.Name, value: out var outputResource)) {
                    if ((pass.Kind == ShaderPipelinePassKind.Fullscreen) && !string.Equals(outputResource.Format, "R8G8B8A8Unorm", StringComparison.OrdinalIgnoreCase)) {
                        Add(diagnostics, "SHADERPIPE_FULLSCREEN_FORMAT", "Fullscreen pass " + pass.Name + " writes format " + outputResource.Format + "; the current graphics service creates R8G8B8A8Unorm targets.", output.Name);
                    }
                    if ((pass.Language == ShaderSourceLanguage.ShadertoyGlsl) && string.Equals(outputResource.Format, "B8G8R8A8Unorm", StringComparison.OrdinalIgnoreCase)) {
                        Add(diagnostics, "SHADERPIPE_SHADERTOY_FORMAT", "Shadertoy pass " + pass.Name + " cannot write B8G8R8A8Unorm through the adapter.", output.Name);
                    }
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
            if (output.Resource.Binding is not null) {
                Add(diagnostics, "SHADERPIPE_OUTPUT_BINDING", "Pipeline outputs cannot declare descriptor bindings; bindings belong to pass references.", output.Name);
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

        if (!Enum.IsDefined(value: resource.Initialization)) {
            Add(diagnostics, "SHADERPIPE_INITIALIZATION", $"Resource {resource.Name} has an unsupported initialization mode {resource.Initialization}.", resource.Name);
        }
        var kindDefined = Enum.IsDefined(value: resource.Kind);
        if (!kindDefined) {
            Add(diagnostics, "SHADERPIPE_RESOURCE_KIND", $"Resource '{resource.Name}' has an unknown resource kind.", resource.Name);
            return;
        }
        if (resource.Dimensions is { } dimensions && !Enum.IsDefined(value: dimensions.Mode)) {
            Add(diagnostics, "SHADERPIPE_DIMENSION_MODE", $"Resource '{resource.Name}' has an unsupported dimension mode '{dimensions.Mode}'.", resource.Name);
        }

        if (resource.Kind is ShaderPipelineResourceKind.Image or ShaderPipelineResourceKind.Depth) {
            if (string.IsNullOrWhiteSpace(value: resource.Format)) {
                Add(diagnostics, "SHADERPIPE_RESOURCE_FORMAT", $"Resource '{resource.Name}' requires a format.", resource.Name);
            } else if (resource.Kind == ShaderPipelineResourceKind.Image &&
                       (!Enum.TryParse<GpuPixelFormat>(resource.Format, ignoreCase: true, out var format) || !Enum.IsDefined(value: format))) {
                Add(diagnostics, "SHADERPIPE_RESOURCE_FORMAT", $"Resource '{resource.Name}' has unsupported image format '{resource.Format}'.", resource.Name);
            }
            if ((resource.Dimensions is null) || !double.IsFinite(resource.Dimensions.Width) || !double.IsFinite(resource.Dimensions.Height) ||
                (resource.Dimensions.Width <= 0) || (resource.Dimensions.Height <= 0)) {
                Add(diagnostics, "SHADERPIPE_RESOURCE_DIMENSIONS", $"Image resource '{resource.Name}' requires non-zero dimensions.", resource.Name);
            }
            if (resource.SizeBytes is not null || resource.ElementType is not null || resource.StrideBytes is not null) {
                Add(diagnostics, "SHADERPIPE_RESOURCE_KIND_FIELDS", $"Image/depth resource '{resource.Name}' cannot declare buffer fields sizeBytes, elementType, or strideBytes.", resource.Name);
            }
        } else {
            if (resource.SizeBytes is null or 0) {
                Add(diagnostics, "SHADERPIPE_BUFFER_SIZE", $"Buffer resource '{resource.Name}' requires a non-zero sizeBytes.", resource.Name);
            } else if ((resource.SizeBytes.Value & 3) != 0) {
                Add(diagnostics, "SHADERPIPE_BUFFER_ALIGNMENT", $"Buffer resource '{resource.Name}' sizeBytes must be divisible by four for backend zero initialization.", resource.Name);
            }
            if (resource.Format is not null) {
                Add(diagnostics, "SHADERPIPE_RESOURCE_KIND_FIELDS", $"Buffer resource '{resource.Name}' cannot declare image format.", resource.Name);
            }
            if (resource.Dimensions is not null) {
                Add(diagnostics, "SHADERPIPE_RESOURCE_KIND_FIELDS", $"Buffer resource '{resource.Name}' cannot declare image dimensions.", resource.Name);
            }
            if (resource.ElementType is not null || resource.StrideBytes is not null) {
                Add(diagnostics, "SHADERPIPE_UNSUPPORTED_TYPED_BUFFER", $"Buffer resource '{resource.Name}' declares typed layout metadata that the runtime does not bind.", resource.Name);
            }
            if (resource.StrideBytes is not null) {
                Add(diagnostics, "SHADERPIPE_BUFFER_STRIDE", $"Buffer resource '{resource.Name}' declares strideBytes, but the runtime does not bind typed buffer strides.", resource.Name);
            }
        }

        if (resource.Kind == ShaderPipelineResourceKind.Depth) {
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


