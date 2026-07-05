namespace Puck.Commands;

/// <summary>
/// The runtime form of a validated <see cref="BindingProfileDocument"/>: per-page binding tables, precomputed
/// <see cref="BindingPageView"/>s, and the ordered-chord → page lookup. Immutable — a profile edit produces a
/// new compiled instance (via <see cref="BindingProfile.Compile"/>) that <see cref="PagedInputBindings.Reload"/>
/// swaps in atomically.
/// </summary>
public sealed class CompiledBindingProfile {
    private readonly int m_basePageIndex;
    private readonly IReadOnlyList<BindingModifierDefinition> m_modifiers;
    private readonly Dictionary<string, int> m_modifierIndexBySource;
    private readonly CompiledBindingPage[] m_pages;

    internal sealed record CompiledBindingPage(
        int[] Chord,
        IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>> Table,
        BindingPageView View
    );

    internal CompiledBindingProfile(
        IReadOnlyList<BindingModifierDefinition> modifiers,
        Dictionary<string, int> modifierIndexBySource,
        CompiledBindingPage[] pages,
        int basePageIndex
    ) {
        m_basePageIndex = basePageIndex;
        m_modifierIndexBySource = modifierIndexBySource;
        m_modifiers = modifiers;
        m_pages = pages;
    }

    /// <summary>Gets the index of the no-modifier (empty-chord) page.</summary>
    public int BasePageIndex => m_basePageIndex;

    /// <summary>Gets the modifier declarations, in document order (a chord references them by index).</summary>
    public IReadOnlyList<BindingModifierDefinition> Modifiers => m_modifiers;

    /// <summary>Gets the number of pages the profile carries.</summary>
    public int PageCount => m_pages.Length;

    /// <summary>Gets the precomputed UI view of a page.</summary>
    /// <param name="pageIndex">The page index, from <c>0</c> to (<see cref="PageCount"/> - 1).</param>
    /// <returns>The page's immutable view.</returns>
    public BindingPageView ViewOf(int pageIndex) {
        return m_pages[pageIndex].View;
    }

    /// <summary>Resolves an ordered held-modifier sequence to its page, falling back to the no-modifier page when no chord matches.</summary>
    /// <param name="heldOrder">The held modifier indices, in press order.</param>
    /// <returns>The matching page index, or <see cref="BasePageIndex"/>.</returns>
    internal int PageIndexOf(ReadOnlySpan<int> heldOrder) {
        for (var pageIndex = 0; (pageIndex < m_pages.Length); pageIndex++) {
            var chord = m_pages[pageIndex].Chord;

            if (heldOrder.SequenceEqual(other: chord)) {
                return pageIndex;
            }
        }

        return m_basePageIndex;
    }

    /// <summary>Gets a page's binding table.</summary>
    /// <param name="pageIndex">The page index, from <c>0</c> to (<see cref="PageCount"/> - 1).</param>
    /// <returns>The page's <c>source → commands</c> table.</returns>
    internal IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>> TableOf(int pageIndex) {
        return m_pages[pageIndex].Table;
    }

    /// <summary>Attempts to resolve a source to the modifier it drives.</summary>
    /// <param name="source">The provider-neutral input source id.</param>
    /// <param name="modifierIndex">The modifier's index into <see cref="Modifiers"/>, when found.</param>
    /// <returns><see langword="true"/> when the source drives a declared modifier.</returns>
    internal bool TryGetModifier(string source, out int modifierIndex) {
        return m_modifierIndexBySource.TryGetValue(
            key: source,
            value: out modifierIndex
        );
    }
}
