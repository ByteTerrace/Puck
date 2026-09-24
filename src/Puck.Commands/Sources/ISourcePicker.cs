using System.Numerics;

namespace Puck.Commands;

/// <summary>The presentation destination's seam: finds the source under a display point for hover and highlight. Nothing
/// it answers reaches state, so an implementation may walk the published <see cref="SourceMapping"/>s on the CPU or read
/// a GPU picking buffer.</summary>
public interface ISourcePicker {
    /// <summary>Finds the source shown under a display point.</summary>
    /// <param name="point">The point, in display pixels from the display's top-left corner.</param>
    /// <param name="pick">The source and the hit on it when this returns <see langword="true"/>; default otherwise.</param>
    /// <returns><see langword="true"/> when a source is shown under the point.</returns>
    bool TryPick(Vector2 point, out SourcePick pick);
}
/// <summary>A source found under a display point.</summary>
/// <param name="Source">The source the point reached; after a nested hit, the innermost one.</param>
/// <param name="Hit">The point on that source.</param>
public readonly record struct SourcePick(SourceHandle Source, SourceHit Hit);
