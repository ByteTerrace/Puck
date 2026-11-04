using Puck.SignedDistance;

namespace Puck.World.Authoring;

public static partial class CreationStampEmitter {
    /// <summary>Whether a creation's noise displaces its contact union.</summary>
    public static bool HasRelief(CreationDocument document) => document.Noise is { Amplitude: not 0f };

    /// <summary>Emits one creation's contact field, including authored blends, per-shape margin and optional relief.
    /// The same scoped union is used by whole-world queries, individual colliders and static surface placement.</summary>
    /// <param name="builder">The target field stream.</param>
    /// <param name="document">The authored creation.</param>
    /// <param name="transform">The canonical fixed-point occurrence frame.</param>
    /// <param name="material">The contact source identity.</param>
    /// <param name="margin">The contact dilation.</param>
    /// <param name="applyRelief">False only when deriving the undisplaced envelope.</param>
    public static void EmitContact(SdfProgramBuilder builder, CreationDocument document, FixedCreationStampTransform transform,
        int material, float margin, bool applyRelief = true) {
        var relieved = applyRelief && HasRelief(document);
        var scoped = document.Shapes is { Count: > 0 } &&
            (relieved || (margin == 0f && ComposesInternally(document)));
        if (scoped) { _ = builder.PushField(compose: SdfBlendOp.Union); }
        EmitFixed(builder, document, transform, _ => material, contactMargin: margin);
        if (scoped && relieved) { EmitNoise(builder, document.Noise!, transform); }
        if (scoped) { _ = builder.PopField(); }
    }
}
