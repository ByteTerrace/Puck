using Puck.HumbleGamingDeck.Post;

if (!CommandLineArguments.TryValidateValues(args: args, error: out var error,
    names: ["--artifacts", "--tier", "--filter", "--sst", "--roms", "--accuracy-coin", "--corpus-cache"])) {
    Console.Error.WriteLine(value: error);

    return 2;
}
var stages = PostStages.Create()
    .Where(predicate: stage => PostStageFilters.TierMatches(stage: stage, tierFilter: CommandLineArguments.Value(args: args, name: "--tier")))
    .Where(predicate: stage => PostStageFilters.NameMatches(stage: stage, nameFilter: CommandLineArguments.Value(args: args, name: "--filter")))
    .ToArray();
CorpusManifest? corpora = null;
var fetch = args.Contains(value: "--fetch-corpora", comparer: StringComparer.OrdinalIgnoreCase);
if (fetch || stages.Any(predicate: static stage => (stage.Tier == PostTier.B))) {
    corpora = CorpusManifest.Load(path: CorpusManifest.InRepository(projectName: "Puck.HumbleGamingDeck.Post"),
        cacheRoot: CommandLineArguments.Value(args: args, name: "--corpus-cache"));
}
if (fetch) {
    Console.Out.WriteLine(value: $"{corpora!.Fetch()} corpus/corpora fetched into {corpora.EffectiveCacheRoot}");

    return 0;
}
var context = new PostContext(
    ArtifactsDirectory: (CommandLineArguments.Value(args: args, name: "--artifacts") ?? "artifacts/hgd-post"),
    SstRoot: corpora?.Resolve(args: args, flag: "--sst", name: "nes6502-sst"),
    TestRomRoot: corpora?.Resolve(args: args, flag: "--roms", name: "nes-test-roms"),
    AccuracyCoinRoot: corpora?.Resolve(args: args, flag: "--accuracy-coin", name: "accuracy-coin"));
var report = new PostBattery<PostContext>(banner: "Puck.HumbleGamingDeck.Post - Humble Gaming Deck power-on self-test", stages: stages).Run(context: context);
report.Write(artifactsDirectory: context.ArtifactsDirectory);
if (report.Abandoned) {
    Environment.Exit(exitCode: report.ExitCode);
}
return report.ExitCode;
