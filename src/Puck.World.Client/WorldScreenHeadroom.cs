using System.Numerics;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>Reserves headroom placeholder screens at free engine surface indices — the shared "authoring room"
/// reservation both a live scene build and a candidate/session probe pad past their authored and derived-face
/// content. FREE means neither authored nor derived-face-reserved; a headroom count the free indices cannot
/// satisfy throws by name rather than silently reserving fewer slots than asked, which would only move the failure
/// to the runtime <c>UpsertScreen</c> call that outgrows the probed envelope.</summary>
internal static class WorldScreenHeadroom {
    /// <summary>Resolves <paramref name="headroomCount"/> placeholder screens at free engine surface indices. The
    /// caller decides what to do with them (append to a screens list, emit directly into a builder).</summary>
    /// <param name="usedIndices">Every index already claimed by an authored or derived-face screen — mutated as
    /// this call claims headroom indices too.</param>
    /// <param name="authoredCount">The number of authored screens, named in the refusal message.</param>
    /// <param name="derivedFaceScreens">The reserved derived-face band's width, named in the refusal message.</param>
    /// <param name="derivedFaceBase">The reserved derived-face band's first index, named in the refusal message.</param>
    /// <param name="headroomCount">The number of headroom slots to reserve (<c>authoring.authoringHeadroomScreens</c>).</param>
    /// <returns>Exactly <paramref name="headroomCount"/> placeholder screens.</returns>
    /// <exception cref="InvalidOperationException">Fewer than <paramref name="headroomCount"/> free indices remain.</exception>
    public static IReadOnlyList<WorldScreen> Reserve(HashSet<int> usedIndices, int authoredCount, int derivedFaceScreens, int derivedFaceBase, int headroomCount) {
        var reserved = new List<WorldScreen>(capacity: headroomCount);

        for (var index = 0; ((index < SdfProgramBuilder.MaxScreenSurfaces) && (reserved.Count < headroomCount)); index++) {
            if (!usedIndices.Add(item: index)) {
                continue;
            }

            reserved.Add(item: new WorldScreen(
                Index: index,
                Origin: Vector3.Zero,
                Right: Vector3.UnitX,
                Up: Vector3.UnitY,
                HalfWidth: 1f,
                HalfHeight: 1f,
                HalfDepth: 0.1f,
                Round: 0.05f,
                Source: new WorldScreenSource.None(),
                Route: WorldScreenRoute.Passive
            ));
        }

        if (reserved.Count < headroomCount) {
            throw new InvalidOperationException(message: $"authoring.authoringHeadroomScreens asks for {headroomCount} reserved screen slot(s), but only {reserved.Count} of the engine's {SdfProgramBuilder.MaxScreenSurfaces} surfaces are free: {authoredCount} carry an authored screen and {derivedFaceScreens} are reserved for derived creation faces at indices {derivedFaceBase}..{((derivedFaceBase + derivedFaceScreens) - 1)}. Lower authoring.authoringHeadroomScreens by {(headroomCount - reserved.Count)}, lower authoring.derivedFaceScreens, or author fewer screens.");
        }

        return reserved;
    }
}
