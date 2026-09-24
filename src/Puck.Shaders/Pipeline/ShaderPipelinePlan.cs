using System.Collections.ObjectModel;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>A resource version in an immutable shader execution plan: who writes it, which storage holds it, how its
/// contents begin, and the passes that may sample it.</summary>
/// <param name="Declaration">The immutable version declaration.</param>
/// <param name="WriterPassIndex">Its writing pass, or -1 for an initialized or external version.</param>
/// <param name="FirstUsePassIndex">Its first pass access, or -1 when only published.</param>
/// <param name="LastUsePassIndex">Its final pass access; the pass count denotes publication or history retained into the
/// next frame.</param>
/// <param name="Storage">The index of the storage holding it in <see cref="ShaderPipelinePlan.Storages"/>. Every version
/// of a forwarding chain shares one storage.</param>
/// <param name="Successor">The version forwarding this one, or <see langword="null"/>.</param>
/// <param name="ConsumedAtPassIndex">The pass index of the successor's writer, which overwrites this version's contents,
/// or -1 when nothing consumes it. A pass samples this version's contents only after <paramref name="WriterPassIndex"/>
/// and before this index.</param>
/// <param name="Contents">How the contents begin when the version is written or first read.</param>
/// <param name="Stored">Whether anything uses the contents after the writer: a reader, a successor, publication, or the
/// next frame.</param>
/// <param name="Public">Whether the version is a public output.</param>
public sealed record ShaderPipelinePlannedResource(
    ShaderPipelineResource Declaration,
    int WriterPassIndex,
    int FirstUsePassIndex = -1,
    int LastUsePassIndex = -1,
    int Storage = 0,
    string? Successor = null,
    int ConsumedAtPassIndex = -1,
    ShaderPipelineContents Contents = ShaderPipelineContents.Discarded,
    bool Stored = true,
    bool Public = false
) {
    /// <summary>Gets the name used by pass bindings.</summary>
    public string Name => Declaration.Name;
    /// <summary>Gets whether a successor overwrites this version within the frame, so its contents are gone by the
    /// frame's end.</summary>
    public bool IsConsumed => (ConsumedAtPassIndex >= 0);
}
/// <summary>One physical storage of a plan: the chain of versions forwarding into each other, allocated once per frame
/// slot and never aliased with another storage.</summary>
/// <param name="Index">The storage's index in <see cref="ShaderPipelinePlan.Storages"/>.</param>
/// <param name="Declaration">The declaration of the chain's first version, which fixes the storage's kind, format, extent,
/// sample count, size and initialization.</param>
/// <param name="Versions">The chain's versions in writing order; the last one is what the frame leaves.</param>
/// <param name="History">Whether the last version is retained into the next frame.</param>
/// <param name="Clear">Which instances the node clears when a graph installs or resets.</param>
/// <param name="FrameEndKind">Where <paramref name="FrameEnd"/> comes from: <see cref="ShaderPipelinePriorKind.Pass"/>
/// when a pass touches this frame's instance, <see cref="ShaderPipelinePriorKind.Host"/> for a host-owned image no pass
/// touches, and <see cref="ShaderPipelinePriorKind.CrossFrame"/> for an initialized storage no pass touches.</param>
/// <param name="FrameEnd">The state this frame's instance is in after the frame's last pass: where publication, the
/// float preview and the hand-back of a host-owned image start from.</param>
public sealed record ShaderPipelinePlannedStorage(
    int Index,
    ShaderPipelineResource Declaration,
    IReadOnlyList<string> Versions,
    bool History,
    ShaderPipelineClear Clear,
    ShaderPipelinePriorKind FrameEndKind,
    ShaderPipelineAccessState FrameEnd
) {
    /// <summary>Gets the storage's name, which is its first version's.</summary>
    public string Name => Declaration.Name;
    /// <summary>Gets whether the host supplies the storage.</summary>
    public bool IsExternal => Declaration.IsExternal;
}
/// <summary>One attachment of a graphics pass's render pass: the version it writes, how the render pass begins it, and
/// whether its contents outlive the pass.</summary>
/// <param name="Version">The version the pass writes into the attachment.</param>
/// <param name="Storage">The index of the storage holding it in <see cref="ShaderPipelinePlan.Storages"/>.</param>
/// <param name="Depth">Whether it is the depth attachment rather than a color attachment.</param>
/// <param name="Load">How the render pass begins it: <see cref="GpuAttachmentLoad.Load"/> for a version that forwards
/// its predecessor, whose contents the pass continues, and <see cref="GpuAttachmentLoad.Clear"/> for one whose writer
/// starts from discarded contents (a color to opaque black, a depth to one).</param>
/// <param name="Store">Whether the render pass keeps what it wrote: <see cref="GpuAttachmentStore.Store"/> for a stored
/// version, <see cref="GpuAttachmentStore.Discard"/> for one nothing uses after its writer.</param>
public sealed record ShaderPipelineAttachment(
    string Version,
    int Storage,
    bool Depth,
    GpuAttachmentLoad Load,
    GpuAttachmentStore Store
);
/// <summary>A pass entry in an immutable shader execution plan.</summary>
/// <param name="Declaration">The pass declaration, with every binding resolved.</param>
/// <param name="Index">The pass's position in execution order.</param>
/// <param name="Dependencies">The indices of the passes that must run first: the writers of what it reads and forwards,
/// and every reader of a version it overwrites.</param>
/// <param name="Parameters">The pass's packed parameter layout.</param>
/// <param name="Accesses">Every storage instance the pass touches, in recording order, with the barrier each needs.</param>
/// <param name="Attachments">A graphics pass's attachments, its color attachment first; empty for a compute pass. The
/// render pass leaves every attachment in its attachment layout, and a later access's planned barrier moves it on.</param>
public sealed record ShaderPipelinePlannedPass(
    ShaderPipelinePass Declaration,
    int Index,
    IReadOnlyList<int> Dependencies,
    ShaderPipelineParameterLayout Parameters,
    IReadOnlyList<ShaderPipelineAccess> Accesses,
    IReadOnlyList<ShaderPipelineAttachment> Attachments
) {
    /// <summary>Gets the pass name.</summary>
    public string Name => Declaration.Name;
}
/// <summary>The result of pipeline planning, with passes in deterministic execution order.</summary>
public sealed class ShaderPipelinePlan {
    internal ShaderPipelinePlan(
        ShaderPipelineDefinition definition,
        IReadOnlyList<ShaderPipelinePlannedResource> resources,
        IReadOnlyList<ShaderPipelinePlannedStorage> storages,
        IReadOnlyList<ShaderPipelinePlannedPass> passes
    ) {
        Definition = Snapshot(definition: definition);
        var passesByName = Definition.Passes.ToDictionary(
            static pass => pass.Name,
            StringComparer.Ordinal
        );
        var resourcesByName = Definition.Resources.ToDictionary(
            static resource => resource.Name,
            StringComparer.Ordinal
        );

        Resources = new ReadOnlyCollection<ShaderPipelinePlannedResource>(list: resources.Select(selector: resource => resource with {
            Declaration = resourcesByName[resource.Name],
        }).ToList());
        Storages = new ReadOnlyCollection<ShaderPipelinePlannedStorage>(list: storages.Select(selector: storage => storage with {
            Declaration = resourcesByName[storage.Name],
            Versions = new ReadOnlyCollection<string>(list: storage.Versions.ToArray()),
        }).ToList());
        Passes = new ReadOnlyCollection<ShaderPipelinePlannedPass>(list: passes.Select(selector: pass => pass with {
            Accesses = new ReadOnlyCollection<ShaderPipelineAccess>(list: pass.Accesses.ToArray()),
            Attachments = new ReadOnlyCollection<ShaderPipelineAttachment>(list: pass.Attachments.ToArray()),
            Declaration = passesByName[pass.Name],
            Dependencies = new ReadOnlyCollection<int>(list: pass.Dependencies.ToArray()),
        }).ToList());
        Outputs = new ReadOnlyCollection<string>(list: Definition.Outputs.ToList());
    }

