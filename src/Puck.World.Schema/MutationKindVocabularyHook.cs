namespace Puck.World.Protocol;

/// <summary>
/// Injection seam for <see cref="MutationKindMask"/>'s name-vocabulary operations
/// (<see cref="MutationKindMask.Describe"/>, <see cref="MutationKindMask.TryParse"/>) — the mutation-kind name
/// catalog (<c>WorldMutationKindCatalog</c>) reflects over <c>WorldMutation</c>'s nested records, which live in
/// <c>Puck.World.Protocol</c>'s own project, downstream of the document model this mask is a field on
/// (<see cref="WorldGrant.KindMask"/>). <c>Puck.World</c>'s module initializer wires this before <c>Main</c>, before
/// the DI container, before any document parse a validator or JSON converter runs during.
/// </summary>
public static class MutationKindVocabularyHook {
    /// <summary>Gets the hook that renders a mask's admitted kinds as its comma-separated declared names.</summary>
    public static Func<MutationKindMask, string>? Describe { get; set; }
    /// <summary>Gets the hook that parses a comma-separated kind-name list into a mask.</summary>
    public static TryParseDelegate? TryParse { get; set; }

    /// <summary>Parses a comma-separated kind-name list into a mask.</summary>
    /// <param name="text">The comma-separated kind names.</param>
    /// <param name="mask">The parsed mask, on success.</param>
    /// <param name="unknown">The first unrecognized name, on failure.</param>
    /// <returns><see langword="true"/> when every name resolved.</returns>
    public delegate bool TryParseDelegate(string? text, out MutationKindMask mask, out string unknown);
}
