using System.Numerics;
using Puck.SignedDistance;
using Puck.Text;
using Puck.World.Authoring;

namespace Puck.World.Client;

public sealed partial class WorldCreationInstances {
    // Glyphs have no CPU influence reading. Carve each rigid owner's rest surface through the existing emitters;
    // the engraving rides the surface it carved. Glass keeps its separate, unengraved field. Each owner's instance holds
    // its own shapes, the carves its partitions borrow (CreationMotionPlan.Borrows), and the runs that ride one of its
    // shapes or creation space; a run riding another owner's shape carves only that owner.
    private static void EmitEngravedOwners(SdfProgramBuilder builder, CreationDocument document, CreationMotionPlan motion, int rootSlot, int[] paletteIds, float placementScale, float materialScale, float radius, PackedFontAtlasCatalog textCatalog, IReadOnlyList<TextLayoutResult>? textLayouts) {
        var shapes = document.Shapes!.Select(selector: (shape, index) => (((shape.Group ?? 0) == 0) ? shape with { Group = -(index + 1), Blend = SdfBlendOp.Union } : shape)).ToArray();
        var partsDocument = document with { Shapes = shapes };
        var runs = (document.TextRuns ?? []);
        var runOwners = runs.Select(selector: run => ((run.ShapeId is { } id) && (Array.FindIndex(array: shapes, match: shape => (shape.Id == id)) is var host and >= 0) ? motion.ShapeSlots[host] : -1)).ToArray();

        foreach (var (owner, glass) in Enumerable.Range(0, shapes.Length).Select(selector: index => (motion.ShapeSlots[index], document.IsGlassShape(shape: shapes[index]))).Distinct()) {
            bool Own(int index) => ((motion.ShapeSlots[index] == owner) && (document.IsGlassShape(shape: shapes[index]) == glass));
            var hidden = Enumerable.Range(0, shapes.Length).Select(selector: index => (!Own(index: index) && !(motion.BorrowedBy(shape: index, slot: owner) && (document.IsGlassShape(shape: shapes[index]) == glass)))).ToArray();
            var slot = (rootSlot + owner);

            _ = builder.BeginInstanceDynamic(slot, Vector3.Zero, radius, separateBake: true, boundInSlotFrame: true, restPoseSkin: true,
                detail: SharedDetail(partsDocument, hidden, placementScale, (glass ? 0f : WorldPlacementStamper.TextDetail(creation: document, scale: placementScale)))).PushField(compose: SdfBlendOp.Union);
            foreach (var group in Enumerable.Range(0, shapes.Length).Where(predicate: Own).Select(selector: index => shapes[index].Group!.Value).Distinct()) {
                EmitGroup(builder, partsDocument, shapes, group, 0, rootSlot, paletteIds, placementScale, materialScale, false, slot, radius, false, hidden, motion, Vector3.Zero);
            }
            if (!glass) {
                CreationStampEmitter.EmitTextDynamic(builder, partsDocument, slot, placementScale, textCatalog.Resolve, run => paletteIds[((run.Material ?? 0) % paletteIds.Length)], textLayouts, includes: index => ((runOwners[index] < 0) || (runOwners[index] == owner)));
            }
            _ = builder.PopField().EndInstance();
        }
    }
}
