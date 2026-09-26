using System.Text.Json;
using Puck.Abstractions.Sources;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for a source's settings and their one canonical form. Members sort by ordinal name at every depth, numbers write
/// by value and strings by value, array order is kept, and no settings equal empty ones. Every pair of settings that
/// <see cref="JsonElement.DeepEquals"/> calls equal has one canonical form, <see cref="ImageSourceSettings.Equal"/> is
/// equality of that form, <see cref="ImageSourceSettings.HashOf"/> agrees with it, and the digest is a fixed-length
/// lowercase hex of it.
/// </summary>
public sealed class ImageSourceSettingsLawTests {
    // Settings spelled as a JSON object, members in its order.
    private static Dictionary<string, JsonElement> Settings(string json) {
        using var document = JsonDocument.Parse(json: json);

        var settings = new Dictionary<string, JsonElement>(comparer: StringComparer.Ordinal);

        foreach (var member in document.RootElement.EnumerateObject()) {
            settings.Add(
                key: member.Name,
                value: member.Value.Clone()
            );
        }

        return settings;
    }

    [InlineData("""{ "b": 1, "a": 2 }""", """{"a":2,"b":1}""")]
    [InlineData("""{ "w": 96.0 }""", """{"w":96}""")]
    [InlineData("""{ "w": 9.6e1 }""", """{"w":96}""")]
    [InlineData("""{ "w": 960E-1 }""", """{"w":96}""")]
    [InlineData("""{ "w": 0.0096e+4 }""", """{"w":96}""")]
    [InlineData("""{ "w": 1200 }""", """{"w":12e2}""")]
    [InlineData("""{ "w": -0.5 }""", """{"w":-5e-1}""")]
    [InlineData("""{ "w": -0.0 }""", """{"w":0}""")]
    [InlineData("""{ "w": 0e7 }""", """{"w":0}""")]
    [InlineData("""{ "s": "A\n" }""", """{"s":"A\n"}""")]
    [InlineData("""{ "o": { "z": [3, 1.0], "a": null, "m": true } }""", """{"o":{"a":null,"m":true,"z":[3,1]}}""")]
    [InlineData("""{}""", """{}""")]
    [Theory]
    public void TheCanonicalFormSortsMembersAndWritesValues(string spelled, string canonical) => Assert.Equal(
        actual: ImageSourceSettings.Canonical(settings: Settings(json: spelled)),
        expected: canonical
    );
    [Fact]
    public void NoSettingsAreEmptySettings() {
        Assert.Equal(expected: "{}", actual: ImageSourceSettings.Canonical(settings: null));
        Assert.True(condition: ImageSourceSettings.Equal(left: null, right: Settings(json: "{}")));
        Assert.Equal(expected: ImageSourceSettings.HashOf(settings: null), actual: ImageSourceSettings.HashOf(settings: Settings(json: "{}")));
        Assert.Equal(expected: ImageSourceSettings.Digest(settings: null), actual: ImageSourceSettings.Digest(settings: Settings(json: "{}")));
    }
    [Fact]
    public void EverySpellingDeepEqualsCallsEqualIsEqualAndDigestsAlike() {
        string[] spellings = [
            """{ "width": 96, "height": 6 }""",
            """{ "height": 6.0, "width": 9.6e1 }""",
            """{ "width": 960e-1, "height": 0.6E+1 }""",
            """{ "width": 97, "height": 6 }""",
            """{ "width": 96, "height": 6, "depth": 1 }""",
            """{ "width": "96", "height": 6 }""",
            """{ "width": [96, 6] }""",
            """{ "width": [6, 96] }""",
            """{ "width": { "b": 1, "a": 2 } }""",
            """{ "width": { "a": 2.0, "b": 10e-1 } }""",
        ];

        foreach (var left in spellings) {
            foreach (var right in spellings) {
                var (a, b) = (Settings(json: left), Settings(json: right));
                var deepEqual = ((a.Count == b.Count) && a.All(predicate: member => (b.TryGetValue(key: member.Key, value: out var theirs) && JsonElement.DeepEquals(element1: member.Value, element2: theirs))));
                var equal = ImageSourceSettings.Equal(left: a, right: b);

                Assert.True(condition: (!deepEqual || equal), userMessage: $"{left} and {right}");
                Assert.Equal(
                    actual: equal,
                    expected: string.Equals(a: ImageSourceSettings.Canonical(settings: a), b: ImageSourceSettings.Canonical(settings: b), comparisonType: StringComparison.Ordinal)
                );
                Assert.Equal(
                    actual: equal,
                    expected: string.Equals(a: ImageSourceSettings.Digest(settings: a), b: ImageSourceSettings.Digest(settings: b), comparisonType: StringComparison.Ordinal)
                );

                if (equal) {
                    Assert.Equal(expected: ImageSourceSettings.HashOf(settings: a), actual: ImageSourceSettings.HashOf(settings: b));
                }
            }
        }
    }
    [Fact]
    public void TheDigestIsAFixedLengthOfLowercaseHex() => Assert.Matches(
        actualString: ImageSourceSettings.Digest(settings: Settings(json: """{ "payload": "relight" }""")),
        expectedRegexPattern: $"^[0-9a-f]{{{ImageSourceSettings.DigestLength}}}$"
    );
}
