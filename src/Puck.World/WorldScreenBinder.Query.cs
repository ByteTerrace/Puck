using Puck.Abstractions.Machines;

namespace Puck.World;

/// <summary>One diegetic screen's live state for the <c>screen.state</c> verb — whether a machine is assigned, the engine
/// that hosts it, the current source handle (nonzero = bound this frame), the stepped-frame count, and the boot fault (a
/// declared machine whose content file was missing, a webcam that would not open, a captured window not found), if any.</summary>
/// <param name="Assigned">Whether a machine is booted on the screen.</param>
/// <param name="Engine">The screen-machine engine id hosting the machine (meaningful only when <paramref name="Assigned"/>).</param>
/// <param name="Handle">The current source image-view handle (0 = unbound → the procedural fallback).</param>
/// <param name="FramesStepped">How many frames the machine has stepped since it booted.</param>
/// <param name="PendingSteps">Accepted queued-machine steps not yet completed; zero for synchronous machines.</param>
/// <param name="MaximumPendingSteps">The queued machine's finite pending-segment capacity; zero for synchronous
/// machines.</param>
/// <param name="BackpressureEvents">How many queued submissions waited for capacity since the current content was
/// loaded; zero for synchronous machines.</param>
/// <param name="Fault">A slot's live fault (a missing content file, no camera device, a window not found), or <see langword="null"/>.</param>
internal readonly record struct WorldScreenState(bool Assigned, string? Engine, nint Handle, long FramesStepped,
    long PendingSteps, int MaximumPendingSteps, long BackpressureEvents, string? Fault);
