using System.Numerics;
using Puck.SignedDistance;
using Puck.Text;
using Puck.World.Authoring;

namespace Puck.World.Client;

public sealed partial class WorldCreationInstances {
    // Glyphs have no CPU influence reading. Carve each rigid owner's rest surface through the existing emitters;
    // the engraving rides the surface it carved. Glass keeps its separate, unengraved field as before.
    private static void EmitEngravedOwners(SdfProgramBuilder builder, CreationDocument document, CreationMotionPlan motion, int rootSlot, int[] paletteIds, float placementScale, float materialScale, float radius, PackedFontAtlasCatalog textCatalog, IReadOnlyList<TextLayoutResult>? textLayouts) {
        var shapes = document.Shapes!.Select(selector: (shape, index) => (((shape.Group ?? 0) == 0) ? shape with { Group = -(index + 1), Blend = SdfBlendOp.Union } : shape)).ToArray();
        var partsDocument = document with { Shapes = shapes };

        foreach (var (owner, glass) in Enumerable.Range(0, shapes.Length).Select(selector: index => (motion.ShapeSlots[index], document.IsGlassShape(shape: shapes[index]))).Distinct()) {
            var hidden = Enumerable.Range(0, shapes.Length).Select(selector: index => ((motion.ShapeSlots[index] != owner) || (document.IsGlassShape(shape: shapes[index]) != glass))).ToArray();
            var slot = (rootSlot + owner);

            _ = builder.BeginInstanceDynamic(slot, Vector3.Zero, radius, separateBake: true, boundInSlotFrame: true, restPoseSkin: true,
                detail: SharedDetail(partsDocument, hidden, placementScale, (glass ? 0f : WorldPlacementStamper.TextDetail(creation: document, scale: placementScale)))).PushField(compose: SdfBlendOp.Union);
            foreach (var group in Enumerable.Range(0, shapes.Length).Where(predicate: index => !hidden[index]).Select(selector: index => shapes[index].Group!.Value).Distinct()) {
                EmitGroup(builder, partsDocument, shapes, group, 0, rootSlot, paletteIds, placementScale, materialScale, false, slot, radius, false, hidden, motion, Vector3.Zero);
            }
            if (!glass) {
                CreationStampEmitter.EmitTextDynamic(builder, partsDocument, slot, placementScale, textCatalog.Resolve, run => paletteIds[((run.Material ?? 0) % paletteIds.Length)], textLayouts);
            }
            _ = builder.PopField().EndInstance();
        }
    }
}
