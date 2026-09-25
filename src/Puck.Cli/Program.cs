using System.Text;

using Puck.Cli;

// Every verb emits source text — matched lines, comment records, drift reports — so the streams carry
// whatever the tree contains. Without this the host falls back to the machine's console code page and
// quietly substitutes non-ASCII (an em dash becomes '-', a math symbol becomes '?'), which corrupts the
// JSONL data streams and makes output machine-dependent. The setter suppresses the byte-order mark.
Console.OutputEncoding = Encoding.UTF8;
// A world source the CLI compiles for a document (a basis or an import `puck test` stages, a composition a verb
// reads) is compiled once across processes, sharing the game's persisted compile cache. The per-user directory is
// named at this entry point, as the game names it at its own.
Puck.World.Transpiler.Composition.WorldCompileCache.Shared.Persist(directory: Puck.Abstractions.PuckUserDirectory.Resolve(name: "compilations"));
return await PuckRootCommand.InvokeAsync(args: args);
