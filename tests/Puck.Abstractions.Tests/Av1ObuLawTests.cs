using Puck.Abstractions.Recording;

namespace Puck.Abstractions.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="Av1Obu.TryRead"/> frames one OBU and refuses a header the walk cannot
/// trust, so every walk over an encoder's temporal unit stops at the same bytes.</summary>
public sealed class Av1ObuLawTests {
    [Fact]
    public void ASizedObuReportsItsTypeAndPayloadAndAdvancesPastIt() {
        // A temporal delimiter (type 2, size field, empty), then a frame header (type 3) carrying two payload bytes.
        byte[] unit = [0x12, 0x00, 0x1A, 0x02, 0xAA, 0xBB];
        var index = 0;

        Assert.True(condition: Av1Obu.TryRead(
            index: ref index,
            obuType: out var firstType,
            payloadLength: out var firstLength,
            payloadOffset: out _,
            temporalUnit: unit
        ));
        Assert.Equal(
            actual: (firstType, firstLength, index),
            expected: (2, 0, 2)
        );
        Assert.True(condition: Av1Obu.TryRead(
            index: ref index,
            obuType: out var secondType,
            payloadLength: out var secondLength,
            payloadOffset: out var secondOffset,
            temporalUnit: unit
        ));
        Assert.Equal(
            actual: (secondType, secondLength, secondOffset, index),
            expected: (3, 2, 4, 6)
        );
        Assert.False(condition: Av1Obu.TryRead(
            index: ref index,
            obuType: out _,
            payloadLength: out _,
            payloadOffset: out _,
            temporalUnit: unit
        ));
    }
    [Fact]
    public void ASetForbiddenBitStopsTheWalkAndLeavesTheIndex() {
        byte[] unit = [0x92, 0x00];
        var index = 0;

        Assert.False(condition: Av1Obu.TryRead(
            index: ref index,
            obuType: out _,
            payloadLength: out _,
            payloadOffset: out _,
            temporalUnit: unit
        ));
        Assert.Equal(
            actual: index,
            expected: 0
        );
    }
    [Theory]
    [InlineData(new byte[] { 0x0A, 0x05, 0x01 })]
    [InlineData(new byte[] { 0x0E })]
    [InlineData(new byte[] { 0x04 })]
    public void ATruncatedSizeOrPayloadIsRefused(byte[] unit) {
        var index = 0;

        Assert.False(condition: Av1Obu.TryRead(
            index: ref index,
            obuType: out _,
            payloadLength: out _,
            payloadOffset: out _,
            temporalUnit: unit
        ));
    }
    [Fact]
    public void AnObuWithoutASizeFieldRunsToTheEndOfTheUnit() {
        byte[] unit = [0x30, 0x01, 0x02, 0x03];
        var index = 0;

        Assert.True(condition: Av1Obu.TryRead(
            index: ref index,
            obuType: out var obuType,
            payloadLength: out var payloadLength,
            payloadOffset: out var payloadOffset,
            temporalUnit: unit
        ));
        Assert.Equal(
            actual: (obuType, payloadLength, payloadOffset, index),
            expected: (6, 3, 1, 4)
        );
    }
}
