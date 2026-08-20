using System.Text.Json;

namespace Puck.Cli.Canary;

internal static class CanaryManifestLoader {
    private const int MaximumExitSeconds = 30;
    private const int MaximumFederatedExitSeconds = 90;
    private const int MaximumFederatedLegTimeoutSeconds = 240;
    private const int MaximumLegTimeoutSeconds = 60;

    public static bool TryLoadAll(string repositoryRoot, out IReadOnlyList<CanaryManifest> manifests, out string error) {
        var canaryRoot = Path.Combine(path1: repositoryRoot, path2: "tests", path3: "Puck.World.Canaries");
        var loaded = new List<CanaryManifest>();

        manifests = loaded;
        error = string.Empty;

        if (!Directory.Exists(path: canaryRoot)) {
            error = $"canary discovery found no manifest directory at {CliPaths.ToDisplay(fullPath: canaryRoot, relativeTo: repositoryRoot)}; an empty suite cannot prove anything.";

            return false;
        }

        var rootFiles = Directory.GetFiles(path: canaryRoot, searchOption: SearchOption.TopDirectoryOnly, searchPattern: "*");

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

            if (!TryLoadManifest(directory: directory, error: out error, manifest: out var manifest, manifestPath: manifestPath, repositoryRoot: repositoryRoot)) {
                return false;
            }

            if (!ids.Add(item: manifest.Id)) {
                error = $"duplicate canary id '{manifest.Id}'; selection would not identify one proof.";

                return false;
            }

            var directoryName = Path.GetFileName(path: directory);

            if (!string.Equals(a: manifest.Id, b: directoryName, comparisonType: StringComparison.Ordinal)) {
                error = $"manifest '{CliPaths.ToDisplay(fullPath: manifestPath, relativeTo: repositoryRoot)}' refused: id '{manifest.Id}' does not match its directory name '{directoryName}'; discovery identity must come from one place.";

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

            var id = ReadRequiredString(context: "manifest root", element: root, member: "id");

            if (!IsSafeToken(allowColon: false, allowDot: false, value: id)) {
                throw new CanaryManifestRefusal(message: $"id '{id}' is not a safe lower-case token; use letters, digits, and single interior hyphens only.");
            }

            var title = ReadRequiredString(context: $"canary '{id}'", element: root, member: "title");
            var binding = ReadRequiredString(context: $"canary '{id}'", element: root, member: "binding");
            var bootShape = ReadBootShape(value: ReadRequiredString(context: $"canary '{id}'", element: root, member: "bootShape"), id: id);
            var requirements = ReadRequirements(element: root, id: id);
            var seconds = ReadInteger(context: $"canary '{id}'", element: root, member: "seconds");
            var timeoutSeconds = ReadInteger(context: $"canary '{id}'", element: root, member: "timeoutSeconds");

            var positive = ReadLeg(
                element: ReadRequiredObject(context: $"canary '{id}'", element: root, member: "positive"),
                id: id,
                name: "positive",
                repositoryRoot: repositoryRoot,
                canaryDirectory: directory
            );
            var discriminating = ReadLeg(
                element: ReadRequiredObject(context: $"canary '{id}'", element: root, member: "discriminating"),
                id: id,
                name: "discriminating",
                repositoryRoot: repositoryRoot,
                canaryDirectory: directory
            );
            // A federated mesh leg spawns several concurrently-launched Puck.World processes on a shared machine
            // rather than one — real spawn/handshake variance earns it a wider ceiling than the single-process budget.
            var isFederated = ((positive.Authorities.Count != 0) || (discriminating.Authorities.Count != 0));
            var maximumExitSeconds = (isFederated ? MaximumFederatedExitSeconds : MaximumExitSeconds);
            var maximumLegTimeoutSeconds = (isFederated ? MaximumFederatedLegTimeoutSeconds : MaximumLegTimeoutSeconds);

            if ((seconds <= 0) || (seconds > maximumExitSeconds)) {
                throw new CanaryManifestRefusal(message: $"canary '{id}' seconds must be in 1..{maximumExitSeconds}; an automatic proof must end on its own and stay cheap.");
            }

            if ((timeoutSeconds <= seconds) || (timeoutSeconds > maximumLegTimeoutSeconds)) {
                throw new CanaryManifestRefusal(message: $"canary '{id}' timeoutSeconds must be greater than seconds and at most {maximumLegTimeoutSeconds}; it must distinguish a hang without making the gate unbounded.");
            }

            if (PathsEqual(left: positive.WorldPath, right: discriminating.WorldPath) && PathsEqual(left: positive.ScriptPath, right: discriminating.ScriptPath)) {
                throw new CanaryManifestRefusal(message: $"canary '{id}' discriminating leg changes neither world nor script; prose alone cannot make the positive observation turn red.");
            }

            var fixtures = ReadFixtures(canaryDirectory: directory, element: root, id: id, repositoryRoot: repositoryRoot);

            RefuseUnexpectedFiles(
                canaryDirectory: directory,
                discriminating: discriminating,
                fixtures: fixtures,
                id: id,
                manifestPath: manifestPath,
                positive: positive,
                repositoryRoot: repositoryRoot
            );

            manifest = new CanaryManifest(
                Binding: binding,
                BootShape: bootShape,
                DirectoryPath: directory,
                Discriminating: discriminating,
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
            error = $"manifest '{CliPaths.ToDisplay(fullPath: manifestPath, relativeTo: repositoryRoot)}' refused: {refusal.Message}";

            return false;
        } catch (JsonException exception) {
            error = $"manifest '{CliPaths.ToDisplay(fullPath: manifestPath, relativeTo: repositoryRoot)}' refused: invalid JSON ({exception.Message.ReplaceLineEndings(replacementText: " ")}).";

            return false;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            error = $"manifest '{CliPaths.ToDisplay(fullPath: manifestPath, relativeTo: repositoryRoot)}' could not be read: {exception.Message.ReplaceLineEndings(replacementText: " ")}.";

            return false;
        }
    }
    private static CanaryLeg ReadLeg(JsonElement element, string id, string name, string repositoryRoot, string canaryDirectory) {
        var context = $"canary '{id}' {name} leg";

        RequireOnlyMembers(element: element, context: context, "authorities", "authorityWorld", "commands", "connect", "expect", "script", "world");

        var worldText = ReadRequiredString(context: context, element: element, member: "world");
        var scriptText = ReadRequiredString(context: context, element: element, member: "script");
        var worldPath = ResolveFile(basePath: repositoryRoot, containmentRoot: repositoryRoot, context: $"{context} world", rawPath: worldText);
        var scriptPath = ResolveFile(basePath: canaryDirectory, containmentRoot: repositoryRoot, context: $"{context} script", rawPath: scriptText);
        string? authorityWorldPath = null;
        var connect = false;

        if (element.TryGetProperty(propertyName: "authorityWorld", value: out var authorityElement)) {
            if ((authorityElement.ValueKind != JsonValueKind.String) || string.IsNullOrWhiteSpace(value: authorityElement.GetString())) {
                throw new CanaryManifestRefusal(message: $"{context} authorityWorld must be a non-empty path string.");
            }

            authorityWorldPath = ResolveFile(rawPath: authorityElement.GetString()!, basePath: repositoryRoot, containmentRoot: repositoryRoot, context: $"{context} authorityWorld");
        }
        if (element.TryGetProperty(propertyName: "connect", value: out var connectElement)) {
            connect = connectElement.ValueKind switch {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new CanaryManifestRefusal(message: $"{context} connect must be true or false."),
            };
        }
        if (connect && (authorityWorldPath is null)) {
            throw new CanaryManifestRefusal(message: $"{context} connect requires authorityWorld so the runner owns the endpoint it dials.");
        }

        var authorities = ReadAuthorities(canaryDirectory: canaryDirectory, context: context, element: element, legScriptPath: scriptPath, legWorldPath: worldPath, repositoryRoot: repositoryRoot);

        if ((authorities.Count != 0) && (authorityWorldPath is not null)) {
            throw new CanaryManifestRefusal(message: $"{context} authorities and authorityWorld are mutually exclusive transport shapes.");
        }

        var commands = ReadCommands(context: context, element: element, scriptPath: scriptPath);
        var assertions = ReadAssertions(authorityIds: authorities.Select(selector: static role => role.Id).ToHashSet(comparer: StringComparer.Ordinal), context: context, element: element);

        return new CanaryLeg(Assertions: assertions, Authorities: authorities, AuthorityWorldPath: authorityWorldPath, Commands: commands, Connect: connect, Name: name, ScriptPath: scriptPath, WorldPath: worldPath);
    }
    // authorities[] replaces the singular authorityWorld/connect pair with an N-ary listener mesh: every entry
    // binds its own real endpoint, none dials out, and neighbours resolve each other symbolically (the quilt
    // adjacency/references mechanism), never through a runner-owned --connect argument.
    private static IReadOnlyList<CanaryAuthorityRole> ReadAuthorities(JsonElement element, string context, string repositoryRoot, string canaryDirectory, string legWorldPath, string legScriptPath) {
        if (!element.TryGetProperty(propertyName: "authorities", value: out var authoritiesElement)) {
            return [];
        }
        if (authoritiesElement.ValueKind != JsonValueKind.Array) {
            throw new CanaryManifestRefusal(message: $"{context} authorities must be an array.");
        }

        var count = authoritiesElement.GetArrayLength();

        if (count < 2) {
            throw new CanaryManifestRefusal(message: $"{context} authorities must name at least two listeners; a single authority is the existing single-process shape.");
        }

        var roles = new List<CanaryAuthorityRole>(capacity: count);
        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < count); index++) {
            var rowContext = $"{context} authorities[{index}]";
            var row = RequireObject(context: rowContext, element: authoritiesElement[index]);

            RequireOnlyMembers(element: row, context: rowContext, "id", "script", "world");

            var authorityId = ReadRequiredString(context: rowContext, element: row, member: "id");

            if (!IsSafeToken(allowColon: false, allowDot: false, value: authorityId)) {
                throw new CanaryManifestRefusal(message: $"{rowContext} id '{authorityId}' is not a safe lower-case token; use letters, digits, and single interior hyphens only.");
            }
            if (!ids.Add(item: authorityId)) {
                throw new CanaryManifestRefusal(message: $"{rowContext} repeats authority id '{authorityId}'.");
            }

            var authorityWorldText = ReadRequiredString(context: rowContext, element: row, member: "world");
            var authorityScriptText = ReadRequiredString(context: rowContext, element: row, member: "script");
            var authorityWorldPath = ResolveFile(basePath: repositoryRoot, containmentRoot: repositoryRoot, context: $"{rowContext} world", rawPath: authorityWorldText);
            var authorityScriptPath = ResolveFile(basePath: canaryDirectory, containmentRoot: repositoryRoot, context: $"{rowContext} script", rawPath: authorityScriptText);

            roles.Add(item: new CanaryAuthorityRole(Id: authorityId, ScriptPath: authorityScriptPath, WorldPath: authorityWorldPath));
        }

