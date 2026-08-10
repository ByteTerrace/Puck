using System.Numerics;
using Puck.SdfVm;
using Puck.World.Server;

namespace Puck.World.Client;

/// <summary>Builds the candidate-aware worst-case program shared by session-screen and away-seat renderers. The
/// placement rows come from the candidate definition while avatar colors come from the live mirror; every catalog
/// avatar/rig is emitted, matching both renderers' construction probe.</summary>
internal static class WorldSessionRenderEnvelope {
    // (See WorldSessionWindowLeases below for the WINDOW projection's own render-cost lease — a distinct concern
    // from this type's program-capacity measurement, kept in the same file because both answer "what does this
    // session render cost.")

    /// <summary>Measures a candidate definition against the same program shape the offscreen renderer probed.</summary>
    public static (int Words, int Instances) MeasureCandidate(WorldDefinition candidate, Func<int, Vector3> bodyColor, bool includeScreens = false, bool includeBorderMargins = false) {
        ArgumentNullException.ThrowIfNull(argument: candidate);
        ArgumentNullException.ThrowIfNull(argument: bodyColor);

        var builder = new SdfProgramBuilder();

        EmitProbe(builder: builder, candidate: candidate, bodyColor: bodyColor, slotBase: 0, includeScreens: includeScreens);

        if (includeBorderMargins) {
            WorldPlacementStamper.EmitProbe(builder: builder, reservedCount: (WorldBorderMarginBands.CollectFrom(definition: candidate).Count * WorldBorderMarginGeometry.MaximumPlacementsPerBand));
        }

        var measured = builder.Build();

        return (Words: measured.Words.Length, Instances: measured.Instances.Count);
    }

    /// <summary>Emits the construction/candidate probe into an existing composition builder.</summary>
    public static void EmitProbe(SdfProgramBuilder builder, WorldDefinition candidate, Func<int, Vector3> bodyColor, int slotBase, bool includeScreens = false) {
        ArgumentNullException.ThrowIfNull(argument: builder);
        ArgumentNullException.ThrowIfNull(argument: candidate);
        ArgumentNullException.ThrowIfNull(argument: bodyColor);

        var reserved = WorldPlacementStamper.StaticStampInstances(creations: candidate.Creations, placements: candidate.Placements);

        WorldPlacementStamper.EmitProbe(builder: builder, reservedCount: reserved);

        if (includeScreens) {
            EmitScreenReservation(builder: builder, candidate: candidate);
        }

        var bodyMaterials = new int[WorldAvatarCatalog.Capacity];
        var accentMaterials = new int[WorldAvatarCatalog.Capacity];
        var noseFactor = candidate.PlayerDefaults.NoseFactor;

        for (var index = 0; (index < WorldAvatarCatalog.Capacity); index++) {
            var color = bodyColor(index);

            bodyMaterials[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: color));
            accentMaterials[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: (color * noseFactor)));
        }

        WorldAvatarCatalog.Emit(
            builder: builder,
            isActive: static _ => true,
            bodyMaterials: bodyMaterials,
            accentMaterials: accentMaterials,
            probeWorstCase: true,
            slotBase: slotBase
        );
    }

    // Reserves every currently-authored slab, the whole derived-face band, and the document's authored screen
    // headroom. The hidden headroom slabs are capacity probes only; live emission adds them if and when an authored
    // mutation consumes those rows. Indices are chosen from the same free range as WorldSceneEmitter's boot probe.
    private static void EmitScreenReservation(SdfProgramBuilder builder, WorldDefinition candidate) {
        var facets = WorldCreationFacets.Derive(
            definition: candidate,
            derivedFaceBase: WorldCreationFacets.DerivedFaceBase,
            derivedFaceScreens: candidate.Authoring.DerivedFaceScreens
        );
        var used = new HashSet<int>();

        foreach (var screen in candidate.Screens) {
            _ = used.Add(item: screen.Index);
            WorldScreenStamper.Emit(builder: builder, screen: screen);
        }

        foreach (var face in facets.Faces) {
            _ = used.Add(item: face.Index);
            WorldScreenStamper.Emit(builder: builder, screen: face);
        }

        for (var index = 0; (index < SdfProgramBuilder.MaxScreenSurfaces); index++) {
            if (WorldAwaySeatQuad.IsReservedIndex(index: index)) {
                _ = used.Add(item: index);
            }
        }

        var reserved = 0;

        for (var index = 0; ((index < SdfProgramBuilder.MaxScreenSurfaces) && (reserved < candidate.Authoring.AuthoringHeadroomScreens)); index++) {
            if (!used.Add(item: index)) {
                continue;
            }

            WorldScreenStamper.Emit(builder: builder, screen: new WorldScreen(
                Index: index,
                Origin: new Vector3(x: 0f, y: -1000f, z: 0f),
                Right: Vector3.UnitX,
                Up: Vector3.UnitY,
                HalfWidth: 0.01f,
                HalfHeight: 0.01f,
                HalfDepth: 0.01f,
                Round: 0f,
                Source: new WorldScreenSource.None(),
                Route: WorldScreenRoute.Passive
            ));
            reserved++;
        }
    }
}

/// <summary>The runtime accounting for <see cref="WorldScreenProjection.Window"/> sessions' true, ALWAYS-PAID render
/// cost — one live count <c>world.faces</c> reads and echoes, so a decision the document already made
/// (<c>WorldDefinitionValidator</c> refuses an over-budget document BY NAME at boot/mutation time — see
/// <see cref="WorldSessionWindowCapacity"/>) is also OBSERVABLE at runtime, not merely asserted. NOT a second gate:
/// this type never refuses anything — the document validator is the one place a window count is REFUSED, this is
/// where the accepted count is READ BACK.</summary>
/// <remarks>A process-wide static counter, deliberately: exactly one <see cref="WorldScreenBinder"/> (the boot
/// world's own presentation) is ever live in one running <c>Puck.World</c> process — the same "no instance-addressed
/// form" fact <c>world.faces</c>' own description already states (screens are the boot instance's presentation
/// state, and a spawned instance carries neither a client nor a machine host to bind one from) — so there is no
/// second binder whose leases this counter could conflate with.</remarks>
internal static class WorldSessionWindowLeases {
    private static int s_liveCount;

    /// <summary>How many window sessions are live right now, process-wide.</summary>
    public static int LiveCount => s_liveCount;

    /// <summary>Acquires one window lease, incrementing <see cref="LiveCount"/> until disposed.</summary>
    /// <param name="width">The window's resolved render width, pixels — carried on the lease purely for a future
    /// per-lease cost breakdown; today's read-back sums leases rather than reading this per-entry.</param>
    /// <param name="height">The window's resolved render height, pixels.</param>
    /// <returns>A disposable that releases the lease exactly once (idempotent past the first <c>Dispose</c>).</returns>
    public static IDisposable Acquire(int width, int height) {
        s_liveCount++;

        return new Lease(width: width, height: height);
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
            s_liveCount--;
        }
    }
}
