namespace Puck.State;

/// <summary>The facet a host serves when it holds bodies a rule can sweep: the list a
/// <see cref="RuleFacts.ForEachBodies"/> rule iterates. A rule naming <c>forEach: $bodies</c> needs it, so
/// <see cref="RuleNeeds.Admit"/> refuses that rule by this facet's name on a host without bodies.</summary>
public interface ILiveBodies : IFacet {
    /// <summary>Appends the index of every body active at this instant, in ascending index order. The caller clears
    /// the list first; the host only appends.</summary>
    /// <param name="into">The list the indices are appended to.</param>
    void CollectLiveBodies(List<int> into);
}