    /// <summary>Gets the version a single-target runtime publishes by default: the first public output.</summary>
    public string DefaultOutput => ((Outputs.Count == 0)
        ? string.Empty
        : Outputs[0]
    );
    /// <summary>Gets the source document copied into this plan.</summary>
    public ShaderPipelineDefinition Definition { get; }
    /// <summary>Gets the public versions.</summary>
    public IReadOnlyList<string> Outputs { get; }
    /// <summary>Gets every pass's parameter block together, in bytes: each pass's frame prefix and config. It is the
    /// pipeline instance's parameter region, the bytes whose residency <c>pipeline.inspect</c> reports.</summary>
    public ulong ParameterBytes => Passes.Aggregate(
        func: static (total, pass) => (total + pass.Parameters.SizeBytes),
        seed: 0UL
    );
    /// <summary>Gets the pass names in execution order.</summary>
    public IReadOnlyList<string> PassOrder => Passes.Select(selector: static pass => pass.Name).ToArray();
    /// <summary>Gets passes in deterministic topological execution order.</summary>
    public IReadOnlyList<ShaderPipelinePlannedPass> Passes { get; }
    /// <summary>Gets the live versions in ordinal name order.</summary>
    public IReadOnlyList<ShaderPipelinePlannedResource> Resources { get; }
    /// <summary>Gets the physical storages, in the ordinal order of their first versions' names.</summary>
    public IReadOnlyList<ShaderPipelinePlannedStorage> Storages { get; }

