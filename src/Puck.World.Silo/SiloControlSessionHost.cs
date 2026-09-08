using Puck.Hosting;

namespace Puck.World.Silo;

/// <summary>Exposes admitted rows through the host-neutral Console attachment seam.</summary>
internal sealed class SiloControlSessionHost(WorldSiloHost silo, SiloConsoleRouting routing) : IControlSessionHost {
    public bool IsReady(string target) => silo.Live && routing.TryGetSession(target, out _);
    public ValueTask<IControlSession> AttachAsync(string target, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsReady(target)) { throw new InvalidOperationException("The configured row is not ready for Console ingress."); }
        return ValueTask.FromResult(routing.CreateControlSession(target));
    }
}
