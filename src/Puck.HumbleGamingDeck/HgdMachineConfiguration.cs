namespace Puck.HumbleGamingDeck;

/// <summary>Immutable machine inputs shared by forks; every behavioral choice participates in snapshot identity.</summary>
public sealed record HgdMachineConfiguration {
    /// <summary>Initializes a new instance of the <see cref="HgdMachineConfiguration"/> class.</summary>
    /// <param name="cartridge">The validated, immutable cartridge image.</param>
    /// <param name="model">The implemented console revision.</param>
    /// <param name="powerOn">The named power-up choices; <see langword="null"/> selects phase zero and zero-filled RAM.</param>
    /// <exception cref="ArgumentNullException">The cartridge is null.</exception>
    /// <exception cref="NotSupportedException">The image requires a timing family other than NTSC.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The console model is unimplemented.</exception>
    public HgdMachineConfiguration(HgdCartridge cartridge, HgdConsoleModel model = HgdConsoleModel.NtscRp2A03G, HgdPowerOnProfile? powerOn = null) {
        ArgumentNullException.ThrowIfNull(argument: cartridge);
        _ = model.CpuDivider();
        if (cartridge.Header.Timing is HgdTiming.Pal or HgdTiming.Dendy) {
            throw new NotSupportedException(message: $"Unimplemented Deck timing: {cartridge.Header.Timing}.");
        }
        Cartridge = cartridge;
        Model = model;
        PowerOn = (powerOn ?? new HgdPowerOnProfile());
    }

    /// <summary>Gets the immutable cartridge image.</summary>
    public HgdCartridge Cartridge {
        get;
    }
    /// <summary>Gets the selected console revision.</summary>
    public HgdConsoleModel Model {
        get;
    }
    /// <summary>Gets the power-up choices.</summary>
    public HgdPowerOnProfile PowerOn {
        get;
    }
}
