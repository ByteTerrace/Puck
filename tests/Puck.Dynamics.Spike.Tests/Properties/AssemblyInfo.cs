using Xunit;

// The measurement file is written by several facts into one shared buffer; serializing the run keeps its sections in a
// stable order so a report can be read as a document rather than as an interleaving.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
