using Puck.Shaders;

namespace Puck.World;

/// <summary>The live <see cref="FullscreenPassNode"/> instances the composed <c>render.extensions</c> chain built,
/// keyed by their document id — a presentation-scope parameter binding's only write target. Filled additively by the
/// render-root factory's <c>Decorate</c> closure as each pass composes; never mutated afterward. Empty in a boot
/// shape that never composes presentation (headless), so a parameter binding's write there is a harmless no-op.
/// </summary>
public sealed class WorldPostRenderExtensionPasses {
    private readonly Dictionary<string, FullscreenPassNode> m_passes = new(comparer: StringComparer.Ordinal);

    /// <summary>Registers a composed pass under its document id.</summary>
    /// <param name="id">The <c>render.extensions[].id</c> the pass was composed from.</param>
    /// <param name="pass">The composed pass.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="pass"/> is <see langword="null"/>.</exception>
    public void Add(string id, FullscreenPassNode pass) {
        ArgumentException.ThrowIfNullOrEmpty(argument: id);
        ArgumentNullException.ThrowIfNull(argument: pass);

        m_passes[id] = pass;
    }
    /// <summary>Looks up a composed pass by its document id.</summary>
    /// <param name="id">The <c>render.extensions[].id</c>.</param>
    /// <param name="pass">The pass, when composed this boot.</param>
    /// <returns><see langword="true"/> when a pass with that id was composed.</returns>
    public bool TryGet(string id, out FullscreenPassNode pass) => m_passes.TryGetValue(key: id, value: out pass!);
}
