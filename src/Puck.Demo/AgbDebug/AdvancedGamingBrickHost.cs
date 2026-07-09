using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.AdvancedGamingBrick;

namespace Puck.Demo.AgbDebug;

/// <summary>
/// A single native ARM7TDMI (Game Boy Advance) machine hosted for the demo — the real
/// <see cref="AdvancedGamingBrickMachine"/> core, direct-booted from a cartridge ROM, plus the minimal GPU path that
/// publishes its 240×160 <c>0xAARRGGBB</c> framebuffer as a shader-readable image view a diegetic screen samples. It
/// owns its own DI scope (so two machines never share a peripheral, mirroring the POST battery's <c>PostMachine</c>),
/// and exposes the raw stepping/inspection surface the <c>agb.*</c> execution-control verbs drive. The host owns
/// cadence entirely: it never steps on its own — the caller decides when (per-frame while running, or one instruction
/// at a time while paused).
/// </summary>
internal sealed class AdvancedGamingBrickHost : IDisposable {
    /// <summary>The native GBA screen width in pixels.</summary>
    public const int ScreenWidth = 240;
    /// <summary>The native GBA screen height in pixels.</summary>
    public const int ScreenHeight = 160;
    /// <summary>The native GBA screen aspect (240×160 = 3:2) — the diegetic slab is sized to it.</summary>
    public const float ScreenAspect = ((float)ScreenWidth / ScreenHeight);

    private readonly ServiceProvider m_provider;
    private readonly IServiceScope m_scope;
    private readonly AdvancedGamingBrickMachine m_machine;
    private readonly AgbBus? m_concreteBus;
    private readonly byte[] m_rgba = new byte[ScreenWidth * ScreenHeight * 4];

    private IGpuSurfaceUpload? m_upload;
    private nint m_viewHandle;
    private Vector3 m_averageColor;
    private long m_frameCount;
    private bool m_disposed;

    private AdvancedGamingBrickHost(ServiceProvider provider, IServiceScope scope, AdvancedGamingBrickMachine machine) {
        m_provider = provider;
        m_scope = scope;
        m_machine = machine;
        m_concreteBus = (machine.Bus as AgbBus);
    }

    /// <summary>Builds an isolated, direct-booted machine for a BIOS image and cartridge ROM. Mirrors the POST
    /// battery's <c>PostMachine.Build</c> so the two cores boot identically.</summary>
    /// <param name="bios">The 16&#160;KiB BIOS image (a zeroed stub is valid under direct boot).</param>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <returns>The assembled, direct-booted host. The caller owns it and must dispose it.</returns>
    public static AdvancedGamingBrickHost Build(ReadOnlyMemory<byte> bios, byte[] rom) {
        var services = new ServiceCollection();

        _ = services.AddAdvancedGamingBrick();
        _ = services.AddReplacementBios(image: bios);
        _ = services.AddScoped<AgbCartridge>(implementationFactory: _ => new AgbCartridge(rom: rom));

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var machine = scope.ServiceProvider.GetRequiredService<AdvancedGamingBrickMachine>();

        machine.DirectBoot();

        return new AdvancedGamingBrickHost(provider: provider, scope: scope, machine: machine);
    }

    /// <summary>The number of whole frames run since boot.</summary>
    public long FrameCount => m_frameCount;

    /// <summary>The pre-flight identity (classification + content hash) of the BIOS this machine booted with.</summary>
    public AgbBiosIdentity BiosIdentity => m_machine.BiosIdentity;

    /// <summary>The master-clock cycle counter (0 when the bus is a bare test bus).</summary>
    public long Cycles => (m_concreteBus?.Cycles ?? 0L);

    /// <summary>The most recent framebuffer's average colour (0..1) — the light a diegetic screen emits into the room.</summary>
    public Vector3 AverageColor => m_averageColor;

    /// <summary>The shader-readable framebuffer view handle (0 until the first <see cref="Produce"/>).</summary>
    public nint ViewHandle => m_viewHandle;

    /// <summary>Executes exactly one instruction (or a pending exception entry).</summary>
    public void Step() => m_machine.Step();

    /// <summary>Runs one whole frame (~280,896 cycles). Returns the number of instructions executed.</summary>
    public int RunFrame() {
        var steps = m_machine.RunFrame();

        ++m_frameCount;

        return steps;
    }

    /// <summary>Captures the machine's entire mutable state into a self-contained snapshot (the whole-machine
    /// savestate). Restore it into this host to rewind.</summary>
    public AgbMachineSnapshot Snapshot() => m_machine.Snapshot();

    /// <summary>Restores a snapshot into this machine, repositioning every component and the master clock, and rewinds
    /// the host's own frame counter to the value it held when the snapshot was taken. Throws if the snapshot's
    /// identity (BIOS/ROM) does not match this machine.</summary>
    /// <param name="snapshot">The snapshot to restore.</param>
    /// <param name="frameCount">The host frame counter to restore alongside the machine state.</param>
    public void RestoreState(AgbMachineSnapshot snapshot, long frameCount) {
        m_machine.Restore(snapshot: snapshot);
        m_frameCount = frameCount;
    }

