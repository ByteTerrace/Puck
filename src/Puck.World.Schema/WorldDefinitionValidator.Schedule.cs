namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The settle margin, ascending ticks, an admitted seat label, and a one-line command
    // opening with a verb WorldScheduleCommands admits. Argument shapes stay the host command registry's to answer,
    // and this project holds none — a malformed argument is a recorded refusal at its tick, not a boot failure.
    private static void ValidateSchedule(WorldDefinition definition, List<string> errors) {
        if (definition.Schedule is not { } schedule) {
            return;
        }

        if (schedule.SettleTicks < 1) {
            errors.Add(item: $"schedule.settleTicks {schedule.SettleTicks} must be at least 1 — a zero margin exports at the same tick the last command was submitted, before it could have applied.");
        } else if (schedule.SettleTicks > WorldScheduleCapacity.MaxSettleTicks) {
            errors.Add(item: $"schedule.settleTicks {schedule.SettleTicks} exceeds {WorldScheduleCapacity.MaxSettleTicks}.");
        }

        var rows = (schedule.Rows ?? []);

        if (rows.Count > WorldScheduleCapacity.MaxRows) {
            errors.Add(item: $"schedule.rows count {rows.Count} exceeds {WorldScheduleCapacity.MaxRows}.");
        }

        var instances = ValidateScheduleInstances(
            errors: errors,
            schedule: schedule
        );
        var previous = 0UL;

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var path = $"schedule.rows[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (row.Tick == 0UL) {
                errors.Add(item: $"{path}.tick is 0 — already in the past; the world has not completed a step yet.");
            } else if (row.Tick < previous) {
                errors.Add(item: $"{path}.tick {row.Tick} is earlier than the preceding row's {previous} — scheduled commands are declared in ascending tick order.");
            }

            previous = Math.Max(
                val1: previous,
                val2: row.Tick
            );

            if (!WorldScheduleCapacity.IsAdmittedPrincipal(
                principal: row.Principal,
                reason: out var reason
            )) {
                errors.Add(item: $"{path}.principal '{row.Principal}' {reason}.");
            }

            if (row.World is { } addressed) {
                if (!instances.Contains(item: addressed)) {
                    errors.Add(item: $"{path}.world '{addressed}' names no instance this run arms — address '{WorldScheduleSection.BootWorldName}' (or omit the member) for the world this process boots with, or declare '{addressed}' in schedule.instances.");
                } else if (
                    (addressed != WorldScheduleSection.BootWorldName) &&
                    !WorldScheduleCommands.IsAddressable(verb: WorldScheduleCommands.LeadingVerb(command: row.Command))
                ) {
                    errors.Add(item: $"{path}.world addresses '{addressed}' with '{WorldScheduleCommands.LeadingVerb(command: row.Command)}', whose grammar carries no world token — that verb reaches the world this process booted with whatever a row asks for. The verbs a row may address a sibling with are {string.Join(
                        separator: ", ",
                        values: WorldScheduleCommands.Addressable
                    )}; drive a sibling's state through what crosses into it.");
                }
            }

            ValidateScheduleCommand(
                command: row.Command,
                errors: errors,
                path: path
            );

            if (row.Refusal is { } refusal) {
                if (row.Expect != WorldScheduleExpectation.Refused) {
                    errors.Add(item: $"{path}.refusal names text beside expect '{row.Expect}' — a refusal's text is only ever what a row expecting a refusal matches against.");
                } else if (string.IsNullOrWhiteSpace(value: refusal)) {
                    errors.Add(item: $"{path}.refusal is blank — omit it to accept any refusal, or name the text the recorded one must carry.");
                } else if (refusal.Length > WorldScheduleCapacity.MaxCommandLength) {
                    errors.Add(item: $"{path}.refusal length {refusal.Length} exceeds {WorldScheduleCapacity.MaxCommandLength}.");
                }
            }
        }
    }
    // The arming list: every name a row may address, plus the reserved boot name. A name is a path segment because
    // it is the directory the instance's owned worlds live in and the suffix of its export's file name.
    private static HashSet<string> ValidateScheduleInstances(WorldScheduleSection schedule, List<string> errors) {
        var named = new HashSet<string>(comparer: StringComparer.Ordinal) { WorldScheduleSection.BootWorldName };
        var instances = (schedule.Instances ?? []);

        if (instances.Count > WorldScheduleCapacity.MaxInstances) {
            errors.Add(item: $"schedule.instances count {instances.Count} exceeds {WorldScheduleCapacity.MaxInstances}.");
        }

        for (var index = 0; (index < instances.Count); index++) {
            var instance = instances[index];
            var path = $"schedule.instances[{index}]";

            if (instance is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.Equals(
                a: instance.Name,
                b: WorldScheduleSection.BootWorldName,
                comparisonType: StringComparison.Ordinal
            )) {
                errors.Add(item: $"{path}.name is '{WorldScheduleSection.BootWorldName}', which names the world this process boots with — a row reaches it by omitting its own world member.");
            } else if (!SafeName.TryParse(
                candidate: instance.Name,
                name: out _,
                reason: out var nameReason
            )) {
                errors.Add(item: $"{path}.name '{instance.Name}' is not a single safe path segment — the name IS the directory this instance's owned worlds live in, and {nameReason}.");
            } else if (!named.Add(item: instance.Name)) {
                errors.Add(item: $"{path}.name '{instance.Name}' is declared more than once.");
            }

            if (string.IsNullOrWhiteSpace(value: instance.Document)) {
                errors.Add(item: $"{path}.document is required — the world document this instance starts from.");
            } else if (!WorldDocumentName.TryValidate(
                name: instance.Document,
                reason: out var documentReason
            )) {
                errors.Add(item: $"{path}.document {documentReason}.");
            }
        }

        return named;
    }
    private static void ValidateScheduleCommand(string? command, string path, List<string> errors) {
        if (string.IsNullOrWhiteSpace(value: command)) {
            errors.Add(item: $"{path}.command is required — one command line in the console/ingress vocabulary.");

            return;
        }

        if (command.Length > WorldScheduleCapacity.MaxCommandLength) {
            errors.Add(item: $"{path}.command length {command.Length} exceeds {WorldScheduleCapacity.MaxCommandLength}.");
        }

        if (
            (command.IndexOf(value: '\n') >= 0) ||
            (command.IndexOf(value: '\r') >= 0)
        ) {
            errors.Add(item: $"{path}.command carries a line break — a scheduled row submits exactly one command.");
        }

        // The console's own script conventions (TextCommandSource.Collect) drop a blank line and a '#' comment
        // before they reach the parser, so a row authoring one would submit nothing and silently look accepted.
        if (command.TrimStart().StartsWith(value: '#')) {
            errors.Add(item: $"{path}.command is a '#' comment — the command pump drops one, so the row would submit nothing.");

            return;
        }

        var verb = WorldScheduleCommands.LeadingVerb(command: command);

        if (!WorldScheduleCommands.IsAdmitted(verb: verb)) {
            errors.Add(item: $"{path}.command names '{verb}', which is not a scheduled step — a row submits a simulation command (a state mutation, a guarded transform, a body intent or pose, a join or a leave), never a host-control, clock, process or file verb. The admitted verbs are {string.Join(
                separator: ", ",
                values: WorldScheduleCommands.Admitted
            )}.");
        }
    }
}
