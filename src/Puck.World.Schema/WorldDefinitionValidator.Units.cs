namespace Puck.World;

public static partial class WorldDefinitionValidator {
    /// <summary>Validates one placement as a unit: every check the whole-document validation makes of a placement row,
    /// run over the placement and the parent chain it is placed under, and nothing of the placements beside it.</summary>
    /// <remarks>The checks between placements (the solids budget, one board per topology, the instance ceilings) see only
    /// the unit, so they are the cross-unit reservations a whole validation still owns. Each call adds one unit to
    /// <see cref="WorldBootWork.Validations"/>.</remarks>
    /// <param name="definition">The document whose other sections the placement's references resolve against.</param>
    /// <param name="unit">The placement's parent chain, outermost first, then the placement, each as the value to judge
    /// (a bound value resolved to a literal at the clock the caller judges it at).</param>
    /// <param name="reason">The first refusal, or empty when the unit is valid.</param>
    /// <returns><see langword="true"/> when every check of the unit passes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="unit"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryValidatePlacementUnit(WorldDefinition definition, IReadOnlyList<WorldPlacement> unit, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: unit);
        WorldBootWork.Count(kind: WorldBootWork.Validations, amount: 1L);

        var errors = new List<string>();
        var projected = (definition with { PlacementsRaw = ((definition.PlacementsRaw ?? new WorldPlacementsSection()) with { Rows = unit }) });

        _ = ValidatePlacements(
            authoring: projected.Authoring,
            definition: projected,
            errors: errors,
            placements: unit,
            requiresField: WorldContactSelection.RequiresField(collision: definition.Collision),
            scope: DeclaredScope(definition: definition)
        );
        reason = ((errors.Count == 0) ? string.Empty : errors[0]);
        return (errors.Count == 0);
    }
    /// <summary>Validates one creation row as a unit: every check of the <c>prototypes</c> partition, judged as
    /// <paramref name="creation"/> in place of the row of its id.</summary>
    /// <remarks>Adds one unit to <see cref="WorldBootWork.Validations"/>.</remarks>
    /// <param name="definition">The document holding a creation row of the same id.</param>
    /// <param name="creation">The value to judge (a bound row resolved to literals at the caller's clock).</param>
    /// <param name="reason">The first refusal, or empty when the unit is valid.</param>
    /// <returns><see langword="true"/> when every check of the unit passes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="creation"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="definition"/> holds no creation of
    /// <paramref name="creation"/>'s id.</exception>
    public static bool TryValidateCreationUnit(WorldDefinition definition, WorldPrototype creation, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: creation);

        var creations = definition.Creations;
        var index = -1;

        for (var row = 0; ((row < creations.Count) && (index < 0)); row++) {
            if (string.Equals(a: creations[row]?.Id.Value, b: creation.Id.Value, comparisonType: StringComparison.Ordinal)) { index = row; }
        }
        if (index < 0) { throw new ArgumentException(message: $"The document holds no creation '{creation.Id.Value}'.", paramName: nameof(creation)); }
        WorldBootWork.Count(kind: WorldBootWork.Validations, amount: 1L);

        var errors = new List<string>();

        ValidateCreationRows(definition: definition, errors: errors, replacement: creation, rows: [index]);
        reason = ((errors.Count == 0) ? string.Empty : errors[0]);
        return (errors.Count == 0);
    }

    // The declared names a placement's references resolve against, read from each section's rows without validating
    // them: what the document partition threads to its placement checks once it has validated those sections.
    private static ValidationScope DeclaredScope(WorldDefinition definition) => new() {
        Cameras = DeclaredNames(rows: definition.Cameras, name: static camera => camera.Name),
        DestinationNames = DeclaredNames(rows: definition.Destinations, name: static destination => destination.Name),
        FontNames = DeclaredNames(rows: definition.Text?.Fonts, name: static font => font.Name),
        HasTextCatalog = (definition.Text is not null),
        KitNames = DeclaredNames(rows: definition.Kits, name: static kit => kit.Name),
        LookNames = DeclaredNames(rows: definition.Looks, name: static look => look.Name.Value),
        PatchIds = DeclaredNames(rows: definition.Patches, name: static patch => patch.Name),
        PrototypeIds = DeclaredNames(rows: definition.Creations, name: static creation => creation.Id.Value),
    };
    // The names a reference to a section's rows resolves against: every present row's non-blank name, read without
    // validating the section (its own partition judges duplicates and blanks).
    private static HashSet<string> DeclaredNames<TRow>(IEnumerable<TRow?>? rows, Func<TRow, string?> name) where TRow : class {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var row in (rows ?? [])) {
            if ((row is not null) && (name(arg: row) is { } value) && !string.IsNullOrWhiteSpace(value: value)) { _ = names.Add(item: value); }
        }
        return names;
    }
}
