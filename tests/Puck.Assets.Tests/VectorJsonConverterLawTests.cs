using System.Numerics;
using System.Text.Json;
using Xunit;

namespace Puck.Assets.Tests;

public sealed class VectorJsonConverterLawTests {
    [Fact]
    public void Vector2_RoundTrips_BitExactly() {
        var value = new Vector2(x: -1.5f, y: 123456.75f);
        var json = JsonSerializer.Serialize(value: value, options: Documents.DocumentJsonOptions.Shared);
        var read = JsonSerializer.Deserialize<Vector2>(json: json, options: Documents.DocumentJsonOptions.Shared);

        Assert.DoesNotContain(expectedSubstring: "\"x\"", actualString: json, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: value, actual: read);
    }
    [Fact]
    public void Vector3_RoundTrips_BitExactly() {
        var value = new Vector3(x: 0.1f, y: -2f, z: 987654.3f);
        var json = JsonSerializer.Serialize(value: value, options: Documents.DocumentJsonOptions.Shared);
        var read = JsonSerializer.Deserialize<Vector3>(json: json, options: Documents.DocumentJsonOptions.Shared);

        Assert.DoesNotContain(expectedSubstring: "\"x\"", actualString: json, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: value, actual: read);
    }
    [Fact]
    public void Quaternion_RoundTrips_BitExactly_InXyzwOrder() {
        var value = new Quaternion(x: 0.34202015f, y: 0f, z: 0f, w: 0.9396926f);
        var json = JsonSerializer.Serialize(value: value, options: Documents.DocumentJsonOptions.Shared);
        var read = JsonSerializer.Deserialize<Quaternion>(json: json, options: Documents.DocumentJsonOptions.Shared);
        // The wire order is [x, y, z, w] — the first array element is X, not W.
        var firstNumber = json.TrimStart('[', '\r', '\n', ' ').Split(separator: ',')[0].Trim();

        Assert.DoesNotContain(expectedSubstring: "isIdentity", actualString: json, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: "0.34202015", actual: firstNumber);
        Assert.Equal(expected: value, actual: read);
    }
    [Theory]
    [InlineData("[1,2]")]
    [InlineData("[1,2,3,4]")]
    public void Vector3_RefusesWrongArity(string json) =>
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<Vector3>(json: json, options: Documents.DocumentJsonOptions.Shared));
    [Fact]
    public void Vector3_RefusesTheObjectForm() {
        var exception = Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<Vector3>(json: """{"x":1,"y":2,"z":3}""", options: Documents.DocumentJsonOptions.Shared));

        Assert.Contains(expectedSubstring: "array", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("[1,2,3,4,5]")]
    public void Quaternion_RefusesWrongArity(string json) =>
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<Quaternion>(json: json, options: Documents.DocumentJsonOptions.Shared));
    [Fact]
    public void Quaternion_RefusesTheIsIdentityObjectForm() {
        var exception = Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<Quaternion>(json: """{"isIdentity":true,"w":1,"x":0,"y":0,"z":0}""", options: Documents.DocumentJsonOptions.Shared));

        Assert.Contains(expectedSubstring: "isIdentity", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void Vector3_NestedInADocument_RefusalNamesTheFieldPath() {
        var json = """{"shapes":[{"position":{"x":0,"y":0,"z":0}}]}""";
        var exception = Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<ShapesWrapper>(json: json, options: Documents.DocumentJsonOptions.Shared));

        Assert.Equal(expected: "$.shapes[0].position", actual: exception.Path);
    }

    private sealed record ShapesWrapper(IReadOnlyList<ShapeWrapper> Shapes);
    private sealed record ShapeWrapper(Vector3 Position);
}
