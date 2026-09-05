using Puck.Physics.Motion;

namespace Puck.Physics.Tests;

/// <summary>Law coverage for the publishable fact vocabulary: the mask and the predicate enum are one vocabulary,
/// every bit is distinct, and the read-back spelling is total.</summary>
public sealed class BodyFactVocabularyLawTests {
    [Fact]
    public void EveryPublishableFactCarriesADistinctBit_AndAllIsExactlyTheirUnion() {
        var union = BodyFacts.None;
        var seen = new HashSet<BodyFacts>();

        foreach (var fact in BodyFactVocabulary.Publishable) {
            var bit = BodyFactVocabulary.Bit(fact: fact);

            Assert.NotEqual(expected: BodyFacts.None, actual: bit);
            Assert.True(condition: seen.Add(item: bit), userMessage: $"{fact} shares a bit with an earlier fact");

            union |= bit;
        }

        Assert.Equal(expected: BodyFacts.All, actual: union);
    }
    [Fact]
    public void OnlyAffectedByLacksABit_SoNoPredicateIsSilentlyUnpublishable() {
        foreach (var fact in Enum.GetValues<ActionFact>()) {
            var expected = (fact != ActionFact.AffectedBy);

            Assert.Equal(
                actual: (BodyFactVocabulary.Bit(fact: fact) != BodyFacts.None),
                expected: expected
            );
        }
    }
    [Fact]
    public void TheEchoJoinsSetBitsInBitOrder_AndReadsNoneWhenEmpty() {
        Assert.Equal(actual: BodyFactVocabulary.Describe(facts: BodyFacts.None), expected: "none");
        Assert.Equal(actual: BodyFactVocabulary.Describe(facts: BodyFacts.Grounded), expected: "grounded");

        // Bit order, not the order the caller happened to write them in.
        Assert.Equal(
            actual: BodyFactVocabulary.Describe(facts: (BodyFacts.HoldingUnwalkable | BodyFacts.Falling | BodyFacts.Airborne)),
            expected: "airborne|falling|holdingunwalkable"
        );
        Assert.Equal(
            actual: BodyFactVocabulary.Describe(facts: BodyFacts.All),
            expected: "grounded|airborne|rising|falling|inmedium|atmediumband|holdingunwalkable|unsupported|resting"
        );
    }
    [Fact]
    public void EveryFactSpellsADistinctLowerCaseToken() {
        var tokens = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var fact in Enum.GetValues<ActionFact>()) {
            var token = BodyFactVocabulary.Token(fact: fact);

            Assert.Equal(actual: token, expected: token.ToLowerInvariant());
            Assert.True(condition: tokens.Add(item: token), userMessage: $"{fact} reuses the token '{token}'");
        }
    }
}
