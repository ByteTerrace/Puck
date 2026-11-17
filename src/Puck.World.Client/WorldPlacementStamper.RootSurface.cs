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

        builder.BeginInstanceDynamic(slot: slot, boundOffset: Vector3.Zero,
            boundRadius: (CreationStampEmitter.RenderReach(document, 1f, ((text is null) ? null : text.Resolve), composeBlends: (hidden is null), textLayouts: textLayouts) + PlacementBoundMargin),
            boundInSlotFrame: true, separateBake: true,
            detail: ReliefDetail(CreationDetail(document, 1f, hidden), noise, 1f));
        if (scoped) { builder.PushField(SdfBlendOp.Union); }
        var transform = new CreationStampTransform(Vector3.Zero, Quaternion.Identity, 1f, null, DynamicSlot: slot);

        CreationStampEmitter.Emit(builder, document, transform, materialFor, inScope: scoped, hidden: hidden);
        if (textRuns && (text is not null) && (document.TextRuns is { Count: > 0 })) {
            CreationStampEmitter.EmitText(builder, document, transform, text.Resolve, textMaterial, textLayouts: textLayouts);
        }
        if (noise is not null) { CreationStampEmitter.EmitNoise(builder, noise, transform); }
        if (scoped) { builder.PopField(); }
        builder.EndInstance();
    }
}
