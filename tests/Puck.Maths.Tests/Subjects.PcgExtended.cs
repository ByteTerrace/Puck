namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- sampling: Pcg32Extended, Pcg32XshRr.Distance, Pcg32XshRr.Preimage/Seeking ----

    private static readonly uint[] PcgExtendedK1024Published = [
        0x35101047U, 0x038b320aU, 0x2d64ba34U, 0x5358b5f9U, 0x94ec4daeU, 0x1018b3a1U,
    ];
    private static readonly uint[] PcgExtendedK2Published = [
        0x23ad45d5U, 0x6e2b9c53U, 0x23cf9e33U, 0x24e90350U, 0x7a160dc4U, 0x5cfeb7adU,
    ];
    private static readonly uint[] PcgExtendedK64Published = [
        0xe85244a0U, 0x7112822fU, 0x9325f975U, 0xf50dea01U, 0x8cec9bbaU, 0xaa9fa4b3U,
    ];
    private static readonly ulong[] PcgExtendedAdvanceLadder = [
        0UL, 1UL, 100UL, 65534UL, 65535UL, 65536UL, 65537UL, 131072UL, 200003UL,
    ];
    private static readonly ulong[] PcgDistancePowerLadder = [
        0UL, 1UL, 2UL, 4UL, 8UL, 16UL, 1024UL, 65536UL,
        (1UL << 20), (1UL << 32), (1UL << 40), (1UL << 48), (1UL << 56), (1UL << 62), (1UL << 63),
        unchecked(0UL - 1UL),
    ];

    /// <summary>Pcg32Extended's opening draws at three published table sizes against pcg-cpp's own test output.</summary>
    public static string? PcgExtendedReferenceVectors() {
        foreach (var (k, expected) in new (int K, uint[] Expected)[] {
            (1024, PcgExtendedK1024Published),
            (2, PcgExtendedK2Published),
            (64, PcgExtendedK64Published),
        }) {
            var generator = Pcg32Extended.Create(
                state: 42UL,
                stream: 54UL,
                k: k
            );

            for (var index = 0; (index < expected.Length); ++index) {
                var drawn = generator.NextUInt32();

                if (drawn != expected[index]) { return $"pcg32_k{k} draw {index} is 0x{drawn:X8}, not the published 0x{expected[index]:X8}"; }
            }
        }

        return null;
    }
    /// <summary>An all-zero extension table reproduces the base <see cref="Pcg32XshRr"/>'s own draws exactly, over
    /// several full index wraps at three table sizes, well short of a tick boundary.</summary>
    public static string? PcgExtendedBaseEquivalence() {
        foreach (var k in new[] { 2, 64, 1024 }) {
            var generator = Pcg32Extended.Create(
                state: 7UL,
                stream: 3UL,
                k: k
            );

            generator.SetExtension(words: new uint[k]);

            var plain = Pcg32XshRr.FromRawBits(
                increment: generator.Increment,
                multiplier: generator.Multiplier,
                state: generator.State
            );
            var drawCount = ((k * 5) + 3);

            for (var index = 0; (index < drawCount); ++index) {
                var fromExtended = generator.NextUInt32();
                var fromPlain = plain.NextUInt32();

                if (fromExtended != fromPlain) { return $"k={k} draw {index}: extended gave 0x{fromExtended:X8}, the zeroed-table equivalent gave 0x{fromPlain:X8}"; }
            }
        }

        return null;
    }
    /// <summary>Setting each visited slot to <c>wanted ^ base</c> ahead of time makes the next <c>k</c> draws exactly
    /// the wanted values, and the draw past that window resumes the ordinary base-XOR-extension rule.</summary>
    public static string? PcgExtendedChosenOutputs() {
        const int k = 8;
        var original = Pcg32Extended.Create(
            state: 11UL,
            stream: 5UL,
            k: k
        );
        var probe = original.Clone();
        var indices = new int[k];

        for (var index = 0; (index < k); ++index) {
            indices[index] = ((int)(probe.State & ((ulong)(k - 1))));
            _ = probe.NextUInt32();
        }

        var baseProbe = Pcg32XshRr.FromRawBits(
            increment: original.Increment,
            multiplier: original.Multiplier,
            state: original.State
        );
        var baseDraws = new uint[k];

        for (var index = 0; (index < k); ++index) {
            baseDraws[index] = baseProbe.NextUInt32();
        }

        var chooser = Pcg32XshRr.Create(
            state: 991UL,
            stream: 13UL
        );
        var wanted = new uint[k];

        for (var index = 0; (index < k); ++index) {
            wanted[index] = chooser.NextUInt32();

            original.SetExtension(
                index: indices[index],
                word: unchecked(wanted[index] ^ baseDraws[index])
            );
        }

        for (var index = 0; (index < k); ++index) {
            var drawn = original.NextUInt32();

            if (drawn != wanted[index]) { return $"draw {index} is 0x{drawn:X8}, not the chosen 0x{wanted[index]:X8}"; }
        }

        var nextIndex = ((int)(original.State & ((ulong)(k - 1))));
        var expectedNextExtension = original.GetExtension(index: nextIndex);
        var expectedNextBase = Pcg32XshRr.FromRawBits(
            increment: original.Increment,
            multiplier: original.Multiplier,
            state: original.State
        ).NextUInt32();
        var next = original.NextUInt32();

        if (next != unchecked(expectedNextBase ^ expectedNextExtension)) { return "the draw past the chosen window did not resume the ordinary base-XOR-extension rule"; }

        return null;
    }
    /// <summary><see cref="Pcg32Extended.Advance(ulong)"/> then a draw agrees with drawing the same count then one
    /// more, across several tick-boundary crossings.</summary>
    public static string? PcgExtendedAdvanceAgreesWithDrawing() {
        foreach (var count in PcgExtendedAdvanceLadder) {
            var advanced = Pcg32Extended.Create(
                state: 7UL,
                stream: 3UL,
                k: 2
            );

            advanced.Advance(count: count);

            var walked = Pcg32Extended.Create(
                state: 7UL,
                stream: 3UL,
                k: 2
            );

            for (var step = 0UL; (step < count); ++step) {
                _ = walked.NextUInt32();
            }

            var advancedDraw = advanced.NextUInt32();
            var walkedDraw = walked.NextUInt32();

            if (advancedDraw != walkedDraw) { return $"Advance({count}) then a draw is 0x{advancedDraw:X8}, {count} draws then one more is 0x{walkedDraw:X8}"; }
            if (advanced.State != walked.State) { return $"Advance({count}) reached a different State than {count} sequential draws"; }
        }

        return null;
    }
    /// <summary>The bounded and fraction draws delegate to this type's own <see cref="Pcg32Extended.NextUInt32()"/>
    /// exactly as <see cref="Pcg32XshRr"/>'s do to its; the construction and index refusal ladder; and the passthrough
    /// properties agree with the wrapped base generator and with <see cref="Pcg32Extended.GetExtension(int)"/>.</summary>
    public static string? PcgExtendedDelegationAndRefusals() {
        var fractions = Pcg32Extended.Create(
            state: 5UL,
            stream: 5UL,
            k: 4
        );
        var raws = Pcg32Extended.Create(
            state: 5UL,
            stream: 5UL,
            k: 4
        );

        for (var index = 0; (index < 256); ++index) {
            var wide = fractions.NextUnitFraction32();
            var expectedWide = raws.NextUInt32();

            if (wide.Value != expectedWide) { return $"NextUnitFraction32 at draw {index} is {wide.Value}, not the raw draw {expectedWide}"; }

            var narrow = fractions.NextUnitFraction16();
            var expectedNarrow = ((ushort)(raws.NextUInt32() >> 16));

            if (narrow.Value != expectedNarrow) { return $"NextUnitFraction16 at draw {index} is {narrow.Value}, not {expectedNarrow}"; }
        }

        var bounded = Pcg32Extended.Create(
            state: 17UL,
            stream: 1UL,
            k: 4
        );
        var twin = Pcg32Extended.Create(
            state: 17UL,
            stream: 1UL,
            k: 4
        );

        if (bounded.NextUInt32(
            maximum: 5U,
            minimum: 5U
        ) != 5U) { return "a singleton bounded range did not return its only value"; }

        _ = twin.NextUInt32();

        if (bounded.State != twin.State) { return "a singleton bounded range did not consume exactly one draw"; }
        if (bounded.NextUInt32(
            maximum: uint.MaxValue,
            minimum: 0U
        ) != twin.NextUInt32()) { return "a full bounded range is not the raw draw"; }
        if (bounded.State != twin.State) { return "a full bounded range did not consume exactly one draw"; }

        for (var index = 0; (index < 64); ++index) {
            var ordered = Pcg32Extended.Create(
                state: ((ulong)index),
                stream: 9UL,
                k: 4
            );
            var swapped = Pcg32Extended.Create(
                state: ((ulong)index),
                stream: 9UL,
                k: 4
            );
            var low = ordered.NextUInt32(
                maximum: 40000U,
                minimum: 100U
            );
            var high = swapped.NextUInt32(
                maximum: 100U,
                minimum: 40000U
            );

            if (low != high) { return $"swapping the bounds at seed {index} changed the draw from {low} to {high}"; }
            if (
                (low < 100U) ||
                (low > 40000U)
            ) { return $"a bounded draw at seed {index} left its interval: {low}"; }
        }

        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => Pcg32Extended.Create(state: 0UL, stream: 0UL, k: 0),
            paramName: "k"
        )) { return "k=0 was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => Pcg32Extended.Create(state: 0UL, stream: 0UL, k: 3),
            paramName: "k"
        )) { return "a non-power-of-two k was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => Pcg32Extended.Create(state: 0UL, stream: 0UL, k: 2048),
            paramName: "k"
        )) { return "k above 1024 was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => Pcg32Extended.Create(state: 0UL, stream: (Pcg32XshRr.MaxStream + 1UL), k: 4),
            paramName: "stream"
        )) { return "a stream above MaxStream was accepted"; }

        _ = Pcg32Extended.Create(state: 0UL, stream: 0UL, k: 2);
        _ = Pcg32Extended.Create(state: 0UL, stream: 0UL, k: 1024);
        _ = Pcg32Extended.Create(state: 0UL, stream: Pcg32XshRr.MaxStream, k: 4);

        var indexed = Pcg32Extended.Create(
            state: 0UL,
            stream: 0UL,
            k: 4
        );

        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => indexed.GetExtension(index: -1),
            paramName: "index"
        )) { return "GetExtension(-1) was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => indexed.GetExtension(index: 4),
            paramName: "index"
        )) { return "GetExtension(k) was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => indexed.SetExtension(index: -1, word: 0U),
            paramName: "index"
        )) { return "SetExtension(-1, _) was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => indexed.SetExtension(index: 4, word: 0U),
            paramName: "index"
        )) { return "SetExtension(k, _) was accepted"; }
        if (!ThrowsExactly<ArgumentException>(
            action: () => indexed.SetExtension(words: new uint[3]),
            paramName: "words"
        )) { return "SetExtension with a short span was accepted"; }
        if (!ThrowsExactly<ArgumentException>(
            action: () => indexed.SetExtension(words: new uint[5]),
            paramName: "words"
        )) { return "SetExtension with a long span was accepted"; }

        var wrapped = Pcg32Extended.Create(
            state: 321UL,
            stream: 8UL,
            k: 4
        );

        for (var index = 0; (index < 5); ++index) {
            _ = wrapped.NextUInt32();
        }

        if (wrapped.Increment != ((8UL << 1) | 1UL)) { return $"Increment is {wrapped.Increment}"; }
        if (wrapped.Multiplier != Pcg32XshRr.DefaultMultiplier) { return $"Multiplier is {wrapped.Multiplier}"; }

        var extension = wrapped.Extension;

        if (extension.Length != 4) { return $"Extension has length {extension.Length}"; }

        for (var index = 0; (index < extension.Length); ++index) {
            if (extension[index] != wrapped.GetExtension(index: index)) { return $"Extension[{index}] disagrees with GetExtension"; }
        }

        wrapped.SetExtension(index: 1, word: 0xABCDEF01U);

        if (wrapped.GetExtension(index: 1) != 0xABCDEF01U) { return "SetExtension did not take"; }
        if (wrapped.Extension[1] != 0xABCDEF01U) { return "Extension did not reflect SetExtension"; }

        return null;
    }
    /// <summary><see cref="Pcg32Extended.CreateWithTable(ulong, ulong, ReadOnlySpan{uint})"/>'s base matches a FRESH
    /// <see cref="Pcg32XshRr.Create(ulong, ulong)"/> exactly (no draws consumed self-seeding), its extension is the
    /// authored table verbatim, and the chosen-output recipe against that fresh base — rather than
    /// <see cref="Pcg32Extended.Create(ulong, ulong, int)"/>'s already-self-seeded one — lands the next k draws on the
    /// wanted values in order; plus the table-length refusal ladder.</summary>
    public static string? PcgExtendedCreateWithTable() {
        const int k = 8;
        const ulong state = 4001UL;
        const ulong stream = 17UL;
        var seeder = Pcg32XshRr.Create(
            state: state,
            stream: stream
        );
        var table = new uint[k];

        for (var index = 0; (index < k); ++index) {
            table[index] = seeder.NextUInt32();
        }

        var withTable = Pcg32Extended.CreateWithTable(
            state: state,
            stream: stream,
            table: table
        );
        var freshBase = Pcg32XshRr.Create(
            state: state,
            stream: stream
        );

        if (withTable.State != freshBase.State) { return "CreateWithTable's base State does not match a fresh Pcg32XshRr.Create at the same seed and stream"; }
        if (withTable.Increment != freshBase.Increment) { return "CreateWithTable's base Increment does not match a fresh Pcg32XshRr.Create"; }
        if (withTable.Multiplier != freshBase.Multiplier) { return "CreateWithTable's base Multiplier does not match a fresh Pcg32XshRr.Create"; }

        var extension = withTable.Extension;

        if (extension.Length != k) { return $"Extension has length {extension.Length}, not k={k}"; }

        for (var index = 0; (index < k); ++index) {
            if (extension[index] != table[index]) { return $"Extension[{index}] is 0x{extension[index]:X8}, not the authored 0x{table[index]:X8}"; }
        }

        // The chosen-output recipe against a FRESH (zero-offset) probe: precompute the table index and base draw a
        // clean Pcg32XshRr.Create(state, stream) produces at each of the first k steps, install wanted XOR base at
        // each, and confirm the constructed generator's own next k draws equal the wanted values in order.
        var indexProbe = Pcg32XshRr.Create(
            state: state,
            stream: stream
        );
        var baseProbe = Pcg32XshRr.Create(
            state: state,
            stream: stream
        );
        var indices = new int[k];
        var baseDraws = new uint[k];

        for (var index = 0; (index < k); ++index) {
            indices[index] = ((int)(indexProbe.State & ((ulong)(k - 1))));
            _ = indexProbe.NextUInt32();
            baseDraws[index] = baseProbe.NextUInt32();
        }

        var chooser = Pcg32XshRr.Create(
            state: 990UL,
            stream: 21UL
        );
        var wanted = new uint[k];
        var chosenTable = new uint[k];

        for (var index = 0; (index < k); ++index) {
            wanted[index] = chooser.NextUInt32();
            chosenTable[indices[index]] = unchecked(wanted[index] ^ baseDraws[index]);
        }

        var chosen = Pcg32Extended.CreateWithTable(
            state: state,
            stream: stream,
            table: chosenTable
        );

        for (var index = 0; (index < k); ++index) {
            var drawn = chosen.NextUInt32();

            if (drawn != wanted[index]) { return $"draw {index} is 0x{drawn:X8}, not the chosen 0x{wanted[index]:X8}"; }
        }

        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => Pcg32Extended.CreateWithTable(state: 0UL, stream: 0UL, table: new uint[3]),
            paramName: "table"
        )) { return "a non-power-of-two table length was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => Pcg32Extended.CreateWithTable(state: 0UL, stream: 0UL, table: new uint[2048]),
            paramName: "table"
        )) { return "a table length above 1024 was accepted"; }
        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => Pcg32Extended.CreateWithTable(state: 0UL, stream: (Pcg32XshRr.MaxStream + 1UL), table: new uint[4]),
            paramName: "stream"
        )) { return "a stream above MaxStream was accepted"; }

        return null;
    }
    /// <summary><see cref="Pcg32XshRr.Distance(in Pcg32XshRr)"/> against its own <see cref="Pcg32XshRr.Advance(ulong)"/>: the
    /// reinterpreted distance equals the advance count exactly, at magnitudes sampled logarithmically across the full
    /// 64-bit range, and re-advancing by that distance lands exactly; and the cross-stream/cross-multiplier
    /// refusal.</summary>
    public static string? PcgDistanceRoundTrip() {
        var origin = Pcg32XshRr.Create(
            state: 100UL,
            stream: 41UL
        );

        foreach (var n in PcgDistancePowerLadder) {
            var advanced = Pcg32XshRr.FromRawBits(
                increment: origin.Increment,
                multiplier: origin.Multiplier,
                state: origin.State
            );

            advanced.Advance(count: n);

            var distance = unchecked((ulong)origin.Distance(other: advanced));

            if (distance != n) { return $"Distance to Advance({n}) reinterpreted as ulong is {distance}, not {n}"; }

            var landed = Pcg32XshRr.FromRawBits(
                increment: origin.Increment,
                multiplier: origin.Multiplier,
                state: origin.State
            );

            landed.Advance(count: distance);

            if (landed.State != advanced.State) { return $"Advance(Distance) for n={n} did not land on the advanced State"; }
        }

        var driver = Pcg32XshRr.Create(
            state: 7777UL,
            stream: 3UL
        );

        for (var trial = 0; (trial < 2000); ++trial) {
            var seed = driver.NextUInt32();
            var n = ((((ulong)driver.NextUInt32()) << 32) | driver.NextUInt32());
            var start = Pcg32XshRr.Create(
                state: seed,
                stream: 21UL
            );
            var advanced = Pcg32XshRr.FromRawBits(
                increment: start.Increment,
                multiplier: start.Multiplier,
                state: start.State
            );

            advanced.Advance(count: n);

            var distance = unchecked((ulong)start.Distance(other: advanced));

            if (distance != n) { return $"trial {trial}: distance {distance} does not match n {n}"; }
        }

        var left = Pcg32XshRr.Create(
            state: 1UL,
            stream: 1UL
        );
        var differentStream = Pcg32XshRr.Create(
            state: 1UL,
            stream: 2UL
        );
        var differentMultiplier = Pcg32XshRr.FromRawBits(
            increment: left.Increment,
            multiplier: 5UL,
            state: left.State
        );

        if (!ThrowsExactly<ArgumentException>(
            action: () => left.Distance(other: differentStream),
            paramName: "other"
        )) { return "a cross-stream Distance was accepted"; }
        if (!ThrowsExactly<ArgumentException>(
            action: () => left.Distance(other: differentMultiplier),
            paramName: "other"
        )) { return "a cross-multiplier Distance was accepted"; }

        return null;
    }
    /// <summary><see cref="Pcg32XshRr.Preimage"/>'s next draw is exactly the requested output over a domain of
    /// outputs and low bits, <see cref="Pcg32XshRr.Seeking"/> draws that output at the requested index, and Seeking's
    /// stream refusal.</summary>
    public static string? PcgPreimageAndSeeking() {
        var driver = Pcg32XshRr.Create(
            state: 55UL,
            stream: 2UL
        );

        for (var trial = 0; (trial < 5000); ++trial) {
            var output = driver.NextUInt32();
            var lowBits = ((((ulong)driver.NextUInt32()) << 32) | driver.NextUInt32());
            var preimage = Pcg32XshRr.Preimage(
                output: output,
                lowBits: lowBits
            );

            if (preimage.NextUInt32() != output) { return $"trial {trial}: Preimage's next draw did not equal the requested output"; }

            var stream = (driver.NextUInt32() % 1000UL);
            var drawIndex = ((ulong)(driver.NextUInt32() % 2000U));
            var seeded = Pcg32XshRr.Seeking(
                drawIndex: drawIndex,
                lowBits: lowBits,
                output: output,
                stream: stream
            );

            for (var step = 0UL; (step < drawIndex); ++step) {
                _ = seeded.NextUInt32();
            }

            var drawn = seeded.NextUInt32();

            if (drawn != output) { return $"trial {trial}: Seeking's draw {drawIndex} is 0x{drawn:X8}, not the requested 0x{output:X8}"; }
        }

        if (!ThrowsExactly<ArgumentOutOfRangeException>(
            action: () => Pcg32XshRr.Seeking(stream: (Pcg32XshRr.MaxStream + 1UL), drawIndex: 0UL, output: 0U, lowBits: 0UL),
            paramName: "stream"
        )) { return "a stream above MaxStream was accepted"; }

        _ = Pcg32XshRr.Seeking(stream: Pcg32XshRr.MaxStream, drawIndex: 0UL, output: 0U, lowBits: 0UL);

        return null;
    }
}
