using Puck.HumbleGamingBrick.Interfaces;
using MachineInstance = Puck.GamingBricks.MachineInstance<Puck.HumbleGamingBrick.Machine, Puck.HumbleGamingBrick.MachineConfiguration>;

namespace Puck.HumbleGamingBrick.Forge.Framework;

public sealed class VerifyMachineDriver : IDisposable {
    private const ulong TCyclesPerFrame = 70224UL;

    private readonly ISystemBus m_bus;
    private readonly ICpu m_cpu;
    private readonly IFramebuffer m_framebuffer;
    private readonly IJoypad m_joypad;
    private readonly string m_label;
    private readonly MachineInstance m_machine;

    /// <summary>Creates a driver over a machine running the cartridge.</summary>
    /// <param name="rom">The cartridge image.</param>
    /// <param name="label">The diagnostic label failures are reported under.</param>
    /// <param name="bootRom">A boot image to execute from reset, or null to start from the seeded post-boot state.</param>
    /// <remarks>
    /// With a boot image the machine spends its first frames inside the boot program, so a caller that wants the
    /// cartridge running must step past the handoff.
    /// </remarks>
    public VerifyMachineDriver(byte[] rom, string label, byte[]? bootRom = null) {
        m_label = label;
        m_machine = MachineFactory.Create(
            configuration: new MachineConfiguration(
                bootRom: bootRom,
                model: ConsoleModel.CgbE,
                cartridgeRom: rom
            ),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );
        m_bus = m_machine.GetRequiredService<ISystemBus>();
        m_framebuffer = m_machine.GetRequiredService<IFramebuffer>();
        m_cpu = m_machine.GetRequiredService<ICpu>();
        m_joypad = m_machine.GetRequiredService<IJoypad>();
    }

    public static void Assert(bool condition, string message, string label) {
        if (!condition) {
            throw new InvalidOperationException(message: $"{label} ROM verification failed: {message}");
        }
    }
    public void Dispose() => m_machine.Dispose();
    public void Press(JoypadButtons buttons) {
        RunFrames(
            buttons: buttons,
            frames: 8
        );
        RunFrames(
            buttons: JoypadButtons.None,
            frames: 6
        );
    }
    public byte Read(ushort address) => m_bus.ReadByte(address: address);
    /// <summary>Reads a completed framebuffer pixel without advancing the machine.</summary>
    /// <param name="x">The pixel column, 0..159.</param>
    /// <param name="y">The pixel row, 0..143.</param>
    /// <returns>The packed 0x00RRGGBB pixel.</returns>
    public uint ReadPixel(int x, int y) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: x);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            value: x,
            other: m_framebuffer.Width
        );
        ArgumentOutOfRangeException.ThrowIfNegative(value: y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            value: y,
            other: m_framebuffer.Height
        );
        return m_framebuffer.Pixels[((y * m_framebuffer.Width) + x)];
    }
    public int ReadWide(ushort address) => Read(address: address) | (Read(address: ((ushort)(address + 1))) << 8);
    /// <summary>Replaces the machine's whole state with a snapshot, so a position reached once can seed many runs.</summary>
    /// <param name="snapshot">A snapshot taken from a driver over the same cartridge.</param>
    /// <exception cref="InvalidOperationException">The snapshot was taken from a different machine or cartridge.</exception>
    public void Restore(MachineSnapshot snapshot) => m_machine.Machine.Restore(snapshot: snapshot);
    public void RunFrames(JoypadButtons buttons, int frames) {
        for (var frame = 0; (frame < frames); frame++) {
            m_joypad.SetButtons(pressed: buttons);
            m_machine.Machine.Run(tCycles: TCyclesPerFrame);
        }

        VerifyMachineSettle.SettleOutOfOamDma(
            machine: m_machine.Machine,
            cpu: m_cpu,
            label: m_label
        );
    }
    /// <summary>Runs whole frames with a button set held until a condition holds, checking it after every frame.</summary>
    /// <param name="buttons">The buttons held on every frame.</param>
    /// <param name="until">The condition, read against settled memory after each frame.</param>
    /// <param name="limit">The most frames the condition may take; reaching it without the condition is a failure.</param>
    /// <param name="awaited">What the condition waits for, named in the failure.</param>
    /// <returns>The frames run, from one to <paramref name="limit"/>.</returns>
    /// <exception cref="InvalidOperationException">The condition did not hold within <paramref name="limit"/> frames.</exception>
    /// <remarks>
    /// A caller pays only for the frames its fact needs; the limit is the failure guard, not a duration. Every frame
    /// settles out of OAM DMA before the condition reads memory, so the boundaries drift by the settle's few cycles
    /// against one long <see cref="RunFrames"/>; a caller measuring cadence over an exact span runs that instead.
    /// </remarks>
    public int RunFramesUntil(JoypadButtons buttons, Func<VerifyMachineDriver, bool> until, int limit, string awaited) {
        ArgumentNullException.ThrowIfNull(argument: until);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: limit);

        for (var frame = 1; (frame <= limit); frame++) {
            RunFrames(
                buttons: buttons,
                frames: 1
            );

            if (until(arg: this)) {
                return frame;
            }
        }

        throw new InvalidOperationException(message: $"{m_label} ROM verification failed: {awaited} did not happen within {limit} frames.");
    }
    /// <summary>Captures the machine's whole state at the current frame boundary.</summary>
    /// <returns>A snapshot that aliases nothing live, for <see cref="Restore"/> on any driver over the same cartridge.</returns>
    public MachineSnapshot Snapshot() => m_machine.Machine.Snapshot();
    /// <summary>Writes one work-memory byte, for setting up a position a cartridge would take many inputs to reach.</summary>
    /// <param name="address">The bus address.</param>
    /// <param name="value">The byte to write.</param>
    /// <remarks>The cartridge cannot tell this from its own store, so what runs afterward is the cartridge's own code.</remarks>
    public void Write(ushort address, byte value) => m_bus.WriteByte(
        address: address,
        value: value
    );
}
