using Puck.HumbleGamingDeck.Post;

var options = new PostCommandLine(args: args,
    flags: ["--fetch-corpora"],
    requiredValues: ["--artifacts", "--tier", "--filter", "--sst", "--roms", "--accuracy-coin", "--corpus-cache"],
    values: ["--artifacts", "--tier", "--filter", "--sst", "--roms", "--accuracy-coin", "--corpus-cache"]);
if (!options.TryValidateValues(error: out var error)) {
    Console.Error.WriteLine(value: error);

    return 2;
}
var stages = PostStages.Create()
    .Where(predicate: stage => PostStageFilters.TierMatches(stage: stage, tierFilter: options.Value(name: "--tier")))
    .Where(predicate: stage => PostStageFilters.NameMatches(stage: stage, nameFilter: options.Value(name: "--filter")))
    .ToArray();
CorpusManifest? corpora = null;
var fetch = options.Flag(name: "--fetch-corpora");
if (fetch || stages.Any(predicate: static stage => (stage.Tier == PostTier.B))) {
    corpora = CorpusManifest.Load(path: CorpusManifest.InRepository(projectName: "Puck.HumbleGamingDeck.Post"),
        cacheRoot: options.Value(name: "--corpus-cache"));
}
if (fetch) {
    Console.Out.WriteLine(value: $"{corpora!.Fetch()} corpus/corpora fetched into {corpora.EffectiveCacheRoot}");

    return 0;
}
var context = new PostContext(
    ArtifactsDirectory: (options.Value(name: "--artifacts") ?? "artifacts/hgd-post"),
    SstRoot: corpora?.Resolve(args: args, flag: "--sst", name: "nes6502-sst"),
    TestRomRoot: corpora?.Resolve(args: args, flag: "--roms", name: "nes-test-roms"),
    ResolveAccuracyCoinRoot: () => corpora?.Resolve(args: args, flag: "--accuracy-coin", name: "accuracy-coin"));
var report = new PostBattery<PostContext>(banner: "Puck.HumbleGamingDeck.Post - Humble Gaming Deck power-on self-test", stages: stages).Run(context: context);
report.Write(artifactsDirectory: context.ArtifactsDirectory);
if (report.Abandoned) {
    Environment.Exit(exitCode: report.ExitCode);
}
return report.ExitCode;
