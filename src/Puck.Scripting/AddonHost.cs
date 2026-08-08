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
/// <para><b>Loading is not admitting.</b> <see cref="Add"/>, <see cref="Reload"/>, and
/// <see cref="SetEnabled"/> all produce instances in the UNADMITTED phase of the two-phase mount (see
/// <see cref="AddonInstance"/>): the consumer runs its own mount gates and calls
/// <see cref="AddonInstance.Admit"/> before the first tick. A live-reload surface therefore owes the whole
/// admit sequence, not just this call — <c>Puck.World</c>'s <c>WorldAddonRuntime.Reload</c>/<c>SetEnabled</c> are that
/// surface, re-running the disclosure + <see cref="AddonInstance.Admit"/> sequence immediately after calling into this
/// type — and a consumer's tick loop must still skip an enabled-but-unadmitted instance rather than tick it, as a
/// defensive floor for any caller that reaches this type directly instead.</para>
/// </summary>
public sealed class AddonHost : IDisposable {
    private readonly Dictionary<string, AddonInstance> m_byName = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, AddonDescriptor> m_descriptors = new(comparer: StringComparer.Ordinal);
    private readonly ScriptingEngine m_engine;
    private readonly List<AddonInstance> m_instances = [];
    private readonly WasmModuleLoader m_loader;
    private readonly IAddonChannelResolver m_channelResolver;
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

    /// <summary>Loads an addon from its descriptor and registers it. Load failures produce a faulted instance
    /// rather than throwing, so one bad addon never fails the whole run.</summary>
    /// <param name="descriptor">The neutral load request.</param>
    /// <exception cref="ArgumentException"><paramref name="descriptor"/> has a null-or-whitespace name.</exception>
    public void Add(in AddonDescriptor descriptor) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: descriptor.Name, paramName: nameof(descriptor));

        var instance = Load(descriptor: in descriptor);

        if (!descriptor.Enabled && (instance.State == AddonState.Enabled)) {
            instance.Disable();
        }

        m_byName[descriptor.Name] = instance;
        m_descriptors[descriptor.Name] = descriptor;
        m_instances.Add(item: instance);
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

    /// <summary>Reloads the named addon from its declared module path — the in-session edit loop: re-reads the
    /// file, recompiles (a changed content hash misses the module cache; an unchanged one reuses it), and swaps
    /// in a fresh store. The reloaded addon runs regardless of its prior state; a load failure swaps in a sticky
    /// faulted instance naming the reason (fix the module and reload again). A declared <c>moduleHash</c> pin
    /// refuses a content change and leaves the running instance untouched.</summary>
    /// <param name="name">The addon name.</param>
    /// <returns>A human-readable status line (the consumer-facing formatting <see cref="Describe"/> established).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public string Reload(string name) {
        if (!TryGet(
            instance: out var previous,
            name: name
        )) {
            return $"unknown addon '{name}'";
        }

        var descriptor = m_descriptors[name];

        if (descriptor.ModuleHash is { } pin) {
            try {
                var info = m_loader.Load(path: descriptor.ModulePath);

                if (!string.Equals(a: info.ContentHash.ToString(), b: pin, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    return $"refused — content {info.ContentHash} no longer matches the declared moduleHash pin {pin}; remove the pin to hot-reload";
                }
            } catch (Exception error) when ((error is ArgumentException or FileNotFoundException or InvalidDataException or WasmtimeException)) {
                // Unreadable content falls through to the swap below, which surfaces it as a sticky faulted instance.
            }
        }

        var instance = Load(descriptor: in descriptor);
        var index = m_instances.IndexOf(item: previous);

        m_byName[name] = instance;
        m_instances[index] = instance;
        previous.Dispose();

        if (instance.State != AddonState.Enabled) {
            return $"faulted — {instance.Fault.Detail}";
        }

        var petname = ContentPetname.From(hashHex: $"{instance.Hash.Value:x16}");

        if (instance.Hash == previous.Hash) {
            return $"{petname} unchanged (fresh store)";
        }

        return $"{ContentPetname.From(hashHex: $"{previous.Hash.Value:x16}")} became {petname}";
    }

    /// <summary>Enables or disables the named addon.</summary>
    /// <param name="name">The addon name.</param>
    /// <param name="enabled">Whether to enable (re-instantiate) or disable it.</param>
    /// <returns><see langword="true"/> if an addon with that name exists; otherwise <see langword="false"/>.</returns>
    public bool SetEnabled(string name, bool enabled) {
        if (!TryGet(
            instance: out var instance,
            name: name
        )) {
            return false;
        }

        if (enabled) {
            instance.Enable();
        } else {
            instance.Disable();
        }

        return true;
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

    private static string DescribeInstance(AddonInstance addon) {
        var petname = ContentPetname.From(hashHex: $"{addon.Hash.Value:x16}");

        return $"{petname}  {addon.Hash}  fuel {addon.FuelPerTick}  {StateLabel(addon: addon)}";
    }
    private static string StateLabel(AddonInstance addon) {
        return addon.State switch {
            AddonState.Enabled => "ENABLED",
            AddonState.Disabled => "DISABLED",
            _ => $"FAULTED({addon.Fault.Kind})",
        };
    }
    private AddonInstance Load(in AddonDescriptor descriptor) {
        try {
            var info = m_loader.Load(path: descriptor.ModulePath);

            // Enforced exactly as Reload enforces it: a declared moduleHash pin is a boot-time integrity check,
            // not decoration. A mismatch must produce a sticky, attributed load fault naming both hashes — the
            // addon must never instantiate on unpinned content.
            if ((descriptor.ModuleHash is { } pin) && !string.Equals(a: info.ContentHash.ToString(), b: pin, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return new AddonInstance(
                    descriptor: in descriptor,
                    fault: new AddonFault(Detail: $"addon {descriptor.Name}: HashMismatch — content {info.ContentHash} does not match the declared moduleHash pin {pin}", Kind: AddonFaultKind.HashMismatch),
                    hash: info.ContentHash
                );
            }

            return new AddonInstance(
                channelResolver: m_channelResolver,
                descriptor: in descriptor,
                engine: m_engine,
                moduleInfo: info
            );
        } catch (Exception error) when ((error is ArgumentException or FileNotFoundException or InvalidDataException or WasmtimeException)) {
            return new AddonInstance(
                descriptor: in descriptor,
                fault: new AddonFault(Detail: $"addon {descriptor.Name}: BadExport — {error.Message}", Kind: AddonFaultKind.BadExport),
                hash: default
            );
        }
    }
}
