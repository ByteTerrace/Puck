namespace Puck.Cli.Format;

// The `puck format` verb: the source rewriters applied in one parse/write per file. Phase 0 always runs
// the SDK whitespace formatter per project first (the .editorconfig baseline the custom passes layer
// onto); the syntactic passes then run as one re-parsing pipeline, and the semantic `named-args` pass
// runs as its own disk phase. The exit code is the worst of the three phases: 1 for drift in a dry mode
// or a skipped rewrite, 2 for a usage error, a missing root, or a tool failure in write mode.
internal static class FormatCommand {
    public static int Run(string[] args) {
        var scanner = new ArgScanner().Flag(name: "WhatIf").Flag(name: "Verify").Value(name: "Only").Flag(name: "h").Flag(name: "help");

        if (!scanner.Parse(args: args)) {
            Console.Error.WriteLine(value: $"ERROR: {scanner.Error}");

            return 2;
        }

        if (scanner.Has(name: "h") || scanner.Has(name: "help")) {
            Console.Out.WriteLine(value: HelpText());

            return 0;
        }

        var selected = FormatPasses.DefaultSelection();

        if (scanner.Get(name: "Only") is { } only) {
            selected = only.Split(separator: ',', options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(selector: static name => name.ToLowerInvariant())
                .ToHashSet(comparer: StringComparer.Ordinal);

            // An empty or all-separator value selects nothing, and a run that rewrites nothing reads
            // exactly like a tree that is already clean. The caller meant something; say what exists.
            if (selected.Count == 0) {
                Console.Error.WriteLine(value: $"ERROR: -Only named no format pass (known: {FormatPasses.Names}).");

                return 2;
            }

            foreach (var name in selected) {
                if (!FormatPasses.IsKnown(name: name)) {
                    Console.Error.WriteLine(value: $"ERROR: unknown format pass '{name}' (known: {FormatPasses.Names}).");

                    return 2;
                }
            }
        }

        var root = ((scanner.Positionals.Count > 0) ? scanner.Positionals[0] : "src");
        var whatIf = scanner.Has(name: "WhatIf");
        var verify = scanner.Has(name: "Verify");

        // Phase 0: the SDK whitespace formatter establishes the .editorconfig baseline (spacing,
        // alignment, newlines) the custom passes then layer bespoke conventions onto. Disjoint concerns —
        // the result is a fixed point of both — so running it first is safe.
        var result = WhitespacePhase.Run(rootArgument: root, verifyOnly: (whatIf || verify));
        var syntacticPasses = FormatPasses.All.Where(predicate: pass => (!pass.Semantic && selected.Contains(item: pass.Name))).ToList();

        if (syntacticPasses.Count > 0) {
            result = Math.Max(val1: result, val2: SourceRewrite.Run(label: "format", rootArgument: root, whatIf: whatIf, verify: verify, passes: syntacticPasses));
        }

        if (FormatPasses.All.Any(predicate: pass => (pass.Semantic && selected.Contains(item: pass.Name)))) {
            result = Math.Max(val1: result, val2: NamedArgsPhase.Run(rootArgument: root, whatIf: whatIf, verify: verify));
        }

        return result;
    }

    // The synopsis, with the pass registry read from its single declaration site. The pass table, the
    // idempotency contract and the safety rules are the README's job; this exists so every verb answers -h.
    private static string HelpText() =>
        $"""
        format [<root=src>]   source rewriters for conventions .editorconfig cannot express

          -Only <p,p>   restrict to named passes (default: every pass but the three
                        vertical line-wrappers)
          -WhatIf       write nothing; exit 1 on any drift, listing the files
          -Verify       write nothing; additionally fail on a rewrite that would
                        introduce syntax errors or a pass that is not a fixed point
          -h / --help   this text

        Passes: {FormatPasses.Names}

        <root> resolves against the working directory, the same rule every verb
        applies. Phase 0 (`dotnet format whitespace`) runs first over the projects
        that own corpus files and needs them restored; in WRITE mode it rewrites any
        whitespace drift in that root — run -WhatIf first on a root you have not
        swept. The semantic named-args pass needs the projects built. A pass whose
        output would add syntax errors is always dropped and reported, never written.
        Exit codes: 0 clean, 1 drift in a dry mode or a skipped rewrite, 2 usage
        error, missing root, or a tool failure in write mode.
        """;
}
