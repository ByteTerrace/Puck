using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Server;

namespace Puck.World.Client;

/// <summary>Builds the candidate-aware worst-case program used by session-screen renderers. The
/// placement rows come from the candidate definition while avatar colors come from the live mirror; every catalog
/// avatar/rig is emitted, matching the renderer's construction probe.</summary>
internal static class WorldSessionRenderEnvelope {
    // Reserves every currently-authored slab, the whole derived-face band, and the document's authored screen
    // headroom. The hidden headroom slabs are capacity probes only; live emission adds them if and when an authored
    // mutation consumes those rows. Indices are chosen from the same free range as WorldSceneEmitter's boot probe.
    private static void EmitScreenReservation(SdfProgramBuilder builder, WorldDefinition candidate) {
        var facets = WorldPrototypeFacets.Derive(
            definition: candidate,
            derivedFaceBase: WorldPrototypeFacets.DerivedFaceBase,
            derivedFaceScreens: candidate.Authoring.DerivedFaceScreens
        );

        WorldStaticSceneEmit.Emit(
            builder: builder,
            derivedFaces: facets.Faces,
            screens: candidate.Screens
        );

        var used = new HashSet<int>();

        foreach (var screen in candidate.Screens) {
            _ = used.Add(item: screen.Index);
        }

        foreach (var face in facets.Faces) {
            _ = used.Add(item: face.Index);
        }

        foreach (var screen in WorldScreenHeadroom.Reserve(
            authoredCount: candidate.Screens.Count,
            derivedFaceBase: WorldPrototypeFacets.DerivedFaceBase,
            derivedFaceScreens: candidate.Authoring.DerivedFaceScreens,
            headroomCount: candidate.Authoring.AuthoringHeadroomScreens,
            usedIndices: used
        )) {
            WorldScreenStamper.Emit(
                builder: builder,
                screen: screen
            );
        }
    }

    /// <summary>Emits the construction/candidate probe into an existing composition builder.</summary>
    public static void EmitProbe(SdfProgramBuilder builder, WorldDefinition candidate, Func<int, Vector3> bodyColor, int slotBase, bool includeScreens = false) {
        ArgumentNullException.ThrowIfNull(argument: builder);
        ArgumentNullException.ThrowIfNull(argument: candidate);
        ArgumentNullException.ThrowIfNull(argument: bodyColor);

        var reserved = WorldPlacementStamper.StaticStampInstances(
            creations: candidate.Creations,
            placements: candidate.Placements,
            worldSeed: (candidate.Generation?.WorldSeed ?? 0UL)
        );

        WorldPlacementStamper.EmitProbe(
            builder: builder,
            reservedCount: reserved
        );

        if (includeScreens) {
            EmitScreenReservation(
                builder: builder,
                candidate: candidate
            );
        }

        var bodyMaterials = new int[WorldRigCatalog.Capacity];
        var accentMaterials = new int[WorldRigCatalog.Capacity];
        var noseFactor = candidate.PlayerDefaults.NoseFactor;

        for (var index = 0; (index < WorldRigCatalog.Capacity); index++) {
            var color = bodyColor(index);

            bodyMaterials[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: color));
            accentMaterials[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: (color * noseFactor)));
        }

        WorldRigCatalog.Emit(
            builder: builder,
            isActive: static _ => true,
            bodyMaterials: bodyMaterials,
            accentMaterials: accentMaterials,
            probeWorstCase: true,
            slotBase: slotBase
        );
    }
    // (See WorldSessionWindowLeases below for the WINDOW projection's own render-cost lease — a distinct concern
    // from this type's program-capacity measurement, kept in the same file because both answer "what does this
    // session render cost.")

    /// <summary>Measures a candidate definition against the same program shape the offscreen renderer probed.</summary>
    public static (int Words, int Instances) MeasureCandidate(WorldDefinition candidate, Func<int, Vector3> bodyColor, bool includeScreens = false, bool includeAdjacencies = false) {
        ArgumentNullException.ThrowIfNull(argument: candidate);
        ArgumentNullException.ThrowIfNull(argument: bodyColor);

        return SdfProgramMeasure.Measure(emit: builder => {
            EmitProbe(
                bodyColor: bodyColor,
                builder: builder,
                candidate: candidate,
                includeScreens: includeScreens,
                slotBase: 0
            );

            if (includeAdjacencies) {
                WorldPlacementStamper.EmitProbe(
                    builder: builder,
                    reservedCount: (WorldAdjacencyBands.ProjectionCapacity(definition: candidate) * WorldAdjacencyGeometry.MaximumPlacementsPerBand)
                );
            }
        });
    }
}

/// <summary>The runtime accounting for <see cref="WorldScreenProjection.Window"/> sessions' true, ALWAYS-PAID render
/// cost — one live count <c>world.faces</c> reads and echoes, so a decision the document already made
/// (<c>WorldDefinitionValidator</c> refuses a document authoring more windows than
/// <see cref="Puck.Abstractions.Presentation.OffscreenRenderBudget.PerProducedFrame"/> BY NAME at boot/mutation
/// time) is also OBSERVABLE at runtime, not merely asserted. NOT a second gate:
/// this type never refuses anything — the document validator is the one place a window count is REFUSED, this is
/// where the accepted count is READ BACK.</summary>
/// <remarks>A process-wide static counter, deliberately: exactly one <c>WorldScreenBinder</c> (the boot
/// world's own presentation) is ever live in one running <c>Puck.World</c> process — the same "no instance-addressed
/// form" fact <c>world.faces</c>' own description already states (screens are the boot instance's presentation
/// state, and a spawned instance carries neither a client nor a machine host to bind one from) — so there is no
/// second binder whose leases this counter could conflate with.</remarks>
public static class WorldSessionWindowLeases {
    private static int LiveLeaseCount;

    /// <summary>How many window sessions are live right now, process-wide.</summary>
    public static int LiveCount => LiveLeaseCount;

    /// <summary>Acquires one window lease, incrementing <see cref="LiveCount"/> until disposed.</summary>
    /// <param name="width">The window's resolved render width, pixels — carried on the lease purely for a future
    /// per-lease cost breakdown; today's read-back sums leases rather than reading this per-entry.</param>
    /// <param name="height">The window's resolved render height, pixels.</param>
    /// <returns>A disposable that releases the lease exactly once (idempotent past the first <c>Dispose</c>).</returns>
    public static IDisposable Acquire(int width, int height) {
        LiveLeaseCount++;

        return new Lease(
            height: height,
            width: width
        );
    }

    private sealed class Lease(int width, int height) : IDisposable {
        private bool m_disposed;

        public int Width { get; } = width;
        public int Height { get; } = height;

        public void Dispose() {
            if (m_disposed) {
                return;
            }

            m_disposed = true;
            LiveLeaseCount--;
        }
    }
}
