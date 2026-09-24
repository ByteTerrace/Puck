using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The ONE shape every refusal law in this suite is asserted through — the architectural enforcement README.md's
/// red-line #2 names ("every test can fail for a real reason ... a new law is proven once by breaking it"). Both
/// the denied case and its passing control are REQUIRED positional arguments; there is no overload that accepts
/// only one, so a control-less refusal test cannot be expressed through this API — the wrong shape is the awkward
/// one to write, not merely the discouraged one. A document law states its pair through the validation overload;
/// the single-sided document asserts serve a law whose denial and control are separate rows of one theory.
/// </summary>
internal static class Laws {
    /// <summary>Asserts a denial/control pair: <paramref name="deniedOutcome"/> must report the action's ordinary
    /// POSITIVE outcome did NOT happen (refused), and <paramref name="controlOutcome"/> — the identical action with
    /// the ONE discriminating fact reversed (the missing grant restored, the reserved name replaced, the actor's own
    /// subject substituted for someone else's) — must report that it DID. Each probe performs its own action and
    /// observation; this method only asserts the pair.</summary>
    /// <param name="lawId">A short, stable id for the case, quoted on failure.</param>
    /// <param name="deniedOutcome">The refused case. Runs first. Must report <see langword="false"/> (no positive
    /// outcome — parsed clean, the document changed, the grant was recorded, etc., whichever the law is about).</param>
    /// <param name="controlOutcome">The SAME action under the passing control. Runs second. Must report
    /// <see langword="true"/>.</param>
    public static void RefusalWithControl(string lawId, Func<bool> deniedOutcome, Func<bool> controlOutcome) {
        ArgumentNullException.ThrowIfNull(argument: deniedOutcome);
        ArgumentNullException.ThrowIfNull(argument: controlOutcome);

        Assert.False(
            condition: deniedOutcome(),
            userMessage: $"{lawId}: the denied case was expected to refuse, but its ordinary positive outcome was observed"
        );
        Assert.True(
            condition: controlOutcome(),
            userMessage: $"{lawId}: the control case was expected to succeed, but it refused"
        );
    }
    /// <summary>Asserts <paramref name="definition"/> refuses with a reason containing <paramref name="needle"/>.</summary>
    /// <param name="definition">The document under test.</param>
    /// <param name="needle">The ordinal substring the refusal names.</param>
    /// <param name="locally">Whether to run only the document-local validator, without neighbour proof.</param>
    public static void Refuses(WorldDefinition definition, string needle, bool locally = false) {
        Assert.False(
            condition: TryValidate(
                definition: definition,
                locally: locally,
                reason: out var reason
            ),
            userMessage: $"expected a refusal naming '{needle}'"
        );
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: needle
        );
    }
    /// <summary>Asserts <paramref name="denied"/> refuses naming <paramref name="needle"/> and
    /// <paramref name="control"/> validates.</summary>
    /// <param name="denied">The document carrying the one broken field.</param>
    /// <param name="control">The same document with that field well-formed.</param>
    /// <param name="needle">The ordinal substring the refusal names.</param>
    /// <param name="locally">Whether to run only the document-local validator, without neighbour proof.</param>
    public static void RefusalWithControl(WorldDefinition denied, WorldDefinition control, string needle, bool locally = false) {
        Refuses(
            definition: denied,
            locally: locally,
            needle: needle
        );
        Validates(
            definition: control,
            locally: locally
        );
    }
    /// <summary>Asserts <paramref name="definition"/> validates, reporting the refusal when it does not.</summary>
    /// <param name="definition">The document under test.</param>
    /// <param name="locally">Whether to run only the document-local validator, without neighbour proof.</param>
    public static void Validates(WorldDefinition definition, bool locally = false) => Assert.True(
        condition: TryValidate(
            definition: definition,
            locally: locally,
            reason: out var reason
        ),
        userMessage: reason
    );

    private static bool TryValidate(WorldDefinition definition, bool locally, out string reason) => (locally
        ? WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out reason
        )
        : WorldDefinitionValidator.TryValidate(
            definition: definition,
            neighbours: null,
            reason: out reason
        ));
}
