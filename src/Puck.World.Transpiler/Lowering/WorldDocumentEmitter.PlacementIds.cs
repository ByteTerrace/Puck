using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

// A placement id is read back out of channel spellings (`$region:<placementId>`, `placement:<id>`), so the ids an
// author writes are held to the rule those spellings need (WorldPlacement.TryValidateId) where the author wrote them.
public static partial class WorldDocumentEmitter {
    private const string UpsertPlacementArm = "upsertPlacement";

    /// <summary>Reports PUCK116 for every placement id <paramref name="document"/> authors that a channel could not
    /// name: a <c>placements</c> row's <c>id</c>, and the placement an <c>upsertPlacement</c> effect writes.</summary>
    /// <param name="document">The lowered document, before any link or door adds the placements those generate.</param>
    /// <param name="scope">The scope the document lowered in.</param>
    private static void RefuseReservedPlacementIds(JsonObject document, DocumentScope scope) {
        void Check(JsonNode? node) {
            if (
                (node is not JsonValue leaf) ||
                !leaf.TryGetValue<string>(value: out var id) ||
                WorldPlacement.TryValidateId(
                id: id,
                reason: out var reason
            )
            ) {
                return;
            }

            var span = SourceSpan.None;

            _ = scope.SourceMap?.TryGetSpan(
                jsonPointer: PointerOf(node: leaf),
                span: out span
            );
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.PlacementIdReserved,
                message: reason,
                span: span
            );
        }
        void Walk(JsonNode? node) {
            switch (node) {
                case JsonObject obj:
                    if (
                        (obj["$type"] is JsonValue arm) &&
                        arm.TryGetValue<string>(value: out var discriminator) &&
                        (discriminator == UpsertPlacementArm)
                    ) {
                        Check(node: obj["placement"]?["id"]);
                    }
                    foreach (var (member, child) in obj) {
                        if (member != "extensions") {
                            Walk(node: child);
                        }
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array) {
                        Walk(node: item);
                    }

                    break;
                default:
                    break;
            }
        }

        if (document["placements"]?["rows"] is JsonArray rows) {
            foreach (var row in rows.OfType<JsonObject>()) {
                Check(node: row["id"]);
            }
        }

        Walk(node: document);
    }
}
