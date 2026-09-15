using Xunit;

namespace Puck.State.Tests;

public sealed class StateVectorTests {
    [Fact]
    public void TryCreate_AdmissibleUnitVector_Succeeds() {
        var components = new sbyte[8];
        components[0] = 127;

        Assert.True(condition: StateVector.TryCreate(components: components, vector: out var vector, error: out var error), userMessage: error);
        Assert.NotNull(@object: vector);
        Assert.Equal(expected: 8, actual: vector.Dimensions);
        Assert.True(condition: vector.Components.SequenceEqual(other: components));
        Assert.True(condition: vector.Memory.Span.SequenceEqual(other: components));
    }

    [Fact]
    public void TryCreate_AllZeros_RefusedAsNonUnit() {
        var components = new sbyte[8];

        Assert.False(condition: StateVector.TryCreate(components: components, vector: out var vector, error: out var error));
        Assert.Null(@object: vector);
        Assert.Contains(expectedSubstring: "near unit length", actualString: error);
    }

    [Fact]
    public void TryCreate_ComponentMinus128_Refused() {
        var components = new sbyte[8];
        components[0] = -128;

        Assert.False(condition: StateVector.TryCreate(components: components, vector: out var vector, error: out var error));
        Assert.Null(@object: vector);
        Assert.Contains(expectedSubstring: "-128", actualString: error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(1025)]
    public void TryCreate_DimensionsOutsideBounds_Refused(int dimensions) {
        var components = new sbyte[dimensions];
        if (dimensions > 0) {
            components[0] = 127;
        }

        Assert.False(condition: StateVector.TryCreate(components: components, vector: out var vector, error: out var error));
        Assert.Null(@object: vector);
        Assert.Contains(expectedSubstring: "outside admitted bounds", actualString: error);
    }

    [Fact]
    public void EqualityAndHashing_ByteIdenticalVectorsAreEqual() {
        var c1 = new sbyte[16];
        c1[0] = 127;
        var c2 = new sbyte[16];
        c2[0] = 127;
        var c3 = new sbyte[16];
        c3[1] = 127;

        Assert.True(condition: StateVector.TryCreate(components: c1, vector: out var v1, error: out _));
        Assert.True(condition: StateVector.TryCreate(components: c2, vector: out var v2, error: out _));
        Assert.True(condition: StateVector.TryCreate(components: c3, vector: out var v3, error: out _));

        Assert.Equal(expected: v1, actual: v2);
        Assert.True(condition: v1 == v2);
        Assert.False(condition: v1 != v2);
        Assert.Equal(expected: v1!.GetHashCode(), actual: v2!.GetHashCode());
        Assert.Equal(expected: v1.ComputeDigest(), actual: v2.ComputeDigest());

        Assert.NotEqual(expected: v1, actual: v3);
        Assert.True(condition: v1 != v3);
        Assert.False(condition: v1 == v3);
    }

    [Fact]
    public void Base64UrlRoundTrip_PreservesComponents() {
        var components = new sbyte[32];
        components[0] = 90;
        components[1] = 90;

        Assert.True(condition: StateVector.TryCreate(components: components, vector: out var original, error: out var error), userMessage: error);
        var base64 = original!.ToBase64Url();

        Assert.NotEmpty(base64);
        Assert.DoesNotContain(expectedSubstring: "=", actualString: base64);
        Assert.DoesNotContain(expectedSubstring: "+", actualString: base64);
        Assert.DoesNotContain(expectedSubstring: "/", actualString: base64);

        Assert.True(condition: StateVector.TryParseBase64Url(text: base64, dimensions: 32, vector: out var parsedWithDim, error: out var err1), userMessage: err1);
        Assert.Equal(expected: original, actual: parsedWithDim);

        Assert.True(condition: StateVector.TryParseBase64Url(text: base64, vector: out var parsedInferred, error: out var err2), userMessage: err2);
        Assert.Equal(expected: original, actual: parsedInferred);
    }

    [Fact]
    public void TryParseBase64Url_InvalidInputs_Refused() {
        // Empty
        Assert.False(condition: StateVector.TryParseBase64Url(text: "", dimensions: 8, vector: out _, error: out var err1));
        Assert.Contains(expectedSubstring: "empty", actualString: err1);

        // Mismatched dimension
        var components = new sbyte[8];
        components[0] = 127;
        Assert.True(condition: StateVector.TryCreate(components: components, vector: out var vec, error: out _));
        var b64 = vec!.ToBase64Url();

        Assert.False(condition: StateVector.TryParseBase64Url(text: b64, dimensions: 16, vector: out _, error: out var err2));
        Assert.Contains(expectedSubstring: "does not match expected dimensions", actualString: err2);

        // Corrupted base64
        Assert.False(condition: StateVector.TryParseBase64Url(text: "!!!notbase64!!!", vector: out _, error: out var err3));
        Assert.Contains(expectedSubstring: "Failed to decode", actualString: err3);
    }
}
