using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Boots one compiled cartridge on the real machine its target names and observes it through the surface both
/// machines share, so a test states its claim once and runs it on either target. A read with no common address on
/// the two machines goes through <see cref="Observe{T}"/>, which hands each driver its own reader.
/// </summary>
public sealed class CartridgeProbe : IDisposable {
    // Bit one of the master sound status register is set while the music channel is sounding, on either machine.
    private const uint AdvancedSoundStatusAddress = 0x04000084u;
    private const ushort HumbleSoundStatusAddress = 0xFF26;

    // The eight keys the two keypads share; the advanced machine's shoulder buttons have no humble line.
    private static readonly (AgbKeys Key, JoypadButtons Button)[] SharedKeys = [
        (AgbKeys.A, JoypadButtons.A),
        (AgbKeys.B, JoypadButtons.B),
        (AgbKeys.Select, JoypadButtons.Select),
        (AgbKeys.Start, JoypadButtons.Start),
        (AgbKeys.Right, JoypadButtons.Right),
        (AgbKeys.Left, JoypadButtons.Left),
        (AgbKeys.Up, JoypadButtons.Up),
        (AgbKeys.Down, JoypadButtons.Down),
    ];

    private readonly AgbVerifyMachineDriver? m_agb;
    private readonly VerifyMachineDriver? m_hgb;

    /// <summary>Initializes a new instance of the <see cref="CartridgeProbe"/> class, booting <paramref name="result"/> on the machine its target names.</summary>
    /// <param name="result">The compiled cartridge.</param>
    /// <param name="label">The label a failed verification is reported under.</param>
    public CartridgeProbe(CartridgeCompilation result, string label) {
        Result = result;
        if (result.Target == "agb") {
            m_agb = new AgbVerifyMachineDriver(
                rom: result.Rom,
                label: label
            );
        } else {
            m_hgb = new VerifyMachineDriver(
                rom: result.Rom,
                label: label
            );
        }
    }

    /// <summary>Gets the compiled cartridge this probe booted.</summary>
    public CartridgeCompilation Result { get; }

