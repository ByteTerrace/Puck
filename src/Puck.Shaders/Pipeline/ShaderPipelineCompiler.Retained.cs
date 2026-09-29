namespace Puck.Shaders;

public sealed partial class ShaderPipelineCompiler {
    private static void ValidateRetained(RenderGraphDefinition definition, IReadOnlyList<ShaderPipelinePlannedResource> resources,
        IReadOnlyList<ShaderPipelinePlannedStorage> storages, IReadOnlyList<ShaderPipelinePlannedPass> passes,
        List<ShaderPipelineDiagnostic> diagnostics) {
        foreach (var declared in definition.Resources) {
            if (declared.Retained && (declared.From is not null)) {
                Add(diagnostics, "SHADERPIPE_RETAINED", $"Resource '{declared.Name}' forwards '{declared.From}' and declares retained; only a chain's first version declares it.", declared.Name);
            }
            if (!declared.PreservesPredecessor) { continue; }
            var version = resources.FirstOrDefault(predicate: resource => (resource.Name == declared.Name));

            if ((declared.From is null) || (version is null) || !storages[version.Storage].Declaration.Retained ||
                (version.WriterPassIndex < 0) || (passes[version.WriterPassIndex].Kind != ShaderPipelinePassKind.Package)) {
                Add(diagnostics, "SHADERPIPE_RETAINED", $"Resource '{declared.Name}' preserves its predecessor, but only a package-written version forwarding retained storage can declare that contract.", declared.Name);
            }
        }
        var outputs = definition.Outputs.ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var storage in storages) {
            var root = storage.Declaration;

            if (!root.Retained) { continue; }
            var why = (root.Transient ? "is transient" : (root.IsExternal ? "is external or host-owned" :
                (storage.History ? "is history" : ((storage.Versions.FirstOrDefault(predicate: outputs.Contains) is { } output) ? $"is published as '{output}'" : null))));

            if (why is not null) {
                Add(diagnostics, "SHADERPIPE_RETAINED", $"Retained storage '{root.Name}' {why}; only an owned intermediate without history can retain one queue-ordered allocation.", root.Name);
            }
        }
    }
}
