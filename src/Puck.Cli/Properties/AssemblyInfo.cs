using System.Runtime.CompilerServices;

// Widen the assembly, not the member: Puck.Cli has no other consumer, and the comparison engine (the manifest
// and contract loaders, the tile comparator, the per-capture outcome model) is deliberately internal — a CLI
// verb's own plumbing, not a public API. A TEST project is CLAUDE.md's one arguable IVT exception.
[assembly: InternalsVisibleTo("Puck.Cli.Tests")]
