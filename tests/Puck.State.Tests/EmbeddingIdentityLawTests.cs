using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Puck.Assets;
using Xunit;

namespace Puck.State.Tests;

/// <summary>Laws for the one embedding-space identity: the limits it refuses by field name, the space that ships
/// inside them, the document spelling a <see cref="StateSpace"/> carries it in, and the hash an embedded text is filed
/// under.</summary>
public sealed class EmbeddingIdentityLawTests {
    private const string Path = "state.spaces[0]";

    [Fact]
    public void TheShippedWordSpySpaceIsInsideEveryLimit() {
        // src/Puck.World/Assets/worlds/games/wordspy.puck declares space wordSpySpace with this identity.
        var identity = new EmbeddingIdentity(Dimensions: 64, Model: "puck-fixture", Revision: "1");
        var refusals = new List<string>();

        Assert.True(condition: identity.TryValidate(path: Path, refusals: refusals));
        Assert.Empty(collection: refusals);
        Assert.True(condition: identity.IsValid);
    }
    [InlineData(StateCapacity.MinVectorDimensions)]
    [InlineData(StateCapacity.MaxVectorDimensions)]
    [Theory]
    public void DimensionsAtEitherLimitAreAdmitted(int dimensions) {
        var refusals = new List<string>();

        Assert.True(condition: Valid(dimensions: dimensions).TryValidate(path: Path, refusals: refusals));
        Assert.Empty(collection: refusals);
    }
    [InlineData((StateCapacity.MinVectorDimensions - 1))]
    [InlineData((StateCapacity.MaxVectorDimensions + 1))]
    [InlineData(0)]
    [InlineData(-1)]
    [Theory]
    public void DimensionsOutsideTheLimitsAreRefusedByName(int dimensions) {
        var identity = Valid(dimensions: dimensions);

        AssertRefusedOnly(field: "dimensions", identity: identity);
        Assert.False(condition: identity.IsValid);
    }
    [Fact]
    public void AModelAtItsLengthLimitIsAdmitted() {
        Assert.True(condition: (Valid() with { Model = new string(c: 'm', count: EmbeddingIdentity.MaxModelLength) }).IsValid);
    }
    [InlineData((EmbeddingIdentity.MaxModelLength + 1))]
    [InlineData(0)]
    [Theory]
    public void AModelThatIsEmptyOrOverLongIsRefusedByName(int length) {
        var identity = (Valid() with { Model = new string(c: 'm', count: length) });

        AssertRefusedOnly(field: "model", identity: identity);
        Assert.False(condition: identity.IsValid);
    }
    [Fact]
    public void ARevisionAtItsLengthLimitIsAdmitted() {
        Assert.True(condition: (Valid() with { Revision = new string(c: 'r', count: EmbeddingIdentity.MaxRevisionLength) }).IsValid);
    }
    [InlineData((EmbeddingIdentity.MaxRevisionLength + 1))]
    [InlineData(0)]
    [Theory]
    public void ARevisionThatIsEmptyOrOverLongIsRefusedByName(int length) {
        var identity = (Valid() with { Revision = new string(c: 'r', count: length) });

        AssertRefusedOnly(field: "revision", identity: identity);
        Assert.False(condition: identity.IsValid);
    }
    [Fact]
    public void EveryFieldOutsideItsLimitDrawsItsOwnRefusal() {
        var identity = new EmbeddingIdentity(
            Dimensions: (StateCapacity.MaxVectorDimensions + 1),
            Model: new string(c: 'm', count: (EmbeddingIdentity.MaxModelLength + 1)),
            Revision: " "
        );
        var refusals = new List<string>();

        Assert.False(condition: identity.TryValidate(path: Path, refusals: refusals));
        Assert.Equal(
            actual: refusals.Select(selector: static refusal => refusal[..refusal.IndexOf(comparisonType: StringComparison.Ordinal, value: ' ')]),
            expected: [$"{Path}.model", $"{Path}.revision", $"{Path}.dimensions"]
        );
    }
    [Fact]
    public void ASpaceCarriesItsIdentityInTheFlatDocumentSpelling() {
        const string Json = """{"name":"lore","model":"puck-fixture","revision":"1","dimensions":64}""";
        var options = new JsonSerializerOptions(options: JsonSerializerOptions.Web) {
            Converters = { new CellNameJsonConverter() },
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { StateSpace.ExtendJson } },
        };
        var space = JsonSerializer.Deserialize<StateSpace>(json: Json, options: options)!;

        Assert.Equal(actual: space.Identity, expected: new EmbeddingIdentity(Dimensions: 64, Model: "puck-fixture", Revision: "1"));
        Assert.Equal(actual: JsonSerializer.Serialize(options: options, value: space), expected: Json);
    }
    [Fact]
    public void AResolverWithoutTheSpaceModifierRefusesTheFlatSpelling() {
        var options = new JsonSerializerOptions(options: JsonSerializerOptions.Web) { Converters = { new CellNameJsonConverter() } };

        _ = Assert.Throws<InvalidOperationException>(testCode: () => JsonSerializer.Deserialize<StateSpace>(
            json: """{"name":"lore","model":"puck-fixture","revision":"1","dimensions":64}""",
            options: options
        ));
    }
    [Fact]
    public void AnEmbeddedTextHashesToThePinOfItsUtf8Bytes() {
        // The key src/Puck.World/Assets/worlds/games/wordspy.embeddings.json files "violin" under.
        Assert.Equal(
            actual: EmbeddingText.Hash(text: "violin"),
            expected: ContentPin.Parse(text: "sha256/00e3e584e242f926ff6b26bdd0cd26aac15b7ae023a7306c81a7d5708ce8459a")
        );
    }

    private static EmbeddingIdentity Valid(int dimensions = 64) => new(
        Dimensions: dimensions,
        Model: "puck-fixture",
        Revision: "1"
    );
    private static void AssertRefusedOnly(string field, EmbeddingIdentity identity) {
        var refusals = new List<string>();

        Assert.False(condition: identity.TryValidate(path: Path, refusals: refusals));
        Assert.StartsWith(
            actualString: Assert.Single(collection: refusals),
            expectedStartString: $"{Path}.{field} "
        );
    }
}
