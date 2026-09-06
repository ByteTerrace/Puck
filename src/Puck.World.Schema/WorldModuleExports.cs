using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.World;

/// <summary>The names one imported module declares and the subset it exports, spelled as the composed document
/// spells them (alias-prefixed under an aliased import). A reference from outside the module is admitted when it
/// names nothing the module declares, or names something the module exports under the referencing field's
/// facet.</summary>
public sealed class WorldModuleSurface {
    private readonly HashSet<string> m_declared;
    private readonly HashSet<string>[] m_exported;

    internal WorldModuleSurface(string name, WorldExports exports, HashSet<string> declared) {
        Name = name;
        Exports = exports;
        m_declared = declared;
        m_exported = new HashSet<string>[3];

        foreach (var facet in (ReadOnlySpan<WorldExportFacet>)[WorldExportFacet.Read, WorldExportFacet.Action, WorldExportFacet.Binding]) {
            m_exported[(int)facet] = new HashSet<string>(collection: exports.Of(facet: facet), comparer: StringComparer.Ordinal);
        }
    }

    /// <summary>Gets the module as the import describes it: its resolved path, with the alias it composed under.</summary>
    public string Name { get; }
    /// <summary>Gets the export lists as authored, prefixed by the alias.</summary>
    public WorldExports Exports { get; }

    /// <summary>Returns whether the module declares <paramref name="name"/>.</summary>
    /// <param name="name">The composed spelling.</param>
    /// <returns><see langword="true"/> when the module minted the name.</returns>
    public bool Declares(string name) => m_declared.Contains(item: name);
    /// <summary>Returns whether a reference to <paramref name="name"/> through a field of <paramref name="facet"/>
    /// is admitted: the name is not the module's, or the module exports it under that facet.</summary>
    /// <param name="name">The composed spelling.</param>
    /// <param name="facet">The referencing field's facet.</param>
    /// <returns><see langword="true"/> when the reference may stand.</returns>
    public bool Admits(string name, WorldExportFacet facet) => (!m_declared.Contains(item: name) || m_exported[(int)facet].Contains(item: name));
}

/// <summary>Enforces a module's export surface at compose time. <see cref="TryTake"/> consumes an imported module's
/// <c>exports</c> member and strips it so the composed document never carries it; <see cref="TryCheckLayers"/> walks
/// every layer of a composition once — each import, the host's own body, its basis — over the same registry
/// <see cref="WorldModuleNamespace"/> prefixes through, validates each module's exports against what it declares,
/// and refuses by name the first reference from outside a module to a name the module declares and does not export
/// under the referencing field's facet. A name spelled in any registered position — a bare name, a reserved
/// channel's segment, a key indirection, an expression operand, a binding token — is found the same way it is
/// renamed.</summary>
public static class WorldModuleExports {
    // What every registered site's Rewrite asks its declared-name map: a map that answers nothing and remembers
    // every question is the name enumeration, with no second parser over the spellings.
    private sealed class NameProbe : IReadOnlyDictionary<string, string> {
        public List<string> Seen { get; } = [];

        public string this[string key] => throw new KeyNotFoundException();
        public IEnumerable<string> Keys => [];
        public IEnumerable<string> Values => [];
        public int Count => 0;

        public bool ContainsKey(string key) => false;
        public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value) {
            if (key.Length > 0) {
                Seen.Add(item: key);
            }

            value = null;

            return false;
        }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    // One layer's walk: what it declares, and every name it spells at a non-declaring site with the site's node,
    // whose JSON path is only rendered for a refusal.
    private sealed class Layer(string name) {
        public string Name { get; } = name;
        public HashSet<string> Declared { get; } = new(comparer: StringComparer.Ordinal);
        public List<(string Name, WorldExportFacet Facet, JsonNode Site)> References { get; } = [];
    }

    /// <summary>Consumes <paramref name="module"/>'s <c>exports</c> member: reads it (an absent member exports
    /// nothing) and strips it.</summary>
    /// <param name="module">The module's composed tree; its <c>exports</c> member is removed.</param>
    /// <param name="moduleName">How the import describes the module, for the refusal.</param>
    /// <param name="exports">The record as authored, or an empty record when the member is absent.</param>
    /// <param name="reason">The one-line refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the member was absent or well-formed.</returns>
    public static bool TryTake(JsonObject module, string moduleName, [NotNullWhen(true)] out WorldExports? exports, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: module);

        exports = new WorldExports();

        if (module.TryGetPropertyValue(propertyName: WorldExports.MemberName, jsonNode: out var node) && (node is not null)) {
            try {
                exports = (JsonSerializer.Deserialize<WorldExports>(node: node, options: WorldJsonContext.Default.Options) ?? exports);
            } catch (JsonException exception) {
                exports = null;
                reason = $"{moduleName}: '{WorldExports.MemberName}' is malformed: {exception.Message}";

                return false;
            }
        }

