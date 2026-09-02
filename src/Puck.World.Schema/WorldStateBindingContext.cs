using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Puck.Commands;
using Puck.Maths;

namespace Puck.World;

/// <summary>Projects one world-state row into a binding-context family. A family named
/// <c>state:&lt;row&gt;</c> publishes the row's scalar value for every seat, or the controlled body's entity-index cell
/// when the row is keyed. Binding context rows can map those published values to different control groups.</summary>
public static class WorldStateBindingContext {
    /// <summary>The prefix identifying a world-state-backed binding-context family.</summary>
    public const string FamilyPrefix = "state:";
    /// <summary>The prefix a <c>state.&lt;row&gt;</c> row reference carries.</summary>
    public const string RowReferencePrefix = "state.";

    // Whether any cell of the row reads differently from one tick to the next with no write in between — an advance
    // or a cycle on the row or on any cell. A control context must change only through explicit state writes.
    private static bool Advances(WorldStateRow row) {
        if ((row.Advance is not null) || (row.Cycle is not null)) {
            return true;
        }

        foreach (var cell in (row.Cells ?? [])) {
            if ((cell?.Advance is not null) || (cell?.Cycle is not null)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Formats one row value using the same author-facing spelling as state mutation and read-back
    /// surfaces.</summary>
    /// <param name="row">The state row declaring the value kind.</param>
    /// <param name="rawValue">The raw numeric value, or <see langword="null"/> for an absent cell.</param>
    /// <param name="text">The text value, or <see langword="null"/> for an absent or numeric cell.</param>
    /// <returns>The binding-context state token.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is <see langword="null"/>.</exception>
    public static string FormatState(WorldStateRow row, long? rawValue, string? text) {
        ArgumentNullException.ThrowIfNull(argument: row);
        var raw = (rawValue ?? 0L);

        return row.Kind switch {
            CellKind.Bool => ((raw != 0L)
            ? "true"
            : "false"),
            CellKind.Fixed => FixedQ4816.FromRawBits(value: raw).ToString(),
            CellKind.Text => (text ?? string.Empty),
            _ => raw.ToString(provider: CultureInfo.InvariantCulture),
        };
    }
    /// <summary>Parses a <c>state.&lt;row&gt;</c> row reference — a document field naming a whole state row whose
    /// CELL KEY comes from the runtime rather than the reference (a binding bar's icon row or a wheel's label/icon
    /// row). The dotted spelling is
    /// the same one an authored value reference (<c>state.colors.paper</c>) and a HUD token (<c>state.&lt;row&gt;</c>)
    /// use; it stops at the row because the key is not knowable until draw time.</summary>
    /// <param name="reference">The reference text.</param>
    /// <param name="rowName">The row name on success.</param>
    /// <returns><see langword="true"/> when the reference is well-formed.</returns>
    public static bool TryParseRowReference(string? reference, [NotNullWhen(true)] out string? rowName) {
        rowName = null;

        if (
            (reference is not { Length: > 0 }) ||
            !reference.StartsWith(comparisonType: StringComparison.Ordinal, value: RowReferencePrefix)
        ) {
            return false;
        }

        var candidate = reference[RowReferencePrefix.Length..];

        if (!WorldCellName.TryParse(
            candidate: candidate,
            name: out var name,
            reason: out _
        )) {
            return false;
        }

        rowName = name.ToString();

        return true;
    }
    /// <summary>Parses a <c>state:&lt;row&gt;</c> family name.</summary>
    /// <param name="family">The binding-context family name.</param>
    /// <param name="rowName">The validated state-row name on success.</param>
    /// <returns><see langword="true"/> when <paramref name="family"/> names a valid state-backed family.</returns>
    public static bool TryParseFamily(string? family, out WorldCellName rowName) {
        rowName = default;

        return (
            (family is not null) &&
            family.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: FamilyPrefix
        ) &&
            WorldCellName.TryParse(
            candidate: family[FamilyPrefix.Length..],
            name: out rowName,
            reason: out _
        )
        );
    }
    /// <summary>Reads the state published for a seat from a delivered world definition.</summary>
    /// <param name="definition">The routed world definition.</param>
    /// <param name="family">The <c>state:&lt;row&gt;</c> family.</param>
    /// <param name="entityIndex">The controlled body's entity index, used as the key for a keyed row.</param>
    /// <param name="tick">The delivered authority tick at which advancing state would be read.</param>
    /// <param name="state">The formatted context state on success.</param>
    /// <returns><see langword="true"/> when the family names a declared, non-advancing row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static bool TryRead(WorldDefinition definition, string family, int entityIndex, ulong tick, out string state) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        state = string.Empty;

        if (!TryParseFamily(
            family: family,
            rowName: out var rowName
        )) {
            return false;
        }

        var row = WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: rowName
        );

        if (
            (row is null) ||
            Advances(row: row)
        ) {
            return false;
        }

        _ = WorldStateReader.TryRead(
            definition: definition,
            rowName: rowName,
            key: (row.IsKeyed
            ? entityIndex.ToString(provider: CultureInfo.InvariantCulture)
            : null),
            tick: tick,
            row: out _,
            rawValue: out var rawValue,
            text: out var text
        );
        state = FormatState(
            rawValue: rawValue,
            row: row,
            text: text
        );

        return true;
    }
    /// <summary>Validates the state-backed context and presentation rows in a binding document against the routed
    /// world's state declarations.</summary>
    /// <param name="document">The binding document to validate.</param>
    /// <param name="stateRows">The routed world's state rows by name.</param>
    /// <param name="errors">The collection receiving refusal messages.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/>, <paramref name="stateRows"/>, or
    /// <paramref name="errors"/> is <see langword="null"/>.</exception>
    public static void Validate(BindingProfileDocument document, IReadOnlyDictionary<string, WorldStateRow> stateRows, List<string> errors) {
        ArgumentNullException.ThrowIfNull(argument: document);
        ArgumentNullException.ThrowIfNull(argument: stateRows);
        ArgumentNullException.ThrowIfNull(argument: errors);
        var contexts = (document.Contexts ?? []);

        for (var index = 0; (index < contexts.Count); index++) {
            if (
                (contexts[index] is not { } context) ||
                string.IsNullOrEmpty(value: context.State) ||
                !TryParseFamily(
                family: context.Family,
                rowName: out var rowName
            )
            ) {
                continue;
            }
            if (!stateRows.TryGetValue(
                key: rowName,
                value: out var row
            )) {
                errors.Add(item: $"contexts row {index} names family \"{context.Family}\", but state row \"{rowName}\" is not declared");

                continue;
            }
            if (Advances(row: row)) {
                errors.Add(item: $"contexts row {index} names family \"{context.Family}\", whose row advances or turns with the tick; control contexts must change through explicit state writes");

                continue;
            }

            if (row.Kind == CellKind.Text) {
                continue;
            }
            if (!WorldStateCellWriter.TryParseNumericToken(
                kind: row.Kind,
                token: context.State,
                value: out var parsed,
                reason: out var reason
            )) {
                errors.Add(item: $"contexts row {index} (family \"{context.Family}\") state \"{context.State}\" is invalid: {reason}");

                continue;
            }

            var canonical = FormatState(
                rawValue: parsed,
                row: row,
                text: null
            );

            if (!string.Equals(
                a: canonical,
                b: context.State,
                comparisonType: StringComparison.Ordinal
            )) {
                errors.Add(item: $"contexts row {index} (family \"{context.Family}\") state \"{context.State}\" is not canonical; author \"{canonical}\"");
            }
        }

        var wheels = (document.Wheels ?? []);

        for (var wheelIndex = 0; (wheelIndex < wheels.Count); wheelIndex++) {
            if (wheels[wheelIndex] is not { } wheel) {
                continue;
            }

            ValidatePresentationRowReference(
                errors: errors,
                path: $"wheels row {wheelIndex}.labelRow",
                reference: wheel.LabelRow,
                stateRows: stateRows
            );
            ValidatePresentationRowReference(
                errors: errors,
                path: $"wheels row {wheelIndex}.iconRow",
                reference: wheel.IconRow,
                stateRows: stateRows
            );

            if ((wheel.LabelRow is null) && (wheel.IconRow is null)) {
                continue;
            }

            // An explicit "rings": null survives parse (the context sets no RespectNullableAnnotations), so refuse it
            // by name here rather than dereferencing it; BindingProfile.Compile applies the same guard.
            if (wheel.Rings is not { } rings) {
                errors.Add(item: $"wheels row {wheelIndex}.rings is required when labelRow or iconRow is authored");

                continue;
            }

            for (var ringIndex = 0; (ringIndex < rings.Count); ringIndex++) {
                if (rings[ringIndex]?.Entries is not { } sectors) {
                    continue;
                }

                for (var sectorIndex = 0; (sectorIndex < sectors.Count); sectorIndex++) {
                    if (sectors[sectorIndex]?.Id is not { Length: > 0 }) {
                        errors.Add(item: $"wheels row {wheelIndex}.rings[{ringIndex}].entries[{sectorIndex}].id is required when labelRow or iconRow is authored");
                    }
                }
            }
        }
    }

