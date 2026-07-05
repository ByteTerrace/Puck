namespace Puck.AdvancedGamingBrick;

/// <summary>
/// A hardware component the <see cref="IAgbBus"/> advances in lockstep with the CPU. Unlike the DMG/CGB, the
/// Advanced GamingBrick clocks every component from the single 16.78&#160;MHz master clock — there is no
/// double-speed split — so a component is advanced simply by a whole number of master cycles. The bus charges
/// each memory access (and each internal CPU cycle) to every registered component before the access is
/// observed, preserving the deferred-cycle model proven on the DMG/CGB core.
/// </summary>
public interface IAgbClockedComponent {
    /// <summary>Advances the component by <paramref name="cycles"/> master clock cycles.</summary>
    /// <param name="cycles">The number of master cycles to advance; always positive.</param>
    void Step(int cycles);
}
