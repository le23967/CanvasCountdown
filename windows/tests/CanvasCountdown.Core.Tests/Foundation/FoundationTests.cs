namespace CanvasCountdown.Tests.Foundation;

public sealed class ValueCollectionTests
{
    private sealed record Holder(ValueList<int> Numbers, ValueSet<string> Names);

    [Fact]
    public void RecordsHoldingValueCollectionsCompareByContents()
    {
        var first = new Holder([1, 2, 3], ["b", "a"]);
        var second = new Holder(new[] { 1, 2, 3 }.ToValueList(), new[] { "a", "b" }.ToValueSet());

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, first with { Numbers = [1, 2] });
    }

    [Fact]
    public void ADefaultValueListIsEmptyRatherThanBroken()
    {
        ValueList<int> unset = default;

        Assert.Empty(unset);
        Assert.Equal(ValueList<int>.Empty, unset);
        Assert.Equal([4], unset.Add(4));
    }

    [Theory]
    // Swift's documented example: move "b" and "d" in front of "e".
    [InlineData(new[] { 1, 3 }, 4, "a,c,b,d,e")]
    [InlineData(new[] { 0 }, 5, "b,c,d,e,a")]
    [InlineData(new[] { 4 }, 0, "e,a,b,c,d")]
    [InlineData(new[] { 2 }, 2, "a,b,c,d,e")]
    public void MoveMatchesSwiftsMoveFromOffsets(int[] sources, int destination, string expected)
    {
        ValueList<string> letters = ["a", "b", "c", "d", "e"];

        Assert.Equal(expected, string.Join(",", letters.Move(sources, destination)));
    }

    [Fact]
    public void ValueListsRoundTripThroughJson()
    {
        var holder = new Holder([3, 1], ["z", "y"]);

        var json = JsonDefaults.Serialize(holder);
        var decoded = JsonDefaults.Deserialize<Holder>(json);

        Assert.Equal("{\"numbers\":[3,1],\"names\":[\"y\",\"z\"]}", json);
        Assert.Equal(holder, decoded);
    }

    [Fact]
    public void SortedStringsUseOrdinalOrderLikeSwift()
    {
        ValueSet<string> names = ["b", "B", "a"];

        Assert.Equal(["B", "a", "b"], names.Sorted());
    }
}

public sealed class PreferencesStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cc-prefs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private string FilePath => Path.Combine(directory, "settings.json");

    [Fact]
    public void AbsentIsDistinguishedFromAStoredFalse()
    {
        var store = JsonPreferencesStore.InMemory();

        Assert.Null(store.GetOptional<bool>("flag"));
        store.Set("flag", false);
        Assert.False(store.GetOptional<bool>("flag"));
    }

    [Fact]
    public void ValuesSurviveReopening()
    {
        var store = new JsonPreferencesStore(FilePath);
        store.Set("interval", 21600);
        store.Set("courses", new ValueSet<string>(["COMP2050", "STAT1010"]));

        var reopened = new JsonPreferencesStore(FilePath);

        Assert.Equal(21600, reopened.Get("interval", 0));
        Assert.Equal(new ValueSet<string>(["STAT1010", "COMP2050"]), reopened.Get("courses", ValueSet<string>.Empty));
    }

    [Fact]
    public void AValueOfTheWrongShapeReadsAsAbsent()
    {
        var store = JsonPreferencesStore.InMemory();
        store.Set("interval", "not a number");

        Assert.False(store.TryGet<int>("interval", out _));
        Assert.Equal(7, store.Get("interval", 7));
    }

    [Fact]
    public void AnUnreadableFileIsSetAsideNotOverwritten()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, "{ this is not json");

        var store = new JsonPreferencesStore(FilePath);
        store.Set("interval", 3600);

        var aside = Directory.GetFiles(directory, "settings.json.unreadable-*");
        Assert.Single(aside);
        Assert.Equal("{ this is not json", File.ReadAllText(aside[0]));
        Assert.Equal(3600, new JsonPreferencesStore(FilePath).Get("interval", 0));
    }

    [Fact]
    public void RemovingAKeyMakesItAbsentAgain()
    {
        var store = new JsonPreferencesStore(FilePath);
        store.Set("dismissed", "1.2.0");
        store.Remove("dismissed");

        Assert.False(new JsonPreferencesStore(FilePath).Contains("dismissed"));
    }
}

public sealed class TextOrderTests
{
    [Fact]
    public void NumbersInNamesSortByValue()
    {
        var titles = new[] { "Lab 10", "lab 2", "Lab 1" };

        Assert.Equal(["Lab 1", "lab 2", "Lab 10"], titles.Order(TextOrder.Comparer));
    }
}
