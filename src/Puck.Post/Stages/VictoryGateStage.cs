using Puck.Scene;

namespace Puck.Post;

/// <summary>
/// Tier-A stage: the 128-bit cartridge win condition's MATH holds — the properties three different games rely on to
/// converge on one shared value deterministically.
/// <list type="number">
///   <item>The two named gate constants decode to the exact bytes a human writes, and both are valid v4 GUIDs (version
///   nibble <c>4</c>, variant bits <c>10</c>).</item>
///   <item>SOLO convergence is order-independent: revealing a target's bits in ANY order (idempotent bit-sets) reaches the
///   target, with the Hamming distance strictly, monotonically falling to zero — so three games with three different
///   progress paths all land on the same 128 bits.</item>
///   <item>META convergence is cooperative and subset-proof: with shares authored so their XOR equals the target, the
///   full XOR reaches the target while dropping ANY one cabinet misses it (each share is non-zero), and no lone cabinet
///   ever sits on the target — the structural "no cabinet wins alone" guarantee.</item>
/// </list>
/// Pure CPU; no emulator or GPU. The address-mapping half (that the region really is the highest SRAM byte, read
/// bank-independently) is proven by the mirrored emulator battery's victory-region stage.
/// </summary>
internal sealed class VictoryGateStage : IPostStage {
    private const int MetaCabinets = 3;
    private static readonly int[] Seeds = [1, 7, 23, 42, 91, 1000];

    /// <inheritdoc/>
    public string Name =>
        "victory-gate";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (CheckLayout() is { } layoutFailure) {
            return PostStageOutcome.Fail(detail: layoutFailure);
        }

        // SOLO: order-independent bit-fill onto each named constant.
        foreach (var guid in (string[])[VictoryConstants.ZeroV4Guid, VictoryConstants.OneV4Guid]) {
            var target = new byte[VictoryGate.RegionByteCount];

            if (!VictoryGate.TryParseGuidBytes(text: guid, destination: target)) {
                return PostStageOutcome.Fail(detail: $"the gate constant {guid} did not parse");
            }

            foreach (var seed in Seeds) {
                if (CheckSoloConvergence(target: target, seed: seed) is { } soloFailure) {
                    return PostStageOutcome.Fail(detail: $"solo {guid} (seed {seed}): {soloFailure}");
                }
            }
        }

        // META: subset-proof cooperative XOR onto the "one" v4 constant.
        var metaTarget = new byte[VictoryGate.RegionByteCount];
        _ = VictoryGate.TryParseGuidBytes(text: VictoryConstants.OneV4Guid, destination: metaTarget);

        foreach (var seed in Seeds) {
            if (CheckMetaShares(target: metaTarget, seed: seed) is { } metaFailure) {
                return PostStageOutcome.Fail(detail: $"meta (seed {seed}): {metaFailure}");
            }
        }

