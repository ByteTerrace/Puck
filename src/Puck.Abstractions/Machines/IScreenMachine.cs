using System.Numerics;
using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Machines;

/// <summary>
/// An arbitrary deterministic machine hosted as a frame + input endpoint — the neutral primitive behind a diegetic
/// screen. A machine holds content (a cartridge, a disc image, a program), advances by an exact integer tick budget with
/// one normalized <see cref="MachinePadState"/> per frame, and exposes its native framebuffer as a shader-readable
/// image-view handle. An SM83 GamingBrick is one implementation; an N64/GameCube/any-other engine is another — the host
/// wires whichever the run document declares and never names a concrete machine. A machine authors nothing: it is a pure
/// function of its content, the tick budget, and the sampled input.
/// </summary>
/// <remarks>Backend-neutral: the framebuffer publishes through the <see cref="Puck.Abstractions.Gpu"/> device/compute
/// seams the host resolves, never a concrete GPU backend. Single-producer by contract, not single-threaded: a host calls
/// <see cref="Step"/>, <see cref="PublishFrame"/>, and the state reads from one producer thread, but an implementation MAY
/// run an internal worker (see <see cref="IQueuedScreenMachine"/>) and publish under its own synchronization. On such an
/// implementation <see cref="Step"/> is a submit-and-drain convenience for generic callers that do not opt into the queued
/// capability.</remarks>
public interface IScreenMachine : IDisposable {
    /// <summary>Gets whether content has been loaded — <see langword="false"/> for an empty machine awaiting
    /// <see cref="LoadContent"/> (a dark framebuffer).</summary>
    bool IsAssigned { get; }

    /// <summary>Gets the native image-view handle of the machine's unresampled framebuffer, already shader-readable — the
    /// handle a screen source samples. 0 before the first <see cref="PublishFrame"/> (or after a device loss).</summary>
    nint NativeImageViewHandle { get; }

    /// <summary>Gets the light the screen emits — the framebuffer's average color, normalized 0..1. Zero for an
    /// unassigned machine.</summary>
    Vector3 EmittedLight { get; }

    /// <summary>Loads (or live-swaps to) content: flushes and tears down any running machine, then assembles the new
    /// content's machine.</summary>
    /// <param name="data">The content image (a ROM / program image).</param>
    /// <param name="savePath">The content's persistent-save path, or <see langword="null"/> for an in-memory-only save.</param>
    void LoadContent(byte[] data, string? savePath = null);

    /// <summary>Ejects the content: flushes its save, tears down the machine, and returns the framebuffer to black. The
    /// machine is empty until content is loaded again.</summary>
    void Eject();

    /// <summary>Advances the machine by a fixed-step tick budget holding <paramref name="input"/> for the whole budget,
    /// then stages a fresh framebuffer. A no-op (returns <see langword="false"/>) for an unassigned machine or a zero
    /// budget.</summary>
    /// <param name="deltaTicks">The frame's fixed-step tick budget.</param>
    /// <param name="input">The normalized controller image held over the budget.</param>
    /// <returns><see langword="true"/> when the machine stepped and staged a fresh framebuffer.</returns>
    bool Step(ulong deltaTicks, in MachinePadState input);

    /// <summary>Uploads the staged framebuffer to a shader-readable GPU image and (re)binds
    /// <see cref="NativeImageViewHandle"/>. Uploads only when the framebuffer changed since the last publish (or after a
    /// device loss); an unassigned machine publishes a solid-black frame once. Never throws for a missing device.</summary>
    /// <param name="deviceContext">The GPU device context to upload on.</param>
    /// <param name="gpu">The neutral GPU compute services (resolves the upload factory).</param>
    void PublishFrame(IGpuDeviceContext deviceContext, IGpuComputeServices gpu);

    /// <summary>Drops the GPU upload after a device loss: the next <see cref="PublishFrame"/> rebuilds it on the fresh
    /// device. The machine's CPU state survives untouched.</summary>
    void NotifyDeviceLost();

    /// <summary>Flushes the machine's persistent save. An unforced flush is gated on dirty state so an idle machine never
    /// writes; a forced flush (eject/dispose) persists a final state. An I/O failure is reported, never propagated.</summary>
    /// <param name="force">When <see langword="true"/>, flush even when only a clock-style change is pending.</param>
    void FlushSave(bool force = false);
}
