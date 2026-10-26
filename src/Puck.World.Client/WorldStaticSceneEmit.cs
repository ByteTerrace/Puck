using System.Numerics;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>Emits the two screen sources every static-scene build composes, in the SAME order: the document's
/// declared <c>screens</c> rows, then the reserved-band <c>derivedFaces</c> rows a creation's own faces resolve
/// to.</summary>
internal static class WorldStaticSceneEmit {
    /// <summary>Emits every screen in <paramref name="screens"/>, then every screen in
    /// <paramref name="derivedFaces"/>.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="screens">The document's declared screen rows.</param>
    /// <param name="derivedFaces">The already-resolved derived-face rows (see <see cref="WorldPrototypeFacets"/>) —
    /// a caller with a long-lived derivation must thread its OWN resolved set here rather than re-deriving one, so
    /// the geometry this call emits and whatever else reads the same derivation can never disagree about which
    /// placement a face belongs to.</param>
    /// <param name="carried">Where a body carries a screen this build (<see cref="WorldCreationInstances.TryCarriedScreen"/>),
    /// or <see langword="null"/> for a build in which every screen stays where it is authored. A carried screen draws no
    /// slab: its surface is declared at the carried frame, and the carrying look's box shows it.</param>
    public static void Emit(SdfProgramBuilder builder, IReadOnlyList<WorldScreen> screens, IReadOnlyList<WorldScreen> derivedFaces, Func<WorldScreen, WorldScreen?>? carried = null) {
        foreach (var screen in screens) {
            if (carried?.Invoke(arg: screen) is { } held) {
                _ = builder.ScreenSurface(
                    halfHeight: held.HalfHeight,
                    halfWidth: held.HalfWidth,
                    screenIndex: held.Index,
                    worldOrigin: held.Origin,
                    worldRight: Vector3.Normalize(value: held.Right),
                    worldUp: Vector3.Normalize(value: held.Up)
                );

                continue;
            }

            WorldScreenStamper.Emit(
                builder: builder,
                screen: screen
            );
        }

        foreach (var screen in derivedFaces) {
            WorldScreenStamper.Emit(
                builder: builder,
                screen: screen
            );
        }
    }
}
