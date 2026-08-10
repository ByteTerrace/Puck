using System.Text.Json;

namespace Puck.Cli.Canary;

internal static class CanaryManifestLoader {
    private const int MaximumExitSeconds = 30;
    private const int MaximumLegTimeoutSeconds = 60;

    public static bool TryLoadAll(string repositoryRoot, out IReadOnlyList<CanaryManifest> manifests, out string error) {
        var canaryRoot = Path.Combine(path1: repositoryRoot, path2: "docs", path3: "verification", path4: "canaries");
        var loaded = new List<CanaryManifest>();

        manifests = loaded;
        error = string.Empty;

        if (!Directory.Exists(path: canaryRoot)) {
            error = $"canary discovery found no manifest directory at {CliPaths.ToDisplay(relativeTo: repositoryRoot, fullPath: canaryRoot)}; an empty suite cannot prove anything.";

            return false;
        }

        var rootFiles = Directory.GetFiles(path: canaryRoot, searchPattern: "*", searchOption: SearchOption.TopDirectoryOnly);
        if (rootFiles.Length != 0) {
            error = $"unexpected file '{CliPaths.ToDisplay(relativeTo: repositoryRoot, fullPath: rootFiles.Order(comparer: StringComparer.Ordinal).First())}' sits outside a canary directory; every proof artifact must be owned by one manifest.";

            return false;
        }

        var directories = Directory.GetDirectories(path: canaryRoot).Order(comparer: StringComparer.Ordinal).ToArray();
        if (directories.Length == 0) {
            error = "canary discovery found zero manifests; an empty suite cannot report green.";

            return false;
        }

        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (var directory in directories) {
            var manifestPath = Path.Combine(path1: directory, path2: "canary.json");
            if (!File.Exists(path: manifestPath)) {
                error = $"canary directory '{Path.GetFileName(path: directory)}' has no canary.json; an orphan directory is not a proof.";

                return false;
            }

            if (!TryLoadManifest(repositoryRoot: repositoryRoot, directory: directory, manifestPath: manifestPath, manifest: out var manifest, error: out error)) {
                return false;
            }

            if (!ids.Add(item: manifest.Id)) {
                error = $"duplicate canary id '{manifest.Id}'; selection would not identify one proof.";

                return false;
            }

            var directoryName = Path.GetFileName(path: directory);
            if (!string.Equals(a: manifest.Id, b: directoryName, comparisonType: StringComparison.Ordinal)) {
                error = $"manifest '{CliPaths.ToDisplay(relativeTo: repositoryRoot, fullPath: manifestPath)}' refused: id '{manifest.Id}' does not match its directory name '{directoryName}'; discovery identity must come from one place.";

                return false;
            }

            loaded.Add(item: manifest);
        }

        return true;
    }

