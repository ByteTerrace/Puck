using System.Numerics;

namespace Puck.Demo.Creator;

/// <summary>
/// The editor→bake seam for the LIVE preview easel: whatever implements this owns rasterizing/quantizing the authored
/// scene (polling <see cref="CreatorScene.Revision"/>, snapshotting via <see cref="CreatorScene.ToDocument"/>) and
/// publishing the result as a GPU image the easel's diegetic screen slab samples. The editor side only ever READS the
/// two members below — a bake failure must degrade to the last image (or none), never throw into the render loop.
/// </summary>
public interface ICreatorBakePreview {
    /// <summary>The GPU image-view handle the preview screen slab samples; 0 = nothing baked yet (the slab falls
    /// back to its flat material, exactly like an unbooted cabinet). Re-read EVERY frame — an upload may replace the
    /// handle on any publish.</summary>
    nint CurrentImageViewHandle { get; }

    /// <summary>The preview's average color — the glow the easel casts into the room (zero when no image).</summary>
    Vector3 PreviewAverageColor { get; }
}

/// <summary>The stand-in before the bake pipeline lands (and the degrade path if it ever goes away): no image, no
/// glow — the easel shows its dark flat panel.</summary>
public sealed class NullCreatorBakePreview : ICreatorBakePreview {
    /// <inheritdoc/>
    public nint CurrentImageViewHandle => 0;

    /// <inheritdoc/>
    public Vector3 PreviewAverageColor => Vector3.Zero;
}
