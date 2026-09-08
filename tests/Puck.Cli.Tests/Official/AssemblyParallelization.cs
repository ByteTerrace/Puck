using Xunit;

// LedgerDriftTests (this project, pre-existing) and the Official/ fixtures both redirect the process-global
// Console.Out/Console.Error to a private StringWriter for the lifetime of one call — a pattern that is only safe
// when no other thread does the same at the same moment. xUnit's default collection parallelism runs different
// test classes concurrently by default, so two classes doing this at once race on the SAME global Console
// properties (one call's SetOut/SetError racing another's), corrupting whichever StringWriter a concurrently
// running Console.Out.Write call happens to land in. Disabling test-collection parallelism for this whole
// assembly is the one fix that protects every current and future console-capturing test here, not just this one.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
