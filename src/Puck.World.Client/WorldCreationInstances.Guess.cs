using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

// The guess layer: the pose of the presenting seat's own body with its leads read from the seat's own input
// (CreationDriverDocument.Lead), drawn in that seat's own view alone.
//
// The invariant: a guess colours only the owning seat's own presented picture. What enforces it:
// - The layer is a registration of its own (Registration.GuessLayer), never an entry of m_pool, so every reader that
//   resolves a body through the pool (anchors and part-anchored cameras, carried screens, other bodies' effectors, rates,
//   body.rig, the portal straddle, picking) resolves the shared pose, which reads its cells.
// - Only a guess layer's view answers a lead (WorldRigReads.ForGuess); a pool registration's view answers none and
//   refuses a guess (WorldRigReads.BindGuess), so the shared pose cannot represent one.
// - The layer packs into rows past the pool's own (GuessOverlay.OverlaySlot), which no program instruction, draw record or
//   pool lookup names. Only a view snapshot carrying GuessOverlay reads them (SdfViewSnapshot.PoseOverlay,
//   sdfViewPoseSlot), and the presenter sets it on the guess's seat's own view alone.
// Where it stops: the layer's rows are uploaded with the rest of the table, so a kernel that reads a row by an index it
// did not take through sdfViewPoseSlot reads the shared pose (a light anchored to a part, a mirror's or a portal's own view);
// a rigid draw's culling box is its shared pose's, so a guessed part that leaves it at the edge of the view can be culled
// for the frames the guess lasts.
public sealed partial class WorldCreationInstances {
    // The layer, the pool registration it poses again, the guess it reads this frame, the rows it reserves past the pool's
    // own, the presentation clock its leads age on, and whether its rows are new this frame (no motion history yet).
    private Registration? m_guessLayer;
    private Registration? m_guessOf;
    private IWorldSeatGuess? m_guess;
    private int m_guessRows;
    private float m_guessClock;
    private bool m_guessFresh;

    /// <summary>Gets the view-local pose overlay that draws the presenting seat's guess in that seat's own view
    /// (<see cref="SdfViewSnapshot.PoseOverlay"/>): the guessed body's slots, read from the guess layer's rows. Default
    /// (no slots) when the pool holds no guess layer or its body was not packed this frame.</summary>
    /// <remarks>Absolute slots of the table the last <see cref="PackTransforms"/> wrote. The presenter hands it only to the
    /// view of <see cref="GuessSlot"/>; every other view, and every host reader of the table, reads the shared pose.</remarks>
    public SdfPoseOverlay GuessOverlay { get; private set; }
    /// <summary>Gets the roster slot of the seat whose own view draws <see cref="GuessOverlay"/>, or -1 for none.</summary>
    public int GuessSlot => ((GuessOverlay.SlotCount > 0) ? (m_guess?.Slot ?? -1) : -1);

    /// <summary>Takes this frame's guess from the source the pool presents (<see cref="IWorldStampSource.SeatGuess"/>)
    /// and reserves the guess layer's rows: the guessed body's registration posed again with its leads, when the body
    /// wears a creation with a led driver. Call once a frame after the pool reconciles and before the frame lays its
    /// slots out (<see cref="DynamicSlotCount"/>).</summary>
    /// <param name="source">The source the pool presents.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public void PrepareGuess(IWorldStampSource source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        var guess = source.SeatGuess;
        var live = ((guess is { Body: >= 0 }) ? FindBody(bodyIndex: guess.Body) : null);

        if ((live is null) || (live.Look is not { } look) || !Leads(rig: live.Rig)) {
            RetireGuess();

            return;
        }
        // A look edit can change the slot partition (its part followers), which the layer's rows must mirror.
        if (!ReferenceEquals(objA: m_guessOf, objB: live) || (m_guessLayer is not { } layer) || !ReferenceEquals(objA: layer.Look, objB: look) || (layer.Slots != live.Slots)) {
            RetireGuess();
            layer = RegisterBody(guessLayer: true, stamp: new BodyStamp(BodyIndex: guess!.Body, Creation: live.Creation, Look: look, Scale: live.Scale));
            ApplyMotion(dynamics: source.Definition.Dynamics, live: layer, motion: look.Motion);
            m_guessLayer = layer;
            m_guessOf = live;
            m_guessFresh = true;
        }

