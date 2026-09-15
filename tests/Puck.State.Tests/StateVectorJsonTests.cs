using System.Text.Json;
using Xunit;

namespace Puck.State.Tests;

public sealed class StateVectorJsonTests {
    private sealed record VectorContainer(StateVector? Vector, string Name);

    [Fact]
    public void Serialize_WritesBase64UrlString() {
        var components = new sbyte[16];
        components[0] = 127;
        Assert.True(condition: StateVector.TryCreate(components: components, vector: out var vector, error: out _));

        var json = JsonSerializer.Serialize(value: vector);
        Assert.Equal(expected: $"\"{vector!.ToBase64Url()}\"", actual: json);
    }

    [Fact]
    public void Deserialize_ValidBase64UrlString_ProducesMatchingVector() {
        var components = new sbyte[16];
        components[0] = 127;
        Assert.True(condition: StateVector.TryCreate(components: components, vector: out var vector, error: out _));

        var json = $"\"{vector!.ToBase64Url()}\"";
        var deserialized = JsonSerializer.Deserialize<StateVector>(json: json);

        Assert.NotNull(@object: deserialized);
        Assert.Equal(expected: vector, actual: deserialized);
    }

    [Fact]
    public void RoundTrip_ContainerObject_PreservesVector() {
        var components = new sbyte[32];
        components[0] = 90;
        components[1] = 90;
        Assert.True(condition: StateVector.TryCreate(components: components, vector: out var vector, error: out _));

        var container = new VectorContainer(Vector: vector, Name: "test");
        var json = JsonSerializer.Serialize(value: container);
        var restored = JsonSerializer.Deserialize<VectorContainer>(json: json);

        Assert.NotNull(@object: restored);
        Assert.Equal(expected: "test", actual: restored.Name);
        Assert.Equal(expected: vector, actual: restored.Vector);
    }

    [Fact]
    public void Deserialize_Null_ReturnsNull() {
        var deserialized = JsonSerializer.Deserialize<StateVector>(json: "null");
        Assert.Null(@object: deserialized);
    }

    [Fact]
    public void Deserialize_NonStringToken_ThrowsJsonException() {
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<StateVector>(json: "123"));
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<StateVector>(json: "[]"));
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<StateVector>(json: "{}"));
    }

    [Fact]
    public void Deserialize_MalformedBase64Url_ThrowsJsonException() {
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize<StateVector>(json: "\"not-valid-base64!\""));
    }
}