internal sealed partial class WorldScreenBinder {
    /// <summary>Returns the live machine on a screen slot as its audio drain seam, or <see langword="null"/> — a facade over
    /// <see cref="Server.WorldMachineHost.AudioMachine"/>.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public IAudioMachine? AudioMachine(int index) => m_machines.AudioMachine(index: index);
    /// <summary>Returns the live cable-link set as derived groups — a facade over
    /// <see cref="Server.WorldMachineHost.CaptureLinks"/>, the <c>world.save</c> fold source.</summary>
    public IReadOnlyList<WorldMachineCableGroup> CaptureLinks() => m_machines.CaptureLinks();
    /// <summary>Returns the current same-device image-view handle bound to a screen index, or 0 when the index is unbound, not
    /// declared, or nothing has been published yet — the live state <c>world.screens</c> reports.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <returns>The bound handle, or 0.</returns>
    public nint CurrentHandle(int index) => (m_slots.TryGetValue(
        key: index,
        value: out var slot
    )
        ? slot.Handle()
        : 0
    );
    /// <summary>Returns a one-line description of every live cable link — a facade over
    /// <see cref="Server.WorldMachineHost.DescribeLinks"/>.</summary>
    public string DescribeLinks() => m_machines.DescribeLinks();
    /// <summary>Returns whether a screen-machine engine is registered under <paramref name="engineId"/> — a facade over
    /// <see cref="Server.WorldMachineHost.HasEngine"/>.</summary>
    /// <param name="engineId">The candidate engine id.</param>
    public bool HasEngine(string engineId) => m_machines.HasEngine(engineId: engineId);
    /// <summary>Returns whether a machine is currently booted on the screen index — a facade over
    /// <see cref="Server.WorldMachineHost.HasMachine"/> (authoritative server state), reachable through the same
    /// reference every existing caller (<c>PlayerCommandModule</c>'s <c>body.engage</c>) already held.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public bool HasMachine(int index) => m_machines.HasMachine(index: index);
    /// <summary>Returns the cable link a screen currently belongs to — a facade over <see cref="Server.WorldMachineHost.LinkOf"/>.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public string? LinkOf(int index) => m_machines.LinkOf(index: index);
    /// <summary>Returns the live state of a declared screen for <c>screen.state</c>, or <see langword="null"/> when the index is
    /// not a declared screen — composed from <see cref="Server.WorldMachineHost.State"/> (machine metadata) plus this
    /// type's own live handle/fault for a machine-owning index, or purely local state (camera/capture/view/pattern)
    /// otherwise.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public WorldScreenState? State(int index) {
        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return null;
        }

        if (m_machines.State(index: index) is { Assigned: true } machineState) {
            return new WorldScreenState(
                Assigned: true,
                Engine: machineState.Engine,
                Handle: m_machines.Handle(index: index),
                FramesStepped: machineState.FramesStepped,
                PendingSteps: machineState.PendingSteps,
                MaximumPendingSteps: machineState.MaximumPendingSteps,
                BackpressureEvents: machineState.BackpressureEvents,
                Fault: machineState.Fault
            );
        }

        return new WorldScreenState(
            Assigned: false,
            Engine: null,
            Handle: slot.Handle(),
            FramesStepped: 0,
            PendingSteps: 0,
            MaximumPendingSteps: 0,
            BackpressureEvents: 0,
            Fault: slot.CurrentFault()
        );
    }
    /// <summary>Gets the live decal-text source at a screen index, or <see langword="null"/> when the slot's current
    /// source is not text — what the frame source's per-frame decal providers consult, so a live source change
    /// clears the decal the same frame it re-binds the image path.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public WorldScreenSource.Text? TextSourceAt(int index) => (m_slots.TryGetValue(
        key: index,
        value: out var slot
    )
        ? slot.Text
        : null
    );
    /// <summary>Returns the screen's live magazine and 0-based selector, or <see langword="false"/> — a facade over
    /// <see cref="Server.WorldMachineHost.TryMagazine"/> (the authoritative selector).</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="selected">The live 0-based selector.</param>
    /// <param name="magazine">The screen's magazine.</param>
    public bool TryMagazine(int index, out int selected, out WorldScreenMagazine magazine) =>
        m_machines.TryMagazine(
            index: index,
            magazine: out magazine,
            selected: out selected
        );
    /// <summary>Reads one memory byte from a screen's machine — a facade over
    /// <see cref="Server.WorldMachineHost.TryPeekMessage"/>.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="address">A machine-defined memory address.</param>
    /// <param name="value">The byte read, or 0 on failure.</param>
    /// <returns>A success flag and, on failure, a message; on success the message is empty.</returns>
    public (bool Ok, string Message) TryPeek(int index, int address, out byte value) => m_machines.TryPeekMessage(
        address: address,
        index: index,
        value: out value
    );
    /// <summary>Reads a live link's member screens by name — a facade over
    /// <see cref="Server.WorldMachineHost.TryReadLinkMembers"/>.</summary>
    /// <param name="name">The link name.</param>
    /// <param name="members">The member screen indices in cable order, on success.</param>
    public bool TryReadLinkMembers(string name, out IReadOnlyList<int> members) => m_machines.TryReadLinkMembers(
        members: out members,
        name: name
    );
    /// <summary>Reads back the live machine insert on a screen index — a facade over
    /// <see cref="Server.WorldMachineHost.TryReadMachineInsert"/>, so <c>world.save</c> can fold a runtime
    /// <c>screen.insert</c> into that screen row's <see cref="WorldScreenSource.Machine"/> source.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="engine">The engine id that booted the live machine.</param>
    /// <param name="contentPath">The content file (a cartridge ROM) the live machine booted.</param>
    /// <param name="options">The options string the live machine booted with, or <see langword="null"/>.</param>
    public bool TryReadMachineInsert(int index, out string engine, out string contentPath, out string? options) =>
        m_machines.TryReadMachineInsert(
            contentPath: out contentPath,
            engine: out engine,
            index: index,
            options: out options
        );
    /// <summary>Reads a screen's machine's current options string — a facade over
    /// <see cref="Server.WorldMachineHost.TryReadOptions"/>.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="options">The current options string.</param>
    public bool TryReadOptions(int index, out string options) => m_machines.TryReadOptions(
        index: index,
        options: out options
    );
    /// <summary>Reconfigures a screen's live machine across the engine's options vocabulary — a facade over
    /// <see cref="Server.WorldMachineHost.TryReconfigure"/>. Presentation calls that need this go through
    /// <c>ScreenCommandModule</c>'s <c>WorldScreenOp.SetOptions</c> submission instead; this facade remains for
    /// symmetry with the type's other read-through members.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="options">The engine-specific options string to retarget to.</param>
    public (bool Ok, string Message) TryReconfigure(int index, string? options) => m_machines.TryReconfigure(
        index: index,
        options: options
    );
}
