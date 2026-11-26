using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// Produces the pointer ray a <c>Simulation</c> screen and a tabletop's rules read: once per host frame, before the frame's ticks are
/// snapshotted, it casts the OS pointer through the camera of the seat whose mouse moved it
/// (<see cref="SourceRay.Through"/> over the view <see cref="WorldSeatViewports"/> published for the frame on screen)
/// and sustains the ray on that seat's lane as the <see cref="SourcePointerCommands.Origin"/> and
/// <see cref="SourcePointerCommands.Direction"/> commands (<see cref="InputRouter.Sustain"/>), so every tick the frame
/// runs carries it, a catch-up burst included. Only a seat that still holds the mouse the position is attributed to
/// points: the ray ends when that mouse leaves the seat or the position names no mouse (a platform that reports only
/// the aggregate cursor, or a gamepad-only seat), when the pointer leaves the seat's view or the window
/// (<see cref="WorldPointer.ForgetPosition"/>), or when the world declares no <c>Simulation</c> screen, no placement
/// pointer surface and no engageable tabletop; the server then reads <c>on</c> 0 and no pointed cell. A seat whose view is
/// first person aims by it: its ray and its mouse's contacts cast through the view's centre, the reticle, since the
/// mouse steers that view and the OS cursor rests where the view's drag locked it, or reports no position at all.
/// <para>The same cast decides each seat's pointer context (<see cref="WorldContextFamilies.Pointer"/>): <c>surface</c>
/// while the ray lands on a pointer surface (<see cref="WorldPointerSurfaces.Lands"/>, the rectangles a <c>$pointer:</c>
/// read maps through), <c>none</c> otherwise, re-read only while the seat holds no pointer button. So a press that lands
/// on a surface stays in the binding group a context row gives the surface until its release, and a drag begun
/// elsewhere stays the seat's own controls' when it crosses one: pointer capture is a binding group the document
/// authors, never a branch on a command here.</para>
/// </summary>
/// <remarks>
/// <para>The same capture casts every pointing contact (<see cref="WorldContacts"/>: a finger, a pen tip, or the mouse
/// while its primary button is held) through its seat's view, the one cast the pointer ray takes, and queues it on the
/// seat (<see cref="SeatController.AddContact"/>) for its next submission, so mouse and touch reach a screen through
/// one door. A contact that touches outside its seat's view is not sent, and nor is anything after it; one that touched
/// inside keeps sending until it lifts, wherever it moves, so a screen always sees its lift.</para>
/// <para>The camera is presentation float state; the ray enters the command plane as the float values the tick's
/// snapshot records and is quantized once into simulation at the seat verb, so a replay reproduces it exactly; a
/// contact's ray is cast in fixed point (<see cref="SourceRay.Through"/>) and rides the submission as cast.
/// Single-threaded: the host loop services every <see cref="ISnapshotInputCapture"/> on the window-pump thread, where
/// the frame source also publishes the seat views.</para>
/// </remarks>
public sealed class WorldPointerRayCapture : ISnapshotInputCapture {
    private readonly WorldContacts? m_contacts;
    private readonly List<WorldContactTransition> m_contactScratch = [];
    private readonly Func<WorldDefinition> m_definition;
    // The contacts whose touch was sent, by seat, device and number, with the last ray each cast: only these send their
    // moves and lift.
    private readonly Dictionary<(int Slot, ContactDevice Device, byte Id), SourceRay> m_sentContacts = [];
    private readonly WorldPointer m_pointer;
    private readonly PlayerRoster m_roster;
    private readonly InputRouter m_router;
    private readonly WorldSeatBindings? m_seatBindings;
    private readonly WorldSeatViewports m_viewports;

    private IReadOnlyList<WorldPointerTarget> m_surfaces = [];
    private WorldDefinition? m_surfacesOf;
    // The seat the ray is sustained on, or -1 while none is.
    private int m_sustainedSlot = -1;

