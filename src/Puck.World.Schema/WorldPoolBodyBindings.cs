namespace Puck.World;

/// <summary>A carrier field and its enum-indexed logical body addresses, resolved once during admission.</summary>
public sealed record CompiledPoolBodyCarrier(int FieldOrdinal, IReadOnlyList<CompiledBodyRef> Bindings);
/// <summary>Validates and compiles logical pool carrier declarations.</summary>
public static class WorldPoolBodyBindings {
    /// <summary>Compiles one pool's carrier mapping, refusing missing fields, members, or body targets by name.</summary>
    public static CompiledPoolBodyCarrier Compile(WorldDefinition definition, StatePoolDescriptor pool) {
        var carriers = (definition.Properties?.Carriers ?? []).Where(predicate: carrier => ((carrier is not null) && (carrier.Pool == pool.Name))).ToArray();

        if (carriers.Length != 1) {
            throw new InvalidOperationException(message: $"Pool '{pool.Name}' requires exactly one carrier declaration for body interactions.");
        }
        var carrier = carriers[0];
        var field = pool.Fields.FirstOrDefault(predicate: field => (field.Name == carrier.Field));
        var domain = ((field.Declaration is { } declaration) ? definition.EnumOf(field: declaration) : null);

        if ((field.Name != carrier.Field) || (field.Kind != CellKind.Int) || (domain is null) || (field.Declaration.Advance is not null)) {
            throw new InvalidOperationException(message: $"Pool carrier '{pool.Name}' field '{carrier.Field}' must name an enum field without automatic advance on its record.");
        }
        var resolved = new CompiledBodyRef[domain.Count];
        var seen = new bool[domain.Count];

        foreach (var binding in carrier.Bindings) {
            if ((binding is null) || !domain.TryGetValue(member: binding.Member, value: out var value) || seen[((int)value)]) {
                throw new InvalidOperationException(message: $"Pool carrier '{pool.Name}' contains a missing, unknown, or duplicate enum member binding.");
            }
            seen[((int)value)] = true;
            if ((binding.Placement is not null) && (binding.Seat is not null)) {
                throw new InvalidOperationException(message: $"Pool carrier '{pool.Name}' member '{binding.Member}' specifies both a placement and seat.");
            }
            if ((binding.Seat is { } seat) && ((seat < 0) || (seat >= definition.Population.LocalSeats))) {
                throw new InvalidOperationException(message: $"Pool carrier '{pool.Name}' seat {seat} is outside the local seats.");
            }
            var target = new CompiledBodyRef(CompiledBodyRefKind.Literal, (binding.Seat ?? -1), null);

            if (binding.Placement is { } placement) {
                var ordinal = -1;

                for (var index = 0; (index < definition.Placements.Count); index++) {
                    if (definition.Placements[index].Id == placement) { ordinal = index; break; }
                }
                if ((ordinal < 0) || (definition.Placements[ordinal].Inhabit?.ResolvedCount.Literal != 1)) {
                    throw new InvalidOperationException(message: $"Pool carrier '{pool.Name}' placement '{placement}' must declare exactly one inhabitant.");
                }
                target = new CompiledBodyRef(CompiledBodyRefKind.Placement, ordinal, null);
            }
            resolved[((int)value)] = target;
        }
        if (seen.Any(predicate: value => !value)) {
            throw new InvalidOperationException(message: $"Pool carrier '{pool.Name}' must map every member of enum '{domain.Name}', including detached members.");
        }
        return new CompiledPoolBodyCarrier(field.Ordinal, Array.AsReadOnly(array: resolved));
    }
}