    /// <summary>Sets the active-low KEYINPUT register (clear bit = pressed).</summary>
    public void SetKeyInput(ushort keys) => m_machine.SetKeyInput(keys: keys);

    /// <summary>Reads a general-purpose register (0–15) as the visible mode bank sees it.</summary>
    public uint GetRegister(int index) => m_machine.Cpu.GetRegister(index: index);

    /// <summary>The current program counter (R15).</summary>
    public uint ProgramCounter => m_machine.Cpu.GetRegister(index: 15);

    /// <summary>The current program status register.</summary>
    public uint Cpsr => m_machine.Cpu.Cpsr;

    /// <summary>Attempts to read the banked SPSR of the current mode (false in User/System mode, which has none).</summary>
    public bool TryGetSpsr(out uint spsr) => m_machine.Cpu.TryGetSpsr(spsr: out spsr);

    /// <summary>Reads an I/O register halfword without advancing the clock.</summary>
    public ushort DebugReadIo(uint offset) => (m_concreteBus?.DebugReadIo(offset: offset) ?? 0);

    /// <summary>Reads a halfword without advancing the clock, prefetch buffer, open-bus latch, or DMA/EEPROM burst
    /// state — see <see cref="AgbBus.DebugRead16"/> for the region-by-region side-effect notes.</summary>
    public ushort DebugRead16(uint address) => (m_concreteBus?.DebugRead16(address: address) ?? 0);

    /// <summary>Reads a word without advancing the clock, prefetch buffer, open-bus latch, or DMA/EEPROM burst
    /// state — see <see cref="AgbBus.DebugRead16"/> for the region-by-region side-effect notes.</summary>
    public uint DebugRead32(uint address) => (m_concreteBus?.DebugRead32(address: address) ?? 0);

    /// <summary>The FNV-1a 64-bit hash of the current framebuffer — a deterministic render fingerprint (the same
    /// probe the POST render-hash stage uses: <see cref="MemoryMarshal.AsBytes{T}(ReadOnlySpan{T})"/> over the span).</summary>
    public ulong FramebufferHash() {
        const ulong offsetBasis = 0xCBF29CE484222325ul;
        const ulong prime = 0x100000001B3ul;

        var bytes = MemoryMarshal.AsBytes(span: m_machine.Framebuffer);
        var hash = offsetBasis;

        foreach (var value in bytes) {
            hash = ((hash ^ value) * prime);
        }

        return hash;
    }

    /// <summary>Uploads the current framebuffer to a shader-readable image view and refreshes the average colour — the
    /// per-frame GPU work, run on the render thread where a device context is available. Idempotent within a frame; the
    /// caller decides whether the machine stepped first.</summary>
    /// <param name="gpu">The world's GPU compute services (for the surface-upload factory).</param>
    /// <param name="deviceContext">The live GPU device context.</param>
    public void Produce(IGpuComputeServices gpu, IGpuDeviceContext deviceContext) {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(deviceContext);

        Repack();

        m_upload ??= gpu.SurfaceTransferFactory.CreateUpload(deviceContext: deviceContext);
        m_viewHandle = m_upload.Upload(
            deviceContext: deviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: ScreenHeight,
            pixels: m_rgba,
            width: ScreenWidth
        );
    }

    /// <summary>Drops the GPU upload after a device loss; the machine (CPU state) survives untouched.</summary>
    public void OnDeviceLost() {
        m_upload?.Dispose();
        m_upload = null;
        m_viewHandle = 0;
    }

    // Repack the machine's 0xAARRGGBB framebuffer as the R,G,B,A bytes the upload wants (alpha forced opaque — the PPU
    // leaves it clear), and accumulate the average colour for the diegetic screen light.
    private void Repack() {
        var pixels = m_machine.Framebuffer;
        var rgba = m_rgba;
        var sumRed = 0;
        var sumGreen = 0;
        var sumBlue = 0;

        for (var index = 0; (index < pixels.Length); ++index) {
            var offset = (index * 4);
            var pixel = pixels[index];
            var red = (byte)(pixel >> 16);
            var green = (byte)(pixel >> 8);
            var blue = (byte)pixel;

            rgba[offset] = red;
            rgba[offset + 1] = green;
            rgba[offset + 2] = blue;
            rgba[offset + 3] = 0xFF;

            sumRed += red;
            sumGreen += green;
            sumBlue += blue;
        }

        var scale = ((pixels.Length == 0) ? 0f : (1f / (255f * pixels.Length)));

        m_averageColor = new Vector3((sumRed * scale), (sumGreen * scale), (sumBlue * scale));
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_upload?.Dispose();
        m_scope.Dispose();
        m_provider.Dispose();
    }
}