    private static bool TryLoadManifest(string repositoryRoot, string directory, string manifestPath, out CanaryManifest manifest, out string error) {
        manifest = null!;
        error = string.Empty;

        try {
            using var document = JsonDocument.Parse(
                utf8Json: File.ReadAllBytes(path: manifestPath),
                options: new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 }
            );

            if (TryFindDuplicateMember(element: document.RootElement, path: "$", duplicate: out var duplicate)) {
                throw new CanaryManifestRefusal(message: $"duplicate JSON member '{duplicate}' is ambiguous; the reviewer and runtime must see one value.");
            }

            var root = RequireObject(element: document.RootElement, context: "manifest root");

            RequireOnlyMembers(
                element: root,
                context: "manifest root",
                "binding", "bootShape", "discriminating", "fixtures", "id", "positive", "requirements", "seconds", "timeoutSeconds", "title"
            );

            var id = ReadRequiredString(element: root, member: "id", context: "manifest root");
            if (!IsSafeToken(value: id, allowColon: false, allowDot: false)) {
                throw new CanaryManifestRefusal(message: $"id '{id}' is not a safe lower-case token; use letters, digits, and single interior hyphens only.");
            }

            var title = ReadRequiredString(element: root, member: "title", context: $"canary '{id}'");
            var binding = ReadRequiredString(element: root, member: "binding", context: $"canary '{id}'");
            var bootShape = ReadBootShape(value: ReadRequiredString(element: root, member: "bootShape", context: $"canary '{id}'"), id: id);
            var requirements = ReadRequirements(element: root, id: id);
            var seconds = ReadInteger(element: root, member: "seconds", context: $"canary '{id}'");
            var timeoutSeconds = ReadInteger(element: root, member: "timeoutSeconds", context: $"canary '{id}'");

            if ((seconds <= 0) || (seconds > MaximumExitSeconds)) {
                throw new CanaryManifestRefusal(message: $"canary '{id}' seconds must be in 1..{MaximumExitSeconds}; an automatic proof must end on its own and stay cheap.");
            }

            if ((timeoutSeconds <= seconds) || (timeoutSeconds > MaximumLegTimeoutSeconds)) {
                throw new CanaryManifestRefusal(message: $"canary '{id}' timeoutSeconds must be greater than seconds and at most {MaximumLegTimeoutSeconds}; it must distinguish a hang without making the gate unbounded.");
            }

            var positive = ReadLeg(
                element: ReadRequiredObject(element: root, member: "positive", context: $"canary '{id}'"),
                id: id,
                name: "positive",
                repositoryRoot: repositoryRoot,
                canaryDirectory: directory
            );
            var discriminating = ReadLeg(
                element: ReadRequiredObject(element: root, member: "discriminating", context: $"canary '{id}'"),
                id: id,
                name: "discriminating",
                repositoryRoot: repositoryRoot,
                canaryDirectory: directory
            );

            if (PathsEqual(left: positive.WorldPath, right: discriminating.WorldPath) && PathsEqual(left: positive.ScriptPath, right: discriminating.ScriptPath)) {
                throw new CanaryManifestRefusal(message: $"canary '{id}' discriminating leg changes neither world nor script; prose alone cannot make the positive observation turn red.");
            }

            var fixtures = ReadFixtures(element: root, id: id, repositoryRoot: repositoryRoot, canaryDirectory: directory);
            RefuseUnexpectedFiles(
                canaryDirectory: directory,
                manifestPath: manifestPath,
                positive: positive,
                discriminating: discriminating,
                fixtures: fixtures,
                repositoryRoot: repositoryRoot,
                id: id
            );

            manifest = new CanaryManifest(
                Binding: binding,
                BootShape: bootShape,
                Discriminating: discriminating,
                DirectoryPath: directory,
                Fixtures: fixtures,
                Id: id,
                Positive: positive,
                Requirements: requirements,
                Seconds: seconds,
                TimeoutSeconds: timeoutSeconds,
                Title: title
            );

            return true;
        } catch (CanaryManifestRefusal refusal) {
            error = $"manifest '{CliPaths.ToDisplay(relativeTo: repositoryRoot, fullPath: manifestPath)}' refused: {refusal.Message}";

            return false;
        } catch (JsonException exception) {
            error = $"manifest '{CliPaths.ToDisplay(relativeTo: repositoryRoot, fullPath: manifestPath)}' refused: invalid JSON ({exception.Message.ReplaceLineEndings(replacementText: " ")}).";

            return false;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            error = $"manifest '{CliPaths.ToDisplay(relativeTo: repositoryRoot, fullPath: manifestPath)}' could not be read: {exception.Message.ReplaceLineEndings(replacementText: " ")}.";

            return false;
        }
    }

    private static CanaryLeg ReadLeg(JsonElement element, string id, string name, string repositoryRoot, string canaryDirectory) {
        var context = $"canary '{id}' {name} leg";

        RequireOnlyMembers(element: element, context: context, "commands", "expect", "script", "world");

        var worldText = ReadRequiredString(element: element, member: "world", context: context);
        var scriptText = ReadRequiredString(element: element, member: "script", context: context);
        var worldPath = ResolveFile(rawPath: worldText, basePath: repositoryRoot, containmentRoot: repositoryRoot, context: $"{context} world");
        var scriptPath = ResolveFile(rawPath: scriptText, basePath: canaryDirectory, containmentRoot: repositoryRoot, context: $"{context} script");
        var commands = ReadCommands(element: element, context: context, scriptPath: scriptPath);
        var assertions = ReadAssertions(element: element, context: context);

        return new CanaryLeg(Assertions: assertions, Commands: commands, Name: name, ScriptPath: scriptPath, WorldPath: worldPath);
    }

    private static IReadOnlyList<CanaryCommandClaim> ReadCommands(JsonElement element, string context, string scriptPath) {
        var array = ReadRequiredArray(element: element, member: "commands", context: context);
        var claims = new List<CanaryCommandClaim>(capacity: array.GetArrayLength());

        for (var index = 0; (index < array.GetArrayLength()); index++) {
            var item = array[index];
            if (item.ValueKind == JsonValueKind.Null) {
                throw new CanaryManifestRefusal(message: $"{context} commands[{index}] is null; every script command needs an outcome.");
            }

            var row = RequireObject(element: item, context: $"{context} commands[{index}]");

            RequireOnlyMembers(element: row, context: $"{context} commands[{index}]", "occurrence", "outcome", "verb");

            var verb = ReadRequiredString(element: row, member: "verb", context: $"{context} commands[{index}]");
            if (!IsSafeToken(value: verb, allowColon: false, allowDot: true)) {
                throw new CanaryManifestRefusal(message: $"{context} command verb '{verb}' is not a lower-case dotted token.");
            }

            var occurrence = ReadInteger(element: row, member: "occurrence", context: $"{context} commands[{index}]");
            if (occurrence <= 0) {
                throw new CanaryManifestRefusal(message: $"{context} command '{verb}' occurrence must be positive.");
            }

            var outcomeText = ReadRequiredString(element: row, member: "outcome", context: $"{context} commands[{index}]");
            var outcome = outcomeText switch {
                "accepted" => CanaryCommandOutcome.Accepted,
                "refused" => CanaryCommandOutcome.Refused,
                _ => throw new CanaryManifestRefusal(message: $"{context} command '{verb}' outcome '{outcomeText}' is invalid; use exactly 'accepted' or 'refused' (casing is significant)."),
            };

            claims.Add(item: new CanaryCommandClaim(Verb: verb, Occurrence: occurrence, Outcome: outcome));
        }

        var submitted = ReadScriptCommands(scriptPath: scriptPath, context: context);
        if (submitted.Count == 0) {
            throw new CanaryManifestRefusal(message: $"{context} script contains zero commands; comments cannot prove behavior.");
        }

        if (claims.Count != submitted.Count) {
            throw new CanaryManifestRefusal(message: $"{context} declares {claims.Count} command outcome(s), but its script submits {submitted.Count}; every non-comment command must be accounted for.");
        }

        for (var index = 0; (index < submitted.Count); index++) {
            var expected = submitted[index];
            var claim = claims[index];
            if (!string.Equals(a: claim.Verb, b: expected.Verb, comparisonType: StringComparison.Ordinal) || (claim.Occurrence != expected.Occurrence)) {
                throw new CanaryManifestRefusal(message: $"{context} commands[{index}] claims {claim.Verb} occurrence {claim.Occurrence}, but the script's command there is {expected.Verb} occurrence {expected.Occurrence}; outcomes bind to script order, verb, and occurrence.");
            }
        }

        return claims;
    }

    private static IReadOnlyList<(string Verb, int Occurrence)> ReadScriptCommands(string scriptPath, string context) {
        var commands = new List<(string Verb, int Occurrence)>();
        var occurrences = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        foreach (var rawLine in File.ReadLines(path: scriptPath)) {
            var line = rawLine.Trim().TrimStart(trimChar: '\uFEFF');
            if ((line.Length == 0) || line.StartsWith(value: '#')) {
                continue;
            }

            var separator = line.IndexOfAny(anyOf: [' ', '\t']);
            var verb = ((separator < 0) ? line : line[..separator]);
            if (!IsSafeToken(value: verb, allowColon: false, allowDot: true)) {
                throw new CanaryManifestRefusal(message: $"{context} script command verb '{verb}' is not a lower-case dotted token.");
            }

            occurrences.TryGetValue(key: verb, value: out var occurrence);
            occurrence++;
            occurrences[verb] = occurrence;
            commands.Add(item: (verb, occurrence));
        }

        return commands;
    }

    private static IReadOnlyList<CanaryAssertion> ReadAssertions(JsonElement element, string context) {
        var array = ReadRequiredArray(element: element, member: "expect", context: context);
        if (array.GetArrayLength() == 0) {
            throw new CanaryManifestRefusal(message: $"{context} expect is empty; a leg with no observation passes vacuously.");
        }

        var assertions = new List<CanaryAssertion>(capacity: array.GetArrayLength());
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var values = new HashSet<string>(comparer: StringComparer.Ordinal);
        var hasPositiveObservation = false;

        for (var index = 0; (index < array.GetArrayLength()); index++) {
            var item = array[index];
            if (item.ValueKind == JsonValueKind.Null) {
                throw new CanaryManifestRefusal(message: $"{context} expect[{index}] is null; a null assertion observes nothing.");
            }

            var row = RequireObject(element: item, context: $"{context} expect[{index}]");
            var type = ReadRequiredString(element: row, member: "type", context: $"{context} expect[{index}]");
            CanaryAssertion assertion = type switch {
                "line" => ReadLineAssertion(element: row, context: $"{context} expect[{index}]"),
                "response" => ReadResponseAssertion(element: row, context: $"{context} expect[{index}]", values: values),
                "sequence" => ReadSequenceAssertion(element: row, context: $"{context} expect[{index}]"),
                "relation" => ReadRelationAssertion(element: row, context: $"{context} expect[{index}]", values: values),
                _ => throw new CanaryManifestRefusal(message: $"{context} expect[{index}] type '{type}' is invalid; use exactly 'line', 'response', 'sequence', or 'relation' (casing is significant)."),
            };

            if (!names.Add(item: assertion.Name)) {
                throw new CanaryManifestRefusal(message: $"{context} repeats assertion name '{assertion.Name}'; verdicts must be individually identifiable.");
            }

            hasPositiveObservation |= ((assertion is not CanaryLineAssertion line) || line.Present);
            assertions.Add(item: assertion);
        }

        foreach (var relation in assertions.OfType<CanaryRelationAssertion>()) {
            RequireKnownOperand(operand: relation.Left, values: values, context: $"{context} assertion '{relation.Name}' left");
            if (relation.Right is { } right) {
                RequireKnownOperand(operand: right, values: values, context: $"{context} assertion '{relation.Name}' right");
            }
        }

        if (!hasPositiveObservation) {
            throw new CanaryManifestRefusal(message: $"{context} expect contains only absence checks; a process that never answered would pass.");
        }

        return assertions;
    }

    private static CanaryLineAssertion ReadLineAssertion(JsonElement element, string context) {
        RequireOnlyMembers(element: element, context: context, "match", "name", "present", "stream", "text", "type");

        var name = ReadRequiredString(element: element, member: "name", context: context);
        var stream = ReadStream(value: ReadRequiredString(element: element, member: "stream", context: context), context: context);
        var matchText = ReadRequiredString(element: element, member: "match", context: context);
        var match = matchText switch {
            "exact" => CanaryLineMatch.Exact,
            "contains" => CanaryLineMatch.Contains,
            _ => throw new CanaryManifestRefusal(message: $"{context} match '{matchText}' is invalid; use exactly 'exact' or 'contains' (casing is significant)."),
        };
        var text = ReadRequiredString(element: element, member: "text", context: context);
        var present = true;

        if (element.TryGetProperty(propertyName: "present", value: out var presentElement)) {
            present = presentElement.ValueKind switch {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new CanaryManifestRefusal(message: $"{context} present must be true or false; omission alone defaults mechanically to true."),
            };
        }

        return new CanaryLineAssertion(Match: match, Name: name, Present: present, Stream: stream, Text: text);
    }

    private static CanaryResponseAssertion ReadResponseAssertion(JsonElement element, string context, HashSet<string> values) {
        RequireOnlyMembers(element: element, context: context, "count", "extract", "name", "occurrence", "stream", "type", "verb");

        var name = ReadRequiredString(element: element, member: "name", context: context);
        var stream = ReadStream(value: ReadRequiredString(element: element, member: "stream", context: context), context: context);
        var selector = ReadSelector(element: element, context: context);
        var extractions = new List<CanaryValueExtraction>();

        if (element.TryGetProperty(propertyName: "extract", value: out var extractionElement)) {
            if (extractionElement.ValueKind != JsonValueKind.Array) {
                throw new CanaryManifestRefusal(message: $"{context} extract must be an array.");
            }

            for (var index = 0; (index < extractionElement.GetArrayLength()); index++) {
                var row = RequireObject(element: extractionElement[index], context: $"{context} extract[{index}]");

                RequireOnlyMembers(element: row, context: $"{context} extract[{index}]", "component", "field", "name");

                var valueName = ReadRequiredString(element: row, member: "name", context: $"{context} extract[{index}]");
                var field = ReadRequiredString(element: row, member: "field", context: $"{context} extract[{index}]");
                int? component = null;

                if (row.TryGetProperty(propertyName: "component", value: out _)) {
                    component = ReadInteger(element: row, member: "component", context: $"{context} extract[{index}]");
                    if (component < 0) {
                        throw new CanaryManifestRefusal(message: $"{context} extract[{index}] component must be zero or greater.");
                    }
                }

                if (!values.Add(item: valueName)) {
                    throw new CanaryManifestRefusal(message: $"{context} repeats extracted value name '{valueName}'.");
                }

                extractions.Add(item: new CanaryValueExtraction(Field: field, Component: component, Name: valueName));
            }
        }

        return new CanaryResponseAssertion(
            Count: selector.Count,
            Extractions: extractions,
            Name: name,
            Occurrence: selector.Occurrence,
            Stream: stream,
            Verb: selector.Verb
        );
    }

    private static CanarySequenceAssertion ReadSequenceAssertion(JsonElement element, string context) {
        RequireOnlyMembers(element: element, context: context, "name", "responses", "stream", "type");

        var name = ReadRequiredString(element: element, member: "name", context: context);
        var stream = ReadStream(value: ReadRequiredString(element: element, member: "stream", context: context), context: context);
        var rows = ReadRequiredArray(element: element, member: "responses", context: context);
        if (rows.GetArrayLength() == 0) {
            throw new CanaryManifestRefusal(message: $"{context} responses is empty; an empty order assertion is vacuous.");
        }

        var responses = new List<CanaryResponseSelector>(capacity: rows.GetArrayLength());
        for (var index = 0; (index < rows.GetArrayLength()); index++) {
            var row = RequireObject(element: rows[index], context: $"{context} responses[{index}]");

            RequireOnlyMembers(element: row, context: $"{context} responses[{index}]", "count", "occurrence", "verb");
            responses.Add(item: ReadSelector(element: row, context: $"{context} responses[{index}]"));
        }

        return new CanarySequenceAssertion(Name: name, Responses: responses, Stream: stream);
    }

    private static CanaryRelationAssertion ReadRelationAssertion(JsonElement element, string context, HashSet<string> values) {
        RequireOnlyMembers(element: element, context: context, "left", "margin", "maximum", "minimum", "name", "operator", "right", "type");

        var name = ReadRequiredString(element: element, member: "name", context: context);
        var left = ReadOperand(element: ReadRequiredObject(element: element, member: "left", context: context), context: $"{context} left");
        var operatorText = ReadRequiredString(element: element, member: "operator", context: context);
        var relationOperator = operatorText switch {
            "equal" => CanaryRelationOperator.Equal,
            "notEqual" => CanaryRelationOperator.NotEqual,
            "betweenInclusive" => CanaryRelationOperator.BetweenInclusive,
            "atLeast" => CanaryRelationOperator.AtLeast,
            "atMost" => CanaryRelationOperator.AtMost,
            "minimumMargin" => CanaryRelationOperator.MinimumMargin,
            _ => throw new CanaryManifestRefusal(message: $"{context} operator '{operatorText}' is invalid; relation tokens are exact and casing is significant."),
        };

        CanaryOperand? right = null;
        double? minimum = null;
        double? maximum = null;
        double? margin = null;

        switch (relationOperator) {
            case CanaryRelationOperator.Equal:
            case CanaryRelationOperator.NotEqual:
                right = ReadOperand(element: ReadRequiredObject(element: element, member: "right", context: context), context: $"{context} right");
                break;
            case CanaryRelationOperator.BetweenInclusive:
                minimum = ReadFiniteNumber(element: element, member: "minimum", context: context);
                maximum = ReadFiniteNumber(element: element, member: "maximum", context: context);
                if (minimum > maximum) {
                    throw new CanaryManifestRefusal(message: $"{context} minimum is greater than maximum.");
                }
                break;
            case CanaryRelationOperator.AtLeast:
                minimum = ReadFiniteNumber(element: element, member: "minimum", context: context);
                break;
            case CanaryRelationOperator.AtMost:
                maximum = ReadFiniteNumber(element: element, member: "maximum", context: context);
                break;
            case CanaryRelationOperator.MinimumMargin:
                right = ReadOperand(element: ReadRequiredObject(element: element, member: "right", context: context), context: $"{context} right");
                margin = ReadFiniteNumber(element: element, member: "margin", context: context);
                if (margin < 0) {
                    throw new CanaryManifestRefusal(message: $"{context} margin must be zero or greater.");
                }
                break;
        }

        _ = values;

        return new CanaryRelationAssertion(
            Left: left,
            Margin: margin,
            Maximum: maximum,
            Minimum: minimum,
            Name: name,
            Operator: relationOperator,
            Right: right
        );
    }

    private static CanaryOperand ReadOperand(JsonElement element, string context) {
        RequireOnlyMembers(element: element, context: context, "literal", "value");

        var hasValue = element.TryGetProperty(propertyName: "value", value: out var valueElement);
        var hasLiteral = element.TryGetProperty(propertyName: "literal", value: out var literalElement);
        if (hasValue == hasLiteral) {
            throw new CanaryManifestRefusal(message: $"{context} must name exactly one of value or literal.");
        }

        if (hasValue) {
            if ((valueElement.ValueKind != JsonValueKind.String) || string.IsNullOrWhiteSpace(value: valueElement.GetString())) {
                throw new CanaryManifestRefusal(message: $"{context} value must be a non-blank extracted name.");
            }

            return new CanaryOperand(ValueName: valueElement.GetString(), StringLiteral: null, NumberLiteral: null);
        }

        return literalElement.ValueKind switch {
            JsonValueKind.String => new CanaryOperand(ValueName: null, StringLiteral: literalElement.GetString(), NumberLiteral: null),
            JsonValueKind.Number when literalElement.TryGetDouble(value: out var number) && double.IsFinite(d: number) => new CanaryOperand(ValueName: null, StringLiteral: null, NumberLiteral: number),
            _ => throw new CanaryManifestRefusal(message: $"{context} literal must be a finite number or a string."),
        };
    }

    private static CanaryResponseSelector ReadSelector(JsonElement element, string context) {
        var verb = ReadRequiredString(element: element, member: "verb", context: context);
        if (!IsSafeToken(value: verb, allowColon: false, allowDot: true)) {
            throw new CanaryManifestRefusal(message: $"{context} verb '{verb}' is not a lower-case dotted token.");
        }

        var occurrence = ReadInteger(element: element, member: "occurrence", context: context);
        var count = ReadInteger(element: element, member: "count", context: context);
        if ((occurrence <= 0) || (count <= 0) || (occurrence > count)) {
            throw new CanaryManifestRefusal(message: $"{context} requires 1 <= occurrence <= count; the selected response must exist and cardinality must be exact.");
        }

        return new CanaryResponseSelector(Verb: verb, Occurrence: occurrence, Count: count);
    }

    private static IReadOnlyList<string> ReadRequirements(JsonElement element, string id) {
        var array = ReadRequiredArray(element: element, member: "requirements", context: $"canary '{id}'");
        var requirements = new List<string>(capacity: array.GetArrayLength());
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < array.GetArrayLength()); index++) {
            var item = array[index];
            if ((item.ValueKind != JsonValueKind.String) || string.IsNullOrWhiteSpace(value: item.GetString())) {
                throw new CanaryManifestRefusal(message: $"canary '{id}' requirements[{index}] must be a non-blank capability token.");
            }

            var value = item.GetString()!;
            var valid = (value is "gpu" or "audio-output") || (value.StartsWith(value: "input:", comparisonType: StringComparison.Ordinal) && IsSafeToken(value: value[6..], allowColon: false, allowDot: false));
            if (!valid) {
                throw new CanaryManifestRefusal(message: $"canary '{id}' requirement '{value}' is invalid; use 'gpu', 'audio-output', or 'input:<lower-case-hardware-name>'.");
            }

            if (!seen.Add(item: value)) {
                throw new CanaryManifestRefusal(message: $"canary '{id}' repeats environmental requirement '{value}'.");
            }

            requirements.Add(item: value);
        }

        return requirements;
    }

    private static IReadOnlyList<string> ReadFixtures(JsonElement element, string id, string repositoryRoot, string canaryDirectory) {
        if (!element.TryGetProperty(propertyName: "fixtures", value: out var fixturesElement)) {
            return [];
        }

        if (fixturesElement.ValueKind != JsonValueKind.Array) {
            throw new CanaryManifestRefusal(message: $"canary '{id}' fixtures must be an array.");
        }

        var fixtures = new List<string>(capacity: fixturesElement.GetArrayLength());
        var seen = new HashSet<string>(comparer: PathComparer());
        for (var index = 0; (index < fixturesElement.GetArrayLength()); index++) {
            var item = fixturesElement[index];
            if ((item.ValueKind != JsonValueKind.String) || string.IsNullOrWhiteSpace(value: item.GetString())) {
                throw new CanaryManifestRefusal(message: $"canary '{id}' fixtures[{index}] must be a non-blank path.");
            }

            var path = ResolveFile(rawPath: item.GetString()!, basePath: canaryDirectory, containmentRoot: repositoryRoot, context: $"canary '{id}' fixtures[{index}]");
            if (!seen.Add(item: path)) {
                throw new CanaryManifestRefusal(message: $"canary '{id}' repeats fixture '{item.GetString()}'.");
            }

            fixtures.Add(item: path);
        }

        return fixtures;
    }

    private static void RefuseUnexpectedFiles(
        string canaryDirectory,
        string manifestPath,
        CanaryLeg positive,
        CanaryLeg discriminating,
        IReadOnlyList<string> fixtures,
        string repositoryRoot,
        string id
    ) {
        var childDirectories = Directory.GetDirectories(path: canaryDirectory, searchPattern: "*", searchOption: SearchOption.AllDirectories);
        if (childDirectories.Length != 0) {
            throw new CanaryManifestRefusal(message: $"canary '{id}' contains unexpected directory '{CliPaths.ToDisplay(relativeTo: repositoryRoot, fullPath: childDirectories.Order(comparer: StringComparer.Ordinal).First())}'; its rigid layout contains files only.");
        }

        var expected = new HashSet<string>(comparer: PathComparer()) {
            Path.GetFullPath(path: manifestPath),
            positive.ScriptPath,
            discriminating.ScriptPath,
        };

        if (IsWithin(root: canaryDirectory, path: positive.WorldPath)) {
            expected.Add(item: positive.WorldPath);
        }
        if (IsWithin(root: canaryDirectory, path: discriminating.WorldPath)) {
            expected.Add(item: discriminating.WorldPath);
        }
        foreach (var fixture in fixtures) {
            expected.Add(item: fixture);
        }

        foreach (var file in Directory.GetFiles(path: canaryDirectory, searchPattern: "*", searchOption: SearchOption.TopDirectoryOnly).Order(comparer: StringComparer.Ordinal)) {
            var fullPath = Path.GetFullPath(path: file);
            if (!expected.Contains(item: fullPath)) {
                throw new CanaryManifestRefusal(message: $"canary '{id}' contains orphan file '{Path.GetFileName(path: file)}'; every file in a canary directory must be the manifest, a selected world/script, or a declared fixture.");
            }
        }
    }

    private static string ResolveFile(string rawPath, string basePath, string containmentRoot, string context) {
        if (Path.IsPathRooted(path: rawPath) || ContainsParentSegment(path: rawPath)) {
            throw new CanaryManifestRefusal(message: $"{context} path '{rawPath}' is not a contained relative path; rooted paths and '..' escapes are refused.");
        }

        string fullPath;
        try {
            fullPath = Path.GetFullPath(path: Path.Combine(path1: basePath, path2: rawPath));
        } catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) {
            throw new CanaryManifestRefusal(message: $"{context} path '{rawPath}' is invalid ({exception.Message.ReplaceLineEndings(replacementText: " ")}).");
        }

        if (!IsWithin(root: containmentRoot, path: fullPath)) {
            throw new CanaryManifestRefusal(message: $"{context} path '{rawPath}' escapes the repository tree.");
        }
        if (!File.Exists(path: fullPath)) {
            throw new CanaryManifestRefusal(message: $"{context} file '{rawPath}' does not exist.");
        }
        if (ContainsReparsePoint(path: fullPath, containmentRoot: containmentRoot)) {
            throw new CanaryManifestRefusal(message: $"{context} path '{rawPath}' crosses a link or reparse point; lexical containment would not prove where it reads.");
        }

        return fullPath;
    }

    private static bool ContainsReparsePoint(string path, string containmentRoot) {
        for (var current = new FileInfo(fileName: path).Directory; (current is not null) && IsWithin(root: containmentRoot, path: current.FullName); current = current.Parent) {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) {
                return true;
            }
        }

        return ((File.GetAttributes(path: path) & FileAttributes.ReparsePoint) != 0);
    }

    private static void RequireKnownOperand(CanaryOperand operand, HashSet<string> values, string context) {
        if ((operand.ValueName is { } valueName) && !values.Contains(item: valueName)) {
            throw new CanaryManifestRefusal(message: $"{context} names unknown extracted value '{valueName}'.");
        }
    }

    private static CanaryBootShape ReadBootShape(string value, string id) => value switch {
        "headless" => CanaryBootShape.Headless,
        "windowed" => CanaryBootShape.Windowed,
        _ => throw new CanaryManifestRefusal(message: $"canary '{id}' bootShape '{value}' is invalid; use exactly 'headless' or 'windowed' (casing is significant)."),
    };

    private static CanaryStream ReadStream(string value, string context) => value switch {
        "stdout" => CanaryStream.Stdout,
        "stderr" => CanaryStream.Stderr,
        _ => throw new CanaryManifestRefusal(message: $"{context} stream '{value}' is invalid; use exactly 'stdout' or 'stderr' (casing is significant)."),
    };

    private static int ReadInteger(JsonElement element, string member, string context) {
        if (!element.TryGetProperty(propertyName: member, value: out var value) || (value.ValueKind != JsonValueKind.Number) || !value.TryGetInt32(value: out var result)) {
            throw new CanaryManifestRefusal(message: $"{context} {member} must be a finite in-range integer.");
        }

        return result;
    }

    private static double ReadFiniteNumber(JsonElement element, string member, string context) {
        if (!element.TryGetProperty(propertyName: member, value: out var value) || (value.ValueKind != JsonValueKind.Number) || !value.TryGetDouble(value: out var result) || !double.IsFinite(d: result)) {
            throw new CanaryManifestRefusal(message: $"{context} {member} must be a finite in-range number.");
        }

        return result;
    }

    private static string ReadRequiredString(JsonElement element, string member, string context) {
        if (!element.TryGetProperty(propertyName: member, value: out var value) || (value.ValueKind != JsonValueKind.String) || string.IsNullOrWhiteSpace(value: value.GetString())) {
            throw new CanaryManifestRefusal(message: $"{context} {member} is required and must be non-blank.");
        }

        return value.GetString()!;
    }

    private static JsonElement ReadRequiredArray(JsonElement element, string member, string context) {
        if (!element.TryGetProperty(propertyName: member, value: out var value) || (value.ValueKind != JsonValueKind.Array)) {
            throw new CanaryManifestRefusal(message: $"{context} {member} is required and must be an array.");
        }

        return value;
    }

    private static JsonElement ReadRequiredObject(JsonElement element, string member, string context) {
        if (!element.TryGetProperty(propertyName: member, value: out var value)) {
            throw new CanaryManifestRefusal(message: $"{context} {member} is required and must be an object.");
        }

        return RequireObject(element: value, context: $"{context} {member}");
    }

    private static JsonElement RequireObject(JsonElement element, string context) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new CanaryManifestRefusal(message: $"{context} must be an object.");
        }

        return element;
    }

    private static void RequireOnlyMembers(JsonElement element, string context, params string[] allowed) {
        var names = new HashSet<string>(collection: allowed, comparer: StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) {
            if (!names.Contains(item: property.Name)) {
                throw new CanaryManifestRefusal(message: $"{context} contains unknown member '{property.Name}'; strict manifests refuse fields the runner does not read.");
            }
        }
    }

    private static bool TryFindDuplicateMember(JsonElement element, string path, out string duplicate) {
        if (element.ValueKind == JsonValueKind.Object) {
            var names = new HashSet<string>(comparer: StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject()) {
                if (!names.Add(item: property.Name)) {
                    duplicate = $"{path}.{property.Name}";

                    return true;
                }
                if (TryFindDuplicateMember(element: property.Value, path: $"{path}.{property.Name}", duplicate: out duplicate)) {
                    return true;
                }
            }
        } else if (element.ValueKind == JsonValueKind.Array) {
            var index = 0;
            foreach (var item in element.EnumerateArray()) {
                if (TryFindDuplicateMember(element: item, path: $"{path}[{index}]", duplicate: out duplicate)) {
                    return true;
                }
                index++;
            }
        }

        duplicate = string.Empty;

        return false;
    }

    private static bool ContainsParentSegment(string path) =>
        path.Split(separator: ['/', '\\'], options: StringSplitOptions.RemoveEmptyEntries).Any(predicate: static segment => (segment == ".."));

    private static bool IsSafeToken(string value, bool allowColon, bool allowDot) {
        if ((value.Length == 0) || (value[0] == '-') || (value[^1] == '-') || value.Contains(value: "--", comparisonType: StringComparison.Ordinal)) {
            return false;
        }

        foreach (var character in value) {
            if (((character >= 'a') && (character <= 'z')) || char.IsAsciiDigit(character) || (character == '-') || (allowDot && (character == '.')) || (allowColon && (character == ':'))) {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool IsWithin(string root, string path) {
        var relative = Path.GetRelativePath(relativeTo: Path.GetFullPath(path: root), path: Path.GetFullPath(path: path));

        return !Path.IsPathRooted(path: relative) && (relative != "..") && !relative.StartsWith(value: $"..{Path.DirectorySeparatorChar}", comparisonType: StringComparison.Ordinal);
    }

    private static bool PathsEqual(string left, string right) => PathComparer().Equals(x: left, y: right);

    private static StringComparer PathComparer() => (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private sealed class CanaryManifestRefusal(string message) : Exception(message: message);
}