    /// <summary>Initializes a new instance of the <see cref="WorldPointerRayCapture"/> class.</summary>
    /// <param name="pointer">The live pointer store, read non-destructively.</param>
    /// <param name="viewports">The per-seat view publication of the frame on screen.</param>
    /// <param name="roster">The roster that says which seat holds the mouse the position is attributed to.</param>
    /// <param name="definition">Reads the delivered definition whose screens the ray may map through.</param>
    /// <param name="router">The router whose seat lanes carry the ray.</param>
    /// <param name="contacts">The contact store whose transitions the capture casts into the seats' submissions, or
    /// <see langword="null"/> for a host that takes no contacts.</param>
    /// <param name="seatBindings">The per-seat binding resolver the pointer context family
    /// (<see cref="WorldContextFamilies.Pointer"/>) publishes into, or <see langword="null"/> for a host that publishes
    /// none.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public WorldPointerRayCapture(WorldPointer pointer, WorldSeatViewports viewports, PlayerRoster roster, Func<WorldDefinition> definition, InputRouter router, WorldContacts? contacts = null, WorldSeatBindings? seatBindings = null) {
        m_contacts = contacts;
        m_seatBindings = seatBindings;
        ArgumentNullException.ThrowIfNull(argument: pointer);
        ArgumentNullException.ThrowIfNull(argument: viewports);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: router);

        m_definition = definition;
        m_pointer = pointer;
        m_roster = roster;
        m_router = router;
        m_viewports = viewports;
    }

    // The document's pointer surfaces, enumerated again only when the delivered document changes.
    private IReadOnlyList<WorldPointerTarget> Surfaces(WorldDefinition definition) {
        if (!ReferenceEquals(objA: definition, objB: m_surfacesOf)) {
            m_surfacesOf = definition;
            m_surfaces = WorldPointerSurfaces.Of(definition: definition);
        }

        return m_surfaces;
    }
    // Whether anything in the world reads a pointer ray: a Simulation screen, a placement's pointer surface, or a
    // tabletop a seat may sit at, whose rules read the cell the ray meets (board(pointer, …)).
    private bool DeclaresPointerTarget(WorldDefinition definition) {
        if (Surfaces(definition: definition).Count > 0) {
            return true;
        }

        foreach (var placement in definition.Placements) {
            if (placement.Board?.Engage is not null) {
                return true;
            }
        }

        return false;
    }
    // Publishes each seat's pointer context: `surface` for the seat whose ray lands on a pointer surface, `none` for
    // every other. A seat holding a pointer button keeps the state its press landed in, so a press is the surface's or
    // the seat's own controls' until its release.
    private void PublishPointer(int pointing, bool lands) {
        if (m_seatBindings is not { } bindings) {
            return;
        }

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (m_pointer.IsAnyButtonDown(slot: slot)) {
                continue;
            }

            bindings.SetContextState(
                family: WorldContextFamilies.Pointer,
                slot: slot,
                state: (((slot == pointing) && lands)
                    ? WorldContextFamilies.PointerSurface
                    : WorldContextFamilies.PointerNone)
            );
        }
    }
    private void End() {
        if (m_sustainedSlot < 0) {
            return;
        }

        _ = m_router.EndSustain(
            command: SourcePointerCommands.Origin,
            slot: m_sustainedSlot
        );
        _ = m_router.EndSustain(
            command: SourcePointerCommands.Direction,
            slot: m_sustainedSlot
        );
        m_sustainedSlot = -1;
    }

    /// <inheritdoc/>
    public void CaptureFrame(ulong frameKey) {
        var definition = m_definition();
        var declares = DeclaresPointerTarget(definition: definition);

        CaptureContacts(declares: declares);

        // The seat and its device in one read, so they always name the same report. A seat that aims by its view points
        // through its reticle whether or not the OS cursor reports a position, so with no positioned mouse the fallback
        // seat still points while its view is first person.
        var slot = (m_pointer.Positioned?.Slot ?? WorldPointerSlot.Resolve(roster: m_roster));
        var mouse = (
            (m_pointer.Positioned is (var positionedSlot, var device)) &&
            (m_roster.DeviceSlot(device: device) == positionedSlot) &&
            (m_roster.KindOf(device: device) == InputDeviceKind.Mouse)
        );
        var aimed = AimsByView(slot: slot);

        if (!declares || (!mouse && !aimed)) {
            End();
            PublishPointer(lands: false, pointing: -1);

            return;
        }
        if (!TryCast(position: m_pointer.Position(slot: slot), ray: out var ray, slot: slot, outside: false, aimed: aimed)) {
            End();
            PublishPointer(lands: false, pointing: -1);

            return;
        }

        PublishPointer(
            lands: WorldPointerSurfaces.Lands(ray: ray, targets: Surfaces(definition: definition)),
            pointing: slot
        );
        if (slot != m_sustainedSlot) {
            End();
        }

        _ = m_router.Sustain(
            command: SourcePointerCommands.Origin,
            slot: slot,
            value: CommandValue.Axis(value: ray.Origin.ToVector3())
        );
        _ = m_router.Sustain(
            command: SourcePointerCommands.Direction,
            slot: slot,
            value: CommandValue.Axis(value: ray.Direction.ToVector3())
        );
        m_sustainedSlot = slot;
    }

    // Casts each queued contact transition through its seat's view onto the seat's next submission. With nothing in
    // the world to read a contact, the transitions are taken and dropped, and so is every contact's sent mark.
    private void CaptureContacts(bool declares) {
        if (m_contacts is not { } contacts) {
            return;
        }

        m_contactScratch.Clear();
        contacts.TakePending(into: m_contactScratch);

        if (!declares) {
            m_sentContacts.Clear();

            return;
        }

        foreach (var transition in m_contactScratch) {
            var key = (transition.Slot, transition.Device, transition.Id);
            var touching = (transition.Phase == ContactPhase.Down);
            var sent = m_sentContacts.TryGetValue(key: key, value: out var lastRay);

            // A second touch of a contact already sent, or a move or lift of one never sent, carries nothing new.
            if (touching == sent) {
                continue;
            }
            if (m_roster.Seat(slot: transition.Slot) is not { } seat) {
                _ = m_sentContacts.Remove(key: key);

                continue;
            }

            var phase = transition.Phase;

            // The mouse points where its seat points: through the reticle while the seat aims by its view. A finger or a
            // pen tip touches the screen where it touches, whatever the view.
            var aimed = ((transition.Device == ContactDevice.Mouse) && AimsByView(slot: transition.Slot));

            if (!TryCast(position: transition.Position, ray: out var ray, slot: transition.Slot, outside: !touching, aimed: aimed)) {
                if (touching) {
                    continue;
                }

                // The seat's view is gone: the contact ends where it was last seen, as a cancellation, never a tap.
                phase = ContactPhase.Cancel;
                ray = lastRay;
            }

            _ = seat.AddContact(contact: new SurfaceContact(Device: transition.Device, Id: transition.Id, Phase: phase, Ray: ray));

            if (phase is ContactPhase.Up or ContactPhase.Cancel) {
                _ = m_sentContacts.Remove(key: key);
            } else {
                m_sentContacts[key] = ray;
            }
        }

        m_contactScratch.Clear();
    }
    // Whether a seat aims by its view: in a first-person view the camera is the seat's eye and the reticle at the image's
    // centre is where it points, so its mouse, which steers that view, points there rather than where the OS cursor last
    // rested (a cursor the view's drag locked and hid, or one that never reported a position).
    private bool AimsByView(int slot) => ((m_roster.Seat(slot: slot) is { } seat) && seat.View.FirstPersonLook);
    // The ray a client point casts through a seat's published camera: the one cast every pointing input takes. A point
    // outside the seat's view casts only when the caller allows it (a contact that touched inside keeps its lift). An
    // aimed cast goes through the view's centre (AimsByView) wherever the point lies.
    private bool TryCast(int slot, Vector2 position, bool outside, out SourceRay ray, bool aimed = false) {
        var view = m_viewports.Seat(slot: slot);
        var place = m_viewports.Locate(
            framePosition: out _,
            local: out var local,
            position: position,
            view: in view
        );

        if (aimed && (place != WorldSeatPointerPlace.NoView)) {
            (place, local) = (WorldSeatPointerPlace.Inside, new Vector2(x: 0.5f, y: 0.5f));
        }
        if ((place == WorldSeatPointerPlace.NoView) || ((place != WorldSeatPointerPlace.Inside) && !outside)) {
            ray = default;

            return false;
        }

        ray = SourceRay.Through(
            camera: view.Camera,
            image: FixedVector2.FromVector2(value: local)
        );

        return true;
    }
}
