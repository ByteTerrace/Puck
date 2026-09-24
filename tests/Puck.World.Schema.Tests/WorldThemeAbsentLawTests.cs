using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>CONTRACT UNDER TEST: every theme recipe's <c>Absent</c> value is fully formed — each color it carries is
/// the transparent black it names and each bindable scalar the literal zero, never a default value with nothing in
/// it. A recipe's <c>Absent</c> is built by a static initializer, so a value it reads from another static member
/// must already exist when it runs.</summary>
public sealed class WorldThemeAbsentLawTests {
    public static TheoryData<string, object> Recipes => new() {
        { nameof(WorldThemeColor), WorldThemeColor.Absent },
        { nameof(WorldThemeDiegetic), WorldThemeDiegetic.Absent },
        { nameof(WorldThemeElevation), WorldThemeElevation.Absent },
    };

    [MemberData(nameof(Recipes))]
    [Theory]
    public void EveryColorInAnAbsentRecipeIsTransparentBlack(string recipe, object absent) {
        var colors = absent.GetType()
            .GetProperties()
            .Where(predicate: static property => (property.PropertyType == typeof(BindableColor)))
            .Select(selector: property => (property.Name, Value: ((BindableColor)property.GetValue(obj: absent)!)))
            .ToArray();

        Assert.NotEmpty(collection: colors);
        foreach (var (name, value) in colors) {
            Assert.True(
                condition: (value.Raw == "#00000000"),
                userMessage: $"{recipe}.Absent.{name} is '{(value.Raw ?? "<null>")}', not transparent black"
            );
        }
    }
    [MemberData(nameof(Recipes))]
    [Theory]
    public void EveryBindableScalarInAnAbsentRecipeIsTheLiteralZero(string recipe, object absent) {
        foreach (var property in absent.GetType().GetProperties().Where(predicate: static property => (property.PropertyType == typeof(BindableScalar)))) {
            var value = ((BindableScalar)property.GetValue(obj: absent)!);

            Assert.True(
                condition: (value.Literal == 0f),
                userMessage: $"{recipe}.Absent.{property.Name} holds no literal zero"
            );
        }
    }
}
