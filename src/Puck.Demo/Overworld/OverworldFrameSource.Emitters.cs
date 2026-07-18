using System.Numerics;
using Puck.Demo.Garden;
using Puck.Demo.Museum;
using Puck.SdfVm;

namespace Puck.Demo.Overworld;

/// <summary>
/// The room composition's own <see cref="ISdfSceneEmitter"/> implementations, one per coherent scene block.
/// Every one is a nested class rather than a top-level type in a public seam: each reads
/// <see cref="OverworldFrameSource"/>'s own private per-frame state directly through <c>owner</c> (a nested class may
/// see its enclosing type's private members — C#'s accessibility rule for nested types), which is the cheapest way to
/// extract these blocks without inventing a second public surface that would exist for these eight classes' benefit
/// alone. <see cref="Puck.Demo.Creator.CreatorSceneEmitter"/>/<see cref="Puck.Demo.World.WorldSceneEmitter"/>/
/// <see cref="Puck.Demo.Creator.CompanionEmitter"/> are the OTHER three room-list members; they live in their own
/// files because they wrap an existing renderer type rather than reading
/// <see cref="OverworldFrameSource"/>'s fields directly.
/// <para>
/// Every emitter here (except <see cref="DiegeticBarEmitter"/>, whose own content signature the room
/// revision folds in — see <see cref="OverworldFrameSource.AdvanceRoomRevision"/>) keeps the DEFAULT
/// <see cref="ISdfSceneEmitter.Revision"/> (0) — the whole room rebuilds as one program on one combined
/// trigger (see <c>m_roomRevision</c>), so <see cref="RoomEmitter"/> alone carries it forward; splitting the trigger
/// per-emitter here would only add risk for no behavior change (nothing downstream cares WHICH emitter's content
/// changed, only THAT the room did).
/// </para></summary>
public sealed partial class OverworldFrameSource {
    /// <summary>The floor, four perimeter walls, and the cartridge-shelf brackets (see
    /// <see cref="EmitFloorWallsShelf"/>) — studio-suppressed like every other room-shell emitter.</summary>
    private sealed class RoomEmitter(OverworldFrameSource owner) : ISdfSceneEmitter {
        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
            if (owner.StudioSuppressed(context: in context)) {
                return;
            }

            owner.EmitFloorWallsShelf(builder: builder);
        }

