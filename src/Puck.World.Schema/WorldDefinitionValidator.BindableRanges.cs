namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // Every authored leaf owes the same field domain, including an initial binding in an inactive key. Runtime
    // state changes use WorldValueDomainGuard; admission never substitutes a clamped or held initial value.
    private static void RequireBindableDomain(BindableScalar value, WorldDefinition definition, string path, WorldValueDomain domain, List<string> errors, string detail = "") {
        var values = new WorldValueResolver(definition, default);

        void Check(BindableScalar scalar, string at) {
            if ((scalar.Keys is not null) || !scalar.IsAuthorable(definition: definition)) { return; }
            var resolved = ((float)values.Scalar(fallback: double.NaN, value: scalar));

            if (!domain.Contains(value: resolved)) {
                errors.Add(item: $"{at} from {WorldValueDomain.SourceOf(value: scalar)} resolves initially to {resolved}; must be in {domain}.{detail}");
            }
        }
        Check(at: path, scalar: value);
        if (value.Keys?.Keys is { } keys) {
            for (var index = 0; (index < keys.Count); index++) {
                if (keys[index] is { } key) { Check(key.Value, $"{path}.keys[{index}].value"); }
            }
        }
    }
}
