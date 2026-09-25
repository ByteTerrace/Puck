using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The composed <c>render.extensions</c> passes of the default render graph's root instance, keyed by their
/// document id: a presentation-scope parameter binding's only write target. The render root attaches the root instance
/// once it is composed; until then, and in a boot shape that composes no presentation (headless), every write is refused,
/// so a parameter binding's write there is a harmless no-op. An id the document names more than once composes one pass
/// per entry, and each pass keeps the config of its own entry: a binding's field is written over each pass's config,
/// never one entry's config over another's.</summary>
public sealed class WorldPostRenderExtensionPasses {
    // Each composed pass's live config, by pass name: its entry's config with every field a binding has written.
    private readonly Dictionary<string, JsonObject> m_live = new(comparer: StringComparer.Ordinal);

    private WorldRootGraph? m_graph;
    private Func<ShaderPipelineRenderNode?>? m_root;

    /// <summary>Attaches the root instance the default render graph's post passes record in.</summary>
    /// <param name="graph">The composed default graph, which names each extension's passes.</param>
    /// <param name="root">Reads the synthesized root instance's node, which is <see langword="null"/> while the graph
    /// draws nothing over the world.</param>
    /// <param name="extensions">The document's <c>render.extensions</c> entries, whose configs each pass starts from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> or <paramref name="root"/> is
    /// <see langword="null"/>.</exception>
    public void Attach(WorldRootGraph graph, Func<ShaderPipelineRenderNode?> root, IReadOnlyList<WorldRenderExtensionEntry>? extensions) {
        ArgumentNullException.ThrowIfNull(argument: graph);
        ArgumentNullException.ThrowIfNull(argument: root);

        m_graph = graph;
        m_root = root;
        m_live.Clear();

        // The graph composes an id's passes in document order, so an id's k-th entry is its k-th pass.
        var seen = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        foreach (var entry in (extensions ?? [])) {
            if (!graph.PostPasses.TryGetValue(
                key: entry.Id,
                value: out var passes
            )) {
                continue;
            }

            var index = seen.GetValueOrDefault(key: entry.Id);

            seen[entry.Id] = (index + 1);

            if (
                (index < passes.Count) &&
                (entry.Config is { ValueKind: JsonValueKind.Object } config)
            ) {
                m_live[passes[index]] = JsonObject.Create(element: config)!;
            }
        }
    }
    /// <summary>Updates a declared floating-point config field of every pass composed from one extension id, over each
    /// pass's own config; the passes' frame blocks carry it from the next frame the root renders.</summary>
    /// <param name="id">The <c>render.extensions[].id</c> the passes were composed from.</param>
    /// <param name="field">The config field's name.</param>
    /// <param name="value">The value, which must be finite and inside the field's range.</param>
    /// <returns><see langword="true"/> when the root has installed its graph and every pass of the id bound the value;
    /// <see langword="false"/> otherwise, and the caller writes it again later.</returns>
    public bool TrySetConfig(string id, string field, float value) => (
        (m_root?.Invoke() is { } root) &&
        TrySetConfig(
            field: field,
            id: id,
            value: value,
            write: (pass, config) => root.TrySetConfig(
                config: config,
                passName: pass,
                reason: out _
            )
        )
    );
    /// <summary>Updates a declared floating-point config field of every pass composed from one extension id, over each
    /// pass's own config, through a writer that rebinds one pass's whole config.</summary>
    /// <param name="id">The <c>render.extensions[].id</c> the passes were composed from.</param>
    /// <param name="field">The config field's name.</param>
    /// <param name="value">The value, which must be finite.</param>
    /// <param name="write">Rebinds one pass's whole config, by pass name, and returns whether it bound.</param>
    /// <returns><see langword="true"/> when every pass of the id bound its config, which the passes then keep;
    /// <see langword="false"/> when no graph is attached, the id composed no pass, the value is not finite, or a pass
    /// refused its config, and no pass's kept config changes.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> or <paramref name="field"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="id"/>, <paramref name="field"/> or
    /// <paramref name="write"/> is <see langword="null"/>.</exception>
    public bool TrySetConfig(string id, string field, float value, Func<string, JsonElement, bool> write) {
        ArgumentException.ThrowIfNullOrEmpty(argument: id);
        ArgumentException.ThrowIfNullOrEmpty(argument: field);
        ArgumentNullException.ThrowIfNull(argument: write);

        if (
            (m_graph is not { } graph) ||
            !graph.PostPasses.TryGetValue(
                key: id,
                value: out var passes
            ) ||
            !float.IsFinite(f: value)
        ) {
            return false;
        }

        var written = new JsonObject[passes.Count];

        for (var index = 0; (index < passes.Count); index++) {
            var live = (m_live.TryGetValue(
                key: passes[index],
                value: out var current
            )
                ? ((JsonObject)current.DeepClone())
                : []);

            live[field] = value;

            using var document = JsonDocument.Parse(json: live.ToJsonString());

            if (!write(
                arg1: passes[index],
                arg2: document.RootElement.Clone()
            )) {
                return false;
            }

            written[index] = live;
        }
        for (var index = 0; (index < passes.Count); index++) {
            m_live[passes[index]] = written[index];
        }

        return true;
    }
}
