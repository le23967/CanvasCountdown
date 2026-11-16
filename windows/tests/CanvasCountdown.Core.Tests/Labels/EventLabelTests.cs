using CanvasCountdown.Tests.Appearance;

namespace CanvasCountdown.Tests.Labels;

/// <summary>
/// Labels: the library's rules, and what survives a restart. All fixtures are
/// invented.
/// </summary>
public sealed class EventLabelTests
{
    /// <summary>
    /// The stored key, spelled out: this is the contract with anything already
    /// on disk, so it is not free to change.
    /// </summary>
    private const string LabelsKey = "CanvasCountdown.settings.eventLabels";

    // The library

    [Fact]
    public void DefaultsAreUsableAndDistinct()
    {
        var library = EventLabelLibrary.Defaults;

        Assert.NotEmpty(library.Labels);
        Assert.True(
            library.Labels.Select(label => label.Name).Distinct().Count() == library.Labels.Count,
            "Two labels with the same name cannot be told apart in a menu");
        Assert.All(library.Labels, label => Assert.NotNull(SrgbColor.FromHex(label.ColorHex)));
    }

    /// <summary>
    /// The bug this pins: the starting labels used to be given fresh ids every
    /// time they were built, so every event labelled with one pointed at a
    /// label that did not exist on the next launch, and its colour vanished.
    /// </summary>
    [Fact]
    public void TheStartingLabelsKeepTheSameIdentityOnEveryLaunch()
    {
        // Written down as constants, and checked as constants. Two stores in
        // one test process would agree even on generated ids — the id has to
        // be the same in the *next* process, which only a fixed value gives.
        Assert.Equal(
            [
                "4C1D0E9A-0000-4000-A000-000000000001",
                "4C1D0E9A-0000-4000-A000-000000000002",
                "4C1D0E9A-0000-4000-A000-000000000003",
                "4C1D0E9A-0000-4000-A000-000000000004",
            ],
            EventLabelLibrary.Defaults.Labels.Select(label => label.Id.ToString("D").ToUpperInvariant()));
    }

    /// <summary>
    /// Deleting every label is a choice, not an empty first run: an emptied
    /// library is stored as such, and reading it back does not fall back to
    /// the starting set the way a missing one does.
    /// </summary>
    [Fact]
    public void AnEmptiedLibraryStaysEmpty()
    {
        using var file = new PreferencesFile();
        var store = file.Open();
        store.Set(LabelsKey, EventLabelLibrary.Defaults);
        foreach (var label in store.Get(LabelsKey, EventLabelLibrary.Defaults).Labels)
        {
            store.Set(LabelsKey, store.Get(LabelsKey, EventLabelLibrary.Defaults).Remove(label.Id));
        }

        Assert.Empty(file.Open().Get(LabelsKey, EventLabelLibrary.Defaults).Labels);
    }

    [Fact]
    public void AddingReturnsTheLabelAndKeepsIt()
    {
        var library = EventLabelLibrary.Empty.Add("  Society  ", "#30A46C", out var added);

        Assert.True(added.Name == "Society", "Surrounding space is not part of a name");
        Assert.Equal([added.Id], library.Labels.Select(label => label.Id));
    }

    [Fact]
    public void NamesMustBeUsableAndUnique()
    {
        var library = EventLabelLibrary.Empty.Add("Important", "#E5484D", out _);

        Assert.Equal(EventLabelNameError.Empty, library.Validate(""));
        Assert.Equal(EventLabelNameError.Empty, library.Validate("   "));
        Assert.Equal(EventLabelNameError.Duplicate, library.Validate("important"));
        Assert.Equal(EventLabelNameError.Duplicate, library.Validate(" IMPORTANT "));
        Assert.Equal(EventLabelNameError.TooLong, library.Validate(new string('A', 25)));
        Assert.Null(library.Validate("Personal"));
        Assert.True(
            library.Validate("Important", excluding: library.Labels[0].Id) is null,
            "A label may keep its own name while being edited");
    }

    [Fact]
    public void RenamingAndRecolouring()
    {
        var library = EventLabelLibrary.Empty.Add("Important", "#E5484D", out var label);

        library = library.Rename(label.Id, "Urgent");
        library = library.Recolor(label.Id, "#3E63DD");

        Assert.Equal("Urgent", library.Labels[0].Name);
        Assert.Equal("#3E63DD", library.Labels[0].ColorHex);
    }

    [Fact]
    public void ARenameToATakenNameIsRefused()
    {
        var library = EventLabelLibrary.Empty
            .Add("Important", "#E5484D", out var first)
            .Add("Personal", "#30A46C", out _);

        var error = Assert.Throws<EventLabelNameException>(() => library.Rename(first.Id, "Personal"));

        Assert.Equal(EventLabelNameError.Duplicate, error.Error);
        Assert.Equal("Important", library.Labels[0].Name);
    }

