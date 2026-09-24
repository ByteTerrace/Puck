namespace Puck.State.Rules;

/// <summary>Walks a compiled effect list together with every effect nested in its arms, the one walk rule-group
/// compilation and a search judge's admission both read a rule's rewinds through.</summary>
public static class RuleEffects {
    /// <summary>Returns whether an effect list, or any arm nested in it, carries a <see cref="RewindGroupEffect"/>.</summary>
    /// <param name="effects">The effect list.</param>
    /// <returns><see langword="true"/> when some effect at any depth is a rewind.</returns>
    public static bool ContainsRewind(IEnumerable<IRuleEffect> effects) => Rewinds(effects: effects).Any();
    /// <summary>Enumerates every <see cref="RewindGroupEffect"/> in an effect list and the arms nested in it, each
    /// effect before the arms it carries.</summary>
    /// <param name="effects">The effect list.</param>
    /// <returns>The rewinds, in reading order.</returns>
    public static IEnumerable<RewindGroupEffect> Rewinds(IEnumerable<IRuleEffect> effects) {
        foreach (var effect in effects) {
            if (effect is RewindGroupEffect rewind) {
                yield return rewind;
            }
            foreach (var arm in effect.Arms) {
                foreach (var nested in Rewinds(effects: arm)) {
                    yield return nested;
                }
            }
        }
    }
}
