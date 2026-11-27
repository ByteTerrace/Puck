namespace Puck.World;

/// <summary>Who writes an identity's durable store, and so which part of it the write may reach. The identity's own
/// owner (<see cref="Home"/>: the account's console doors) writes any fact or pool. A world's rules write as their
/// package (<see cref="Package"/>): a fact only under that package's namespace, a pool only when it is not another
/// package's or home-scoped. Every write an identity's store takes passes <see cref="Admits"/> through the one door
/// that applies it (<see cref="WorldIdentity.TrySetFact"/>, <see cref="WorldIdentityStage"/>), so the namespace a rule
/// writes is the one its host derived from the executing document, never one the rule names.</summary>
/// <remarks>The guarantee binds every write a document drives: a rule's host builds the writer from the executing
/// world's package, and a rule cannot choose it. Engine code running in the process is trusted with
/// <see cref="Home"/>, as the console verbs are; across machines, a package is bound to an authority only once
/// write-back is signed.</remarks>
public readonly record struct WorldIdentityWriter {
    private WorldIdentityWriter(string? @namespace) => Namespace = @namespace;

    /// <summary>Gets the identity's own owner, which writes every fact and pool.</summary>
    public static WorldIdentityWriter Home => default;
    /// <summary>Gets the writer of a world in no package and with no document id: it writes nothing.</summary>
    public static WorldIdentityWriter Nobody { get; } = new(@namespace: string.Empty);
    /// <summary>Gets the package namespace this writer writes as, or <see langword="null"/> for <see cref="Home"/>.</summary>
    public string? Namespace { get; }
    /// <summary>Gets a value indicating whether this is the identity's own owner.</summary>
    public bool IsHome => (Namespace is null);

    /// <summary>Returns the writer for a world's rules: the package the executing document belongs to.</summary>
    /// <param name="namespace">The package's namespace; a non-empty name free of both generated-name joiners.</param>
    /// <returns>The writer.</returns>
    /// <exception cref="ArgumentException"><paramref name="namespace"/> is empty or carries a joiner.</exception>
    public static WorldIdentityWriter Package(string @namespace) {
        ArgumentException.ThrowIfNullOrEmpty(argument: @namespace);
        if (@namespace.Contains(value: GeneratedName.Joiner) || @namespace.Contains(value: GeneratedName.FileJoiner)) {
            throw new ArgumentException(message: $"the namespace '{@namespace}' carries a generated-name joiner", paramName: nameof(@namespace));
        }

        return new WorldIdentityWriter(@namespace: @namespace);
    }
    /// <summary>Decides whether this writer may write one fact or pool of an identity.</summary>
    /// <param name="identity">The identity's section, which names its home-scoped facts and pools, or
    /// <see langword="null"/> for a traveller carrying none.</param>
    /// <param name="name">The fact's full stored key, or the pool's name on the identity.</param>
    /// <param name="fact"><see langword="true"/> for a fact, <see langword="false"/> for a pool.</param>
    /// <param name="reason">The refusal naming the writer and the name, or empty when admitted.</param>
    /// <returns><see langword="true"/> when the write is admitted.</returns>
    public bool Admits(WorldIdentityDefinition? identity, CellName name, bool fact, out string reason) {
        if (Namespace is not { } head) {
            reason = string.Empty;
            return true;
        }
        if (head.Length == 0) {
            reason = $"a world in no package writes no identity's {(fact ? "fact" : "pool")} '{name}'";
            return false;
        }
        if (identity?.IsHomeScoped(name: name) == true) {
            reason = $"{(fact ? "fact" : "pool")} '{name}' is home-scoped: only the identity's own owner writes it, never package '{head}'";
            return false;
        }
        if (fact) {
            if (!GeneratedName.HasHead(head: head, name: name.Value)) {
                reason = $"fact '{name}' lies outside package '{head}''s namespace: a package writes only facts it qualifies with its own name ('{head}{GeneratedName.Joiner}…')";
                return false;
            }
        } else if (name.Value.Contains(value: GeneratedName.Joiner) && !GeneratedName.HasHead(head: head, name: name.Value)) {
            reason = $"pool '{name}' is another package's: package '{head}' writes only a shared pool or one it qualifies with its own name";
            return false;
        }

        reason = string.Empty;
        return true;
    }
    /// <inheritdoc/>
    public override string ToString() => ((Namespace is { } head) ? $"package:{head}" : "home");
}