    /// <summary>Validates an optional state-row reference used as a keyed text presentation table.</summary>
    /// <param name="reference">The optional <c>state.&lt;row&gt;</c> reference.</param>
    /// <param name="path">The author-facing document path naming the field.</param>
    /// <param name="stateRows">The routed world's state rows by name.</param>
    /// <param name="errors">The collection receiving refusal messages.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/>, <paramref name="stateRows"/>, or
    /// <paramref name="errors"/> is <see langword="null"/>.</exception>
    internal static void ValidatePresentationRowReference(string? reference, string path, IReadOnlyDictionary<string, WorldStateRow> stateRows, List<string> errors) {
        ArgumentNullException.ThrowIfNull(argument: path);
        ArgumentNullException.ThrowIfNull(argument: stateRows);
        ArgumentNullException.ThrowIfNull(argument: errors);

        if (reference is null) {
            return;
        }
        if (!TryParseRowReference(
            reference: reference,
            rowName: out var rowName
        )) {
            errors.Add(item: $"{path} '{reference}' must be spelled state.<row> with a valid row name");

            return;
        }
        if (!stateRows.TryGetValue(
            key: rowName,
            value: out var row
        )) {
            errors.Add(item: $"{path} '{reference}' names no declared state row");

            return;
        }
        if (row.Kind != CellKind.Text) {
            errors.Add(item: $"{path} '{reference}' names a {row.Kind} row; presentation rows must be text");
        }
        if (!row.IsKeyed) {
            errors.Add(item: $"{path} '{reference}' names a scalar row; presentation rows must be keyed by action or sector id");
        }
    }
}
