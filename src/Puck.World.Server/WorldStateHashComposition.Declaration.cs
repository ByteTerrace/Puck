using Puck.Maths;

namespace Puck.World.Server;

public static partial class WorldStateHashComposition {
    /// <summary>Folds what a state section declares rather than stores: each row's kind, envelope, capacity,
    /// overflow, drive and eviction policy, domain, audience, and time traits, and each cell's own traits.</summary>
    /// <param name="hash">The running hash.</param>
    /// <param name="state">The state section, or <see langword="null"/>.</param>
    /// <remarks>A re-declared row set changes what every future read of the row answers while leaving its stored
    /// values alone, so the declaration is folded beside the store rather than assumed constant.</remarks>
    public static void AppendDeclaration(ref Fnv1aHash hash, WorldStateSection? state) {
        var rows = (state?.World ?? []);

        hash.Add(value: ((uint)rows.Count));

        for (var rowIndex = 0; (rowIndex < rows.Count); rowIndex++) {
            var row = rows[rowIndex];
            var cells = (row.Cells ?? []);

            AppendString(
                hash: ref hash,
                value: row.Name.Value
            );
            hash.Add(value: ((byte)row.Kind));
            hash.Add(value: ((byte)((row.Min is null)
                ? 0
                : 1)));
            hash.Add(value: (row.Min ?? 0L));
            hash.Add(value: ((byte)((row.Max is null)
                ? 0
                : 1)));
            hash.Add(value: (row.Max ?? 0L));
            hash.Add(value: ((byte)((row.Capacity is null)
                ? 0
                : 1)));
            hash.Add(value: ((uint)(row.Capacity ?? 0)));
            hash.Add(value: ((byte)row.Overflow));
            hash.Add(value: ((byte)(row.GatesDrive
                ? 1
                : 0)));
            hash.Add(value: ((byte)(row.Evicts
                ? 1
                : 0)));
            AppendDomain(
                hash: ref hash,
                row: row
            );
            AppendAdvance(
                advance: row.Advance,
                hash: ref hash
            );
            AppendDraw(
                draw: row.Draw,
                hash: ref hash
            );
            AppendDynamics(
                dynamics: row.Dynamics,
                hash: ref hash
            );
            AppendLattice(
                hash: ref hash,
                lattice: row.Field
            );
            AppendCycle(
                cycle: row.Cycle,
                hash: ref hash
            );
            // Folded only when authored, unlike the presence-byte traits above: an unconditional byte would move
            // every recorded state baseline for a document that declares no verdict anywhere, which is information
            // no reader gains. A verdict row still changes this hash the moment it exists.
            if (row.Verdict is { } verdict) {
                AppendString(
                    hash: ref hash,
                    value: verdict.Gate
                );
                AppendString(
                    hash: ref hash,
                    value: verdict.Status.Value
                );
            }

            if (row.Witness is { } witnessed) {
                AppendString(
                    hash: ref hash,
                    value: "witness"
                );
                AppendString(
                    hash: ref hash,
                    value: witnessed.Value
                );
            }

            hash.Add(value: ((uint)cells.Count));

            for (var cellIndex = 0; (cellIndex < cells.Count); cellIndex++) {
                var cell = cells[cellIndex];

                AppendString(
                    hash: ref hash,
                    value: cell.Key.Value
                );
                AppendAdvance(
                    advance: cell.Advance,
                    hash: ref hash
                );
                AppendDynamics(
                    dynamics: cell.Dynamics,
                    hash: ref hash
                );
                AppendCycle(
                    cycle: cell.Cycle,
                    hash: ref hash
                );
            }
        }
    }

