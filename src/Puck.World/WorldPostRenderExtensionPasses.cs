using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Shaders;

namespace Puck.World;

/// <summary>The composed <c>render.extensions</c> passes of the default render graph's root instance, keyed by their
/// document id: a presentation-scope parameter binding's only write target. The render root attaches the root instance
/// once it is composed; until then, and in a boot shape that composes no presentation (headless), every write is refused,
/// so a parameter binding's write there is a harmless no-op.</summary>
public sealed class WorldPostRenderExtensionPasses {
    private readonly Dictionary<string, JsonObject> m_live = new(comparer: StringComparer.Ordinal);

    private WorldRootGraph? m_graph;
    private ShaderPipelineRenderNode? m_root;

    /// <summary>Attaches the root instance the default render graph's post passes record in.</summary>
    /// <param name="graph">The composed default graph, which names each extension's passes.</param>
    /// <param name="root">The root instance's node, or <see langword="null"/> when the graph draws nothing over the
    /// world.</param>
    /// <param name="extensions">The document's <c>render.extensions</c> entries, whose configs each pass starts from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> is <see langword="null"/>.</exception>
    public void Attach(WorldRootGraph graph, ShaderPipelineRenderNode? root, IReadOnlyList<WorldRenderExtensionEntry>? extensions) {
        ArgumentNullException.ThrowIfNull(argument: graph);

        m_graph = graph;
        m_root = root;
        m_live.Clear();

        foreach (var entry in (extensions ?? [])) {
            if (
                !m_live.ContainsKey(key: entry.Id) &&
                (entry.Config is { ValueKind: JsonValueKind.Object } config)
            ) {
                m_live.Add(
                    key: entry.Id,
                    value: JsonObject.Create(element: config)!
                );
            }
        }
    }
    /// <summary>Updates a declared floating-point config field of every pass composed from one extension id; the passes'
    /// frame blocks carry it from the next frame the root renders.</summary>
    /// <param name="id">The <c>render.extensions[].id</c> the passes were composed from.</param>
    /// <param name="field">The config field's name.</param>
    /// <param name="value">The value, which must be finite and inside the field's range.</param>
    /// <returns><see langword="true"/> when the root has installed its graph and every pass of the id bound the value;
    /// <see langword="false"/> otherwise, and the caller writes it again later.</returns>
    public bool TrySetConfig(string id, string field, float value) {
        ArgumentException.ThrowIfNullOrEmpty(argument: id);
        ArgumentException.ThrowIfNullOrEmpty(argument: field);

        if (
            (m_root is not { } root) ||
            (m_graph is not { } graph) ||
            !graph.PostPasses.TryGetValue(
                key: id,
                value: out var passes
            ) ||
            !float.IsFinite(f: value)
        ) {
            return false;
        }

        var live = (m_live.TryGetValue(
            key: id,
            value: out var current
        )
            ? ((JsonObject)current.DeepClone())
            : []);

        live[field] = value;

        using var document = JsonDocument.Parse(json: live.ToJsonString());
        var config = document.RootElement.Clone();

        foreach (var pass in passes) {
            if (!root.TrySetConfig(
                config: config,
                passName: pass,
                reason: out _
            )) {
                return false;
            }
        }

        m_live[id] = live;

        return true;
    }
}
