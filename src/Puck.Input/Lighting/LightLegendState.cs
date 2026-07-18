namespace Puck.Input.Lighting;

/// <summary>
/// One tick's legend: which keyboard sources are bound (and to what category / availability), which held chord
/// modifiers are active, and which sources fired a command this tick. A host rebuilds this each tick from its
/// live binding and command state and hands it to <see cref="LightLegendComposer"/>; the reusable instance is
/// cleared and refilled rather than reallocated. Keyed by the neutral <see cref="InputSources.Keyboard"/> source
/// string, so it never depends on a device's lamp layout.
/// </summary>
/// <remarks>
/// The chord layer works by content: to recolor the board for a held chord, the host binds this state to the
/// active chord page's entries (and marks the held modifier sources with <see cref="HoldModifier"/> so their keys
/// light up). The composer renders whatever legend it is given, so the page swap <em>is</em> the recolor.
/// </remarks>
public sealed class LightLegendState {
    private readonly Dictionary<string, BindLegendEntry> m_bindings = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<string> m_flashed = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<string> m_heldModifiers = new(comparer: StringComparer.Ordinal);

    /// <summary>Clears every binding, flash, and held-modifier mark, readying the instance for a new tick.</summary>
    public void Clear() {
        m_bindings.Clear();
        m_flashed.Clear();
        m_heldModifiers.Clear();
    }

    /// <summary>Binds a keyboard source to a category for this tick.</summary>
    /// <param name="source">The neutral keyboard source string (from <see cref="InputSources.Keyboard"/>).</param>
    /// <param name="category">The category that colors the key.</param>
    /// <param name="isAvailable">Whether the command is currently available; an unavailable bind is dimmed.</param>
    public void Bind(string source, BindCategory category, bool isAvailable = true) {
        ArgumentNullException.ThrowIfNull(source);

        m_bindings[source] = new BindLegendEntry(Category: category, IsAvailable: isAvailable);
    }

    /// <summary>Marks that the command bound to a source fired this tick (drives the activation flash layer).</summary>
    /// <param name="source">The neutral keyboard source string.</param>
    public void Flash(string source) {
        ArgumentNullException.ThrowIfNull(source);

        _ = m_flashed.Add(item: source);
    }

    /// <summary>Marks a keyboard source as a currently-held chord modifier (its key gets the modifier highlight).</summary>
    /// <param name="source">The neutral keyboard source string.</param>
    public void HoldModifier(string source) {
        ArgumentNullException.ThrowIfNull(source);

        _ = m_heldModifiers.Add(item: source);
    }

    /// <summary>Gets whether the state carries any binding (a fully-empty legend paints only idle/ambient).</summary>
    public bool HasBindings => (m_bindings.Count != 0);

    /// <summary>Tries to read the binding for a source.</summary>
    /// <param name="source">The neutral keyboard source string.</param>
    /// <param name="entry">When this method returns <see langword="true"/>, the binding.</param>
    /// <returns><see langword="true"/> when the source is bound this tick.</returns>
    public bool TryGetBinding(string source, out BindLegendEntry entry) {
        return m_bindings.TryGetValue(key: source, value: out entry);
    }

    /// <summary>Gets whether a source fired a command this tick.</summary>
    /// <param name="source">The neutral keyboard source string.</param>
    /// <returns><see langword="true"/> when the source flashed.</returns>
    public bool WasFlashed(string source) {
        return m_flashed.Contains(item: source);
    }

    /// <summary>Gets whether a source is a currently-held chord modifier.</summary>
    /// <param name="source">The neutral keyboard source string.</param>
    /// <returns><see langword="true"/> when the source is a held modifier.</returns>
    public bool IsHeldModifier(string source) {
        return m_heldModifiers.Contains(item: source);
    }
}

/// <summary>One bound key's legend entry: the category that colors it and whether its command is available.</summary>
/// <param name="Category">The category that selects the key's color.</param>
/// <param name="IsAvailable">Whether the command is currently available; an unavailable bind is dimmed.</param>
public readonly record struct BindLegendEntry(BindCategory Category, bool IsAvailable);
