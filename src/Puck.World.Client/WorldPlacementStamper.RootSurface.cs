using System.Numerics;
using Puck.SignedDistance;
using Puck.Text;
using Puck.World.Authoring;

namespace Puck.World.Client;

public static partial class WorldPlacementStamper {
    // One immutable field in a moving root's units. Both rigid templates and whole-creation animation use this
    // emitter, so composition, text order, relief spacing and glass separation do not depend on the root's source.
    internal static void EmitRootSurface(SdfProgramBuilder builder, CreationDocument document, int slot, bool[]? hidden,
        bool textRuns, PackedFontAtlasCatalog? text, Func<ShapeDocument, int> materialFor, Func<TextRunDocument, int> textMaterial,
        bool glass = false, IReadOnlyList<TextLayoutResult>? textLayouts = null) {
        var scoped = (glass ? document.GlassNeedsScope() : CreationStampEmitter.RequiresScope(document));
        var noise = (glass ? null : document.Noise);
        var reach = CreationStampEmitter.RenderReach(document, 1f, ((text is null) ? null : text.Resolve), composeBlends: (hidden is null), textLayouts: textLayouts);
        var textInScope = (textRuns && (text is not null) && (document.TextRuns is { Count: > 0 }));

        // A scoped surface's shapes and relief each declare a part in the slot's frame (WorldShapeDetail.PartsOf), as a
        // static scoped creation's do.
        builder.BeginInstanceDynamic(slot: slot, boundOffset: Vector3.Zero,
            boundRadius: (reach + PlacementBoundMargin),
            boundInSlotFrame: true, separateBake: true,
            detail: ReliefDetail(CreationDetail(document, 1f, hidden), noise, 1f),
            parts: (scoped
                ? WorldShapeDetail.PartsOf(document: document, hidden: hidden, relief: noise, reliefBounds: (textInScope ? (new Vector3(value: -reach), new Vector3(value: reach)) : null), slot: slot, transform: new CreationStampTransform(Vector3.Zero, Quaternion.Identity, 1f, null))
                : null));
        if (scoped) { builder.PushField(SdfBlendOp.Union); }
        var transform = new CreationStampTransform(Vector3.Zero, Quaternion.Identity, 1f, null, DynamicSlot: slot);

        CreationStampEmitter.Emit(builder, document, transform, materialFor, inScope: scoped, hidden: hidden);
        if (textInScope) {
            CreationStampEmitter.EmitText(builder, document, transform, text!.Resolve, textMaterial, textLayouts: textLayouts);
        }
        if (noise is not null) { CreationStampEmitter.EmitNoise(builder, noise, transform); }
        if (scoped) { builder.PopField(); }
        builder.EndInstance();
    }
}
