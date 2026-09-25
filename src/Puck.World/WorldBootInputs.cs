using Puck.Abstractions;
using Puck.Networking;
using Puck.World.Machines;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// Everything a boot resolves before it composes its services: the loaded world, the host settings that select its
/// shape, the extensions and machine catalog it validated against, and the command-line reflections the registrations
/// read. <c>Program.cs</c> builds one from its parsed options and hands it to
/// <see cref="WorldBootComposition.AddWorldBoot"/>; the composition laws build one for a fixture world and read what
/// the same method registers.
/// </summary>
/// <param name="Extensions">The composed host extensions.</param>
/// <param name="MachineCatalog">The immutable machine catalog selected from <paramref name="Extensions"/>.</param>
/// <param name="Source">The loaded world and the path it loaded from, with its admission receipt when it has one.</param>
/// <param name="HostSettings">The resolved host settings; their presentation selects the boot shape.</param>
/// <param name="Authenticator">The federation identity door.</param>
/// <param name="StateRoot">The run's state root, which the peer identity and every other per-run file resolve under.</param>
public sealed record WorldBootInputs(
    PuckExtensionSet Extensions,
    WorldMachineCatalog MachineCatalog,
    WorldDefinitionSource Source,
    WorldHostSettings HostSettings,
    IAuthenticator Authenticator,
    WorldStateRoot StateRoot
) {
    /// <summary>Gets the subject a <c>--authentication-config-file</c> connection signs as, which the boot server
    /// takes as its authority identity, or <see langword="null"/> to keep the document's own.</summary>
    public string? ConnectionSubject { get; init; }
    /// <summary>Gets whether the backend enables its validation layers when it creates the device
    /// (<c>--debug-layers</c>).</summary>
    public bool DebugLayers { get; init; }
    /// <summary>Gets the service-extension configuration (<c>--extensions-config-file</c>), or
    /// <see langword="null"/> for none.</summary>
    public WorldExtensionConfiguration? ExtensionsConfiguration { get; init; }
    /// <summary>Gets the peer identity key file (<c>--federation-key-file</c>), or <see langword="null"/> for the
    /// state root's own.</summary>
    public string? FederationKeyFile { get; init; }
    /// <summary>Gets the <c>--storage-discovery-uri</c> override, or <see langword="null"/> to let the document
    /// decide.</summary>
    public string? StorageDiscoveryEndpoint { get; init; }
    /// <summary>Gets the <c>--storage-uri</c> override, or <see langword="null"/> to let the document decide.</summary>
    public string? StorageEndpoint { get; init; }
    /// <summary>Gets the <c>--user-id</c> override, or <see langword="null"/> to let the document decide.</summary>
    public string? StorageUserId { get; init; }
    /// <summary>Gets whether the headless tick host runs without pacing (<c>--unpaced</c>).</summary>
    public bool Unpaced { get; init; }
}
