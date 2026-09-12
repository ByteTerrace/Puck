using System.Collections.ObjectModel;

namespace Puck.Shaders;

/// <summary>A resource entry in an immutable shader execution plan.</summary>
/// <param name="Declaration">The immutable resource declaration.</param>
/// <param name="WriterPassIndex">Its producing pass, or -1 for an initialized or external resource.</param>
/// <param name="FirstUsePassIndex">Its first pass access, or -1 when only published.</param>
/// <param name="LastUsePassIndex">Its final pass access; the pass count denotes publication or cross-frame retention.</param>
public sealed record ShaderPipelinePlannedResource(
    ShaderPipelineResource Declaration,
    int WriterPassIndex,
    int FirstUsePassIndex = -1,
    int LastUsePassIndex = -1
) {
    /// <summary>Gets the name used by pass bindings.</summary>
    public string Name => Declaration.Name;
}

/// <summary>A pass entry in an immutable shader execution plan.</summary>
public sealed record ShaderPipelinePlannedPass(
    ShaderPipelinePass Declaration,
    int Index,
    IReadOnlyList<int> Dependencies,
    ShaderPipelineParameterLayout Parameters
) {
    /// <summary>Gets the pass name.</summary>
    public string Name => Declaration.Name;
}

/// <summary>The result of pipeline planning, with passes in deterministic execution order.</summary>
public sealed class ShaderPipelinePlan {
    internal ShaderPipelinePlan(
        ShaderPipelineDefinition definition,
        IReadOnlyList<ShaderPipelinePlannedResource> resources,
        IReadOnlyList<ShaderPipelinePlannedPass> passes,
        IReadOnlyList<ShaderPipelineOutput> outputs
    ) {
        Definition = Snapshot(definition: definition);
        var passesByName = Definition.Passes.ToDictionary(static pass => pass.Name, StringComparer.Ordinal);
        var resourcesByName = Definition.Resources.ToDictionary(static resource => resource.Name, StringComparer.Ordinal);
        Resources = new ReadOnlyCollection<ShaderPipelinePlannedResource>(resources.Select(resource =>
            new ShaderPipelinePlannedResource(resourcesByName[resource.Name], resource.WriterPassIndex, resource.FirstUsePassIndex, resource.LastUsePassIndex)).ToList());
        Passes = new ReadOnlyCollection<ShaderPipelinePlannedPass>(passes.Select(pass =>
            new ShaderPipelinePlannedPass(passesByName[pass.Name], pass.Index, new ReadOnlyCollection<int>(pass.Dependencies.ToArray()), pass.Parameters)).ToList());
        Outputs = new ReadOnlyCollection<ShaderPipelineOutput>(Definition.Outputs.ToList());
    }

    /// <summary>Gets the source document copied into this plan.</summary>
    public ShaderPipelineDefinition Definition { get; }
    /// <summary>Gets resources in ordinal name order.</summary>
    public IReadOnlyList<ShaderPipelinePlannedResource> Resources { get; }
    /// <summary>Gets passes in deterministic topological execution order.</summary>
    public IReadOnlyList<ShaderPipelinePlannedPass> Passes { get; }
    /// <summary>Gets the validated public outputs.</summary>
    public IReadOnlyList<ShaderPipelineOutput> Outputs { get; }
    /// <summary>Gets the resource exposed by the first output, for single-target runtimes.</summary>
    public string OutputResourceName => Outputs.Count == 0 ? string.Empty : Outputs[0].Resource.Name;
    /// <summary>Gets the pass names in execution order.</summary>
    public IReadOnlyList<string> PassOrder => Passes.Select(static pass => pass.Name).ToArray();

    private static Dictionary<string, ShaderConfigField> SnapshotConfig(IReadOnlyDictionary<string, ShaderConfigField> config) =>
        config.ToDictionary(static pair => pair.Key, static pair => SnapshotField(pair.Value), StringComparer.Ordinal);

    private static ShaderConfigField SnapshotField(ShaderConfigField field) => field with {
        Default = field.Default is { } value ? value.Clone() : null,
    };
    private static ShaderPipelineDefinition Snapshot(ShaderPipelineDefinition definition) {
        var resources = definition.Resources.Select(resource => resource with {
            Dimensions = resource.Dimensions is null ? null : resource.Dimensions with { },
        }).ToArray();
        var passes = definition.Passes.Select(pass => pass with {
            Inputs = new ReadOnlyCollection<ResourceReference>(pass.InputReferences.Select(static input => input with { }).ToList()),
            Outputs = new ReadOnlyCollection<ResourceReference>(pass.OutputReferences.Select(static output => output with { }).ToList()),
            Config = pass.Config is null ? null : new ReadOnlyDictionary<string, ShaderConfigField>(
                SnapshotConfig(pass.Config)),
        }).ToArray();
        var outputs = definition.Outputs.Select(output => output with { Resource = output.Resource with { } }).ToArray();
        var config = definition.Config is null ? null : new ReadOnlyDictionary<string, ShaderConfigField>(
            SnapshotConfig(definition.Config));
        return new ShaderPipelineDefinition(
            Schema: definition.Schema,
            Name: definition.Name,
            Resources: new ReadOnlyCollection<ShaderPipelineResource>(resources),
            Passes: new ReadOnlyCollection<ShaderPipelinePass>(passes),
            Outputs: new ReadOnlyCollection<ShaderPipelineOutput>(outputs),
            Config: config);
    }
}