    // A null string folds as a value no length can produce, so an absent member is never the empty one.
    private static void AppendString(ref Fnv1aHash hash, string? value) {
        if (value is null) {
            hash.Add(value: uint.MaxValue);

            return;
        }

        hash.Add(value: ((uint)value.Length));

        foreach (var character in value) {
            hash.Add(value: ((uint)character));
        }
    }
    private static void AppendAdvance(ref Fnv1aHash hash, StateAdvance? advance) {
        hash.Add(value: ((byte)((advance is null)
            ? 0
            : 1)));

        if (advance is not null) {
            hash.Add(value: advance.PerSecondNumerator);
            hash.Add(value: advance.PerSecondDenominator);
        }
    }
    private static void AppendCycle(ref Fnv1aHash hash, StateCycle? cycle) {
        hash.Add(value: ((byte)((cycle is null)
            ? 0
            : 1)));

        if (cycle is null) {
            return;
        }

        var word = cycle.Word;

        hash.Add(value: ((uint)(word?.Count ?? 0)));

        if (word is not null) {
            for (var index = 0; (index < word.Count); index++) {
                hash.Add(value: word[index]);
            }
        }

        hash.Add(value: cycle.Power);
        hash.Add(value: ((byte)cycle.Output));
        hash.Add(value: cycle.TicksPerStep);
    }
    private static void AppendDomain(ref Fnv1aHash hash, WorldStateRow row) {
        var domain = row.EffectiveDomain;

        hash.Add(value: ((byte)(domain switch {
            StateDomain.Slot => 0,
            StateDomain.Keys => 1,
            StateDomain.KeysOf => 2,
            StateDomain.CellsOf => 3,
            StateDomain.Ring => 4,
            _ => throw new InvalidOperationException(message: $"unknown state domain '{domain.GetType().Name}'"),
        })));
        switch (domain) {
            case StateDomain.KeysOf keysOf:
                AppendString(
                    hash: ref hash,
                    value: keysOf.Row.Value
                );
                hash.Add(value: ((byte)(keysOf.Ordered
                    ? 1
                    : 0)));

                break;
            case StateDomain.CellsOf cellsOf:
                AppendString(
                    hash: ref hash,
                    value: cellsOf.Topology
                );
                hash.Add(value: cellsOf.Empty);

                break;
            case StateDomain.Ring ring:
                hash.Add(value: ring.Capacity);
                hash.Add(value: ring.Empty);

                break;
            default:
                break;
        }
        AppendString(
            hash: ref hash,
            value: row.ValuesFrom
        );
        AppendString(
            hash: ref hash,
            value: row.PhaseOf
        );
        AppendString(
            hash: ref hash,
            value: row.Inverse?.Tokens.Value
        );
        AppendString(
            hash: ref hash,
            value: row.Inverse?.Codes.Value
        );
        AppendString(
            hash: ref hash,
            value: row.Knowledge?.Source
        );
        AppendString(
            hash: ref hash,
            value: row.Knowledge?.Mask
        );
        AppendVisibility(
            hash: ref hash,
            visibility: row.Visibility
        );
    }
    private static void AppendDraw(ref Fnv1aHash hash, Draw? draw) {
        hash.Add(value: ((byte)((draw is null)
            ? 0
            : 1)));

        if (draw is null) {
            return;
        }

        AppendString(
            hash: ref hash,
            value: draw.Source?.Value
        );
        AppendGenerator(
            generator: draw.Generator,
            hash: ref hash
        );
        hash.Add(value: ((byte)draw.Timing));
        hash.Add(value: ((byte)((draw.Secret is null)
            ? 0
            : 1)));

        if (draw.Secret is { } secret) {
            hash.Add(value: secret.Word0);
            hash.Add(value: secret.Word1);
            hash.Add(value: secret.Word2);
            hash.Add(value: secret.Word3);
        }
    }
    private static void AppendDynamics(ref Fnv1aHash hash, StateDynamics? dynamics) {
        hash.Add(value: ((byte)((dynamics is null)
            ? 0
            : 1)));

        if (dynamics is not null) {
            AppendString(
                hash: ref hash,
                value: dynamics.Row
            );
        }
    }
    private static void AppendGenerator(ref Fnv1aHash hash, StateGenerator? generator) {
        hash.Add(value: ((byte)((generator is null)
            ? 0
            : 1)));

        if (generator is null) {
            return;
        }

        hash.Add(value: ((byte)generator.Source));
        AppendString(
            hash: ref hash,
            value: generator.Start?.Value
        );
        hash.Add(value: ((uint)generator.Bound));
        hash.Add(value: ((byte)generator.Mode));
        hash.Add(value: ((byte)((generator.RangeMin is null)
            ? 0
            : 1)));
        hash.Add(value: (generator.RangeMin ?? 0L));
        hash.Add(value: ((byte)((generator.RangeMax is null)
            ? 0
            : 1)));
        hash.Add(value: (generator.RangeMax ?? 0L));

        var contexts = (generator.Contexts ?? []);

        hash.Add(value: ((uint)contexts.Count));

        for (var contextIndex = 0; (contextIndex < contexts.Count); contextIndex++) {
            var context = contexts[contextIndex];
            var alternatives = (context.Alternatives ?? []);

            AppendString(
                hash: ref hash,
                value: context.Key.Value
            );
            hash.Add(value: ((uint)alternatives.Count));

            for (var alternativeIndex = 0; (alternativeIndex < alternatives.Count); alternativeIndex++) {
                var alternative = alternatives[alternativeIndex];

                AppendString(
                    hash: ref hash,
                    value: alternative.Token
                );
                hash.Add(value: alternative.Weight);
                AppendString(
                    hash: ref hash,
                    value: alternative.Next.Value
                );
                hash.Add(value: ((byte)((alternative.Multiplicity is null)
                    ? 0
                    : 1)));
                hash.Add(value: ((uint)(alternative.Multiplicity ?? 0)));
            }
        }

        var weighted = (generator.Weighted ?? []);

        hash.Add(value: ((uint)weighted.Count));

        for (var weightedIndex = 0; (weightedIndex < weighted.Count); weightedIndex++) {
            var outcome = weighted[weightedIndex];

            hash.Add(value: outcome.Value);
            hash.Add(value: outcome.Weight);
            hash.Add(value: ((byte)((outcome.Multiplicity is null)
                ? 0
                : 1)));
            hash.Add(value: ((uint)(outcome.Multiplicity ?? 0)));
        }
    }
    private static void AppendLattice(ref Fnv1aHash hash, WorldStateFieldTrait? lattice) {
        hash.Add(value: ((byte)((lattice is null)
            ? 0
            : 1)));

        if (lattice is null) {
            return;
        }

        hash.Add(value: BitConverter.SingleToUInt32Bits(value: lattice.Initial));
        hash.Add(value: BitConverter.SingleToUInt32Bits(value: lattice.Min));
        hash.Add(value: BitConverter.SingleToUInt32Bits(value: lattice.Max));
        hash.Add(value: BitConverter.SingleToUInt32Bits(value: lattice.HeightScale));
        AppendString(
            hash: ref hash,
            value: lattice.Color
        );
        hash.Add(value: ((byte)((lattice.Medium is null)
            ? 0
            : 1)));

        var paint = (lattice.Paint ?? []);

        hash.Add(value: ((uint)paint.Count));

        for (var paintIndex = 0; (paintIndex < paint.Count); paintIndex++) {
            switch (paint[paintIndex]) {
                case WorldLatticeFill.Rect rect:
                    hash.Add(value: ((byte)1));
                    hash.Add(value: BitConverter.SingleToUInt32Bits(value: rect.Value));
                    hash.Add(value: BitConverter.SingleToUInt32Bits(value: rect.MinX));
                    hash.Add(value: BitConverter.SingleToUInt32Bits(value: rect.MinZ));
                    hash.Add(value: BitConverter.SingleToUInt32Bits(value: rect.MaxX));
                    hash.Add(value: BitConverter.SingleToUInt32Bits(value: rect.MaxZ));

                    break;
                case WorldLatticeFill.Noise noise:
                    hash.Add(value: ((byte)2));
                    hash.Add(value: BitConverter.SingleToUInt32Bits(value: noise.Value));
                    hash.Add(value: ((uint)noise.Frequency));
                    hash.Add(value: BitConverter.SingleToUInt32Bits(value: noise.Threshold));
                    hash.Add(value: ((uint)noise.Octaves));
                    hash.Add(value: noise.Seed);

                    break;
                case WorldLatticeFill.Scatter scatter:
                    hash.Add(value: ((byte)3));
                    hash.Add(value: BitConverter.SingleToUInt32Bits(value: scatter.Value));
                    hash.Add(value: ((uint)scatter.Spacing));
                    hash.Add(value: ((uint)scatter.Radius));
                    hash.Add(value: scatter.Seed);

                    break;
                case WorldLatticeFill.Draw draw:
                    hash.Add(value: ((byte)4));
                    AppendString(
                        hash: ref hash,
                        value: draw.Source?.Value
                    );
                    AppendGenerator(
                        generator: draw.Generator,
                        hash: ref hash
                    );

                    break;
                default:
                    throw new InvalidOperationException(message: $"unsupported lattice fill '{paint[paintIndex].GetType().Name}'");
            }
        }
    }
    private static void AppendVisibility(ref Fnv1aHash hash, StateVisibility? visibility) {
        StateVisibilityHash.Append(hash: ref hash, visibility: visibility);
    }
}