        return PostStageOutcome.Pass(detail: $"layout + v4 validity for both constants; solo bit-fill order-independent & Hamming-monotone over {Seeds.Length} permutations each; meta XOR subset-proof across {MetaCabinets} cabinets over {Seeds.Length} seeds");
    }

    // The two constants decode to their exact byte images and are both structurally valid v4 GUIDs.
    private static string? CheckLayout() {
        var zero = new byte[VictoryGate.RegionByteCount];
        var one = new byte[VictoryGate.RegionByteCount];

        if (!VictoryGate.TryParseGuidBytes(text: VictoryConstants.ZeroV4Guid, destination: zero) || !VictoryGate.TryParseGuidBytes(text: VictoryConstants.OneV4Guid, destination: one)) {
            return "a named constant failed to parse";
        }

        // Zero v4: all bytes 0 except the version byte 0x40 and the variant byte 0x80.
        byte[] expectedZero = [0, 0, 0, 0, 0, 0, 0x40, 0, 0x80, 0, 0, 0, 0, 0, 0, 0];
        // One v4: all bytes 0xFF except the version byte 0x4F and the variant byte 0xBF.
        byte[] expectedOne = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x4F, 0xFF, 0xBF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

        if (!zero.AsSpan().SequenceEqual(other: expectedZero)) {
            return "the zero v4 GUID did not lay out as 00…40…80…00 (big-endian field order)";
        }

        if (!one.AsSpan().SequenceEqual(other: expectedOne)) {
            return "the one v4 GUID did not lay out as FF…4F…BF…FF (big-endian field order)";
        }

        foreach (var (name, bytes) in ((string, byte[])[])[("zero", zero), ("one", one)]) {
            if ((bytes[6] & 0xF0) != 0x40) {
                return $"the {name} v4 GUID's version nibble is not 4 (byte 6 = 0x{bytes[6]:X2})";
            }

            if ((bytes[8] & 0xC0) != 0x80) {
                return $"the {name} v4 GUID's variant bits are not 10 (byte 8 = 0x{bytes[8]:X2})";
            }
        }

        return null;
    }

    // Reveal the target's 128 bits in a seeded-random order; each idempotent bit-set must drop the Hamming distance by
    // exactly one (strict, monotone), never reach the target early, and always finish exactly on it.
    private static string? CheckSoloConvergence(byte[] target, int seed) {
        var rng = new Random(Seed: seed);
        var order = Shuffle(count: (VictoryGate.RegionByteCount * 8), rng: rng);

        // Start every bit wrong (V = ~T), so Hamming distance is a full 128 and every reveal is a genuine step.
        var v = new byte[VictoryGate.RegionByteCount];

        for (var index = 0; (index < v.Length); index++) {
            v[index] = (byte)~target[index];
        }

        var distance = Hamming(a: v, b: target);

        if (distance != (VictoryGate.RegionByteCount * 8)) {
            return $"~T should differ from T in all 128 bits, measured {distance}";
        }

        for (var step = 0; (step < order.Length); step++) {
            var position = order[step];
            var byteIndex = (position >> 3);
            var mask = (byte)(1 << (position & 7));

            // Idempotent reveal: force this bit to the target's value.
            v[byteIndex] = (byte)((v[byteIndex] & ~mask) | (target[byteIndex] & mask));

            var next = Hamming(a: v, b: target);

            if (next != (distance - 1)) {
                return $"reveal step {step} moved Hamming {distance}→{next} (expected −1); bit-fill is not monotone";
            }

            distance = next;

            var reached = VictoryGate.RegionEquals(region: v, target: target);

            if (reached != (step == (order.Length - 1))) {
                return $"reached-target={reached} at step {step} of {order.Length} — the gate fired at the wrong moment";
            }
        }

        return VictoryGate.RegionEquals(region: v, target: target) ? null : "final value is not the target";
    }

    // Author n shares whose XOR is the target (each non-zero and not itself the target), then prove the cooperative gate:
    // the full XOR hits the target, dropping any one cabinet misses it, and no lone cabinet is already on it.
    private static string? CheckMetaShares(byte[] target, int seed) {
        var rng = new Random(Seed: seed);
        var shares = new byte[MetaCabinets][];

        for (var cabinet = 0; (cabinet < (MetaCabinets - 1)); cabinet++) {
            shares[cabinet] = RandomNonTrivial(target: target, rng: rng);
        }

        // The last share closes the group: S_last = T XOR (all the others).
        var last = new byte[VictoryGate.RegionByteCount];
        target.CopyTo(array: last, index: 0);

        for (var cabinet = 0; (cabinet < (MetaCabinets - 1)); cabinet++) {
            VictoryGate.Xor(accumulator: last, operand: shares[cabinet]);
        }

        shares[MetaCabinets - 1] = last;

        // Every share must be non-zero (else dropping it wouldn't change the XOR) and not the target (else that cabinet
        // would win alone). Re-rolling the free shares keeps this deterministic per seed; the closing share we just assert.
        for (var cabinet = 0; (cabinet < MetaCabinets); cabinet++) {
            if (IsZero(bytes: shares[cabinet])) {
                return $"share {cabinet} is zero — dropping that cabinet would not change the XOR";
            }

            if (VictoryGate.RegionEquals(region: shares[cabinet], target: target)) {
                return $"share {cabinet} equals the target — that cabinet would win alone";
            }
        }

        // Full XOR == target.
        var full = new byte[VictoryGate.RegionByteCount];

        for (var cabinet = 0; (cabinet < MetaCabinets); cabinet++) {
            VictoryGate.Xor(accumulator: full, operand: shares[cabinet]);
        }

        if (!VictoryGate.RegionEquals(region: full, target: target)) {
            return "the full XOR of the shares did not reach the target";
        }

        // Dropping any one cabinet must miss the target (the all-but-i XOR equals T XOR S_i, non-target since S_i != 0).
        for (var dropped = 0; (dropped < MetaCabinets); dropped++) {
            var subset = new byte[VictoryGate.RegionByteCount];

            for (var cabinet = 0; (cabinet < MetaCabinets); cabinet++) {
                if (cabinet != dropped) {
                    VictoryGate.Xor(accumulator: subset, operand: shares[cabinet]);
                }
            }

            if (VictoryGate.RegionEquals(region: subset, target: target)) {
                return $"the subset without cabinet {dropped} still XORed to the target — a partial group could win";
            }
        }

        return null;
    }

    private static byte[] RandomNonTrivial(byte[] target, Random rng) {
        while (true) {
            var candidate = new byte[VictoryGate.RegionByteCount];
            rng.NextBytes(buffer: candidate);

            if (!IsZero(bytes: candidate) && !VictoryGate.RegionEquals(region: candidate, target: target)) {
                return candidate;
            }
        }
    }

    private static int[] Shuffle(int count, Random rng) {
        var order = new int[count];

        for (var index = 0; (index < count); index++) {
            order[index] = index;
        }

        // Fisher–Yates with the seeded RNG — a deterministic permutation per seed.
        for (var index = (count - 1); (index > 0); index--) {
            var swap = rng.Next(maxValue: (index + 1));
            (order[index], order[swap]) = (order[swap], order[index]);
        }

        return order;
    }

    private static int Hamming(byte[] a, byte[] b) {
        var bits = 0;

        for (var index = 0; (index < a.Length); index++) {
            bits += System.Numerics.BitOperations.PopCount(value: (uint)(byte)(a[index] ^ b[index]));
        }

        return bits;
    }

    private static bool IsZero(byte[] bytes) {
        foreach (var value in bytes) {
            if (value != 0) {
                return false;
            }
        }

        return true;
    }
}
