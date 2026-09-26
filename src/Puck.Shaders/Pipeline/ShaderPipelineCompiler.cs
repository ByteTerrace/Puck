using Puck.Abstractions.Gpu;
using System.Collections.ObjectModel;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>The one pass planner: validates a graph's passes and versions and compiles its same-frame dependency graph
/// into an immutable plan. <see cref="RenderGraphCompiler"/> checks a graph document's schema tag and packages and plans
/// through it. This class does not load source, invoke a compiler, or create GPU objects.</summary>
public sealed partial class ShaderPipelineCompiler {
    private readonly ShaderPipelineLimits m_limits;

    /// <summary>Initializes a planner with the documented default resource limits.</summary>
    public ShaderPipelineCompiler(ShaderPipelineLimits? limits = null) {
        m_limits = (limits ?? new ShaderPipelineLimits());
        ValidateLimits(limits: m_limits);
    }

    private static void Add(List<ShaderPipelineDiagnostic> diagnostics, string code, string message, string? name) =>
        diagnostics.Add(item: new ShaderPipelineDiagnostic(
            Code: code,
            Message: message,
            Name: name
        ));
    private static bool ExceedsInvocationLimit(uint x, uint y, uint z, uint limit) {
        if (
            (x == 0) ||
            (y == 0) ||
            (z == 0)
        ) { return false; }
        var xy = (((ulong)x) * y);

        return (
            (xy > limit) ||
            (z > (limit / xy))
        );
    }
    private static IReadOnlyList<string> FindCycle(RenderGraphDefinition definition, IReadOnlyList<HashSet<int>> dependencies) {
        var state = new int[dependencies.Count];
        var stack = new List<int>();

        for (var start = 0; (start < dependencies.Count); start++) {
            if (
                (state[start] == 0) &&
                Visit(pass: start)
            ) {
                var first = stack.IndexOf(item: stack[^1]);

                return stack.Skip(count: Math.Max(
                    val1: 0,
                    val2: first
                )).Select(selector: index => definition.ShaderPasses[index].Name).ToArray();
            }
        }

        return definition.ShaderPasses.Select(selector: static pass => pass.Name).ToArray();

        bool Visit(int pass) {
            state[pass] = 1;
            stack.Add(item: pass);

            foreach (var dependency in dependencies[pass].OrderBy(keySelector: static index => index)) {
                if (state[dependency] == 1) {
                    stack.Add(item: dependency);
                    return true;
                }
                if (
                    (state[dependency] == 0) &&
                    Visit(pass: dependency)
                ) {
                    return true;
                }
            }

            stack.RemoveAt(index: (stack.Count - 1));
            state[pass] = 2;
            return false;
        }
    }
    private static List<int> TopologicalOrder(RenderGraphDefinition definition, IReadOnlyList<HashSet<int>> dependencies, List<ShaderPipelineDiagnostic> diagnostics) {
        var remaining = dependencies.Select(selector: static set => set.Count).ToArray();
        var dependents = Enumerable.Range(
            start: 0,
            count: definition.ShaderPasses.Count
        ).Select(selector: static _ => new List<int>()).ToArray();

        for (var pass = 0; (pass < dependencies.Count); pass++) {
            foreach (var dependency in dependencies[pass]) {
                dependents[dependency].Add(item: pass);
            }
        }

        var ready = new SortedSet<int>(collection: dependencies.Select(selector: (set, index) => (set, index)).Where(predicate: static pair => (pair.set.Count == 0)).Select(selector: static pair => pair.index));
        var order = new List<int>(capacity: definition.ShaderPasses.Count);

        while (ready.Count != 0) {
            var pass = ready.Min;

            ready.Remove(item: pass);
            order.Add(item: pass);

            foreach (var dependent in dependents[pass]) {
                if (--remaining[dependent] == 0) {
                    ready.Add(item: dependent);
                }
            }
        }

        if (order.Count == definition.ShaderPasses.Count) {
            return order;
        }

        var cycle = FindCycle(
            definition: definition,
            dependencies: dependencies
        );

        Add(
            diagnostics,
            "SHADERPIPE_CYCLE",
            $"Same-frame pass dependency cycle: {string.Join(
                separator: " -> ",
                values: cycle
            )}.",
            cycle.FirstOrDefault()
        );
        return order;
    }
    private static void ValidateConfig(IReadOnlyDictionary<string, ShaderConfigField>? config, string owner, List<ShaderPipelineDiagnostic> diagnostics) {
        if (config is null) {
            return;
        }
        try {
            ShaderConfigBinding.ValidateSchema(
                ownerName: owner,
                schema: config
            );
        } catch (InvalidDataException exception) {
            Add(
                diagnostics,
                "SHADERPIPE_CONFIG_SCHEMA",
                exception.Message,
                owner
            );
        }
    }
    private void ValidateDefinition(RenderGraphDefinition definition, List<ShaderPipelineDiagnostic> diagnostics, IReadOnlySet<string> packages) {
        if (
            (definition.Resources is null) ||
            (definition.Outputs is null) ||
            definition.Resources.Any(predicate: static resource => ((resource is null) || string.IsNullOrWhiteSpace(value: resource.Name))) ||
            definition.ShaderPasses.Any(predicate: static pass => ((pass is null) || string.IsNullOrWhiteSpace(value: pass.Name) ||
                pass.InputReferences.Concat(second: pass.OutputReferences).Any(predicate: static reference => ((reference is null) || string.IsNullOrWhiteSpace(value: reference.Name))))) ||
            definition.Outputs.Any(predicate: static output => string.IsNullOrWhiteSpace(value: output))
        ) {
            Add(
                diagnostics,
                "SHADERPIPE_DOCUMENT_SHAPE",
                "Pipeline resources, passes, outputs and resource references must be non-null and named.",
                definition.Name
            );
            return;
        }
        if (string.IsNullOrWhiteSpace(value: definition.Name)) {
            Add(
                diagnostics,
                "SHADERPIPE_NAME",
                "Pipeline name must not be empty.",
                definition.Name
            );
        }
        if (definition.Resources.Count > m_limits.MaxResources) {
            Add(
                diagnostics,
                "SHADERPIPE_LIMIT_RESOURCES",
                $"Pipeline declares {definition.Resources.Count} resources; the limit is {m_limits.MaxResources}.",
                definition.Name
            );
        }
        if (definition.ShaderPasses.Count > m_limits.MaxPasses) {
            Add(
                diagnostics,
                "SHADERPIPE_LIMIT_PASSES",
                $"Pipeline declares {definition.ShaderPasses.Count} passes; the limit is {m_limits.MaxPasses}.",
                definition.Name
            );
        }

        var resources = new Dictionary<string, ShaderPipelineResource>(comparer: StringComparer.Ordinal);

        foreach (var resource in definition.Resources) {
            if (!resources.TryAdd(
                key: resource.Name,
                value: resource
            )) {
                Add(
                    diagnostics,
                    "SHADERPIPE_DUPLICATE_RESOURCE",
                    $"Resource '{resource.Name}' is declared more than once.",
                    resource.Name
                );
                continue;
            }
            ValidateResource(
                diagnostics: diagnostics,
                resource: resource
            );
        }

        var successors = Successors(definition: definition);
        var passNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var writers = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var pass in definition.ShaderPasses) {
            if (!passNames.Add(item: pass.Name)) {
                Add(
                    diagnostics,
                    "SHADERPIPE_DUPLICATE_PASS",
                    $"Pass '{pass.Name}' is declared more than once.",
                    pass.Name
                );
            }
            if (
                string.IsNullOrWhiteSpace(value: pass.Name) ||
                string.IsNullOrWhiteSpace(value: pass.Source) ||
                (
                    !packages.Contains(item: pass.Name) &&
                    string.IsNullOrWhiteSpace(value: pass.EntryPoint)
                )
            ) {
                Add(
                    diagnostics,
                    "SHADERPIPE_PASS_SHAPE",
                    $"Pass '{pass.Name}' requires a name, shader source, and entry point.",
                    pass.Name
                );
            }
            ValidateConfig(
                config: pass.Config,
                owner: $"pass '{pass.Name}'",
                diagnostics: diagnostics
            );
            if (pass.OutputReferences.Count == 0) {
                Add(
                    diagnostics,
                    "SHADERPIPE_PASS_OUTPUTS",
                    (("Pass " + pass.Name) + " declares no outputs and cannot participate in the executable graph."),
                    pass.Name
                );
            }
            if (!Enum.IsDefined(value: pass.Kind)) {
                Add(
                    diagnostics,
                    "SHADERPIPE_PASS_KIND",
                    $"Pass '{pass.Name}' has an unsupported pass kind '{pass.Kind}'.",
                    pass.Name
                );
            }
            if (
                (pass.Kind == ShaderPipelineDocumentPassKind.Compute) &&
                ((pass.GroupSizeX == 0) || (pass.GroupSizeY == 0) || (pass.GroupSizeZ == 0))
            ) {
                Add(
                    diagnostics,
                    "SHADERPIPE_WORKGROUP",
                    $"Compute pass '{pass.Name}' requires non-zero GroupSizeX, GroupSizeY, and GroupSizeZ.",
                    pass.Name
                );
            }
            if (pass.Kind == ShaderPipelineDocumentPassKind.Compute) {
                if (
                    (pass.GroupSizeX > m_limits.MaxComputeWorkGroupSizeX) ||
                    (pass.GroupSizeY > m_limits.MaxComputeWorkGroupSizeY) ||
                    (pass.GroupSizeZ > m_limits.MaxComputeWorkGroupSizeZ)
                ) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_WORKGROUP_LIMIT",
                        $"Compute pass '{pass.Name}' workgroup dimensions {pass.GroupSizeX}x{pass.GroupSizeY}x{pass.GroupSizeZ} exceed the portable limits {m_limits.MaxComputeWorkGroupSizeX}x{m_limits.MaxComputeWorkGroupSizeY}x{m_limits.MaxComputeWorkGroupSizeZ}.",
                        pass.Name
                    );
                }
                if (ExceedsInvocationLimit(
                    pass.GroupSizeX,
                    pass.GroupSizeY,
                    pass.GroupSizeZ,
                    m_limits.MaxComputeWorkGroupInvocations
                )) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_WORKGROUP_INVOCATIONS",
                        $"Compute pass '{pass.Name}' workgroup {pass.GroupSizeX}x{pass.GroupSizeY}x{pass.GroupSizeZ} exceeds the portable limit of {m_limits.MaxComputeWorkGroupInvocations} invocations.",
                        pass.Name
                    );
                }
            }
            if (pass.InputReferences.Count > m_limits.MaxInputsPerPass) {
                Add(
                    diagnostics,
                    "SHADERPIPE_LIMIT_INPUTS",
                    $"Pass '{pass.Name}' declares {pass.InputReferences.Count} inputs; the limit is {m_limits.MaxInputsPerPass}.",
                    pass.Name
                );
            }
            if (pass.OutputReferences.Count > m_limits.MaxOutputsPerPass) {
                Add(
                    diagnostics,
                    "SHADERPIPE_LIMIT_OUTPUTS",
                    $"Pass '{pass.Name}' declares {pass.OutputReferences.Count} outputs; the limit is {m_limits.MaxOutputsPerPass}.",
                    pass.Name
                );
            }

            if (pass.IsGraphics) {
                if (pass.OutputReferences.Any(predicate: static output => (output.As is not null))) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_GRAPHICS_ATTACHMENT_AS",
                        $"Graphics pass '{pass.Name}' outputs are attachments, which its source never names, so none takes \"as\".",
                        pass.Name
                    );
                }
                ValidateGraphics(
                    diagnostics: diagnostics,
                    pass: pass,
                    resources: resources
                );
            } else {
                ValidateComputeFields(
                    diagnostics: diagnostics,
                    pass: pass
                );
            }
            var package = packages.Contains(item: pass.Name);

            ValidateDispatch(
                diagnostics: diagnostics,
                package: package,
                pass: pass,
                resources: resources
            );

            if (!package) {
                ValidatePackageStorage(
                    diagnostics: diagnostics,
                    pass: pass,
                    resources: resources
                );
            }
            var bindings = new HashSet<(string Name, bool PreviousFrame)>();
            var outputs = pass.OutputReferences.Select(selector: static output => output.Name).ToHashSet(comparer: StringComparer.Ordinal);

            foreach (var input in pass.InputReferences) {
                if (!bindings.Add(item: (input.Name, input.PreviousFrame))) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_DUPLICATE_PORT",
                        $"Pass '{pass.Name}' binds resource '{input.Name}' more than once.",
                        pass.Name
                    );
                }
                if (
                    !input.PreviousFrame &&
                    outputs.Contains(item: input.Name)
                ) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_SAME_PASS_FEEDBACK",
                        $"Pass '{pass.Name}' reads and writes resource '{input.Name}' in the same frame; mark the input previousFrame for explicit feedback.",
                        input.Name
                    );
                }
                if (!resources.TryGetValue(
                    key: input.Name,
                    value: out var resource
                )) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_UNKNOWN_RESOURCE",
                        $"Pass '{pass.Name}' reads undeclared resource '{input.Name}'.",
                        input.Name
                    );
                } else if (resource.Kind == ShaderPipelineResourceKind.Depth) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_DEPTH_SAMPLED",
                        $"Pass '{pass.Name}' reads depth version '{input.Name}'; a depth attachment is tested and written by geometry passes and never sampled.",
                        input.Name
                    );
                } else if (
                    pass.IsGraphics &&
                    (resource.Kind == ShaderPipelineResourceKind.Buffer)
                ) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_UNSUPPORTED_GRAPHICS_BUFFER",
                        $"Graphics pass '{pass.Name}' reads buffer '{input.Name}'; the graphics binding contract supports sampled images only.",
                        input.Name
                    );
                } else if (
                    input.PreviousFrame &&
                    resource.IsExternal
                ) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_EXTERNAL_HISTORY",
                        "Previous-frame reads from external resources are unsupported because host bindings have no history slots.",
                        input.Name
                    );
                } else if (
                    input.PreviousFrame &&
                    successors.ContainsKey(key: input.Name)
                ) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_DISCARDED_READ",
                        $"Pass '{pass.Name}' reads '{input.Name}' from the previous frame, but '{successors[input.Name]}' overwrites it within that frame; read the last version of the chain instead.",
                        input.Name
                    );
                } else if (
                    input.PreviousFrame &&
                    (!resource.History || (ChainRoot(
                        name: input.Name,
                        resources: resources
                    ).Initialization == ShaderPipelineInitialization.Undefined))
                ) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_FEEDBACK_DECLARATION",
                        $"Pass '{pass.Name}' reads '{input.Name}' from the previous frame, but that version must be declared history and its chain's first version must declare an initialization.",
                        input.Name
                    );
                }
            }

            foreach (var output in pass.OutputReferences) {
                if (!resources.ContainsKey(key: output.Name)) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_UNKNOWN_RESOURCE",
                        $"Pass '{pass.Name}' writes undeclared resource '{output.Name}'.",
                        output.Name
                    );
                } else if (
                    pass.IsGraphics &&
                    (resources[output.Name].Kind == ShaderPipelineResourceKind.Buffer)
                ) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_UNSUPPORTED_GRAPHICS_OUTPUT",
                        $"Graphics pass '{pass.Name}' writes buffer '{output.Name}', which is not an attachment.",
                        output.Name
                    );
                } else if (
                    (pass.Kind != ShaderPipelineDocumentPassKind.Geometry) &&
                    (resources[output.Name].Kind == ShaderPipelineResourceKind.Depth)
                ) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_DEPTH_WRITER",
                        $"{pass.Kind} pass '{pass.Name}' writes depth version '{output.Name}'; only a geometry pass writes a depth attachment.",
                        output.Name
                    );
                } else if (resources[output.Name].IsExternal) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_EXTERNAL_WRITE",
                        $"Pass '{pass.Name}' writes external resource '{output.Name}'; external resources are host inputs and cannot have a pass writer.",
                        output.Name
                    );
                }
                if (!writers.TryAdd(
                    key: output.Name,
                    value: pass.Name
                )) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_SINGLE_WRITER",
                        $"Resource '{output.Name}' is written by both pass '{writers[output.Name]}' and pass '{pass.Name}'.",
                        output.Name
                    );
                }
                if (output.PreviousFrame) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_FEEDBACK_OUTPUT",
                        $"Pass '{pass.Name}' output '{output.Name}' cannot be marked previousFrame; feedback is an input property.",
                        output.Name
                    );
                }
            }
        }

        foreach (var resource in definition.Resources) {
            var hasWriter = writers.ContainsKey(key: resource.Name);
            var hasInitialContents = (resource.Initialization != ShaderPipelineInitialization.Undefined);

            if (
                !hasWriter &&
                !hasInitialContents
            ) {
                Add(
                    diagnostics,
                    "SHADERPIPE_UNINITIALIZED_RESOURCE",
                    $"Resource '{resource.Name}' has no writer or initialization.",
                    resource.Name
                );
            }
        }

        var outputNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        // A package's structured or counted storage is published only by the package that writes it, for another
        // instance to read across a buffer edge; the pipeline node publishes raw buffers of fixed size. The writers are
        // the passes whose planned kind is Package: the ones the package entry brought.
        var packageWritten = definition.ShaderPasses
            .Where(predicate: pass => packages.Contains(item: pass.Name))
            .SelectMany(selector: static pass => pass.OutputReferences)
            .Select(selector: static reference => reference.Name)
            .ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var output in definition.Outputs) {
            if (!outputNames.Add(item: output)) {
                Add(
                    code: "SHADERPIPE_DUPLICATE_OUTPUT",
                    diagnostics: diagnostics,
                    message: $"Pipeline output '{output}' is declared more than once.",
                    name: output
                );
            }
            if (!resources.TryGetValue(
                key: output,
                value: out var published
            )) {
                Add(
                    code: "SHADERPIPE_UNKNOWN_RESOURCE",
                    diagnostics: diagnostics,
                    message: $"Pipeline output '{output}' names undeclared version '{output}'.",
                    name: output
                );
            } else if (published.Kind == ShaderPipelineResourceKind.Depth) {
                Add(
                    code: "SHADERPIPE_DEPTH_PUBLIC",
                    diagnostics: diagnostics,
                    message: $"Pipeline output '{output}' is a depth version; a depth attachment is never published.",
                    name: output
                );
            } else if (
                (published.Kind == ShaderPipelineResourceKind.Buffer) &&
                published.IsPackageStorage &&
                !packageWritten.Contains(item: output)
            ) {
                Add(
                    code: "SHADERPIPE_PACKAGE_STORAGE",
                    diagnostics: diagnostics,
                    message: $"Pipeline output '{output}' declares a stride or a count, and no package pass writes it; only the package that writes a package's storage publishes it.",
                    name: output
                );
            }
        }
        ValidateForwards(
            definition: definition,
            diagnostics: diagnostics,
            outputs: outputNames,
            resources: resources,
            successors: successors
        );
    }
    private static void ValidateLimits(ShaderPipelineLimits limits) {
        if (
            (limits.MaxResources <= 0) ||
            (limits.MaxPasses <= 0) ||
            (limits.MaxInputsPerPass <= 0) ||
            (limits.MaxOutputsPerPass <= 0) ||
            (limits.MaxPassBlockBytes == 0) ||
            (limits.MaxComputeWorkGroupSizeX == 0) ||
            (limits.MaxComputeWorkGroupSizeY == 0) ||
            (limits.MaxComputeWorkGroupSizeZ == 0) ||
            (limits.MaxComputeWorkGroupInvocations == 0) ||
            (limits.MaxVertexAttributes <= 0) ||
            (limits.MaxGeometryBytes == 0)
        ) {
            throw new ArgumentOutOfRangeException(
                nameof(limits),
                "Shader pipeline limits must be positive."
            );
        }
    }
    private static void ValidateResource(ShaderPipelineResource resource, List<ShaderPipelineDiagnostic> diagnostics) {
        if (string.IsNullOrWhiteSpace(value: resource.Name)) {
            Add(
                diagnostics,
                "SHADERPIPE_RESOURCE_NAME",
                "Resource name must not be empty.",
                resource.Name
            );
        }

        if (!Enum.IsDefined(value: resource.Initialization)) {
            Add(
                diagnostics,
                "SHADERPIPE_INITIALIZATION",
                $"Resource {resource.Name} has an unsupported initialization mode {resource.Initialization}.",
                resource.Name
            );
        }
        var kindDefined = Enum.IsDefined(value: resource.Kind);

        if (!kindDefined) {
            Add(
                diagnostics,
                "SHADERPIPE_RESOURCE_KIND",
                $"Resource '{resource.Name}' has an unknown resource kind.",
                resource.Name
            );
            return;
        }
        if (
            resource.IsHostBuffer &&
            (
                (resource.Kind != ShaderPipelineResourceKind.Buffer) ||
                (resource.SizeBytes is null) ||
                (resource.Count is not null) ||
                resource.History
            )
        ) {
            Add(
                diagnostics,
                "SHADERPIPE_INITIALIZATION",
                $"Resource '{resource.Name}' is a host buffer port, which is a buffer of a fixed sizeBytes and keeps no history.",
                resource.Name
            );
        }
        if (
            (resource.Dimensions is { } dimensions) &&
            !Enum.IsDefined(value: dimensions.Mode)
        ) {
            Add(
                diagnostics,
                "SHADERPIPE_DIMENSION_MODE",
                $"Resource '{resource.Name}' has an unsupported dimension mode '{dimensions.Mode}'.",
                resource.Name
            );
        }

        if (resource.Kind is ShaderPipelineResourceKind.Image or ShaderPipelineResourceKind.Depth) {
            if (string.IsNullOrWhiteSpace(value: resource.Format)) {
                Add(
                    diagnostics,
                    "SHADERPIPE_RESOURCE_FORMAT",
                    $"Resource '{resource.Name}' requires a format.",
                    resource.Name
                );
            } else if (
                (resource.Kind == ShaderPipelineResourceKind.Image) &&
                (!Enum.TryParse<GpuPixelFormat>(
                resource.Format,
                ignoreCase: true,
                out var format
            ) || !Enum.IsDefined(value: format) || GpuPixelFormats.IsDepth(format: format) || GpuPixelFormats.IsBlockCompressed(format: format))
            ) {
                Add(
                    diagnostics,
                    "SHADERPIPE_RESOURCE_FORMAT",
                    $"Resource '{resource.Name}' has unsupported image format '{resource.Format}'; an image has an uncompressed color format, a depth format belongs to a depth resource, and a block-compressed format is only ever sampled.",
                    resource.Name
                );
            }
            if (
                (resource.Dimensions is null) ||
                !double.IsFinite(d: resource.Dimensions.Width) ||
                !double.IsFinite(d: resource.Dimensions.Height) ||
                (resource.Dimensions.Width <= 0) ||
                (resource.Dimensions.Height <= 0)
            ) {
                Add(
                    diagnostics,
                    "SHADERPIPE_RESOURCE_DIMENSIONS",
                    $"Image resource '{resource.Name}' requires non-zero dimensions.",
                    resource.Name
                );
            }
            if (resource.SizeBytes is not null) {
                Add(
                    diagnostics,
                    "SHADERPIPE_RESOURCE_KIND_FIELDS",
                    $"Image/depth resource '{resource.Name}' cannot declare the buffer field sizeBytes.",
                    resource.Name
                );
            }
            if (resource.Samples != 1) {
                Add(
                    diagnostics,
                    "SHADERPIPE_UNSUPPORTED_SAMPLES",
                    $"Resource '{resource.Name}' declares {resource.Samples} samples; only single-sampled images are executable on every backend.",
                    resource.Name
                );
            }
        } else {
            if (
                (resource.Count is null) &&
                (resource.SizeBytes is null or 0)
            ) {
                Add(
                    diagnostics,
                    "SHADERPIPE_BUFFER_SIZE",
                    $"Buffer resource '{resource.Name}' requires a non-zero sizeBytes or a count.",
                    resource.Name
                );
            } else if (
                (resource.SizeBytes is { } sizeBytes) &&
                ((sizeBytes & 3) != 0)
            ) {
                Add(
                    diagnostics,
                    "SHADERPIPE_BUFFER_ALIGNMENT",
                    $"Buffer resource '{resource.Name}' sizeBytes must be divisible by four for backend zero initialization.",
                    resource.Name
                );
            }
            if (resource.Format is not null) {
                Add(
                    diagnostics,
                    "SHADERPIPE_RESOURCE_KIND_FIELDS",
                    $"Buffer resource '{resource.Name}' cannot declare image format.",
                    resource.Name
                );
            }
            if (resource.Dimensions is not null) {
                Add(
                    diagnostics,
                    "SHADERPIPE_RESOURCE_KIND_FIELDS",
                    $"Buffer resource '{resource.Name}' cannot declare image dimensions.",
                    resource.Name
                );
            }
            if (resource.Samples != 1) {
                Add(
                    diagnostics,
                    "SHADERPIPE_RESOURCE_KIND_FIELDS",
                    $"Buffer resource '{resource.Name}' cannot declare a sample count.",
                    resource.Name
                );
            }
        }
        ValidateBufferLayout(
            diagnostics: diagnostics,
            resource: resource
        );

        if (resource.Kind == ShaderPipelineResourceKind.Depth) {
            ValidateDepth(
                diagnostics: diagnostics,
                resource: resource
            );
        }
    }

    /// <summary>Compiles a graph of shader passes into an execution plan. It checks no schema tag and knows no package; a
    /// graph document plans through <see cref="RenderGraphCompiler"/>, which checks both.</summary>
    /// <param name="definition">The graph.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ShaderPipelineCompilationException">The definition names a package pass, has invalid names,
    /// bindings, initialization or resource declarations, a cycle, or exceeds a plan limit.</exception>
    public ShaderPipelinePlan Compile(RenderGraphDefinition definition) => Compile(
        definition: definition,
        packages: []
    );

    // A frame graph's package passes join its shader passes after them, each in a compute shape beside its port
    // accesses, so one planner orders, versions and barriers both. This is the only way package work enters planning: the graph
    // compiler checks a document's package passes against its host's packages and hands them here, so a definition
    // reaching the planner with its own is refused. The names they bring are the passes whose planned kind is Package.
    // Planning works on a copy whose passes are both, and the plan keeps the graph's shader passes: a package's planned
    // pass carries its step, never its compute shape.
    internal ShaderPipelinePlan Compile(RenderGraphDefinition definition, IReadOnlyList<ShaderPipelinePackagePass> packages) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: packages);
        var diagnostics = new List<ShaderPipelineDiagnostic>();

        if (definition.PackagePasses.Count != 0) {
            Add(
                diagnostics,
                "SHADERPIPE_PACKAGE_PASS",
                $"Graph '{definition.Name}' names package passes, which only a graph compiler over its host's packages plans.",
                definition.Name
            );

            throw new ShaderPipelineCompilationException(diagnostics: diagnostics);
        }

        var graph = definition;
        var packageNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var packageByName = new Dictionary<string, ShaderPipelinePackagePass>(comparer: StringComparer.Ordinal);

        foreach (var package in packages) {
            ArgumentNullException.ThrowIfNull(argument: package, paramName: nameof(packages));

            if (!package.HasValidAccesses) {
                throw new ArgumentException(
                    message: $"Package pass '{package.Name}' must declare one read access per input and one write access per output.",
                    paramName: nameof(packages)
                );
            }

            _ = packageNames.Add(item: package.Name);
            packageByName.TryAdd(
                key: package.Name,
                value: package
            );
        }
        if (packages.Count != 0) {
            definition = definition with { Passes = [.. definition.ShaderPasses, .. packages.Select(selector: static package => package.Shape())] };
        }

        ValidateDefinition(
            definition: definition,
            diagnostics: diagnostics,
            packages: packageNames
        );

        if (diagnostics.Count != 0) {
            throw new ShaderPipelineCompilationException(diagnostics: diagnostics);
        }

        var resourceByName = definition.Resources.ToDictionary(
            keySelector: static resource => resource.Name,
            comparer: StringComparer.Ordinal
        );
        var writerByResource = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        for (var passIndex = 0; (passIndex < definition.ShaderPasses.Count); passIndex++) {
            foreach (var output in definition.ShaderPasses[passIndex].OutputReferences) {
                writerByResource.Add(
                    key: output.Name,
                    value: passIndex
                );
            }
        }

        var dependencies = new List<HashSet<int>>(capacity: definition.ShaderPasses.Count);

        for (var passIndex = 0; (passIndex < definition.ShaderPasses.Count); passIndex++) {
            var passDependencies = new HashSet<int>();

            dependencies.Add(item: passDependencies);

            foreach (var input in ReadsOf(pass: definition.ShaderPasses[passIndex])) {
                if (
                    !input.PreviousFrame &&
                    writerByResource.TryGetValue(
                    key: input.Name,
                    value: out var writer
                ) &&
                    (writer != passIndex)
                ) {
                    passDependencies.Add(item: writer);
                }
            }
        }
        AddForwardDependencies(
            definition: definition,
            dependencies: dependencies,
            writerByResource: writerByResource
        );

        var order = TopologicalOrder(
            definition: definition,
            dependencies: dependencies,
            diagnostics: diagnostics
        );

        if (diagnostics.Count != 0) {
            throw new ShaderPipelineCompilationException(diagnostics: diagnostics);
        }

        // Liveness follows both current- and previous-frame reads, and a forward's predecessor. A history writer is
        // needed for future frames even when no current-frame pass consumes its result. External writes are not admitted.
        var livePasses = new HashSet<int>();
        var liveResources = definition.Outputs.ToHashSet(comparer: StringComparer.Ordinal);
        var pendingResources = new Stack<string>(collection: liveResources);

        while (pendingResources.TryPop(result: out var resourceName)) {
            if (
                (resourceByName[resourceName].From is { } predecessor) &&
                liveResources.Add(item: predecessor)
            ) {
                pendingResources.Push(item: predecessor);
            }
            if (
                !writerByResource.TryGetValue(
                key: resourceName,
                value: out var writer
            ) ||
                !livePasses.Add(item: writer)
            ) { continue; }
            var pass = definition.ShaderPasses[writer];

            foreach (var reference in pass.OutputReferences.Concat(second: ReadsOf(pass: pass))) {
                if (liveResources.Add(item: reference.Name)) { pendingResources.Push(item: reference.Name); }
            }
        }
        order.RemoveAll(match: pass => !livePasses.Contains(item: pass));
        if (order.Count == 0) {
            Add(
                diagnostics,
                "SHADERPIPE_NO_LIVE_PASSES",
                "Pipeline outputs have no executable writer pass; initialized-only graphs are not supported.",
                definition.Name
            );
            throw new ShaderPipelineCompilationException(diagnostics: diagnostics);
        }
        var ordinal = new Dictionary<int, int>();

        for (var index = 0; (index < order.Count); index++) {
            ordinal[order[index]] = index;
        }

        var plannedPasses = new List<ShaderPipelinePlannedPass>(capacity: order.Count);
        // The shape each planned pass is ordered and versioned by, in execution order: a shader pass's declaration, or a
        // package pass's compute shape, which planning reads and the plan does not keep. A package pass's port
        // accesses stand beside its shape.
        var shapes = new List<ShaderPipelinePass>(capacity: order.Count);
        var shapePackages = new List<ShaderPipelinePackagePass?>(capacity: order.Count);
        var interfacesBySource = new Dictionary<string, (string Pass, ShaderInterface Interface)>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < order.Count); index++) {
            var passIndex = order[index];
            var pass = definition.ShaderPasses[passIndex];
            var package = packageNames.Contains(item: pass.Name);
            ShaderPipelineParameterLayout parameters;

            try {
                parameters = (package
                    ? ShaderPipelineParameterLayout.ForPackage(
                        config: pass.Config,
                        members: packageByName[pass.Name].Members,
                        package: pass.Source
                    )
                    : ShaderPipelineParameterLayout.Resolve(
                        pass: pass,
                        resources: resourceByName
                    ));
            } catch (InvalidDataException exception) {
                Add(
                    diagnostics,
                    "SHADERPIPE_INTERFACE",
                    $"Pass '{pass.Name}' has no interface: {exception.Message}",
                    pass.Name
                );
                continue;
            }

            if (parameters.SizeBytes > m_limits.MaxPassBlockBytes) {
                Add(
                    diagnostics,
                    "SHADERPIPE_PASS_BLOCK_LIMIT",
                    $"Pass '{pass.Name}' pass block is {parameters.SizeBytes} bytes with its config; the portable limit is {m_limits.MaxPassBlockBytes} bytes.",
                    pass.Name
                );
            }
            // A source includes one generated interface, so every pass compiling it must read the same one. A package
            // pass compiles no source.
            if (!package) {
                if (
                    interfacesBySource.TryGetValue(
                        key: pass.Source,
                        value: out var shared
                    ) &&
                    (shared.Interface.Hash != parameters.Interface.Hash)
                ) {
                    Add(
                        diagnostics,
                        "SHADERPIPE_INTERFACE_CONFLICT",
                        $"Passes '{shared.Pass}' and '{pass.Name}' compile '{pass.Source}' with different config, so the source would read two interfaces.",
                        pass.Name
                    );
                } else {
                    interfacesBySource[pass.Source] = (pass.Name, parameters.Interface);
                }
            }
            shapes.Add(item: pass);
            shapePackages.Add(item: (package
                ? packageByName[pass.Name]
                : null));
            plannedPasses.Add(item: new ShaderPipelinePlannedPass(
                Declaration: (package
                    ? null
                    : pass),
                Name: pass.Name,
                Package: null,
                Index: index,
                Dependencies: new ReadOnlyCollection<int>(list: dependencies[passIndex].OrderBy(keySelector: value => ordinal[value]).Select(selector: value => ordinal[value]).ToList()),
                Parameters: parameters,
                Accesses: [],
                Attachments: [],
                Kind: (package
                    ? ShaderPipelinePassKind.Package
                    : pass.Kind.PlanKind()),
                Extent: null
            ));
        }
        if (diagnostics.Count != 0) {
            throw new ShaderPipelineCompilationException(diagnostics: diagnostics);
        }
        var versions = PlanVersions(
            definition: definition,
            liveResources: liveResources,
            packages: shapePackages,
            passes: shapes
        );
        var plannedResources = versions.Resources.ToDictionary(
            keySelector: static resource => resource.Name,
            comparer: StringComparer.Ordinal
        );

        return new ShaderPipelinePlan(
            definition: graph with {
                Passes = ((graph.Passes is null)
                    ? null
                    : definition.ShaderPasses.Take(count: graph.Passes.Count).ToArray()),
            },
            passes: plannedPasses.Select(selector: (pass, index) => pass with {
                Accesses = versions.Accesses[index],
                Attachments = AttachmentsOf(
                    pass: shapes[index],
                    resources: plannedResources
                ),
                Extent = ExtentOf(
                    pass: shapes[index],
                    resources: plannedResources,
                    storages: versions.Storages
                ),
                Package = ((pass.Kind == ShaderPipelinePassKind.Package)
                    ? StepOf(pass: shapes[index])
                    : null),
            }).ToArray(),
            resources: versions.Resources,
            storages: versions.Storages
        );
    }

    // The one pass-extent rule, for every pass kind: the dimensions of the first reference whose storage declares them,
    // outputs before inputs, or none, so the pass runs at the frame's extent.
    private static ShaderPipelineDimensions? ExtentOf(ShaderPipelinePass pass, IReadOnlyDictionary<string, ShaderPipelinePlannedResource> resources, IReadOnlyList<ShaderPipelinePlannedStorage> storages) =>
        pass.OutputReferences.Concat(second: pass.InputReferences).Select(selector: reference => storages[resources[reference.Name].Storage].Declaration.Dimensions).FirstOrDefault(predicate: static dimensions => (dimensions is not null));
    // A package pass's step: its package and its ports' versions.
    private static ShaderPipelinePackageStep StepOf(ShaderPipelinePass pass) => new(
        Inputs: new ReadOnlyCollection<ResourceReference>(list: pass.InputReferences.ToArray()),
        Outputs: new ReadOnlyCollection<ResourceReference>(list: pass.OutputReferences.ToArray()),
        Package: pass.Source
    );

    /// <summary>Convenience static entry point for callers that do not need custom limits.</summary>
    public static ShaderPipelinePlan Plan(RenderGraphDefinition definition) => new ShaderPipelineCompiler().Compile(definition: definition);
    /// <summary>Attempts to compile a definition without throwing for authored validation errors.</summary>
    public bool TryCompile(RenderGraphDefinition definition, out ShaderPipelinePlan? plan, out IReadOnlyList<ShaderPipelineDiagnostic> diagnostics) {
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
}


