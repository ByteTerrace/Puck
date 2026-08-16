using System.Text;

using Puck.Cli.Analysis;
using Puck.Cli.Architecture;
using Puck.Cli.Bench;
using Puck.Cli.Canary;
using Puck.Cli.Citations;
using Puck.Cli.Format;
using Puck.Cli.FontAtlas;
using Puck.Cli.Landing;
using Puck.Cli.Packaging;
using Puck.Cli.Parity;
using Puck.Cli.Scan;
using Puck.Cli.Schema;
using Puck.Cli.Search;
using Puck.Cli.WasmStdlib;
using Puck.Cli.WorktreeBase;

// Every verb emits source text — matched lines, comment records, drift reports — so the streams carry
// whatever the tree contains. Without this the host falls back to the machine's console code page and
// quietly substitutes non-ASCII (an em dash becomes '-', a math symbol becomes '?'), which corrupts the
// JSONL data streams and makes output machine-dependent. The setter suppresses the byte-order mark.
Console.OutputEncoding = Encoding.UTF8;
// puck — the consolidated Puck developer CLI. A verb is the first positional; every remaining argument
// forwards to the verb implementation unchanged (same flags, same output, same exit codes). Hand-rolled
// first-arg dispatch, no command framework — the verbs have wholly separate argument grammars.
return args switch {
    ["architecture", .. var architectureArgs] => ArchitectureCommand.Run(args: architectureArgs),
    ["bench", .. var benchArgs] => BenchRunner.Run(args: benchArgs),
    ["canary", .. var canaryArgs] => CanaryCommand.Run(args: canaryArgs),
    ["citations", .. var citationsArgs] => CitationsCommand.Run(args: citationsArgs),
    ["declarations", .. var declarationsArgs] => DeclarationsCommand.Run(args: declarationsArgs),
    ["format", .. var formatArgs] => FormatCommand.Run(args: formatArgs),
    ["font-atlas", .. var fontAtlasArgs] => FontAtlasCommand.Run(args: fontAtlasArgs),
    ["landing", .. var landingArgs] => LandingCommand.Run(args: landingArgs),
    ["packages", .. var packagesArgs] => PackagesCommand.Run(args: packagesArgs),
    ["parity", .. var parityArgs] => ParityCommand.Run(args: parityArgs),
    ["references", .. var referencesArgs] => ReferencesCommand.Run(args: referencesArgs),
    ["scan", .. var scanArgs] => ScanCommand.Run(args: scanArgs),
    ["schema", .. var schemaArgs] => SchemaCommand.Run(args: schemaArgs),
    ["search", .. var searchArgs] => SearchCommand.Run(args: searchArgs),
    ["wasm-stdlib", .. var wasmStdlibArgs] => WasmStdlibCommand.Run(args: wasmStdlibArgs),
    ["worktree-base", .. var worktreeBaseArgs] => WorktreeBaseCommand.Run(args: worktreeBaseArgs),
    _ => Usage(),
};
// No verb, or an unknown one, is a usage error (exit 2) listing the verbs.
static int Usage() {
    Console.Error.WriteLine(value:
            """
            puck — Puck developer CLI

            Usage: puck <verb> [args ...]

            Verbs:
              architecture  project-layering report: the build-time gate's explain surface
              bench         the Puck.Maths micro-benchmark microscope (BenchmarkDotNet)
              canary        run bounded positive-and-discriminating Puck.World proofs
              citations     cited verb tokens checked against vocabularies swept from the code
              declarations  declaration inventory read off the parsed syntax, no build
              format        source rewriters for conventions .editorconfig cannot express
              font-atlas    managed OpenType font/collection to SDF atlas generation
              landing       refuse a commit that drops a landing you never worked from
              packages      published ByteTerrace.Puck.* NuGet package report, id/description/tags
              parity        cross-backend composed-frame comparison against the real Puck.World
              references    references, implementers and overrides of a symbol, solution-wide
              scan          source sweep: comments, comment smells, locks, clones
              schema        generated JSON Schema for puck.world.def.v1, checked and regenerated
              search        content search over a linear-time symbolic-derivatives regex engine
              wasm-stdlib   regenerate the WASM standard library's generated Rust sources
              worktree-base put a worktree's HEAD at a base commit, refusing a dirty reset

            Run 'puck <verb> -h' for verb-specific usage; bench spells it '--help',
            because the benchmark harness owns '-h' as its own 'hide' option.
            """);

    return 2;
}