        public int Revision => owner.m_roomRevision;
    }

    /// <summary>The console stands (pedestal, screen slab, cartridge-slot patch, control housing, and the animated
    /// control cluster — see <see cref="EmitConsoleStands"/>/<see cref="PackControlTransforms"/>). Its dynamic slots
    /// are the per-console control clusters (<see cref="ControlsPerConsole"/> each), sized once at construction from
    /// the room's (fixed-for-the-run) console count.</summary>
    private sealed class ConsoleStandEmitter(OverworldFrameSource owner) : ISdfSceneEmitter {
        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
            if (owner.StudioSuppressed(context: in context)) {
                return;
            }

            owner.EmitConsoleStands(builder: builder, bootedMask: owner.m_world.BootedMask, probeWorstCase: context.Probe, controlSlotBase: context.SlotBase);
        }

        public int DynamicSlotCount => (owner.m_room.Consoles.Count * ControlsPerConsole);

        public void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context) =>
            owner.PackControlTransforms(slots: slots, slotBase: context.SlotBase);
    }

    /// <summary>ONE player box per fixed <see cref="OverworldWorld.MaxPlayers"/> slot (see
    /// <see cref="EmitPlayerBoxes"/>) — bakes THIS frame's FRESH <see cref="m_playerRenderTransforms"/> (populated
    /// earlier in <see cref="CaptureFrame"/>, before any composition source packs — see that field's remarks).</summary>
    private sealed class PlayerBoxEmitter(OverworldFrameSource owner) : ISdfSceneEmitter {
        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
            if (owner.StudioSuppressed(context: in context)) {
                return;
            }

            owner.EmitPlayerBoxes(builder: builder, probeWorstCase: context.Probe, slotBase: context.SlotBase);
        }

        public int DynamicSlotCount => OverworldWorld.MaxPlayers;

        public void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context) {
            for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
                slots[(context.SlotBase + slot)] = owner.m_playerRenderTransforms[slot];
            }
        }
    }

    /// <summary>The diegetic workbench prop (see <see cref="EmitWorkbench"/>) — the desk + terminal panel that lights
    /// up on the editor reveal.</summary>
    private sealed class WorkbenchEmitter(OverworldFrameSource owner) : ISdfSceneEmitter {
        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
            if (owner.StudioSuppressed(context: in context)) {
                return;
            }

            owner.EmitWorkbench(builder: builder, origin: Vector3.Zero, probeWorstCase: context.Probe);
        }
    }

    /// <summary>The diegetic console terminal (see <see cref="EmitTerminal"/>) — a room-only prop whose CRT slab
    /// live-mirrors the developer console.</summary>
    private sealed class TerminalEmitter(OverworldFrameSource owner) : ISdfSceneEmitter {
        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
            if (owner.StudioSuppressed(context: in context)) {
                return;
            }

            owner.EmitTerminal(builder: builder, origin: Vector3.Zero, probeWorstCase: context.Probe);
        }
    }

    /// <summary>The diegetic UI bar (Tier 2 — see <see cref="EmitDiegeticBar"/>): the overlay action bar mirrored as
    /// camera-rig-mounted geometry. <see cref="SlotBase"/> caches the ONE dynamic slot the composition host assigns
    /// this emitter (captured on the first <see cref="Emit"/>/<see cref="PackDynamicTransforms"/> call — the
    /// construction-time worst-case probe guarantees this happens before <see cref="InstallDiegeticUi"/> ever needs
    /// it — see that method's remarks).</summary>
    private sealed class DiegeticBarEmitter(OverworldFrameSource owner) : ISdfSceneEmitter {
        /// <summary>The dynamic-transform slot this emitter's content rides — captured from the FIRST
        /// <see cref="SdfEmitContext.SlotBase"/> it ever saw (stable for the composition's lifetime — see
        /// <see cref="SdfCompositionFrameSource"/>'s slot-assignment contract). -1 before that first call.</summary>
        public int SlotBase { get; private set; } = -1;

        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
            SlotBase = context.SlotBase;

            if (owner.StudioSuppressed(context: in context)) {
                return;
            }

            owner.EmitDiegeticBar(builder: builder, probeWorstCase: context.Probe, slotBase: context.SlotBase);
        }

        public int DynamicSlotCount => DiegeticUiSlotCount;

        public void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context) {
            SlotBase = context.SlotBase;
            // Recomputed each frame from the room camera (m_currentViews, fresh — see CaptureFrame) so the physical
            // HUD tracks the camera without any program rebuild. A hidden/absent bar leaves an identity transform on
            // its (unreferenced) slot — the bar instance is only emitted when visible, so the slot value then never
            // matters.
            slots[context.SlotBase] = (owner.m_diegeticMount?.Invoke(owner.m_currentViews)
                ?? new DynamicTransform(Position: owner.HiddenPosition(), Orientation: Quaternion.Identity));
        }

        // The bar's own content signature (page/binding/family) already joins the shared room revision (see
        // AdvanceRoomRevision) — no independent Revision needed here.
    }

    /// <summary>The diegetic sagging link cable (see <see cref="EmitLinkCable"/>) — whimsy, emitted only while two
    /// cabinets are linked (its own gate lives inside <see cref="EmitLinkCable"/>, NOT <see cref="StudioSuppressed"/>
    /// — the cable is not part of the studio-suppressed room block in the original BuildProgram).</summary>
    private sealed class LinkCableEmitter(OverworldFrameSource owner) : ISdfSceneEmitter {
        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) =>
            owner.EmitLinkCable(builder: builder, origin: Vector3.Zero, probeWorstCase: context.Probe);
    }

    /// <summary>The studio cyclorama backdrop (see <see cref="EmitStudioBackdrop"/>) — the neutral gray shell a
    /// <c>--scenario</c> studio review sits inside; its own gate (the INVERSE of <see cref="StudioSuppressed"/>)
    /// lives inside <see cref="EmitStudioBackdrop"/>.</summary>
    private sealed class StudioBackdropEmitter(OverworldFrameSource owner) : ISdfSceneEmitter {
        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) =>
            owner.EmitStudioBackdrop(builder: builder, origin: Vector3.Zero, probeWorstCase: context.Probe);
    }

    /// <summary>The planted gardens (the deterministic-garden feature): each seed's tree, grown to however far its
    /// (Seed, CurrentTick − PlantedTick) puts it — see <see cref="Puck.Demo.Garden.GardenRenderer"/>, which wraps an
    /// existing renderer (mirrors <see cref="Creator.CompanionEmitter"/>'s pattern) rather than reading this source's
    /// fields directly, so this class's own coupling stays flat.</summary>
    private sealed class GardenEmitter(OverworldFrameSource owner) : ISdfSceneEmitter {
        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
            if (owner.StudioSuppressed(context: in context)) {
                return;
            }

            GardenRenderer.Emit(builder: builder, origin: Vector3.Zero, floorY: owner.m_room.FloorY, gardens: owner.m_world.Gardens, currentTick: owner.m_world.CurrentTick, probeWorstCase: context.Probe);
        }
    }

    /// <summary>THE REPLAY MUSEUM + THE DROSTE DOOR (personal-touch feature): the museum's four wall screens/
    /// placards and the door's frame + opening — see <see cref="Puck.Demo.Museum.MuseumRenderer"/>, which wraps an
    /// existing-renderer shape (mirrors <see cref="GardenEmitter"/>'s pattern) rather than reading this source's
    /// fields directly. Purely static room furniture: no sim state, so the probe and every live rebuild emit
    /// byte-identically.</summary>
    private sealed class MuseumEmitter(OverworldFrameSource owner) : ISdfSceneEmitter {
        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
            if (owner.StudioSuppressed(context: in context)) {
                return;
            }

            MuseumRenderer.Emit(builder: builder, origin: Vector3.Zero, boundsMin: owner.m_room.BoundsMin, boundsMax: owner.m_room.BoundsMax, floorY: owner.m_room.FloorY, wallThickness: owner.m_room.WallThickness);
        }
    }
}
