using System.Numerics;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>The ordered local point deformations shared by static and pooled creation emission.</summary>
public static class ShapePointOps {
    /// <summary>Applies twist, bend, flare, shear, bumps, warp and elongation in document order.
    /// <paramref name="lengthScale"/> converts authored creation lengths into the chain's point units;
    /// inverse lengths convert reciprocally. A chain already carrying the placement Scale uses one.</summary>
    public static SdfProgramBuilder Apply(SdfProgramBuilder chain, float lengthScale = 1f, float twist = 0f,
        float bend = 0f, ShapeFlareDocument? flare = null, ShapeShearDocument? shear = null,
        IReadOnlyList<ShapeBumpDocument>? bumps = null, ShapeWarpDocument? warp = null,
        DocumentVector3? elongate = null, bool probeWorstCase = false) {
        if (
            probeWorstCase ||
            (twist != 0f)
        ) {
            chain = chain.TwistY(rate: (probeWorstCase
                ? 1f
                : (twist / lengthScale)));
        }

        if (
            probeWorstCase ||
            (bend != 0f)
        ) {
            chain = chain.BendY(rate: (probeWorstCase
                ? 1f
                : (bend / lengthScale)));
        }

        if (
            probeWorstCase ||
            (flare is not null)
        ) {
            chain = chain.AxialProfile(
                amount: (probeWorstCase ? ShapeFlareDocument.MaxAmount : flare!.Amount),
                bulge: (probeWorstCase ? ShapeFlareDocument.MaxBulge : flare!.Bulge),
                top: (probeWorstCase ? 0f : ((flare!.Top ?? 0f) * lengthScale)),
                span: (probeWorstCase ? 1f : (flare!.Span * lengthScale)),
                axis: (probeWorstCase ? SdfAxis.Y : (SdfAxis)flare!.Axis),
                startScale: (probeWorstCase ? ShapeFlareDocument.MaxStartScale : flare!.StartScale)
            );
        }

        if (
            probeWorstCase ||
            (shear is not null)
        ) {
            chain = chain.Shear(
                linear: (probeWorstCase ? ShapeDocument.MaxShear : shear!.Linear),
                quadratic: (probeWorstCase ? ShapeDocument.MaxShear : (shear!.Quadratic / lengthScale)),
                cubic: (probeWorstCase ? ShapeDocument.MaxShear : (shear!.Cubic / (lengthScale * lengthScale))),
                target: (probeWorstCase ? SdfAxis.X : (SdfAxis)shear!.Target),
                driver: (probeWorstCase ? SdfAxis.Y : (SdfAxis)shear!.Driver)
            );
        }

        if (probeWorstCase) {
            // Worst-case reserves the widest declared bump list; every entry is otherwise identical for capacity
            // purposes (each costs the same single instruction) — Push length feeds the reach probe below,
            // not the instruction count.
            for (var i = 0; (i < ShapeBumpDocument.MaxBumps); i++) {
                chain = chain.GaussianPush(center: Vector3.Zero, radii: Vector3.One, push: new Vector3(value: ShapeBumpDocument.MaxPushMagnitude));
            }
        } else {
            foreach (var bump in (bumps ?? [])) {
    
                chain = chain.GaussianPush(
                    center: (bump.Center.Value * lengthScale),
                    radii: (bump.Radii.Value * lengthScale),
                    push: (bump.Push.Value * lengthScale)
                );
            }
        }

        if (probeWorstCase || (warp is not null)) {

            chain = chain.DomainWarp(
                amplitude: (probeWorstCase ? ShapeWarpDocument.MaxAmplitude : (warp!.Amplitude * lengthScale)),
                frequency: (probeWorstCase ? new Vector3(value: ShapeWarpDocument.MaxFrequency) : (warp!.Frequency.Value / lengthScale))
            );
        }

        if (probeWorstCase || (elongate is not null)) {

            chain = chain.Elongate(extents: (probeWorstCase ? new Vector3(value: ShapeDocument.MaxElongate) : (elongate!.Value * lengthScale)));
        }

        return chain;
    }
}
