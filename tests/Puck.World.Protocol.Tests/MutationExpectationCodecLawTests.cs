using Puck.Commands;
using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>
/// THE LAW: a mutation payload carries the activation it expects through the canonical codec exactly, and one that
/// expects none carries none. A malformed expectation (a flag that is neither 0 nor 1, or the empty activation) is
/// refused at decode; the red leg is the well-formed payload, which decodes. The expectation is part of the payload's
/// canonical bytes, so it is bound into the operation's identity.
/// </summary>
public sealed class MutationExpectationCodecLawTests {
    private static readonly Guid Expected = Guid.Parse(input: "a1b2c3d4-e5f6-0718-293a-4b5c6d7e8f90");

    private static WorldMutation Mutation => new WorldMutation.RemovePlacement(Id: "crate1", Principal: Principal.Console);

    private static byte[] Encode(WorldSubmissionPayload.Mutation payload) {
        Assert.True(condition: WorldSubmissionCodec.TryEncode(bytes: out var bytes, failure: out var failure, kind: out var kind, payload: payload), userMessage: failure.ToString());
        Assert.Equal(actual: kind, expected: WorldSubmissionKind.Mutation);

        return bytes;
    }
    private static WorldSubmissionPayload.Mutation? Decode(byte[] bytes) => (WorldSubmissionCodec.TryDecode(
        bytes: bytes,
        failure: out _,
        kind: WorldSubmissionKind.Mutation,
        payload: out var payload
    ) ? (WorldSubmissionPayload.Mutation?)payload : null);

    [Fact]
    public void AMutationPayloadCarriesItsExpectationExactly() {
        var expecting = new WorldSubmissionPayload.Mutation(ExpectedActivation: Expected, Value: Mutation);
        var plain = new WorldSubmissionPayload.Mutation(Value: Mutation);

        Assert.Equal(actual: Decode(bytes: Encode(payload: expecting)), expected: expecting);
        Assert.Equal(actual: Decode(bytes: Encode(payload: plain)), expected: plain);
        Assert.NotEqual(actual: Encode(payload: expecting), expected: Encode(payload: plain));
    }
    [Fact]
    public void AMalformedExpectationIsRefused() {
        var bytes = Encode(payload: new WorldSubmissionPayload.Mutation(ExpectedActivation: Expected, Value: Mutation));

        // Red leg: the payload as encoded decodes.
        Assert.NotNull(@object: Decode(bytes: bytes));

        var flag = bytes.ToArray();

        flag[0] = 2;
        Assert.Null(@object: Decode(bytes: flag));

        var empty = bytes.ToArray();

        empty.AsSpan(length: 16, start: 1).Clear();
        Assert.Null(@object: Decode(bytes: empty));
    }
}
