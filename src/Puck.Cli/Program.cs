using System.Text;

using Puck.Cli;

// Every verb emits source text — matched lines, comment records, drift reports — so the streams carry
// whatever the tree contains. Without this the host falls back to the machine's console code page and
// quietly substitutes non-ASCII (an em dash becomes '-', a math symbol becomes '?'), which corrupts the
// JSONL data streams and makes output machine-dependent. The setter suppresses the byte-order mark.
Console.OutputEncoding = Encoding.UTF8;
return await PuckRootCommand.InvokeAsync(args: args);
