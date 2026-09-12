using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler.Decompiler;

// `placements { policy { } placement "id" { } }` inverse (§4.2), mirroring `LowerPlacementsBlock`/
// `LowerPlacementRow`: `id` maps back to the block's quoted name (not `name` — `WorldPlacement`'s identity field is
// `Id`), `prototypeId` maps back to a bare `prototype:` property, `yawDegrees`/`scale` elide at their defaults, and
// `{"margin":0}` maps back to the bare `solid` flag.
public static partial class WorldDecompiler {
    // Whether every row in a `placements` section is one the `placement "id" { }` grammar can carry. A row with no
    // `id` is a basis-merge directive, not a placement; the whole section then prints through the generic value path.
    private static bool CanSugarPlacements(JsonObject placements) {
        if (placements["rows"] is not JsonArray rows) {
            return false;
        }
        foreach (var item in rows) {
            if (item is not JsonObject row || row["id"] is not JsonValue idVal || !idVal.TryGetValue<string>(out var id) || (id.Length == 0)) {
                return false;
            }
        }
        return true;
    }

    private static void DecompilePlacementsBlock(StringBuilder sb, JsonObject placements, int indentLevel) {
        var indent = new string(' ', indentLevel * 4);
        var inner = new string(' ', (indentLevel + 1) * 4);
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}placements {{");
        var wroteAny = false;

        foreach (var (key, value) in placements) {
            if (string.Equals(key, "rows", StringComparison.Ordinal) || value is null) {
                continue;
            }
            EmitField(sb, key, value, indentLevel + 1);
            wroteAny = true;
        }

        if (placements["rows"] is JsonArray rows) {
            foreach (var row in rows) {
                if (row is not JsonObject rowObj) {
                    continue;
                }
                if (wroteAny) {
                    sb.AppendLine();
                }
                wroteAny = true;
                AppendPlacementRow(sb, rowObj, indentLevel + 1);
            }
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
    }

    private static void AppendPlacementRow(StringBuilder sb, JsonObject row, int indentLevel) {
        var indent = new string(' ', indentLevel * 4);
        var inner = new string(' ', (indentLevel + 1) * 4);
        var id = row["id"]?.ToString() ?? "";
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}placement \"{EscapeString(id)}\" {{");

        var elide = new HashSet<string>(StringComparer.Ordinal) { "id" };

        // A row with no prototypeId of its own is a basis-merge directive or a partial row a basis completes; the
        // emitter fills no default there, so eliding one here would silently drop an authored value.
        if (row["prototypeId"] is { } prototypeId) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}prototype{FieldSeparator(prototypeId)}{FormatValue(prototypeId, indentLevel + 1)}");
            elide.Add("prototypeId");
            foreach (var key in WorldDocumentRowDefaults.PlacementKeys) {
                if (WorldDocumentRowDefaults.IsDefaultValue(key, row[key], index: 0, shape: false)) {
                    elide.Add(key);
                }
            }
        }

        var bareSolid = false;
        if (row["solid"] is JsonObject solidObj) {
            elide.Add("solid");
            bareSolid = WorldDocumentRowDefaults.IsBareSolid(solidObj);
        }

        foreach (var (k, v) in row) {
            if (elide.Contains(k)) {
                continue;
            }
            if (v is null) {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}{k}: null");
                continue;
            }
            if (v is JsonValue numeric && UnitSuffixFor(k, numeric) is { } unit) {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}{k}: {FormatValue(numeric, indentLevel + 1)}{unit}");
                continue;
            }
            EmitField(sb, k, v, indentLevel + 1);
        }

        if (bareSolid) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}solid");
        } else if (row["solid"] is JsonObject solidFull) {
            EmitField(sb, "solid", solidFull, indentLevel + 1);
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
    }
}
