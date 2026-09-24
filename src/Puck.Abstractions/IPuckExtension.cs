namespace Puck.Abstractions;

/// <summary>
/// The one entry contract for a Puck extension, whether a host composes it as a built-in or discovers it installed
/// beside the host. An extension describes itself only by the contributions it registers; every host composes them
/// through <see cref="PuckExtensionSet.Compose"/> and reads the kinds it consumes.
/// </summary>
public interface IPuckExtension {
    /// <summary>Gets the extension's name — by convention the name of the assembly that declares it, which is also its
    /// installation directory's name. It is unique among one host's extensions and names the extension in every
    /// conflict and read-back.</summary>
    string Name { get; }

    /// <summary>Registers this extension's contributions. The registry is valid only for the duration of this call and
    /// performs no I/O; a contribution's own factory runs later, when a host selects it.</summary>
    /// <param name="registry">The registry that records this extension's contributions.</param>
    void Register(IPuckExtensionRegistry registry);
}
/// <summary>Records one extension's contributions during <see cref="IPuckExtension.Register"/>.</summary>
public interface IPuckExtensionRegistry {
    /// <summary>Adds a contribution of kind <typeparamref name="TContribution"/> under a key unique to that kind.</summary>
    /// <typeparam name="TContribution">The contribution kind: the exact type a host reads it back as.</typeparam>
    /// <param name="key">The contribution's ordinal key within its kind.</param>
    /// <param name="contribution">The contribution.</param>
    /// <exception cref="PuckExtensionException">The key is blank, the contribution is <see langword="null"/>, another
    /// extension already holds the key for this kind, or the registry is used after registration ended.</exception>
    void Add<TContribution>(string key, TContribution contribution) where TContribution : class;
}
/// <summary>One contribution recorded by an extension.</summary>
/// <typeparam name="TContribution">The contribution kind.</typeparam>
/// <param name="Key">The ordinal key unique within the kind.</param>
/// <param name="Extension">The name of the extension that registered it.</param>
/// <param name="Value">The contribution.</param>
public sealed record PuckContribution<TContribution>(string Key, string Extension, TContribution Value) where TContribution : class;
/// <summary>A host-lifetime service contributed by an extension. Every host that composes extensions starts it after its
/// own services and stops and disposes it at shutdown.</summary>
/// <param name="Create">Creates the service from the host's service provider.</param>
public sealed record PuckHostedService(Func<IServiceProvider, IPuckHostedService> Create);
/// <summary>A refused extension composition: an installation, registration, or selection conflict named by the extension
/// and contribution involved.</summary>
public sealed class PuckExtensionException : InvalidOperationException {
    /// <summary>Initializes a new instance of the <see cref="PuckExtensionException"/> class.</summary>
    /// <param name="message">What was refused and which extensions were involved.</param>
    public PuckExtensionException(string message) : base(message: message) { }
    /// <summary>Initializes a new instance of the <see cref="PuckExtensionException"/> class for a refusal caused by
    /// another failure.</summary>
    /// <param name="message">What was refused and which extensions were involved.</param>
    /// <param name="inner">The failure that caused the refusal.</param>
    public PuckExtensionException(string message, Exception inner) : base(
        innerException: inner,
        message: message
    ) { }
}
