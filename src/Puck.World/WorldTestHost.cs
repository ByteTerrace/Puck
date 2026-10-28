using Microsoft.Extensions.Hosting;

namespace Puck.World;

/// <summary>Builds the ordinary native host a scenario worker retains until its batch completes.</summary>
public sealed class WorldTestHost {
    internal Action<IHost>? Initialize { get; set; }

    /// <summary>Gets the number of native host containers built by this worker.</summary>
    public int Boots { get; private set; }

    /// <summary>Builds an independent host, preserving the builder's validation, scopes and disposal ownership.</summary>
    /// <param name="builder">The world's fully configured native builder.</param>
    /// <returns>The native host, owned by the caller.</returns>
    public IHost Build(HostApplicationBuilder builder) {
        // Arbitrary factories and extensions can create child scopes or supply caller-owned instances. Rewriting
        // singleton lifetimes cannot preserve their native semantics. Keep the native host and restore authorities.
        var host = builder.Build();

        Boots++;
        return host;
    }
}
