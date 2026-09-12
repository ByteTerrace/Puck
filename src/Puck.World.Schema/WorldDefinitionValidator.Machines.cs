using Puck.Abstractions.Machines;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateMachines(WorldDefinition definition, IMachineValidationCatalog? catalog,
        List<string> errors, ICollection<string>? deferred) {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < definition.Machines.Count; index++) {
            var machine = definition.Machines[index];
            var path = $"machines[{index}]";
            if (machine is null) {
                errors.Add($"{path}: a machine declaration is required.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(machine.Name)) {
                errors.Add($"{path}.name: a nonempty instance name is required.");
            } else if (!names.Add(machine.Name)) {
                errors.Add($"{path}.name: duplicate instance '{machine.Name}'.");
            }
            path = $"{path} ({machine.Name})";
            ValidateMachineBindings(definition, machine, path, errors);
            if (string.IsNullOrWhiteSpace(machine.Engine)) {
                errors.Add($"{path}.engine: a provider identifier is required.");
            } else if (catalog is null) {
                deferred?.Add($"{path}.configuration: validation is deferred because no machine catalog was supplied for '{machine.Engine}'.");
            } else if (!catalog.TryDescriptor(machine.Engine, out var descriptor)) {
                errors.Add($"{path}.engine: provider '{machine.Engine}' is unavailable in the selected machine catalog.");
            } else {
                var configurationErrors = new List<string>();
                _ = MachineConfigurationValidation.TryValidate(descriptor.Configuration, machine.Configuration,
                    schemaTag: true, configurationErrors);
                foreach (var error in configurationErrors) {
                    errors.Add($"{path}.{error}");
                }
            }
        }
    }

    private static void ValidateMachineBindings(WorldDefinition definition, WorldMachine machine, string path, List<string> errors) {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in machine.Memory ?? []) {
            if (binding is null) {
                errors.Add($"{path}.memory: a binding is required.");
                continue;
            }
            var site = $"{path}.memory[{binding.Name}]";
            if (string.IsNullOrWhiteSpace(binding.Name) || !names.Add(binding.Name)) {
                errors.Add($"{site}.name: binding names must be nonempty and unique within the machine.");
            }
            if (!Enum.IsDefined(binding.Direction)) {
                errors.Add($"{site}.direction: unsupported direction.");
            }
            if (binding.Width == 0) {
                errors.Add($"{site}.format: unsupported scalar format '{binding.Format}'.");
            }
            if (string.IsNullOrWhiteSpace(binding.Space)) {
                errors.Add($"{site}.space: a provider address space is required.");
            }
            if (binding.Address.HasValue == (binding.Symbol is not null) || binding.Symbol is { Length: 0 }) {
                errors.Add($"{site}: specify exactly one raw address or nonempty symbol.");
            }
            if (binding.Direction == WorldMachineMemoryDirection.Read ? binding.Access != "inspect" : binding.Access is not ("patch" or "bus")) {
                errors.Add($"{site}.access: reads require inspect; writes require patch or bus.");
            }
            if (binding.Update is not ("onChange" or "everyTick")) {
                errors.Add($"{site}.update: expected onChange or everyTick.");
            }
            if (binding.Conversion is not ("checked" or "truncate")) {
                errors.Add($"{site}.conversion: expected checked or truncate.");
            }
            ValidateMemoryRow(binding.Row, binding.Key, site, definition, errors);
        }
    }
}
