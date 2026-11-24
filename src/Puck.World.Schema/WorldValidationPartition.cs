namespace Puck.World;

/// <summary>How a validation partition divides what it owns into the units it validates and counts.</summary>
public enum WorldValidationGranularity : byte {
    /// <summary>The partition validates what it owns as one unit.</summary>
    Section,
    /// <summary>The partition validates each row of the part it owns as one unit, and validates a changed row without
    /// the rows beside it.</summary>
    Rows,
}

/// <summary>One partition of document validation: the parts of a <see cref="WorldDefinition"/> whose units it
/// validates, every part its validator may read while doing so, and how it divides what it owns into units.</summary>
/// <remarks>
/// <para>The partitions are one table (<see cref="WorldDefinitionValidator.Partitions"/>). Every part of a document is
/// owned by exactly one partition, so whole-document validation is running every partition, and what an edit makes a
/// validator run is derived from the table: a partition runs when the edit changes a part it owns, or changes a part it
/// reads in a way other partitions can see (<see cref="WorldDefinitionValidator.TryValidateChange"/>).</para>
/// <para><see cref="Reads"/> is enforced, not trusted: each run records what the validator read of the document
/// (<see cref="WorldDocumentReads"/>), and a read past the declared parts refuses the document by the partition's
/// name, so a new read cannot leave an edit's derived set too small without failing the first validation that makes
/// it.</para>
/// </remarks>
public sealed class WorldValidationPartition {
    private readonly Func<WorldDefinition, int> m_rowCount;

    internal WorldValidationPartition(string name, WorldValidationGranularity granularity, WorldDocumentInputs owns, WorldDocumentInputs reads,
        Action<WorldValidationRun, IReadOnlyList<int>?> validate, Func<WorldDefinition, int>? rowCount = null, Func<WorldDefinition, WorldDefinition, WorldValidationRowChange>? rowChange = null,
        WorldBoundValidation? bound = null, Func<WorldDefinition, WorldDefinition, WorldDocumentInputs, IReadOnlyList<int>?>? selectDependentRows = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);
        ArgumentNullException.ThrowIfNull(argument: validate);
        if (granularity == WorldValidationGranularity.Rows && (rowCount is null || rowChange is null || bound is null)) {
            throw new ArgumentException(message: "A row partition must count, compare and validate individual rows.", paramName: nameof(granularity));
        }

        Name = name;
        Granularity = granularity;
        Owns = owns;
        Reads = (reads | owns);
        Validate = validate;
        m_rowCount = (rowCount ?? (static _ => 1));
        RowChange = rowChange;
        Bound = bound;
        SelectDependentRows = selectDependentRows;
    }

    /// <summary>Gets the partition's name, which a refusal of its own reads and the unit count's reports use.</summary>
    public string Name { get; }
    /// <summary>Gets how the partition divides what it owns into units.</summary>
    public WorldValidationGranularity Granularity { get; }
    /// <summary>Gets the parts whose units this partition validates.</summary>
    public WorldDocumentInputs Owns { get; }
    /// <summary>Gets every part the partition's validator may read, the owned parts included.</summary>
    public WorldDocumentInputs Reads { get; }

    // Validates every unit (a null list) or the listed rows of a row partition.
    internal Action<WorldValidationRun, IReadOnlyList<int>?> Validate { get; }
    // How a row partition's rows changed between two documents; null for a section partition.
    internal Func<WorldDefinition, WorldDefinition, WorldValidationRowChange>? RowChange { get; }
    internal WorldBoundValidation? Bound { get; }
    internal Func<WorldDefinition, WorldDefinition, WorldDocumentInputs, IReadOnlyList<int>?>? SelectDependentRows { get; }

    /// <summary>Whether bound writes can validate individual units through this partition.</summary>
    public bool SupportsBoundWrites => Bound is not null;

    /// <summary>Returns how many units the partition validates in a whole-document validation of
    /// <paramref name="definition"/>: one for a section partition, one per row for a row partition.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The unit count.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public int UnitsIn(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        using (WorldDocumentReads.Unrecorded()) { return m_rowCount(arg: definition); }
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} (owns {Owns}; reads {Reads})";
}

internal sealed record WorldBoundValidation(
    Func<WorldDefinition, IEnumerable<object>> Enumerate,
    Func<WorldDefinition, object, Func<object, object?>, WorldBoundValidationUnit?> Resolve,
    Func<WorldDefinition, WorldDefinition> Remove
);

internal sealed record WorldBoundValidationUnit(WorldDefinition Definition, IReadOnlyList<int>? Rows = null);

/// <summary>How a row partition's rows changed between two documents.</summary>
/// <param name="Rows">The indices, in the later document, of the rows to validate; empty when none changed.</param>
/// <param name="Private">Whether every change is one no other partition reads: the same rows, in the same order, each
/// differing only in what the owning partition alone reads.</param>
internal readonly record struct WorldValidationRowChange(IReadOnlyList<int> Rows, bool Private);

/// <summary>One validation's inputs and the refusals its partitions collect, shared by every partition it runs.</summary>
internal sealed class WorldValidationRun {
    public required WorldDefinition Definition { get; init; }
    public WorldDefinition? Before { get; init; }
    public bool BoundUnit { get; init; }
    public List<string> Errors { get; } = [];
    public ICollection<string>? Deferred { get; init; }
    public Puck.Abstractions.Machines.IMachineValidationCatalog? Machines { get; init; }
    public IWorldNeighbourResolver? Neighbours { get; init; }
    public bool ValidateAdjacencyClaims { get; init; }
    public bool RetainCompilation { get; init; }
    public bool ProveCensusOutcomes { get; init; } = true;
    // The document came from another process: it is checked as what its recipient presents. Content it names by pin
    // is checked when this process holds it and is otherwise left to its reader, no file it names is opened, and its
    // rules, which a recipient never runs, are not compiled.
    public bool Delivered { get; init; }
    // The programs the document partition compiled, when it ran and was asked to keep them.
    public WorldRuleCompilation? Compilation { get; set; }
    // Set by a partition whose refusal leaves nothing after it safe to read (the state expansion).
    public bool Stopped { get; set; }
}
