using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Puck.World.Transpiler.Decompiler;

// `placements { policy: { } placement "id" { } }` inverse (§4.2), mirroring `LowerPlacementsBlock`/
// `LowerPlacementRow`: `id` maps back to the block's quoted name (not `name` — `WorldPlacement`'s identity field is
// `Id`), `prototypeId` maps back to a bare `prototype:` property, `yawDegrees`/`scale` elide at their defaults, and
// `{"margin":0}` maps back to the bare `solid` flag.
public static partial class WorldDecompiler {
    private static void DecompilePlacementsBlock(StringBuilder sb, JsonObject placements, int indentLevel) {
        var indent = new string(' ', indentLevel * 4);
        var inner = new string(' ', (indentLevel + 1) * 4);
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}placements {{");
        var wroteAny = false;

        if (placements["policy"] is { } policy) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}policy: {FormatValue(policy, indentLevel + 1)}");
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

        if (row["prototypeId"] is { } prototypeId) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}prototype: {FormatValue(prototypeId, indentLevel + 1)}");
            elide.Add("prototypeId");
        }
        if (row["yawDegrees"] is JsonValue yawVal && IsNumberEqualTo(yawVal, 0.0)) {
            elide.Add("yawDegrees");
        }
        if (row["scale"] is JsonValue scaleVal && IsNumberEqualTo(scaleVal, 1.0)) {
            elide.Add("scale");
        }

        var bareSolid = false;
        if (row["solid"] is JsonObject solidObj) {
            elide.Add("solid");
            bareSolid = (solidObj.Count == 1) && solidObj["margin"] is JsonValue marginVal && IsNumberEqualTo(marginVal, 0.0);
        }

        foreach (var (k, v) in row) {
            if (elide.Contains(k) || v is null) {
                continue;
            }
            if (string.Equals(k, "yawDegrees", StringComparison.Ordinal) && v is JsonValue yawPrint) {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}{k}: {FormatValue(yawPrint, indentLevel + 1)}deg");
                continue;
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}{k}: {FormatValue(v, indentLevel + 1)}");
        }

        if (bareSolid) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}solid");
        } else if (row["solid"] is JsonObject solidFull) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}solid: {FormatValue(solidFull, indentLevel + 1)}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
    }
}
