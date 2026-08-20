using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>
/// The wire law for the name-keyed grant subjects: <c>creation:&lt;id&gt;</c>/<c>placement:&lt;id&gt;</c>/
/// <c>adjacency:&lt;name&gt;</c> survive the
/// SAME grant leaf the live submission path and the replay tape both ride, id lane intact — a subject whose id was
/// dropped in transit would seat a hold over the empty row nothing can ever match. The paired refusal is the
/// retirement convention: wire value 8 (the former <c>Table</c> kind) must stay undecodable rather than aliasing a
/// successor, which is exactly what reassigning it would do silently.
/// </summary>
public sealed class RowScopedSubjectCodecLawTests {
    /// <summary>The retired discriminant — never reassigned, and the control that proves the decode side is a closed
    /// map rather than a cast.</summary>
    private const byte RetiredTableWireValue = 8;

    [Theory]
    [InlineData("piece-nw")]
    [InlineData("slot-nw")]
    public void RowScopedSubjectsRoundTripWithTheirId(string id) {
        Assert.Equal(actual: RoundTrip(subject: GrantSubject.Creation(id: id)), expected: GrantSubject.Creation(id: id));
        Assert.Equal(actual: RoundTrip(subject: GrantSubject.Placement(id: id)), expected: GrantSubject.Placement(id: id));
        Assert.Equal(actual: RoundTrip(subject: GrantSubject.Adjacency(name: id)), expected: GrantSubject.Adjacency(name: id));
    }

    [Fact]
    public void RetiredSubjectWireValueRefuses_WhileItsSuccessorsDecode() {
        var placement = EncodeGrant(subject: GrantSubject.Placement(id: "slot-nw"));
        var kindOffset = FindSubjectKindOffset(bytes: placement);

        // The one reversed fact: the same bytes with the subject's kind discriminant rewritten to the retired value.
        var sabotaged = placement.ToArray();

        sabotaged[kindOffset] = RetiredTableWireValue;

        Assert.False(condition: WorldSubmissionCodec.TryDecodeGrant(bytes: sabotaged, failure: out _, grant: out _), userMessage: "wire value 8 is retired and must never decode — reassigning it would silently alias a successor kind");
        Assert.True(condition: WorldSubmissionCodec.TryDecodeGrant(bytes: placement, failure: out var failure, grant: out _), userMessage: $"the unmodified control was expected to decode: {failure}");
    }

    private static byte[] EncodeGrant(GrantSubject subject) {
        // Console, not World: the codec refuses World as a SUBMITTER by design, which is not what this law is about.
        var grant = new WorldGrant(
            Capability: WorldCapability.Mutate,
            Exclusive: false,
            Principal: WorldPrincipal.Console,
            Subject: subject
        );

        Assert.True(condition: WorldSubmissionCodec.TryEncodeGrant(bytes: out var bytes, failure: out var failure, grant: grant), userMessage: $"encode refused: {failure}");

        return bytes;
    }
    /// <summary>Locates the subject's kind byte by encoding the SAME grant twice with two subject kinds that differ in
    /// nothing else, and returning the one offset whose byte differs — so this law never hard-codes a leaf layout that
    /// would go stale the moment a field moves.</summary>
    private static int FindSubjectKindOffset(byte[] bytes) {
        var other = EncodeGrant(subject: GrantSubject.Creation(id: "slot-nw"));

        Assert.Equal(actual: other.Length, expected: bytes.Length);

        var offset = -1;

        for (var index = 0; (index < bytes.Length); index++) {
            if (bytes[index] != other[index]) {
                Assert.Equal(actual: offset, expected: -1);

                offset = index;
            }
        }

        Assert.NotEqual(actual: offset, expected: -1);

        return offset;
    }
    private static GrantSubject RoundTrip(GrantSubject subject) {
        Assert.True(condition: WorldSubmissionCodec.TryDecodeGrant(bytes: EncodeGrant(subject: subject), failure: out var failure, grant: out var decoded), userMessage: $"decode refused: {failure}");

        return decoded.Subject;
    }
}
