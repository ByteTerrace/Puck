namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // Seat programs and the routes they play: each program's operations name known input sources with admissible
    // parameters, and each route names a program, places that are region placements, spawn points that exist, and
    // actions its program presses.
    private static void ValidateRoutes(WorldDefinition definition, List<string> errors) {
        if ((definition.SeatProgramsRaw is not { Count: > 0 }) && (definition.RoutesRaw is not { Count: > 0 })) {
            return;
        }

        var programs = new Dictionary<string, WorldSeatProgram>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < definition.SeatPrograms.Count); index++) {
            var program = definition.SeatPrograms[index];
            var path = $"seatPrograms[{index}]";

            if (string.IsNullOrWhiteSpace(value: program?.Name)) {
                errors.Add(item: $"{path}.name is required.");
                continue;
            }
            if (!programs.TryAdd(key: program.Name, value: program)) {
                errors.Add(item: $"{path}.name '{program.Name}' is duplicated.");
            }
            if (program.Operations is not { Count: > 0 }) {
                errors.Add(item: $"{path} ('{program.Name}') has no operations.");
                continue;
            }

            var actions = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var op = 0; (op < program.Operations.Count); op++) {
                var operation = program.Operations[op];
                var at = $"{path}.operations[{op}] ({operation?.Opcode})";

                if (operation is null) {
                    errors.Add(item: $"{path}.operations[{op}] is null.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(value: operation.Driven)) {
                    errors.Add(item: $"{at}.source is required.");
                } else if ((InputSourceVocabularyHook.IsKnownSourceId is { } known) && !known(arg: operation.Driven)) {
                    errors.Add(item: $"{at}.source '{operation.Driven}' names no input source.");
                }

                switch (operation) {
                    case WorldSeatProgramOp.Move move when (!(move.Brake > 0f) || !(move.CornerSpeed > 0f) || !(move.CornerDegrees is > 0f and < 180f) || !(move.Least is >= 0f and <= 1f)):
                        errors.Add(item: $"{at} needs brake and cornerSpeed above zero, cornerDegrees in (0, 180) and least in [0, 1].");
                        break;
                    case WorldSeatProgramOp.Turn turn when (!(turn.Gain is > 0f and <= 1f) || !(turn.DeadbandDegrees >= 0f)):
                        errors.Add(item: $"{at} needs gain in (0, 1] and deadbandDegrees at least zero.");
                        break;
                    case WorldSeatProgramOp.Hold hold when (hold.Paces is not { Count: > 0 }):
                        errors.Add(item: $"{at} names no paces.");
                        break;
                    case WorldSeatProgramOp.Pulse pulse when ((pulse.Paces is not { Count: > 0 }) || !(pulse.PeriodSeconds > 0f)):
                        errors.Add(item: $"{at} needs paces and periodSeconds above zero.");
                        break;
                    case WorldSeatProgramOp.Rise rise when (!(rise.Band >= 0f) || !(rise.Brake > 0f) || (rise.Toward is not (WorldSeatRiseDirection.Up or WorldSeatRiseDirection.Down))):
                        errors.Add(item: $"{at} needs band at least zero, brake above zero and toward Up or Down.");
                        break;
                    case WorldSeatProgramOp.Tap press when string.IsNullOrWhiteSpace(value: press.Action):
                        errors.Add(item: $"{at}.action is required.");
                        break;
                    case WorldSeatProgramOp.Tap press when !actions.Add(item: press.Action):
                        errors.Add(item: $"{at}.action '{press.Action}' is pressed by an earlier operation too.");
                        break;
                }
            }
        }

        var places = new HashSet<string>(collection: definition.Placements.Where(predicate: static placement => (placement?.Region is not null)).Select(selector: static placement => placement.Id), comparer: StringComparer.Ordinal);
        var spawns = new HashSet<string>(collection: (definition.SpawnPointsRaw ?? definition.SpawnPoints).Select(selector: static spawn => spawn.Id), comparer: StringComparer.Ordinal);
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < definition.Routes.Count); index++) {
            var route = definition.Routes[index];
            var path = $"routes[{index}]";

            if (string.IsNullOrWhiteSpace(value: route?.Name)) {
                errors.Add(item: $"{path}.name is required.");
                continue;
            }
            if (!names.Add(item: route.Name)) {
                errors.Add(item: $"{path}.name '{route.Name}' is duplicated.");
            }
            if (route.Seat is < 1 or > WorldBodiesLimits.LocalSeatCount) {
                errors.Add(item: $"{path}.seat {route.Seat} is not a local seat (1..{WorldBodiesLimits.LocalSeatCount}).");
            }
            if (!(route.Margin >= 0f)) {
                errors.Add(item: $"{path}.margin must be at least zero.");
            }

            programs.TryGetValue(key: route.Program ?? string.Empty, value: out var program);

            if (program is null) {
                errors.Add(item: $"{path}.program '{route.Program}' names no seatPrograms row.");
            }

            var actions = (program?.Operations.OfType<WorldSeatProgramOp.Tap>().Select(selector: static press => press.Action).ToHashSet(comparer: StringComparer.Ordinal) ?? []);
            var chapters = new HashSet<string>(comparer: StringComparer.Ordinal);

            if (route.Chapters is not { Count: > 0 }) {
                errors.Add(item: $"{path} ('{route.Name}') has no chapters.");
                continue;
            }

            for (var chapterIndex = 0; (chapterIndex < route.Chapters.Count); chapterIndex++) {
                var chapter = route.Chapters[chapterIndex];
                var chapterPath = $"{path}.chapters[{chapterIndex}]";

                if (string.IsNullOrWhiteSpace(value: chapter?.Name) || !chapters.Add(item: chapter.Name)) {
                    errors.Add(item: $"{chapterPath}.name is missing or duplicated within the route.");
                    continue;
                }
                if (!(chapter.TimeoutSeconds > 0f)) {
                    errors.Add(item: $"{chapterPath}.timeoutSeconds must be above zero.");
                }
                if (chapter.Steps is not { Count: > 0 }) {
                    errors.Add(item: $"{chapterPath} ('{chapter.Name}') has no steps.");
                    continue;
                }

                for (var stepIndex = 0; (stepIndex < chapter.Steps.Count); stepIndex++) {
                    var step = chapter.Steps[stepIndex];
                    var at = $"{chapterPath}.steps[{stepIndex}] ({step?.Opcode})";

                    // A step that names the world it is played in names that world's places, which this document need
                    // not author; the seat driver checks them against what that world delivers.
                    void Place(string? place, string member, string? world) {
                        if ((world is not null) && !string.Equals(a: world, b: definition.DocumentId, comparisonType: StringComparison.Ordinal)) {
                            return;
                        }
                        if ((place is not null) && !places.Contains(item: place)) {
                            errors.Add(item: $"{at}.{member} '{place}' names no region placement.");
                        }
                    }
                    void World(string? world) {
                        if ((world is not null) && string.IsNullOrWhiteSpace(value: world)) {
                            errors.Add(item: $"{at}.world names no world.");
                        }
                    }

                    switch (step) {
                        case null:
                            errors.Add(item: $"{chapterPath}.steps[{stepIndex}] is null.");
                            break;
                        case WorldRouteStep.PoseBody pose:
                            if ((pose.Spawn is null) == (pose.Point is null)) {
                                errors.Add(item: $"{at} names exactly one of spawn and point.");
                            } else if ((pose.Spawn is { } spawn) && !spawns.Contains(item: spawn)) {
                                errors.Add(item: $"{at}.spawn '{spawn}' names no spawn point.");
                            }
                            break;
                        case WorldRouteStep.Line line when string.IsNullOrWhiteSpace(value: line.Text):
                            errors.Add(item: $"{at}.text is required.");
                            break;
                        case WorldRouteStep.Go go:
                            if ((go.Place is null) == (go.Point is null)) {
                                errors.Add(item: $"{at} names exactly one of place and point.");
                            }
                            if (!(go.Within > 0f)) {
                                errors.Add(item: $"{at}.within must be above zero.");
                            }
                            World(world: go.World);
                            Place(member: "place", place: go.Place, world: go.World);
                            foreach (var corridor in (go.Corridor ?? [])) {
                                Place(member: "corridor", place: corridor, world: go.World);
                            }
                            break;
                        case WorldRouteStep.Cross cross:
                            // The place a crossing ends in may lie in the world it crosses into, which this document
                            // need not author, so it is not checked against this document's placements.
                            if (!(cross.Metres > 0f)) {
                                errors.Add(item: $"{at}.metres must be above zero.");
                            }
                            if ((cross.Until is not null) && string.IsNullOrWhiteSpace(value: cross.Until)) {
                                errors.Add(item: $"{at}.until names no place.");
                            }
                            break;
                        case WorldRouteStep.Fly fly:
                            if ((fly.Point is null) || !float.IsFinite(f: fly.Point.Value.LengthSquared())) {
                                errors.Add(item: $"{at}.point must be a finite point.");
                            }
                            if (!(fly.Within > 0f)) {
                                errors.Add(item: $"{at}.within must be above zero.");
                            }
                            if ((fly.Lands ? 1 : 0) + ((fly.Enters is null) ? 0 : 1) + ((fly.Until is null) ? 0 : 1) > 1) {
                                errors.Add(item: $"{at} names at most one of lands, enters and until.");
                            }
                            World(world: fly.World);
                            World(world: fly.Enters);
                            Place(member: "until", place: fly.Until, world: fly.World);
                            break;
                        case WorldRouteStep.Rest rest:
                            if (!(rest.Seconds > 0f)) {
                                errors.Add(item: $"{at}.seconds must be above zero.");
                            }
                            if ((rest.FaceDegrees is not null) && (rest.LookAt is not null)) {
                                errors.Add(item: $"{at} names at most one of faceDegrees and lookAt.");
                            }
                            break;
                        case WorldRouteStep.PressAction press:
                            if ((program is not null) && !actions.Contains(item: press.Action)) {
                                errors.Add(item: $"{at}.action '{press.Action}' is pressed by no operation of program '{program.Name}'.");
                            }
                            if (!(press.Seconds > 0f)) {
                                errors.Add(item: $"{at}.seconds must be above zero.");
                            }
                            World(world: press.World);
                            Place(member: "until", place: press.Until, world: press.World);
                            break;
                    }
                }
            }
        }
    }
}