        if (!roles.Any(predicate: role => (PathsEqual(left: role.WorldPath, right: legWorldPath) && PathsEqual(left: role.ScriptPath, right: legScriptPath)))) {
            throw new CanaryManifestRefusal(message: $"{context} authorities must include an entry whose world and script equal this leg's own — the entry unscoped assertions read by default.");
        }

        return roles;
    }
    private static IReadOnlyList<CanaryCommandClaim> ReadCommands(JsonElement element, string context, string scriptPath) {
        var array = ReadRequiredArray(context: context, element: element, member: "commands");
        var claims = new List<CanaryCommandClaim>(capacity: array.GetArrayLength());

        for (var index = 0; (index < array.GetArrayLength()); index++) {
            var item = array[index];

            if (item.ValueKind == JsonValueKind.Null) {
                throw new CanaryManifestRefusal(message: $"{context} commands[{index}] is null; every script command needs an outcome.");
            }

            var row = RequireObject(context: $"{context} commands[{index}]", element: item);

            RequireOnlyMembers(element: row, context: $"{context} commands[{index}]", "occurrence", "outcome", "stream", "verb");

            var verb = ReadRequiredString(context: $"{context} commands[{index}]", element: row, member: "verb");

            if (!IsSafeToken(allowColon: false, allowDot: true, value: verb)) {
                throw new CanaryManifestRefusal(message: $"{context} command verb '{verb}' is not a lower-case dotted token.");
            }

            var occurrence = ReadInteger(context: $"{context} commands[{index}]", element: row, member: "occurrence");

            if (occurrence <= 0) {
                throw new CanaryManifestRefusal(message: $"{context} command '{verb}' occurrence must be positive.");
            }

            var outcomeText = ReadRequiredString(context: $"{context} commands[{index}]", element: row, member: "outcome");
            var outcome = outcomeText switch {
                "accepted" => CanaryCommandOutcome.Accepted,
                "refused" => CanaryCommandOutcome.Refused,
                _ => throw new CanaryManifestRefusal(message: $"{context} command '{verb}' outcome '{outcomeText}' is invalid; use exactly 'accepted' or 'refused' (casing is significant)."),
            };
            CanaryStream? streamOverride = null;

            // Server narration (a mutation's own echo — [world.grant: …], [world.revoke: …], …) always lands on
            // stderr, accepted or not, unlike an ordinary accepted command's stdout read-back. stream lets a claim
            // declare that exception explicitly instead of forcing the accepted-implies-stdout default.
            if (row.TryGetProperty(propertyName: "stream", value: out var streamElement)) {
                if (outcome != CanaryCommandOutcome.Accepted) {
                    throw new CanaryManifestRefusal(message: $"{context} command '{verb}' stream override applies only to an accepted outcome; a refused command's response is always stderr by protocol.");
                }

                var streamContext = $"{context} commands[{index}]";
                var streamText = (((streamElement.ValueKind == JsonValueKind.String) ? streamElement.GetString() : null)
                    ?? throw new CanaryManifestRefusal(message: $"{streamContext} stream must be a non-blank string."));

                streamOverride = ReadStream(context: streamContext, value: streamText);

                if (streamOverride == CanaryStream.Stdout) {
                    throw new CanaryManifestRefusal(message: $"{context} command '{verb}' stream override to stdout is redundant; an accepted outcome already expects stdout by default.");
                }
            }

            claims.Add(item: new CanaryCommandClaim(Occurrence: occurrence, Outcome: outcome, StreamOverride: streamOverride, Verb: verb));
        }

        var submitted = ReadScriptCommands(context: context, scriptPath: scriptPath);

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

            if (!IsSafeToken(allowColon: false, allowDot: true, value: verb)) {
                throw new CanaryManifestRefusal(message: $"{context} script command verb '{verb}' is not a lower-case dotted token.");
            }

            occurrences.TryGetValue(key: verb, value: out var occurrence);
            occurrence++;
            occurrences[verb] = occurrence;
            commands.Add(item: (verb, occurrence));
        }

        return commands;
    }
    private static IReadOnlyList<CanaryAssertion> ReadAssertions(JsonElement element, string context, IReadOnlySet<string> authorityIds) {
        var array = ReadRequiredArray(context: context, element: element, member: "expect");

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

            var row = RequireObject(context: $"{context} expect[{index}]", element: item);
            var type = ReadRequiredString(context: $"{context} expect[{index}]", element: row, member: "type");
            CanaryAssertion assertion = type switch {
                "line" => ReadLineAssertion(authorityIds: authorityIds, context: $"{context} expect[{index}]", element: row),
                "response" => ReadResponseAssertion(authorityIds: authorityIds, context: $"{context} expect[{index}]", element: row, values: values),
                "sequence" => ReadSequenceAssertion(authorityIds: authorityIds, context: $"{context} expect[{index}]", element: row),
                "relation" => ReadRelationAssertion(context: $"{context} expect[{index}]", element: row, values: values),
                "filesDiffer" => ReadFileDifferenceAssertion(context: $"{context} expect[{index}]", element: row),
                "framesAgree" => ReadFrameAgreementAssertion(context: $"{context} expect[{index}]", element: row),
                _ => throw new CanaryManifestRefusal(message: $"{context} expect[{index}] type '{type}' is invalid; use exactly 'line', 'response', 'sequence', 'relation', 'filesDiffer', or 'framesAgree' (casing is significant)."),
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
    private static CanaryFileDifferenceAssertion ReadFileDifferenceAssertion(JsonElement element, string context) {
        RequireOnlyMembers(element: element, context: context, "after", "before", "different", "name", "type");

        var name = ReadRequiredString(context: context, element: element, member: "name");
        var before = ReadRunRelativePath(context: context, element: element, member: "before");
        var after = ReadRunRelativePath(context: context, element: element, member: "after");
        var different = true;

        if (element.TryGetProperty(propertyName: "different", value: out var differentElement)) {
            different = differentElement.ValueKind switch {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new CanaryManifestRefusal(message: $"{context} different must be true or false."),
            };
        }

        if (string.Equals(a: before, b: after, comparisonType: StringComparison.Ordinal)) {
            throw new CanaryManifestRefusal(message: $"{context} before and after must name two distinct files.");
        }

        return new CanaryFileDifferenceAssertion(After: after, Before: before, Different: different, Name: name);
    }
    private static CanaryFrameAgreementAssertion ReadFrameAgreementAssertion(JsonElement element, string context) {
        RequireOnlyMembers(element: element, context: context, "after", "agree", "before", "name", "type");

        var name = ReadRequiredString(context: context, element: element, member: "name");
        var before = ReadRunRelativePath(context: context, element: element, member: "before");
        var after = ReadRunRelativePath(context: context, element: element, member: "after");

        // Stated, never defaulted: the two directions are opposite proofs, and a silently-defaulted one would read as
        // the assertion the author did not write.
        if (!element.TryGetProperty(propertyName: "agree", value: out var agreeElement)) {
            throw new CanaryManifestRefusal(message: $"{context} framesAgree must state agree explicitly as true or false.");
        }

        var agree = agreeElement.ValueKind switch {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new CanaryManifestRefusal(message: $"{context} agree must be true or false."),
        };

        if (string.Equals(a: before, b: after, comparisonType: StringComparison.Ordinal)) {
            throw new CanaryManifestRefusal(message: $"{context} before and after must name two distinct files.");
        }

        return new CanaryFrameAgreementAssertion(After: after, Agree: agree, Before: before, Name: name);
    }
    private static string ReadRunRelativePath(JsonElement element, string member, string context) {
        var value = ReadRequiredString(context: context, element: element, member: member);

        if (Path.IsPathRooted(path: value) || ContainsParentSegment(path: value) || value.Contains(value: '{') || value.Contains(value: '}')) {
            throw new CanaryManifestRefusal(message: $"{context} {member} '{value}' must be a run-directory-relative path without parent segments or tokens.");
        }

        return value;
    }
    private static CanaryLineAssertion ReadLineAssertion(JsonElement element, string context, IReadOnlySet<string> authorityIds) {
        RequireOnlyMembers(element: element, context: context, "authority", "match", "name", "present", "stream", "text", "type");

        var name = ReadRequiredString(context: context, element: element, member: "name");
        var authority = ReadAssertionAuthority(authorityIds: authorityIds, context: context, element: element);
        var stream = ReadStream(value: ReadRequiredString(context: context, element: element, member: "stream"), context: context);
        var matchText = ReadRequiredString(context: context, element: element, member: "match");
        var match = matchText switch {
            "exact" => CanaryLineMatch.Exact,
            "contains" => CanaryLineMatch.Contains,
            _ => throw new CanaryManifestRefusal(message: $"{context} match '{matchText}' is invalid; use exactly 'exact' or 'contains' (casing is significant)."),
        };
        var text = ReadRequiredString(context: context, element: element, member: "text");
        var present = true;

        if (element.TryGetProperty(propertyName: "present", value: out var presentElement)) {
            present = presentElement.ValueKind switch {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new CanaryManifestRefusal(message: $"{context} present must be true or false; omission alone defaults mechanically to true."),
            };
        }

        return new CanaryLineAssertion(Authority: authority, Match: match, Name: name, Present: present, Stream: stream, Text: text);
    }
    // Absent (null) reads the leg's own default transcript — the entry whose world/script equal the leg's, or the
    // sole transcript for a non-federated leg. A leg with no authorities[] cannot name one at all.
    private static string? ReadAssertionAuthority(JsonElement element, string context, IReadOnlySet<string> authorityIds) {
        if (!element.TryGetProperty(propertyName: "authority", value: out var authorityElement)) {
            return null;
        }
        if ((authorityElement.ValueKind != JsonValueKind.String) || string.IsNullOrWhiteSpace(value: authorityElement.GetString())) {
            throw new CanaryManifestRefusal(message: $"{context} authority must be a non-blank authority id.");
        }

        var authorityId = authorityElement.GetString()!;

        if (!authorityIds.Contains(item: authorityId)) {
            throw new CanaryManifestRefusal(message: $"{context} names authority '{authorityId}', which this leg's authorities[] does not declare.");
        }

        return authorityId;
    }
    private static CanaryResponseAssertion ReadResponseAssertion(JsonElement element, string context, HashSet<string> values, IReadOnlySet<string> authorityIds) {
        RequireOnlyMembers(element: element, context: context, "authority", "count", "extract", "name", "occurrence", "stream", "type", "verb");

        var name = ReadRequiredString(context: context, element: element, member: "name");
        var authority = ReadAssertionAuthority(authorityIds: authorityIds, context: context, element: element);
        var stream = ReadStream(value: ReadRequiredString(context: context, element: element, member: "stream"), context: context);
        var selector = ReadSelector(context: context, element: element);
        var extractions = new List<CanaryValueExtraction>();

        if (element.TryGetProperty(propertyName: "extract", value: out var extractionElement)) {
            if (extractionElement.ValueKind != JsonValueKind.Array) {
                throw new CanaryManifestRefusal(message: $"{context} extract must be an array.");
            }

            for (var index = 0; (index < extractionElement.GetArrayLength()); index++) {
                var row = RequireObject(element: extractionElement[index], context: $"{context} extract[{index}]");

                RequireOnlyMembers(element: row, context: $"{context} extract[{index}]", "component", "field", "name");

                var valueName = ReadRequiredString(context: $"{context} extract[{index}]", element: row, member: "name");
                var field = ReadRequiredString(context: $"{context} extract[{index}]", element: row, member: "field");
                int? component = null;

                if (row.TryGetProperty(propertyName: "component", value: out _)) {
                    component = ReadInteger(context: $"{context} extract[{index}]", element: row, member: "component");
                    if (component < 0) {
                        throw new CanaryManifestRefusal(message: $"{context} extract[{index}] component must be zero or greater.");
                    }
                }

                if (!values.Add(item: valueName)) {
                    throw new CanaryManifestRefusal(message: $"{context} repeats extracted value name '{valueName}'.");
                }

                extractions.Add(item: new CanaryValueExtraction(Component: component, Field: field, Name: valueName));
            }
        }

        return new CanaryResponseAssertion(
            Authority: authority,
            Count: selector.Count,
            Extractions: extractions,
            Name: name,
            Occurrence: selector.Occurrence,
            Stream: stream,
            Verb: selector.Verb
        );
    }
    private static CanarySequenceAssertion ReadSequenceAssertion(JsonElement element, string context, IReadOnlySet<string> authorityIds) {
        RequireOnlyMembers(element: element, context: context, "authority", "name", "responses", "stream", "type");

        var name = ReadRequiredString(context: context, element: element, member: "name");
        var authority = ReadAssertionAuthority(authorityIds: authorityIds, context: context, element: element);
        var stream = ReadStream(value: ReadRequiredString(context: context, element: element, member: "stream"), context: context);
        var rows = ReadRequiredArray(context: context, element: element, member: "responses");

        if (rows.GetArrayLength() == 0) {
            throw new CanaryManifestRefusal(message: $"{context} responses is empty; an empty order assertion is vacuous.");
        }

        var responses = new List<CanaryResponseSelector>(capacity: rows.GetArrayLength());

        for (var index = 0; (index < rows.GetArrayLength()); index++) {
            var row = RequireObject(element: rows[index], context: $"{context} responses[{index}]");

            RequireOnlyMembers(element: row, context: $"{context} responses[{index}]", "count", "occurrence", "verb");
            responses.Add(item: ReadSelector(context: $"{context} responses[{index}]", element: row));
        }

        return new CanarySequenceAssertion(Authority: authority, Name: name, Responses: responses, Stream: stream);
    }
    private static CanaryRelationAssertion ReadRelationAssertion(JsonElement element, string context, HashSet<string> values) {
        RequireOnlyMembers(element: element, context: context, "left", "margin", "maximum", "minimum", "name", "operator", "right", "type");

        var name = ReadRequiredString(context: context, element: element, member: "name");
        var left = ReadOperand(element: ReadRequiredObject(context: context, element: element, member: "left"), context: $"{context} left");
        var operatorText = ReadRequiredString(context: context, element: element, member: "operator");
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
                right = ReadOperand(element: ReadRequiredObject(context: context, element: element, member: "right"), context: $"{context} right");
                break;
            case CanaryRelationOperator.BetweenInclusive:
                minimum = ReadFiniteNumber(context: context, element: element, member: "minimum");
                maximum = ReadFiniteNumber(context: context, element: element, member: "maximum");
                if (minimum > maximum) {
                    throw new CanaryManifestRefusal(message: $"{context} minimum is greater than maximum.");
                }
                break;
            case CanaryRelationOperator.AtLeast:
                minimum = ReadFiniteNumber(context: context, element: element, member: "minimum");
                break;
            case CanaryRelationOperator.AtMost:
                maximum = ReadFiniteNumber(context: context, element: element, member: "maximum");
                break;
            case CanaryRelationOperator.MinimumMargin:
                right = ReadOperand(element: ReadRequiredObject(context: context, element: element, member: "right"), context: $"{context} right");
                margin = ReadFiniteNumber(context: context, element: element, member: "margin");
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
            JsonValueKind.Number when (literalElement.TryGetDouble(value: out var number) && double.IsFinite(d: number)) => new CanaryOperand(NumberLiteral: number, StringLiteral: null, ValueName: null),
            _ => throw new CanaryManifestRefusal(message: $"{context} literal must be a finite number or a string."),
        };
    }
    private static CanaryResponseSelector ReadSelector(JsonElement element, string context) {
        var verb = ReadRequiredString(context: context, element: element, member: "verb");

        if (!IsSafeToken(allowColon: false, allowDot: true, value: verb)) {
            throw new CanaryManifestRefusal(message: $"{context} verb '{verb}' is not a lower-case dotted token.");
        }

        var occurrence = ReadInteger(context: context, element: element, member: "occurrence");
        var count = ReadInteger(context: context, element: element, member: "count");

        if ((occurrence <= 0) || (count <= 0) || (occurrence > count)) {
            throw new CanaryManifestRefusal(message: $"{context} requires 1 <= occurrence <= count; the selected response must exist and cardinality must be exact.");
        }

        return new CanaryResponseSelector(Count: count, Occurrence: occurrence, Verb: verb);
    }
    private static IReadOnlyList<string> ReadRequirements(JsonElement element, string id) {
        var array = ReadRequiredArray(context: $"canary '{id}'", element: element, member: "requirements");
        var requirements = new List<string>(capacity: array.GetArrayLength());
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < array.GetArrayLength()); index++) {
            var item = array[index];

            if ((item.ValueKind != JsonValueKind.String) || string.IsNullOrWhiteSpace(value: item.GetString())) {
                throw new CanaryManifestRefusal(message: $"canary '{id}' requirements[{index}] must be a non-blank capability token.");
            }

            var value = item.GetString()!;
            var valid = ((value is "gpu" or "audio-output") || (value.StartsWith(comparisonType: StringComparison.Ordinal, value: "input:") && IsSafeToken(value: value[6..], allowColon: false, allowDot: false)));

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
        var childDirectories = Directory.GetDirectories(path: canaryDirectory, searchOption: SearchOption.AllDirectories, searchPattern: "*");

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
        foreach (var role in positive.Authorities.Concat(second: discriminating.Authorities)) {
            expected.Add(item: role.ScriptPath);

            if (IsWithin(root: canaryDirectory, path: role.WorldPath)) {
                expected.Add(item: role.WorldPath);
            }
        }
        foreach (var fixture in fixtures) {
            expected.Add(item: fixture);
        }

        foreach (var file in Directory.GetFiles(path: canaryDirectory, searchOption: SearchOption.TopDirectoryOnly, searchPattern: "*").Order(comparer: StringComparer.Ordinal)) {
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
        } catch (Exception exception) when ((exception is ArgumentException or NotSupportedException or PathTooLongException)) {
            throw new CanaryManifestRefusal(message: $"{context} path '{rawPath}' is invalid ({exception.Message.ReplaceLineEndings(replacementText: " ")}).");
        }

        if (!IsWithin(path: fullPath, root: containmentRoot)) {
            throw new CanaryManifestRefusal(message: $"{context} path '{rawPath}' escapes the repository tree.");
        }
        if (!File.Exists(path: fullPath)) {
            throw new CanaryManifestRefusal(message: $"{context} file '{rawPath}' does not exist.");
        }
        if (ContainsReparsePoint(containmentRoot: containmentRoot, path: fullPath)) {
            throw new CanaryManifestRefusal(message: $"{context} path '{rawPath}' crosses a link or reparse point; lexical containment would not prove where it reads.");
        }

        return fullPath;
    }
    private static bool ContainsReparsePoint(string path, string containmentRoot) {
        for (var current = new FileInfo(fileName: path).Directory; ((current is not null) && IsWithin(root: containmentRoot, path: current.FullName)); current = current.Parent) {
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
        "stub" => CanaryBootShape.Stub,
        _ => throw new CanaryManifestRefusal(message: $"canary '{id}' bootShape '{value}' is invalid; use exactly 'headless', 'windowed', or 'stub' (casing is significant)."),
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

        return RequireObject(context: $"{context} {member}", element: value);
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
                if (TryFindDuplicateMember(duplicate: out duplicate, element: item, path: $"{path}[{index}]")) {
                    return true;
                }
                index++;
            }
        }

        duplicate = string.Empty;

        return false;
    }
    private static bool ContainsParentSegment(string path) =>
        path.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ['/', '\\']).Any(predicate: static segment => (segment == ".."));
    private static bool IsSafeToken(string value, bool allowColon, bool allowDot) {
        if ((value.Length == 0) || (value[0] == '-') || (value[^1] == '-') || value.Contains(comparisonType: StringComparison.Ordinal, value: "--")) {
            return false;
        }

        foreach (var character in value) {
            if (((character >= 'a') && (character <= 'z')) || char.IsAsciiDigit(c: character) || (character == '-') || (allowDot && (character == '.')) || (allowColon && (character == ':'))) {
                continue;
            }

            return false;
        }

        return true;
    }
    private static bool IsWithin(string root, string path) {
        var relative = Path.GetRelativePath(relativeTo: Path.GetFullPath(path: root), path: Path.GetFullPath(path: path));

        return (!Path.IsPathRooted(path: relative) && (relative != "..") && !relative.StartsWith(comparisonType: StringComparison.Ordinal, value: $"..{Path.DirectorySeparatorChar}"));
    }
    private static bool PathsEqual(string left, string right) => PathComparer().Equals(x: left, y: right);
    private static StringComparer PathComparer() => (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private sealed class CanaryManifestRefusal(string message) : Exception(message: message);
}
