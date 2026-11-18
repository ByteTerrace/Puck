using Xunit;

namespace Puck.State.Tests;

public sealed class StateReadAccessLawTests {
    [Fact]
    public void DeclarationAcquisitionAlsoAdmitsTheAudienceRowItNames() {
        var section = new StateSection(Rows: [
            new(CellName.Parse("public"), CellKind.Int, Visibility: new StateVisibility(ReadersFrom: "readers")),
            new(CellName.Parse("readers"), CellKind.Text, Domain: StateDomain.Keys.Instance)
        ]);
        var arena = new StateArena(StateCatalog.Compile(section), section, ArenaTime.Origin);
        var access = arena.RestrictReads(ordinal => ordinal == 0);
        var row = access.ResolveRow("public");
        Assert.Throws<StateUnavailableException>(() => access.Declaration(row));
        Assert.True(access.Failed);
        var admitted = arena.RestrictReads(_ => true);
        Assert.Equal("readers", admitted.Declaration(admitted.ResolveRow("public")).Visibility!.ReadersFrom);
    }

    [Fact]
    public void FilteredReductionsSaturateAndAcquireTheFilterEvenOnAnEmptyRow() {
        var first = CellName.Parse("a");
        var second = CellName.Parse("b");
        var section = new StateSection(Rows: [
            new(CellName.Parse("values"), CellKind.Int, Cells: [new(first, CellValue.Int(long.MaxValue)), new(second, CellValue.Int(1))]),
            new(CellName.Parse("gate"), CellKind.Int, Cells: [new(first, CellValue.Int(1)), new(second, CellValue.Int(1))]),
            new(CellName.Parse("empty"), CellKind.Int, Domain: StateDomain.Keys.Instance),
        ]);
        var arena = new StateArena(StateCatalog.Compile(section), section, ArenaTime.Origin);
        var showFilter = true;
        var access = arena.RestrictReads(ordinal => ordinal != 1 || showFilter);
        var values = access.ResolveRow("values");
        var filter = access.ResolveRow("gate");
        var empty = access.ResolveRow("empty");
        Assert.Equal(long.MaxValue, StateReader.ReduceRaw(access, values, StateReduceOp.Sum, ArenaTime.Origin, filter));
        Assert.Equal(1L, StateReader.ReduceRaw(access, values, StateReduceOp.Count, ArenaTime.Origin, filter, (0L, 1L)));
        showFilter = false;
        Assert.Throws<StateUnavailableException>(() => StateReader.ReduceRaw(access, empty, StateReduceOp.Count, ArenaTime.Origin, filter));
        Assert.True(access.Failed);
    }

    [Fact]
    public void RowAdmissionDoesNotAcquireAnInlineGeneratorProgram() {
        var section = new StateSection(Rows: [new(CellName.Parse("draw"), CellKind.Int,
            Draw: new Draw(Generator: new StateGenerator(Source: GeneratorSource.StreamDraw,
                Extended: new GeneratorExtended(128, Script: [7, 11]))))]);
        var arena = new StateArena(StateCatalog.Compile(section), section, ArenaTime.Origin);
        var access = arena.RestrictReads(_ => true);
        Assert.Null(access.Declaration(access.ResolveRow("draw")).Draw!.Generator);
        Assert.NotNull(StateRowDeclaration.From(section.Rows![0]).Draw!.Generator);
    }

    private static StateArena Store(bool hidden) {
        var rows = new List<StateRow> {
            new(CellName.Parse("public"), CellKind.Int, Domain: StateDomain.Keys.Instance,
                Cells: [new(CellName.Parse("a"), CellValue.Int(7)), new(CellName.Parse("b"), CellValue.Int(10))])
        };
        if (hidden) {
            rows.Add(new(CellName.Parse("secret"), CellKind.Text,
                Cells: [new(StateRow.SlotKey, CellValue.Text("withheld"))]));
        }
        var section = new StateSection(Rows: rows);
        return new StateArena(StateCatalog.Compile(section), section, ArenaTime.Origin);
    }

    [Fact]
    public void MissingAndWithheldAcquisitionFailAlikeAndCatchingCannotClearFailure() {
        var failures = new List<string>();
        foreach (var hidden in new[] { false, true }) {
            var access = Store(hidden).RestrictReads(row => row == 0);
            var failure = Assert.Throws<StateUnavailableException>(() => access.ResolveRow("secret"));
            failures.Add(failure.Message);
            Assert.True(access.Failed);
            Assert.Throws<StateUnavailableException>(() => access.Declaration(default));
            Assert.Throws<StateUnavailableException>(() => access.CellCount(default));
            Assert.Throws<StateUnavailableException>(() => access.TryResolveRow("public", out _));
            Assert.Throws<StateUnavailableException>(() => access.TryRead(default, default, out _));
            Assert.Throws<StateUnavailableException>(() => access.TryReadLive(default, default, ArenaTime.Origin, out _));
            Assert.Throws<StateUnavailableException>(() => access.TryReadLiveNumber(default, default, ArenaTime.Origin, out _));
            Assert.Throws<StateUnavailableException>(() => access.TryKeyAt(default, 0, out _));
            Assert.Throws<StateUnavailableException>(() => access.TryResolveKey(default, CellName.Parse("a"), out _));
            Assert.Throws<StateUnavailableException>(() => access.TryKeyName(default, default, out _));
            Assert.Throws<StateUnavailableException>(() => { var cursor = 0; access.TryNextCell(default, ref cursor, out _); });
        }
        Assert.Equal(failures[0], failures[1]);
    }

