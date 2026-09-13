namespace Puck.Text.Tests;

public sealed class GenerationBudgetTests {
    private static FontAtlas Generate(FontAtlasGenerationLimits limits, CancellationToken token = default) =>
        new ManagedFontAtlasGenerator().Generate(new() {
            FontBytes = SyntheticTrueTypeFont.Build(SyntheticKerning.GposPairFormat2),
            FontIdentifier = "test://budget", Limits = limits, CancellationToken = token,
            Options = new() { AllowedCharacters = "ABC", AllowedCodePointRanges = [] }
        });

    [Fact]
    public void DefaultsAdmitTheControlFont() => Assert.NotEmpty(Generate(new(), TestContext.Current.CancellationToken).Glyphs);

    [Theory]
    [InlineData("input")]
    [InlineData("geometry")]
    [InlineData("glyph")]
    [InlineData("work")]
    [InlineData("kerning-pair")]
    public void WholeJobLimitsRefuseBeforeReturningAnAtlas(string limit) {
        var limits = limit switch {
            "input" => new FontAtlasGenerationLimits { MaxFontBytes = 1 },
            "geometry" => new() { MaxGeometryElements = 1 },
            "glyph" => new() { MaxGlyphs = 1 },
            "work" => new() { MaxWork = 1 },
            _ => new() { MaxKerningPairs = 1 }
        };
        var error = Record.Exception(() => Generate(limits, TestContext.Current.CancellationToken));
        Assert.NotNull(error);
        Assert.Contains($"whole-job {limit}", error.Message);
    }

    [Fact]
    public void CancellationIsObservedAtEntryAndBetweenWorkReservations() {
        using var source = new CancellationTokenSource();
        var budget = new FontGenerationBudget(new(), source.Token);
        budget.Work(5);
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => budget.Work());
        Assert.Throws<OperationCanceledException>(() => Generate(new(), source.Token));
    }

    [Fact]
    public void WorkReservationCannotOverflowAndGeometryIsCumulative() {
        var budget = new FontGenerationBudget(new() { MaxWork = long.MaxValue, MaxGeometryElements = 3 }, default);
        budget.Geometry(2);
        Assert.Throws<InvalidDataException>(() => budget.Geometry(2));
        var work = new FontGenerationBudget(new() { MaxWork = long.MaxValue }, default);
        work.Work(long.MaxValue);
        Assert.Throws<InvalidDataException>(() => work.Work());
    }
}