        m_guess = guess;
        m_guessRows = layer.Slots;
    }
    // Packs the guess layer into its rows, after the pool and the rigid copies, and publishes the overlay that reads them.
    private void PackGuess(Span<DynamicTransform> transforms, IWorldStampSource client, SdfMovedTransforms moved, int slotBase, Vector3 parkPosition, float deltaSeconds) {
        GuessOverlay = default;
        if ((m_guessLayer is not { } layer) || (m_guessOf is not { } live)) { return; }

        var poolIndex = Array.IndexOf(array: m_pool, value: live);
        var overlaySlot = (slotBase + ArticulatedSlotCount + Rigid.SlotCount);

        m_guessClock += deltaSeconds;
        if ((poolIndex < 0) || !ReferenceEquals(objA: m_packedRegistrations[poolIndex], objB: live) || ((overlaySlot + layer.Slots) > transforms.Length)) { return; }

        var (rootPosition, rootRotation, placementScale) = RootPose(client: client, live: layer);
        var shapeCount = (layer.Creation.EngineDocument.Shapes?.Count ?? 0);

        if (m_partSpheres.Length < shapeCount) { Array.Resize(array: ref m_partSpheres, newSize: shapeCount); }
        layer.Reads.Bind(bodyIndex: (layer.BodyIndex ?? -1), mirror: client.StateMirror);
        layer.Reads.Arrive(first: layer.Creation, second: layer.Look);
        layer.View.Bind(body: (layer.BodyIndex ?? -1), client: client, poses: this);
        layer.View.BindGuess(clockSeconds: m_guessClock, guess: m_guess);
        _ = layer.View.Revision;

        var slotsMoved = PackRegistration(
            client: client,
            deltaSeconds: deltaSeconds,
            live: layer,
            parkPosition: parkPosition,
            placementScale: placementScale,
            rootDeltaSeconds: deltaSeconds,
            rootPosition: rootPosition,
            rootRotation: rootRotation,
            rootSlot: overlaySlot,
            shapeCount: shapeCount,
            transforms: transforms
        );

        layer.PackedRoot = transforms[overlaySlot];
        // New rows have no motion history, so their first frame is a cut the reprojection does not read as motion.
        _ = moved.Commit(count: layer.Slots, moved: slotsMoved, reseat: m_guessFresh, start: overlaySlot);
        m_guessFresh = false;
        GuessOverlay = new SdfPoseOverlay(FirstSlot: (slotBase + RootSlotOffset(index: poolIndex)), OverlaySlot: overlaySlot, SlotCount: layer.Slots);
    }
    /// <summary>Reads the guess layer's last packed rig state for a body, beside <see cref="TryBodyRig"/>'s shared pose:
    /// the drivers as the seat's own view drew them, each led driver naming whether it read the seat's guess.</summary>
    /// <param name="bodyIndex">The population entity index.</param>
    /// <param name="state">The guess layer's state, or <see langword="null"/> when the pool holds no guess layer for the
    /// body.</param>
    /// <param name="slot">The roster slot of the seat whose own view draws the layer, or -1.</param>
    /// <returns><see langword="true"/> when the pool's guess layer poses the body.</returns>
    public bool TryGuessRig(int bodyIndex, out WorldRigState? state, out int slot) {
        state = null;
        slot = -1;
        if ((m_guessLayer is not { } layer) || (layer.BodyIndex != bodyIndex) || (layer.PackedRoot is null)) { return false; }

        state = RigState(live: layer, shapeName: null);
        slot = (m_guess?.Slot ?? -1);

        return true;
    }
    private void RetireGuess() {
        m_guessLayer?.Reads.Release();
        m_guessLayer = null;
        m_guessOf = null;
        m_guess = null;
        m_guessRows = 0;
        GuessOverlay = default;
    }
    private static bool Leads(Puck.World.Authoring.CreationRig rig) {
        foreach (var driver in rig.Drivers) {
            if (driver.Lead is not null) { return true; }
        }

        return false;
    }
}
