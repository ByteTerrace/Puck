using System.Text;

using Puck.Assets;

using Wasmtime;

namespace Puck.Scripting;

/// <summary>
/// Composes a <see cref="ScriptingEngine"/> and <see cref="WasmModuleLoader"/> and owns the addon instance
/// set keyed by name — the object a consumer pumps each tick. A bad addon (missing file, bad bytes, bad
/// export, or content that no longer matches a declared <c>moduleHash</c> pin) loads faulted and never
/// crashes the run. Takes ownership of the engine and disposes it (with every instance's store) on
/// <see cref="Dispose"/>.
/// <para><b>Loading is not admitting, and preparing is not committing.</b> <see cref="Prepare"/> produces an
/// instance in the unadmitted phase of the two-phase mount (see <see cref="AddonInstance"/>): the consumer runs
/// its own mount gates and <see cref="AddonInstance.Admit"/> before the first tick, then publishes the complete
/// prepared registry through <see cref="Adopt"/> once its whole transaction may commit — <c>Puck.World</c>'s
/// <c>WorldAddonRuntime.TryPrepare</c>/<c>Commit</c> is that surface. A consumer's tick loop must still skip an
/// enabled-but-unadmitted instance rather than tick it, as a defensive floor for any caller that reaches this
/// type directly instead.</para>
/// </summary>
public sealed class AddonHost : IDisposable {
    // Reassigned WHOLESALE by Adopt — three reference swaps, never mutated element-by-element — so a caller
    // publishing a whole-runtime prepare/commit transaction (Puck.World.Addons.WorldAddonRuntime) can adopt a
    // complete replacement registry, including the removal of a superseded name, with no fallible operation.
    private Dictionary<string, AddonInstance> m_byName = new(comparer: StringComparer.Ordinal);
    private Dictionary<string, AddonDescriptor> m_descriptors = new(comparer: StringComparer.Ordinal);
    private List<AddonInstance> m_instances = [];

    private readonly IAddonChannelResolver m_channelResolver;
    private readonly ScriptingEngine m_engine;
    private readonly WasmModuleLoader m_loader;

    private bool m_disposed;

    /// <summary>Initializes a host over an engine and loader.</summary>
    /// <param name="engine">The engine every addon store is created against; the host takes ownership.</param>
    /// <param name="loader">The module loader addons are compiled through.</param>
    /// <param name="channelResolver">The host channel table every addon's declared channel names are resolved
    /// against at instantiation — supplied by the consumer because the table is lane knowledge this core
    /// deliberately does not reference (see <see cref="IAddonChannelResolver"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/>, <paramref name="loader"/>, or <paramref name="channelResolver"/> is <see langword="null"/>.</exception>
    public AddonHost(ScriptingEngine engine, WasmModuleLoader loader, IAddonChannelResolver channelResolver) {
        ArgumentNullException.ThrowIfNull(argument: engine);
        ArgumentNullException.ThrowIfNull(argument: loader);
        ArgumentNullException.ThrowIfNull(argument: channelResolver);

        m_channelResolver = channelResolver;
        m_engine = engine;
        m_loader = loader;
    }

    /// <summary>Gets the loaded addon instances in load order.</summary>
    public IReadOnlyList<AddonInstance> Instances => m_instances;

    private static string DescribeInstance(AddonInstance addon) {
        var petname = ContentPetname.From(hashHex: $"{addon.Hash.Value:x16}");

        return $"{petname}  {addon.Hash}  fuel {addon.FuelPerTick}  {StateLabel(addon: addon)}";
    }
    private AddonInstance Load(in AddonDescriptor descriptor) {
        try {
            var info = m_loader.Load(path: descriptor.ModulePath);

            // Enforced exactly as Reload enforces it: a declared moduleHash pin is a boot-time integrity check,
            // not decoration. A mismatch must produce a sticky, attributed load fault naming both hashes — the
            // addon must never instantiate on unpinned content.
            if (
                (descriptor.ModuleHash is { } pin) &&
                !string.Equals(
                a: info.ContentHash.ToString(),
                b: pin,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )
            ) {
                return new AddonInstance(
                    descriptor: in descriptor,
                    fault: new AddonFault(
                        Detail: $"addon {descriptor.Name}: HashMismatch — content {info.ContentHash} does not match the declared moduleHash pin {pin}",
                        Kind: AddonFaultKind.HashMismatch
                    ),
                    hash: info.ContentHash
                );
            }

            return new AddonInstance(
                channelResolver: m_channelResolver,
                descriptor: in descriptor,
                engine: m_engine,
                moduleInfo: info
            );
        } catch (Exception error) when ((error is ArgumentException or FileNotFoundException or InvalidDataException or WasmtimeException or UnauthorizedAccessException or IOException)) {
            // A locked file, a permissions denial, or a transient disk error is an ordinary environment failure a
            // module read can hit — a sticky load fault, never a tick-thread exception.
            return new AddonInstance(
                descriptor: in descriptor,
                fault: new AddonFault(
                    Detail: $"addon {descriptor.Name}: BadExport — {error.Message}",
                    Kind: AddonFaultKind.BadExport
                ),
                hash: default
            );
        }
    }
    private static string StateLabel(AddonInstance addon) {
        return addon.State switch {
            AddonState.Enabled => "ENABLED",
            AddonState.Disabled => "DISABLED",
            _ => $"FAULTED({addon.Fault.Kind})",
        };
    }

