namespace Puck.Text.Tests;

public sealed class GenerationBudgetTests {
    private static FontAtlas Generate(FontAtlasGenerationLimits limits, CancellationToken token = default) =>
        new ManagedFontAtlasGenerator().Generate(request: new() {
            FontBytes = SyntheticTrueTypeFont.Build(SyntheticKerning.GposPairFormat2),
            FontIdentifier = "test://budget",
            Limits = limits,
            CancellationToken = token,
            Options = new() { AllowedCharacters = "ABC", AllowedCodePointRanges = [] },
        });

    [Fact]
    public void CancellationIsObservedAtEntryAndBetweenWorkReservations() {
        using var source = new CancellationTokenSource();
        var budget = new FontGenerationBudget(
            new(),
            source.Token
        );

        budget.Work(amount: 5);
        source.Cancel();
        Assert.Throws<OperationCanceledException>(testCode: () => budget.Work());
        Assert.Throws<OperationCanceledException>(testCode: () => Generate(
            limits: new(),
            token: source.Token
        ));
    }
    [Fact]
    public void DefaultsAdmitTheControlFont() => Assert.NotEmpty(collection: Generate(
        limits: new(),
        token: TestContext.Current.CancellationToken
    ).Glyphs);
    [InlineData("input")]
    [InlineData("geometry")]
    [InlineData("glyph")]
    [InlineData("work")]
    [InlineData("kerning-pair")]
    [Theory]
    public void WholeJobLimitsRefuseBeforeReturningAnAtlas(string limit) {
        var limits = limit switch {
            "input" => new FontAtlasGenerationLimits { MaxFontBytes = 1 },
            "geometry" => new() { MaxGeometryElements = 1 },
            "glyph" => new() { MaxGlyphs = 1 },
            "work" => new() { MaxWork = 1 },
            _ => new() { MaxKerningPairs = 1 }
        };
        var error = Record.Exception(testCode: () => Generate(
            limits: limits,
            token: TestContext.Current.CancellationToken
        ));

        Assert.NotNull(@object: error);
        Assert.Contains(
            $"whole-job {limit}",
            error.Message
        );
    }
    [Fact]
    public void WorkReservationCannotOverflowAndGeometryIsCumulative() {
        var budget = new FontGenerationBudget(
            new() { MaxGeometryElements = 3, MaxWork = long.MaxValue },
            default
        );

        budget.Geometry(amount: 2);
        Assert.Throws<InvalidDataException>(testCode: () => budget.Geometry(amount: 2));
        var work = new FontGenerationBudget(
            new() { MaxWork = long.MaxValue },
            default
        );

        work.Work(amount: long.MaxValue);
        Assert.Throws<InvalidDataException>(testCode: () => work.Work());
    }
}