    [Fact]
    public void RestrictedAndAuthorityReadsUseTheSameReductionKernel() {
        foreach (var hidden in new[] { false, true }) {
            var arena = Store(hidden);
            var access = arena.RestrictReads(row => row == 0);
            var row = access.ResolveRow("public");
            Assert.Equal(17, StateReader.ReduceRaw(access, row, StateReduceOp.Sum, 0, 0));
            Assert.Equal(StateReader.ReduceRaw(arena, 0, StateReduceOp.Sum, 0, 0),
                StateReader.ReduceRaw(access, row, StateReduceOp.Sum, 0, 0));
            Assert.False(access.Failed);
        }
    }

    [Fact]
    public void ADeclarationIsASeparateSealedTypeWithNoStorageState() {
        var access = Store(true).RestrictReads(row => row == 0);
        var declaration = access.Declaration(access.ResolveRow("public"));
        Assert.True(typeof(StateRowDeclaration).IsSealed);
        Assert.False(typeof(StateRow).IsAssignableFrom(declaration.GetType()));
        foreach (var name in new[] { "Cells", "CellSlots", "ReservedCapacity", "DrawCursor", "DrawnMasks", "HistoryCursor", "Phase", "Arena", "Keys" }) {
            Assert.Null(declaration.GetType().GetProperty(name));
        }
        Assert.Null(typeof(StateDrawDeclaration).GetProperty("Secret"));
        Assert.Equal(CellKind.Int, declaration.Kind);
        Assert.Equal(StateDomain.Keys.Instance, declaration.EffectiveDomain);
    }

    [Fact]
    public void BoundRowAccessRechecksPolicyAndCannotReturnHiddenMembershipOrKind() {
        var admitted = true;
        var access = Store(true).RestrictReads(_ => admitted);
        var row = access.ResolveRow("secret");
        admitted = false;
        Assert.Throws<StateUnavailableException>(() => access.Declaration(row));
        Assert.True(access.Failed);
    }

    [Fact]
    public void RestrictedHandlesDoNotRevealHiddenRowsOrInternedKeys() {
        var section = new StateSection(Rows: [
            new(CellName.Parse("hidden"), CellKind.Int, Cells: [new(StateRow.SlotKey, CellValue.Int(91))]),
            new(CellName.Parse("public"), CellKind.Int, Domain: StateDomain.Keys.Instance,
                Cells: [new(CellName.Parse("a"), CellValue.Int(7)), new(CellName.Parse("b"), CellValue.Int(10))])
        ]);
        var arena = new StateArena(StateCatalog.Compile(section), section, ArenaTime.Origin);
        var access = arena.RestrictReads(row => row == 1);
        var without = Store(false).RestrictReads(_ => true);
        var row = access.ResolveRow("public");
        var other = without.ResolveRow("public");
        Assert.Throws<StateUnavailableException>(() => access.CellCount(other));
        access = arena.RestrictReads(ordinal => ordinal == 1);
        row = access.ResolveRow("public");
        Assert.True(access.TryResolveKey(row, CellName.Parse("a"), out var key));
        Assert.True(without.TryResolveKey(other, CellName.Parse("a"), out var otherKey));
        Assert.Equal(otherKey.Ordinal, key.Ordinal);
        Assert.False(arena.TryRead(1, key, out _));
        Assert.False(access.TryRead(row, otherKey, out _));
        Assert.Equal(17, StateReader.ReduceRaw(access, row, StateReduceOp.Sum, 0, 0));
    }

    [Fact]
    public void StreamSecretsAndProtocolSequencesNeverEnterTheDeclaration() {
        var row = new StateRow(CellName.Parse("draw"), CellKind.Int, Domain: StateDomain.Keys.Instance,
            Draw: new PrivateDrawFixture().Bind(new Draw(Source: CellName.Parse("source"), PrivateStream: true), 7),
            Phase: new StatePhase(19), DrawCursor: 8, HistoryCursor: 23);
        var changed = row with {
            Draw = new PrivateDrawFixture().Bind(row.Draw!, 97),
            Phase = new StatePhase(113), DrawCursor = 223, HistoryCursor = 227
        };
        Assert.Equal(StateRowDeclaration.From(row), StateRowDeclaration.From(changed));
    }

    [Fact]
    public void AnAdmittedVectorCannotExposeMutableArenaStorage() {
        var section = new StateSection(Rows: [new StateRow(CellName.Parse("vectors"), CellKind.Vector,
            Domain: StateDomain.Keys.Instance, Space: "embedding",
            Cells: [new StateCell(CellName.Parse("a"), CellValue.Vector(new sbyte[] { 4, 0, 0, 0, 0, 0, 0, 0 }))])],
            Spaces: [new StateSpace(CellName.Parse("embedding"), "law", "one", 8)]);
        var arena = new StateArena(StateCatalog.Compile(section), section, ArenaTime.Origin);
        var access = arena.RestrictReads(_ => true);
        var row = access.ResolveRow("vectors");
        Assert.True(access.TryResolveKey(row, CellName.Parse("a"), out var key));
        Assert.True(access.TryRead(row, key, out var value));
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(value.AsVector, out var bytes));
        bytes.Array![bytes.Offset] = 99;
        Assert.True(access.TryRead(row, key, out var fresh));
        Assert.Equal(4, fresh.AsVector.Span[0]);
    }
}