    /// <summary>Publishes a complete replacement registry by three reference swaps — every entry in
    /// <paramref name="byName"/>/<paramref name="descriptors"/>/<paramref name="instances"/>, and nothing else: a
    /// name registered before this call but absent from them is no longer registered after it. Pure adoption — no
    /// compile, I/O, per-name enable/disable, or allocation of its own; the caller builds the three collections
    /// during its own prepare phase and hands them over already complete.</summary>
    /// <param name="byName">The complete replacement name-to-instance map.</param>
    /// <param name="descriptors">The complete replacement name-to-descriptor map.</param>
    /// <param name="instances">The complete replacement instance list, in mount order.</param>
    public void Adopt(Dictionary<string, AddonInstance> byName, Dictionary<string, AddonDescriptor> descriptors, List<AddonInstance> instances) {
        ArgumentNullException.ThrowIfNull(argument: byName);
        ArgumentNullException.ThrowIfNull(argument: descriptors);
        ArgumentNullException.ThrowIfNull(argument: instances);

        m_byName = byName;
        m_descriptors = descriptors;
        m_instances = instances;
    }
    /// <summary>Compiles a guest under <paramref name="descriptor"/> without registering it — the PREPARE half of
    /// the prepare/commit split: the fallible load (missing file, bad bytes, bad export, a <c>moduleHash</c> pin
    /// mismatch) runs here. The returned instance is not reachable through <see cref="TryGet"/> or
    /// <see cref="Instances"/> until a complete registry containing it is published through <see cref="Adopt"/>;
    /// a discarded preparation is undone by disposing the instance.</summary>
    /// <param name="descriptor">The neutral load request.</param>
    /// <returns>The prepared instance — faulted rather than thrown on a load failure.</returns>
    /// <exception cref="ArgumentException"><paramref name="descriptor"/> has a null-or-whitespace name.</exception>
    public AddonInstance Prepare(in AddonDescriptor descriptor) {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            argument: descriptor.Name,
            paramName: nameof(descriptor)
        );

        return Load(descriptor: in descriptor);
    }
    /// <summary>Renders one line per addon: petname, content hash, fuel budget, and state.</summary>
    /// <returns>A newline-joined description, or <c>"no addons"</c> when none are loaded.</returns>
    public string Describe() {
        if (m_instances.Count == 0) {
            return "no addons";
        }

        var builder = new StringBuilder();

        for (var index = 0; (index < m_instances.Count); ++index) {
            if (index > 0) {
                builder.Append(value: '\n');
            }

            builder.Append(value: DescribeInstance(addon: m_instances[index]));
        }

        return builder.ToString();
    }
    /// <summary>Disposes every addon store and the owned engine.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        foreach (var instance in m_instances) {
            instance.Dispose();
        }

        m_instances.Clear();
        m_byName.Clear();
        m_descriptors.Clear();
        m_engine.Dispose();
        GC.SuppressFinalize(obj: this);
    }
    /// <summary>Looks up an addon by name.</summary>
    /// <param name="name">The addon name.</param>
    /// <param name="instance">When this returns <see langword="true"/>, the matching addon.</param>
    /// <returns><see langword="true"/> if found; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public bool TryGet(string name, out AddonInstance instance) {
        ArgumentNullException.ThrowIfNull(argument: name);

        return m_byName.TryGetValue(
            key: name,
            value: out instance!
        );
    }
}