    private static ShaderPipelineDefinition Snapshot(ShaderPipelineDefinition definition) {
        var resources = definition.Resources.Select(selector: resource => resource with {
            Dimensions = ((resource.Dimensions is null)
            ? null
            : resource.Dimensions with { }),
        }).ToArray();
        var passes = definition.Passes.Select(selector: pass => pass with {
            Inputs = new ReadOnlyCollection<ResourceReference>(list: pass.InputReferences.Select(selector: static input => input with { }).ToList()),
            Outputs = new ReadOnlyCollection<ResourceReference>(list: pass.OutputReferences.Select(selector: static output => output with { }).ToList()),
            Config = ((pass.Config is null)
            ? null
            : new ReadOnlyDictionary<string, ShaderConfigField>(dictionary: SnapshotConfig(config: pass.Config))),
            Geometry = ((pass.Geometry is { } geometry)
            ? geometry with {
                Attributes = new ReadOnlyCollection<ShaderPipelineVertexAttribute>(list: geometry.Attributes.ToArray()),
                Indices = new ReadOnlyCollection<uint>(list: geometry.Indices.ToArray()),
                Vertices = new ReadOnlyCollection<float>(list: geometry.Vertices.ToArray()),
            }
            : null),
        }).ToArray();
        var config = ((definition.Config is null)
            ? null
            : new ReadOnlyDictionary<string, ShaderConfigField>(dictionary: SnapshotConfig(config: definition.Config))
        );

        return new ShaderPipelineDefinition(
            Schema: definition.Schema,
            Name: definition.Name,
            Resources: new ReadOnlyCollection<ShaderPipelineResource>(list: resources),
            Passes: new ReadOnlyCollection<ShaderPipelinePass>(list: passes),
            Outputs: new ReadOnlyCollection<string>(list: definition.Outputs.ToArray()),
            Config: config
        );
    }
    private static Dictionary<string, ShaderConfigField> SnapshotConfig(IReadOnlyDictionary<string, ShaderConfigField> config) =>
        config.ToDictionary(
            static pair => pair.Key,
            static pair => SnapshotField(field: pair.Value),
            StringComparer.Ordinal
        );
    private static ShaderConfigField SnapshotField(ShaderConfigField field) => field with {
        Default = ((field.Default is { } value)
        ? value.Clone()
        : null),
    };

    /// <summary>Finds a live version by name.</summary>
    /// <param name="name">The version name.</param>
    /// <returns>The planned version, or <see langword="null"/> when no live version has that name.</returns>
    public ShaderPipelinePlannedResource? FindResource(string name) {
        foreach (var resource in Resources) {
            if (string.Equals(
                a: resource.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return resource;
            }
        }

        return null;
    }
}
