using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The laws that make <see cref="MutationKindMask"/>'s lane width real rather than declared. The mask carries one bit
/// per mutation-kind ordinal, and both of its failure modes are SILENT — which is why each law below is written to
/// fail on the specific wrong implementation rather than merely to pass on the right one.
/// </summary>
/// <remarks>
/// <para><b>Why the obvious test is not enough.</b> A round-trip over ordinals 0-63 passes on a codec that drops
/// every bit above 63: those bits round-trip perfectly, and a truncated mask reads back as a plausible grant that
/// merely admits fewer kinds than were authored. So the 0-63 leg below is necessary and is NOT sufficient, and the
/// two legs that cross the old ceiling are what actually hold the widen up.</para>
/// <para><b>Why the negative assertion carries the weight.</b> .NET masks a shift count by the operand's width, so on
/// the former <c>ulong</c> lane <c>1UL &lt;&lt; 64</c> silently evaluated to <c>1UL &lt;&lt; 0</c>. A mask built from
/// ordinal 64 therefore did not merely fail to admit ordinal 64 — it admitted ordinal 0 (<c>UpsertKit</c>) instead,
/// opening a door nobody authored. Asserting <c>Contains(64)</c> alone would pass on that broken lane, because bit 0
/// being set makes neither assertion about 64 fail on its own; only <c>Contains(0)</c> being FALSE distinguishes a
/// real 128-bit lane from a 64-bit lane that wrapped.</para>
/// </remarks>
public sealed class MutationKindMaskLawTests {
    // One ordinal past the old ceiling: the exact value that used to alias bit 0.
    private const int PastOldCeiling = 64;

    [Fact]
    public void OrdinalPastOldCeiling_SetsItsOwnBitAndNotBitZero() {
        var mask = MutationKindMask.Empty.With(ordinal: PastOldCeiling);

        Assert.True(condition: mask.Contains(ordinal: PastOldCeiling));

        // THE DISCRIMINATOR. On a ulong lane this is where the old code fails: 1UL << 64 wraps to bit 0, so the mask
        // silently admits UpsertKit. Without this line the whole law passes on the pre-widen implementation.
        Assert.False(condition: mask.Contains(ordinal: 0));
    }

    [Fact]
    public void OrdinalPastOldCeiling_SurvivesTheGrantWireCodec() {
        // The cast-truncation catcher. BinaryWriter has no UInt128 overload, so `w.Write((ulong)value.Bits)` COMPILES
        // and silently drops this bit; the encode/decode pair below is the only leg that can see that happen.
        var authored = MutationKindMask.Empty.With(ordinal: PastOldCeiling).With(ordinal: 3);
        var decoded = RoundTrip(mask: authored);

        Assert.True(condition: decoded.Contains(ordinal: PastOldCeiling));
        Assert.True(condition: decoded.Contains(ordinal: 3));
        Assert.False(condition: decoded.Contains(ordinal: 0));
        Assert.Equal(expected: authored, actual: decoded);
    }

    [Fact]
    public void EveryOrdinalInTheOldRange_SurvivesUnchanged() {
        // Necessary but NOT sufficient (see this class's remarks): every assertion here passes on a truncating codec.
        // It is here to prove the widen APPENDED rather than re-laid — the existing range must be untouched.
        var mask = MutationKindMask.Empty;

        for (var ordinal = 0; (ordinal <= 63); ordinal++) {
            mask = mask.With(ordinal: ordinal);
        }

        var decoded = RoundTrip(mask: mask);

        for (var ordinal = 0; (ordinal <= 63); ordinal++) {
            Assert.True(condition: decoded.Contains(ordinal: ordinal));
        }

        Assert.Equal(expected: mask, actual: decoded);
    }

    [Fact]
    public void EveryDeclaredKind_FitsTheLane() {
        // The catalog's own ordinals must all be addressable, and each must set exactly the bit it names — the
        // property that ties the lane back to what actually dispatches, rather than to a hand-picked constant.
        foreach (var entry in WorldMutationKindCatalog.All()) {
            var mask = MutationKindMask.Empty.With(ordinal: entry.Ordinal);

            Assert.True(condition: mask.Contains(ordinal: entry.Ordinal));
            Assert.InRange(actual: entry.Ordinal, low: 0, high: WorldMutationKindCatalog.MaxOrdinal);
        }
    }

    [Fact]
    public void AnOrdinalOutsideTheLane_AdmitsNothing() {
        // Defence in depth behind the catalog's boot refusal: an out-of-lane ordinal must resolve to NO bit rather
        // than wrapping onto a real kind. 128 is to the new lane what 64 was to the old one.
        var mask = MutationKindMask.Empty.With(ordinal: 128);

        Assert.True(condition: mask.IsEmpty);
        Assert.False(condition: mask.Contains(ordinal: 0));
    }

    // Encodes and decodes a grant carrying the mask through the SAME leaf the live submission path and the replay
    // tape both use (the tape rides the shared grant/revoke leaf), so this covers both doors at once.
    private static MutationKindMask RoundTrip(MutationKindMask mask) {
        var grant = new WorldGrant(
            // Console, not World: the codec refuses World as a SUBMITTER by design (the world's own program acts
            // inside the process and is stamped by the server), and that refusal is not what this law is about.
            Principal: WorldPrincipal.Console,
            Capability: WorldCapability.Mutate,
            Subject: GrantSubject.All,
            Exclusive: false,
            KindMask: mask
        );

        Assert.True(condition: WorldSubmissionCodec.TryEncodeGrant(grant: grant, bytes: out var bytes, failure: out var encodeFailure), userMessage: $"encode refused: {encodeFailure}");
        Assert.True(condition: WorldSubmissionCodec.TryDecodeGrant(bytes: bytes, grant: out var decoded, failure: out var decodeFailure), userMessage: $"decode refused: {decodeFailure}");

        return (decoded.KindMask ?? MutationKindMask.Empty);
    }
}