    /// <summary>Compiles, boots, and runs a document for <paramref name="frames"/> frames with no input held.</summary>
    /// <param name="document">The cartridge source.</param>
    /// <param name="frames">The frames to run before the caller observes.</param>
    /// <param name="label">The label a failed verification is reported under.</param>
    /// <returns>The running probe, which the caller disposes.</returns>
    public static CartridgeProbe Boot(CartridgeDocument document, int frames, string label) {
        var probe = new CartridgeProbe(
            label: label,
            result: Compile(document: document)
        );

        try {
            probe.Run(frames: frames);
        } catch {
            probe.Dispose();
            throw;
        }

        return probe;
    }
    /// <summary>Compiles a document with the compiler its target names.</summary>
    /// <param name="document">The cartridge source.</param>
    /// <returns>The compiled cartridge.</returns>
    public static CartridgeCompilation Compile(CartridgeDocument document) => Compiler(target: document.Target).Compile(document: document);
    /// <summary>Returns the compiler for a target: the advanced one for agb and the humble one otherwise, so an unknown target reaches the humble compiler, which refuses it by name.</summary>
    /// <param name="target">The document's target.</param>
    /// <returns>A fresh compiler.</returns>
    public static ICartridgeCompiler Compiler(string target) => ((target == "agb")
        ? new AgbCartridgeCompiler()
        : new HgbCartridgeCompiler()
    );
    /// <inheritdoc/>
    public void Dispose() {
        m_agb?.Dispose();
        m_hgb?.Dispose();
    }
    /// <summary>Reads through whichever driver is running, for a fact the two machines keep at different places.</summary>
    /// <typeparam name="T">The observed value's type.</typeparam>
    /// <param name="advanced">The reader when the cartridge targets agb.</param>
    /// <param name="humble">The reader otherwise.</param>
    /// <returns>The value the running machine's reader returns.</returns>
    public T Observe<T>(Func<AgbVerifyMachineDriver, T> advanced, Func<VerifyMachineDriver, T> humble) => ((m_agb is { } agb)
        ? advanced(arg: agb)
        : humble(arg: m_hgb!)
    );
    /// <summary>Reads one completed framebuffer pixel.</summary>
    /// <param name="x">The pixel column.</param>
    /// <param name="y">The pixel row.</param>
    /// <returns>The machine's packed pixel; the two machines pack colour differently, so compare within one target.</returns>
    public uint Pixel(int x, int y) => Observe(
        advanced: agb => agb.ReadPixel(
            x: x,
            y: y
        ),
        humble: hgb => hgb.ReadPixel(
            x: x,
            y: y
        )
    );
    /// <summary>Reads one byte without advancing the machine.</summary>
    /// <param name="address">The bus address; the humble machine takes its low sixteen bits.</param>
    /// <returns>The byte at <paramref name="address"/>.</returns>
    public byte Read(uint address) => Observe(
        advanced: agb => agb.ReadByte(address: address),
        humble: hgb => hgb.Read(address: ((ushort)address))
    );
    /// <summary>Reads a byte variable's current value.</summary>
    /// <param name="variable">The variable's authored name.</param>
    /// <returns>The byte at the variable's compiled address.</returns>
    public byte Read(string variable) => Read(address: Result.Variables[variable]);
    /// <summary>Reads a two-byte variable's current value, stored low byte first on both machines.</summary>
    /// <param name="variable">The variable's authored name.</param>
    /// <returns>The value, 0 through 65535.</returns>
    public int ReadWide(string variable) {
        var address = Result.Variables[variable];

        return Read(address: address) | (Read(address: (address + 1u)) << 8);
    }
    /// <summary>Runs whole frames with a key set held.</summary>
    /// <param name="frames">The frames to run.</param>
    /// <param name="keys">The keys held on every frame.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="keys"/> holds L or R on the humble machine, which has no shoulder buttons.</exception>
    public void Run(int frames, AgbKeys keys = AgbKeys.None) {
        m_agb?.RunFrames(
            frames: frames,
            keys: keys
        );
        m_hgb?.RunFrames(
            buttons: Humble(keys: keys),
            frames: frames
        );
    }
    /// <summary>Runs whole frames with no input until a condition holds, failing at <paramref name="limit"/>.</summary>
    /// <param name="until">The condition, read after every frame.</param>
    /// <param name="limit">The most frames the condition may take.</param>
    /// <param name="awaited">What the condition waits for, named in the failure.</param>
    public void RunUntil(Func<CartridgeProbe, bool> until, int limit, string awaited) {
        _ = m_agb?.RunFramesUntil(
            awaited: awaited,
            keys: AgbKeys.None,
            limit: limit,
            until: _ => until(arg: this)
        );
        _ = m_hgb?.RunFramesUntil(
            awaited: awaited,
            buttons: JoypadButtons.None,
            limit: limit,
            until: _ => until(arg: this)
        );
    }
    /// <summary>Reads the master sound status register.</summary>
    /// <returns>The register; bit one is set while the second pulse channel is sounding.</returns>
    public uint SoundStatus() => Observe(
        advanced: agb => ((uint)agb.ReadHalf(address: AdvancedSoundStatusAddress)),
        humble: hgb => ((uint)hgb.Read(address: HumbleSoundStatusAddress))
    );

    private static JoypadButtons Humble(AgbKeys keys) {
        if ((keys & (AgbKeys.L | AgbKeys.R)) != AgbKeys.None) {
            throw new ArgumentOutOfRangeException(
                message: "The humble machine has no shoulder buttons.",
                paramName: nameof(keys)
            );
        }

        var buttons = JoypadButtons.None;

        foreach (var (key, button) in SharedKeys) {
            if ((keys & key) != AgbKeys.None) {
                buttons |= button;
            }
        }

        return buttons;
    }

}
