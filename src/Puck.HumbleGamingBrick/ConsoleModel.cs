namespace Puck.HumbleGamingBrick;

/// <summary>The hardware model a machine emulates, which selects its boot behaviour, register handoff state, and the
/// availability of Color features.</summary>
public enum ConsoleModel {
    /// <summary>The original monochrome GamingBrick (DMG).</summary>
    Dmg = 0,
    /// <summary>The colour GamingBrick (CGB).</summary>
    Cgb = 1,
    /// <summary>The Advanced GamingBrick playing a monochrome/colour cartridge through its built-in Color compatibility hardware
    /// (AGB). Identical to <see cref="Cgb"/> in every emulated gate except the boot handoff, whose extra
    /// <c>inc b</c> is what cartridges probe to detect Advanced hardware.</summary>
    Agb = 2,
}

/// <summary>The capability questions components ask of a model, so a gate reads as the capability it means rather
/// than an equality against one model.</summary>
public static class ConsoleModelExtensions {
    /// <summary>Whether the model has Color hardware (palette RAM, HDMA, double-speed, the Color I/O block). True for
    /// <see cref="ConsoleModel.Cgb"/> and <see cref="ConsoleModel.Agb"/> — the Advance plays GB/GBC cartridges on its
    /// Color-compatible silicon.</summary>
    /// <param name="model">The model to interrogate.</param>
    /// <returns><see langword="true"/> when the model has Color hardware.</returns>
    public static bool SupportsColor(this ConsoleModel model) =>
        (model != ConsoleModel.Dmg);
}