    [Fact]
    public void AnUnreadableColourIsNotStored()
    {
        var library = EventLabelLibrary.Empty.Add("Important", "#E5484D", out var label);

        library = library.Recolor(label.Id, "not a colour");

        Assert.Equal("#E5484D", library.Labels[0].ColorHex);
    }

    [Fact]
    public void AnUnusableLabelIsDroppedWhenRead()
    {
        var json = $$"""
            {"version":1,"labels":[
                {"id":"{{Guid.NewGuid()}}","name":"Good","colorHex":"#30A46C"},
                {"id":"{{Guid.NewGuid()}}","name":"  ","colorHex":"#30A46C"},
                {"id":"{{Guid.NewGuid()}}","name":"Bad colour","colorHex":"zzz"}
            ]}
            """;

        var library = JsonDefaults.Deserialize<EventLabelLibrary>(json)!;

        Assert.Equal(["Good"], library.Labels.Select(label => label.Name));
    }

    [Fact]
    public void ALibraryFromALaterVersionIsLeftAloneRatherThanHalfRead()
    {
        var json = $$"""
            {"version":99,"labels":[
                {"id":"{{Guid.NewGuid()}}","name":"From the future","colorHex":"#30A46C"}
            ]}
            """;

        var library = JsonDefaults.Deserialize<EventLabelLibrary>(json)!;

        Assert.Empty(library.Labels);
    }

    // Windows additions

    [Fact]
    public void LabelsSurviveReopeningThePreferences()
    {
        using var file = new PreferencesFile();
        var library = EventLabelLibrary.Defaults.Add("Society", "#8E4EC6", out var added);
        file.Open().Set(LabelsKey, library);

        var reopened = file.Open().Get(LabelsKey, EventLabelLibrary.Empty);

        Assert.Equal(library, reopened);
        Assert.Equal(added, reopened.Label(added.Id));
    }

    [Fact]
    public void TheStoredFormIsTheMacs()
    {
        var json = JsonDefaults.Serialize(new EventLabelLibrary([EventLabelLibrary.Defaults.Labels[0]]));

        Assert.Equal(
            "{\"version\":1,\"labels\":[{\"id\":\"4C1D0E9A-0000-4000-A000-000000000001\",\"name\":\"Important\",\"colorHex\":\"#E5484D\"}]}",
            json);
    }

    [Fact]
    public void FindingALabelByIdIncludingNone()
    {
        var library = EventLabelLibrary.Defaults;

        Assert.Equal("Personal", library.Label(EventLabelLibrary.BuiltIn.Personal)?.Name);
        Assert.Null(library.Label(null));
        Assert.Null(library.Label(Guid.NewGuid()));
    }

    [Fact]
    public void TheLibraryKnowsWhenItIsFull()
    {
        var library = EventLabelLibrary.Empty;
        for (var index = 0; index < EventLabelLibrary.MaximumCount - 1; index++)
        {
            library = library.Add($"Label {index}", "#30A46C", out _);
        }
        Assert.False(library.IsFull);

        library = library.Add("One more", "#30A46C", out _);

        Assert.True(library.IsFull);
    }

    /// <summary>The Mac counts an emoji as one character of the 24, and compares names as Foundation does.</summary>
    [Fact]
    public void NamesAreMeasuredAndComparedAsTheMacDoes()
    {
        var library = EventLabelLibrary.Empty.Add("Straße", "#30A46C", out _);

        Assert.Null(library.Validate("🎓" + new string('a', 23)));
        Assert.Equal(EventLabelNameError.TooLong, library.Validate("🎓" + new string('a', 24)));
        Assert.Equal(EventLabelNameError.Duplicate, library.Validate("STRASSE"));
    }

    [Fact]
    public void AColourWithSpacesInsideIsReadAsTheMacReadsIt()
    {
        var library = EventLabelLibrary.Empty.Add("Reading", "#30A46C", out var label);

        library = library.Recolor(label.Id, "#12 34 56");

        Assert.Equal("#12 34 56", library.Labels[0].ColorHex);
        Assert.Equal("#123456", library.Labels[0].Color.HexString);
        Assert.Equal(SrgbColor.SystemGray, (label with { ColorHex = "nope" }).Color);
    }

    [Fact]
    public void RefusalsSayWhy()
    {
        Assert.Equal("Give the label a name.", EventLabelNameError.Empty.Message());
        Assert.Equal("Names can be up to 24 characters.", EventLabelNameError.TooLong.Message());
        Assert.Equal("There is already a label with that name.", EventLabelNameError.Duplicate.Message());
        Assert.Equal("Give the label a name.", Assert.Throws<EventLabelNameException>(() => EventLabelLibrary.Empty.Add(" ", "#30A46C", out _)).Message);
    }
}
