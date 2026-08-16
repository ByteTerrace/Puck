using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for <see cref="WorldTcpWireFormat.ReadLengthPrefixedString"/> — the packed-body string field
/// <c>WorldRemoteAuthority.TryReadCompletion</c> and <c>WorldFederatedServerLink</c> both read a remote authority's
/// Session/Query Completion frame through. That body arrives over the wire from a peer authority, so a declared
/// length that runs past it is untrusted input, not a programmer error.
/// </summary>
public sealed class WorldTcpWireFormatLawTests {
    /// <summary>The control: a well-formed field decodes and advances the cursor exactly past it.</summary>
    [Fact]
    public void ReadLengthPrefixedString_DecodesAWellFormedField() {
        byte[] body = [3, 0, ((byte)'a'), ((byte)'b'), ((byte)'c')];
        var offset = 0;

        var text = WorldTcpWireFormat.ReadLengthPrefixedString(
            body: body,
            offset: ref offset,
            ok: out var ok
        );

        Assert.True(condition: ok);
        Assert.Equal(
            actual: text,
            expected: "abc"
        );
        Assert.Equal(
            actual: offset,
            expected: 5
        );
    }
    /// <summary>A declared length that runs past the end of the body must report failure rather than throw.
    /// Falsifier: removing the post-prefix bounds check before the <c>Slice</c> call turns this into an escaping
    /// <see cref="ArgumentOutOfRangeException"/> instead of <c>ok</c> reading <see langword="false"/>.</summary>
    [Fact]
    public void ReadLengthPrefixedString_ReportsFailure_ForALengthThatRunsPastTheBody() {
        byte[] body = [100, 0, 0x41, 0x42];
        var offset = 0;

        var text = WorldTcpWireFormat.ReadLengthPrefixedString(
            body: body,
            offset: ref offset,
            ok: out var ok
        );

        Assert.False(condition: ok);
        Assert.Equal(
            actual: text,
            expected: string.Empty
        );
        Assert.Equal(
            expected: body.Length,
            actual: offset
        );
    }
    /// <summary>A body too short to even carry the two-byte length prefix is the same failure.</summary>
    [Fact]
    public void ReadLengthPrefixedString_ReportsFailure_WhenTheLengthPrefixItselfIsTruncated() {
        byte[] body = [0x41];
        var offset = 0;

        var text = WorldTcpWireFormat.ReadLengthPrefixedString(
            body: body,
            offset: ref offset,
            ok: out var ok
        );

        Assert.False(condition: ok);
        Assert.Equal(
            actual: text,
            expected: string.Empty
        );
    }
}