        _ = module.Remove(propertyName: WorldExports.MemberName);
        reason = string.Empty;

        return true;
    }
    /// <summary>Checks every layer of one file's composition: walks each import once to learn what it declares and
    /// what it references, validates each import's exports against its declarations, then refuses the first
    /// reference — from a sibling import, the file's own body, or its composed basis — to a name a module declares
    /// and does not export under the referencing field's facet. A name a layer itself declares is the layer's own
    /// and is never checked against a module.</summary>
    /// <param name="hostPath">The composing file, for the refusal.</param>
    /// <param name="ownBody">The file's own body, basis and imports members stripped.</param>
    /// <param name="basis">The file's composed basis chain; empty when it names none.</param>
    /// <param name="imports">Each import's description, composed tree (exports stripped, alias applied), and
    /// exports record, in list order; two entries describing the same module share one surface.</param>
    /// <param name="surfaces">Each distinct module's surface on success.</param>
    /// <param name="reason">The one-line refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when every export names a declaration and every layer binds only what its
    /// modules export.</returns>
    public static bool TryCheckLayers(string hostPath, JsonObject ownBody, JsonObject basis, IReadOnlyList<(string Name, JsonObject Tree, WorldExports Exports)> imports, out IReadOnlyList<WorldModuleSurface> surfaces, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: ownBody);
        ArgumentNullException.ThrowIfNull(argument: basis);
        ArgumentNullException.ThrowIfNull(argument: imports);

        var probe = new NameProbe();
        var byName = new Dictionary<string, WorldModuleSurface>(comparer: StringComparer.Ordinal);
        var layers = new List<(Layer Layer, WorldModuleSurface? Own)>(capacity: (imports.Count + 2));

        foreach (var (name, tree, exports) in imports) {
            var layer = Walk(tree: tree, name: name, probe: probe);

            if (!exports.TryValidate(declared: layer.Declared, reason: out var exportsReason)) {
                surfaces = [];
                reason = $"{hostPath} imports {name}: {exportsReason}";

                return false;
            }

            if (!byName.TryGetValue(key: name, value: out var surface)) {
                surface = new WorldModuleSurface(name: name, exports: exports, declared: layer.Declared);
                byName[key: name] = surface;
            }

            layers.Add(item: (layer, surface));
        }

        layers.Add(item: (Walk(tree: ownBody, name: hostPath, probe: probe), null));

        if (basis.Count > 0) {
            layers.Add(item: (Walk(tree: basis, name: $"the basis of {hostPath}", probe: probe), null));
        }

        var modules = byName.Values.ToArray();

        foreach (var (layer, own) in layers) {
            foreach (var (name, facet, site) in layer.References) {
                if (layer.Declared.Contains(item: name)) {
                    continue;
                }

                foreach (var module in modules) {
                    if (ReferenceEquals(objA: module, objB: own) || module.Admits(name: name, facet: facet)) {
                        continue;
                    }

                    surfaces = [];
                    reason = $"{layer.Name} names '{name}' at {site.GetPath()}, which {module.Name} declares and does not export under {WorldExports.MemberOf(facet: facet)}; a host binds only what its module exports.";

                    return false;
                }
            }
        }

        surfaces = modules;
        reason = string.Empty;

        return true;
    }

    private static Layer Walk(JsonObject tree, string name, NameProbe probe) {
        var layer = new Layer(name: name);

        WorldModuleNamespace.Visit(node: tree, type: typeof(WorldDefinition), visitor: (_, _, value, field) => {
            if (field.Role == WorldNameRole.Declares) {
                if ((value is JsonValue leaf) && leaf.TryGetValue<string>(value: out var text) && (text.Length > 0)) {
                    _ = layer.Declared.Add(item: text);
                }

                return;
            }

            switch (value) {
                case JsonArray list when (field.Role == WorldNameRole.Names):
                    foreach (var element in list) {
                        if ((element is JsonValue item) && item.TryGetValue<string>(value: out var spelled)) {
                            Collect(probe: probe, text: spelled, field: field, site: element, layer: layer);
                        }
                    }

                    break;
                case JsonValue leaf when leaf.TryGetValue<string>(value: out var text):
                    Collect(probe: probe, text: text, field: field, site: value, layer: layer);

                    break;
            }
        });

        return layer;
    }
    private static void Collect(NameProbe probe, string text, WorldNameField field, JsonNode site, Layer layer) {
        probe.Seen.Clear();
        _ = WorldModuleNamespace.Rewrite(text: text, role: field.Role, declared: probe);

        foreach (var name in probe.Seen) {
            layer.References.Add(item: (name, field.Facet, site));
        }
    }
}
