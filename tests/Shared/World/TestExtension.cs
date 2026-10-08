using Puck.Abstractions;

namespace Puck.World.Testing;

/// <summary>An extension whose registration is a delegate, for composition laws.</summary>
internal sealed class TestExtension(string name, Action<IPuckExtensionRegistry> register) : IPuckExtension {
    public string Name => name;

    public void Register(IPuckExtensionRegistry registry) => register(obj: registry);
}
