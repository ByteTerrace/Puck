using Xunit;

namespace Puck.State.Tests;

public sealed class StateVectorTests {
    [Fact]
    public void TryCreate_AdmissibleUnitVector_Succeeds() {
        var components = new sbyte[8];

        components[0] = 127;

        Assert.True(condition: StateVector.TryCreate(components: components, error: out var error, vector: out var vector), userMessage: error);
        Assert.NotNull(@object: vector);
        Assert.Equal(expected: 8, actual: vector.Dimensions);
        Assert.True(condition: vector.Components.SequenceEqual(other: components));
        Assert.True(condition: vector.Memory.Span.SequenceEqual(other: components));
    }
    [Fact]
    public void TryCreate_AllZeros_RefusedAsNonUnit() {
        var components = new sbyte[8];

        Assert.False(condition: StateVector.TryCreate(components: components, error: out var error, vector: out var vector));
        Assert.Null(@object: vector);
        Assert.Contains(actualString: error, expectedSubstring: "near unit length");
    }
    [Fact]
    public void TryCreate_ComponentMinus128_Refused() {
        var components = new sbyte[8];

        components[0] = -128;

        Assert.False(condition: StateVector.TryCreate(components: components, error: out var error, vector: out var vector));
        Assert.Null(@object: vector);
        Assert.Contains(actualString: error, expectedSubstring: "-128");
    }
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(1025)]
    [Theory]
    public void TryCreate_DimensionsOutsideBounds_Refused(int dimensions) {
        var components = new sbyte[dimensions];

        if (dimensions > 0) {
            components[0] = 127;
        }

        Assert.False(condition: StateVector.TryCreate(components: components, error: out var error, vector: out var vector));
        Assert.Null(@object: vector);
        Assert.Contains(actualString: error, expectedSubstring: "outside admitted bounds");
    }
    [Fact]
    public void EqualityAndHashing_ByteIdenticalVectorsAreEqual() {
        var c1 = new sbyte[16];

        c1[0] = 127;
        var c2 = new sbyte[16];

        c2[0] = 127;
        var c3 = new sbyte[16];

        c3[1] = 127;

        Assert.True(condition: StateVector.TryCreate(components: c1, error: out _, vector: out var v1));
        Assert.True(condition: StateVector.TryCreate(components: c2, error: out _, vector: out var v2));
        Assert.True(condition: StateVector.TryCreate(components: c3, error: out _, vector: out var v3));

        Assert.Equal(actual: v2, expected: v1);
        Assert.True(condition: (v1 == v2));
        Assert.False(condition: (v1 != v2));
        Assert.Equal(expected: v1!.GetHashCode(), actual: v2!.GetHashCode());
        Assert.Equal(expected: v1.ComputeDigest(), actual: v2.ComputeDigest());

        Assert.NotEqual(actual: v3, expected: v1);
        Assert.True(condition: (v1 != v3));
        Assert.False(condition: (v1 == v3));
    }
    [Fact]
    public void Base64UrlRoundTrip_PreservesComponents() {
        var components = new sbyte[32];

        components[0] = 90;
        components[1] = 90;

        Assert.True(condition: StateVector.TryCreate(components: components, error: out var error, vector: out var original), userMessage: error);
        var base64 = original!.ToBase64Url();

        Assert.NotEmpty(collection: base64);
        Assert.DoesNotContain(actualString: base64, expectedSubstring: "=");
        Assert.DoesNotContain(actualString: base64, expectedSubstring: "+");
        Assert.DoesNotContain(actualString: base64, expectedSubstring: "/");

        Assert.True(condition: StateVector.TryParseBase64Url(dimensions: 32, error: out var err1, text: base64, vector: out var parsedWithDim), userMessage: err1);
        Assert.Equal(actual: parsedWithDim, expected: original);

        Assert.True(condition: StateVector.TryParseBase64Url(error: out var err2, text: base64, vector: out var parsedInferred), userMessage: err2);
        Assert.Equal(actual: parsedInferred, expected: original);
    }
    [Fact]
    public void TryParseBase64Url_InvalidInputs_Refused() {
        // Empty
        Assert.False(condition: StateVector.TryParseBase64Url(dimensions: 8, error: out var err1, text: "", vector: out _));
        Assert.Contains(actualString: err1, expectedSubstring: "empty");

        // Mismatched dimension
        var components = new sbyte[8];

        components[0] = 127;
        Assert.True(condition: StateVector.TryCreate(components: components, error: out _, vector: out var vec));
        var b64 = vec!.ToBase64Url();

        Assert.False(condition: StateVector.TryParseBase64Url(dimensions: 16, error: out var err2, text: b64, vector: out _));
        Assert.Contains(actualString: err2, expectedSubstring: "does not match expected dimensions");

        // Corrupted base64
        Assert.False(condition: StateVector.TryParseBase64Url(error: out var err3, text: "!!!notbase64!!!", vector: out _));
        Assert.Contains(actualString: err3, expectedSubstring: "Failed to decode");
    }
}
