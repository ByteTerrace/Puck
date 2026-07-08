using Microsoft.Extensions.Hosting;
using Puck.Commands;

namespace Puck.Demo;

/// <summary>
/// Wires the scripted-console <c>step</c>/<c>settle</c> HOLD gate onto the stdin <see cref="TextCommandSource"/> AFTER
/// the DI graph is built. This indirection breaks a dependency cycle: the gate lives on
/// <see cref="OverworldControlCommandModule"/>, but that module cannot take <see cref="TextCommandSource"/> in its
/// constructor — <see cref="TextCommandSource"/> depends on <c>CommandRegistry</c>, which depends on every
/// <see cref="ICommandModule"/>, so the module depending back on the text source is a circular dependency the
/// container cannot resolve. A hosted service resolves both post-build (no cycle) and connects them. It runs on the
/// composition root only; a non-overworld root simply never holds (the module's gate returns false).
/// </summary>
internal sealed class OverworldControlGateInstaller : IHostedService {
    private readonly OverworldControlCommandModule m_module;
    private readonly TextCommandSource m_textSource;

    public OverworldControlGateInstaller(OverworldControlCommandModule module, TextCommandSource textSource) {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(textSource);

        m_module = module;
        m_textSource = textSource;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) {
        m_textSource.HoldGate = m_module.ShouldHold;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) {
        // Detach so a torn-down module can't hold a shutting-down pipe (defensive; the process is exiting anyway).
        m_textSource.HoldGate = null;

        return Task.CompletedTask;
    }
}
