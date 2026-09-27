namespace Puck.HumbleGamingDeck;

/// <summary>The CPU and video timing revision selected for a Deck.</summary>
public enum HgdConsoleModel {
    /// <summary>The NTSC RP2A03G and RP2C02G clock family.</summary>
    NtscRp2A03G,
}
/// <summary>Hardware capabilities cached by the components that use them.</summary>
public static class HgdConsoleModelExtensions {
    /// <summary>Gets the integer master-clock rate.</summary>
    /// <param name="model">The implemented hardware revision.</param>
    /// <returns>Master ticks per whole number of seconds.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The model is unimplemented.</exception>
    public static MachineCycleRate MasterClockRate(this HgdConsoleModel model) {
        return model switch {
            HgdConsoleModel.NtscRp2A03G => new(cycles: 236_250_000, seconds: 11),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(model), actualValue: model, message: "Unimplemented Deck console model."),
        };
    }
    /// <summary>Gets the master ticks in one CPU cycle.</summary>
    /// <param name="model">The implemented hardware revision.</param>
    /// <returns>The CPU clock divider.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The model is unimplemented.</exception>
    public static int CpuDivider(this HgdConsoleModel model) {
        return model switch {
            HgdConsoleModel.NtscRp2A03G => 12,
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(model), actualValue: model, message: "Unimplemented Deck console model."),
        };
    }
    /// <summary>Gets the deterministic special-bus precharge for the unstable immediate instructions.</summary>
    /// <param name="model">The implemented hardware revision.</param>
    /// <returns>The unhalted bus precharge mask.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The model is unimplemented.</exception>
    public static byte SpecialBusPrecharge(this HgdConsoleModel model) {
        return model switch {
            HgdConsoleModel.NtscRp2A03G => 0xEE,
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(model), actualValue: model, message: "Unimplemented Deck console model."),
        };
    }
}
