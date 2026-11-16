namespace CanvasCountdown.Tests.Appearance;

/// <summary>
/// Saved countdown themes: naming rules, what survives a restart, and what the
/// theme row claims is in force. All fixtures are invented.
/// </summary>
public sealed class DockThemePresetTests
{
    private const string PresetsKey = "CanvasCountdown.settings.dockThemePresets";

    private static DockColours Colours(
        string top = "#06381C",
        string bottom = "#0E5C30",
        string number = "#FFFFFF",
        string label = "#EAFBF1")
        => new() { BackgroundTop = top, BackgroundBottom = bottom, Number = number, Label = label };

    private static DockThemePresetLibrary Library(params string[] names)
    {
        var library = DockThemePresetLibrary.Empty;
        foreach (var name in names)
        {
            library = library.Add(name, Colours());
        }
        return library;
    }

    // Naming

    [Fact]
    public void ANameIsTrimmedBeforeItIsStored()
    {
        var library = DockThemePresetLibrary.Empty.Add("   Green Gradient  ", Colours(), out var id);

        Assert.NotNull(id);
        Assert.Equal("Green Gradient", library.Preset(id.Value)?.Name);
    }

    [Fact]
    public void AnEmptyNameIsRefused()
    {
        var result = DockThemePresetLibrary.Validate("   ", DockThemePresetLibrary.Empty);

        Assert.Equal(DockPresetNameValidation.Failure(DockPresetNameError.Empty), result);
    }

    [Fact]
    public void ANameLongerThanFortyCharactersIsRefused()
    {
        var tooLong = new string('a', 41);

        Assert.Equal(
            DockPresetNameValidation.Failure(DockPresetNameError.TooLong),
            DockThemePresetLibrary.Validate(tooLong, DockThemePresetLibrary.Empty));
        Assert.Equal(
            DockPresetNameValidation.Success(new string('a', 40)),
            DockThemePresetLibrary.Validate(new string('a', 40), DockThemePresetLibrary.Empty));
    }

    [Fact]
    public void ANameAlreadyUsedByAnotherPresetIsRefused()
    {
        var existing = Library("Green Gradient");

        Assert.Equal(
            DockPresetNameValidation.Failure(DockPresetNameError.Duplicate),
            DockThemePresetLibrary.Validate("green gradient", existing));
    }

    [Fact]
    public void ANameAlreadyUsedByABuiltInThemeIsRefused()
    {
        foreach (var preset in DockThemePresets.Selectable)
        {
            Assert.Equal(
                DockPresetNameValidation.Failure(DockPresetNameError.Duplicate),
                DockThemePresetLibrary.Validate(preset.Title(), DockThemePresetLibrary.Empty));
        }
        Assert.Equal(
            DockPresetNameValidation.Failure(DockPresetNameError.Duplicate),
            DockThemePresetLibrary.Validate("Custom", DockThemePresetLibrary.Empty));
    }

    [Fact]
    public void RenamingIgnoresThePresetsOwnName()
    {
        var existing = Library("Green Gradient");
        var id = existing.Presets[0].Id;

        existing = existing.Rename(id, "Green Gradient", out var keptName);
        Assert.True(keptName);
        existing = existing.Rename(id, "Forest", out var renamed);
        Assert.True(renamed);
        Assert.Equal("Forest", existing.Presets[0].Name);
    }

    [Fact]
    public void RenamingOntoAnotherPresetsNameIsRefused()
    {
        var existing = Library("Green Gradient", "Forest");
        var id = existing.Presets[0].Id;

        existing = existing.Rename(id, "Forest", out var renamed);

        Assert.False(renamed);
        Assert.Equal("Green Gradient", existing.Presets[0].Name);
    }

    // Duplicating

    [Fact]
    public void DuplicatingAppendsCopy()
    {
        var existing = Library("Green Gradient");
        var id = existing.Presets[0].Id;

        existing = existing.Duplicate(id, out var copy);

        Assert.NotNull(copy);
        Assert.Equal("Green Gradient Copy", existing.Preset(copy.Value)?.Name);
        Assert.Equal(Colours(), existing.Preset(copy.Value)?.Colours);
        Assert.Equal(2, existing.Presets.Count);
    }

    [Fact]
    public void DuplicatingTwiceStillProducesAUsableName()
    {
        var existing = Library("Green Gradient");
        var id = existing.Presets[0].Id;

        existing = existing.Duplicate(id);
        existing = existing.Duplicate(id, out var second);

        Assert.NotNull(second);
        Assert.Equal("Green Gradient Copy 2", existing.Preset(second.Value)?.Name);
        Assert.Equal(3, existing.Presets.Count);
    }

    // Deleting

    [Fact]
    public void DeletingRemovesOnlyThatPreset()
    {
        var existing = Library("Green Gradient", "Forest");
        var id = existing.Presets[0].Id;

        existing = existing.Remove(id);

        Assert.Equal(["Forest"], existing.Presets.Select(preset => preset.Name));
    }

    // What the theme row says

    [Fact]
    public void ABuiltInThemeIsNamedAsItself()
    {
        var appearance = DockAppearance.Applying(DockThemePreset.Dark, DockAppearance.Defaults);

        Assert.Equal(new DockThemeStatus.BuiltIn(DockThemePreset.Dark), DockThemePresetLibrary.Empty.Status(appearance));
        Assert.Equal("Dark", DockThemePresetLibrary.Empty.Status(appearance).Title);
    }

    [Fact]
    public void EditingABuiltInThemeIsUnsaved()
    {
        var dark = DockAppearance.Applying(DockThemePreset.Dark, DockAppearance.Defaults);
        var appearance = dark with
        {
            Colours = dark.Colours with { BackgroundTop = "#0A5C2E" },
            Preset = DockThemePreset.Custom,
        };

        var status = DockThemePresetLibrary.Empty.Status(appearance);

        Assert.Equal(new DockThemeStatus.Unsaved(), status);
        Assert.Equal("Custom — Unsaved", status.Title);
        Assert.True(status.IsSavable);
    }

    [Fact]
    public void ASavedPresetIsNamedAsItself()
    {
        var existing = Library("Green Gradient");
        var preset = existing.Presets[0];
        var appearance = DockAppearance.Applying(preset, DockAppearance.Defaults);

        Assert.Equal(new DockThemeStatus.Saved(preset), existing.Status(appearance));
        Assert.Equal("Green Gradient", existing.Status(appearance).Title);
        Assert.False(existing.Status(appearance).IsSavable);
    }

    [Fact]
    public void EditingASavedPresetIsMarkedModified()
    {
        var existing = Library("Green Gradient");
        var preset = existing.Presets[0];
        var applied = DockAppearance.Applying(preset, DockAppearance.Defaults);
        var appearance = applied with { Colours = applied.Colours with { Number = "#101010" } };

        var status = existing.Status(appearance);

        Assert.Equal(new DockThemeStatus.Modified(preset), status);
        Assert.Equal("Green Gradient — Modified", status.Title);
        Assert.Equal(preset, status.EditedPreset);
    }

    [Fact]
    public void APresetDeletedElsewhereDoesNotLeaveAGhostName()
    {
        var existing = Library("Green Gradient");
        var preset = existing.Presets[0];
        var appearance = DockAppearance.Applying(preset, DockAppearance.Defaults);

        var emptied = existing.Remove(preset.Id);

        Assert.True(
            emptied.Status(appearance) == new DockThemeStatus.Unsaved(),
            "A name that no longer exists must not still be displayed");
    }

    // Persistence

    [Fact]
    public void PresetsSurviveAReload()
    {
        using var file = new PreferencesFile();
        file.Open().Set(PresetsKey, Library("Green Gradient", "Forest"));

        var reloaded = file.Open().Get(PresetsKey, DockThemePresetLibrary.Empty);

        Assert.Equal(["Green Gradient", "Forest"], reloaded.Presets.Select(preset => preset.Name));
        Assert.Equal(Colours(), reloaded.Presets[0].Colours);
    }

    [Fact]
    public void ACorruptPresetIsDroppedAndTheRestSurvive()
    {
        var good = new UserDockThemePreset { Name = "Green Gradient", Colours = Colours() };
        var bad = new UserDockThemePreset { Name = "Broken", Colours = Colours(top: "not a colour") };
        var unnamed = new UserDockThemePreset { Name = "   ", Colours = Colours() };
        var encoded = JsonDefaults.Serialize(new DockThemePresetLibrary([good, bad, unnamed]));

        var decoded = JsonDefaults.Deserialize<DockThemePresetLibrary>(encoded)!;

        Assert.Equal(["Green Gradient"], decoded.Presets.Select(preset => preset.Name));
    }

    [Fact]
    public void ALibraryFromALaterVersionIsNotMisread()
    {
        var json = $$$"""
            {"version": 99, "presets": [{"id": "{{{Guid.NewGuid()}}}",
            "name": "From The Future", "colours": {"backgroundTop": "#000000",
            "backgroundBottom": "#000000", "number": "#FFFFFF", "label": "#FFFFFF"}}]}
            """;

        var decoded = JsonDefaults.Deserialize<DockThemePresetLibrary>(json)!;

        Assert.Empty(decoded.Presets);
    }

    [Fact]
    public void GarbageInDefaultsFallsBackToAnEmptyLibrary()
    {
        var store = JsonPreferencesStore.InMemory();
        store.Set(PresetsKey, "not json");

        Assert.Empty(store.Get(PresetsKey, DockThemePresetLibrary.Empty).Presets);
    }

    [Fact]
    public void TheThemeChoiceMatchesWhateverTheRowIsShowing()
    {
        var existing = Library("Green Gradient");
        var preset = existing.Presets[0];

        Assert.Equal(new DockThemeChoice.BuiltIn(DockThemePreset.Dark), DockThemeChoice.From(new DockThemeStatus.BuiltIn(DockThemePreset.Dark)));
        Assert.Equal(new DockThemeChoice.Saved(preset.Id), DockThemeChoice.From(new DockThemeStatus.Saved(preset)));
        Assert.True(
            DockThemeChoice.From(new DockThemeStatus.Modified(preset)) == new DockThemeChoice.Saved(preset.Id),
            "Edits still belong to the preset they came from");
        Assert.Equal(new DockThemeChoice.Unsaved(), DockThemeChoice.From(new DockThemeStatus.Unsaved()));
    }

    // Windows additions

    /// <summary>
    /// The Mac counts characters as people do, so an emoji is one of the forty;
    /// .NET's own length would count it as two and refuse a name the Mac took.
    /// </summary>
    [Fact]
    public void ANameIsMeasuredInCharactersAsPeopleCountThem()
    {
        var name = "🎓" + new string('a', 39);

        Assert.Equal(41, name.Length);
        Assert.Equal(DockPresetNameValidation.Success(name), DockThemePresetLibrary.Validate(name, DockThemePresetLibrary.Empty));
    }

    [Fact]
    public void NamesAreComparedAsTheMacComparesThem()
    {
        var existing = Library("Straße", "Café");

        Assert.Equal(DockPresetNameError.Duplicate, DockThemePresetLibrary.Validate("STRASSE", existing).Error);
        Assert.Equal(DockPresetNameError.Duplicate, DockThemePresetLibrary.Validate("café", existing).Error);
        Assert.True(DockThemePresetLibrary.Validate("Cafe", existing).IsSuccess, "An accent is not case");
    }

    /// <summary>
    /// A preset missing a field cannot be read at all, and on the Mac that
    /// takes the whole library with it: the store falls back to an empty one.
    /// </summary>
    [Fact]
    public void APresetMissingAFieldMakesTheLibraryUnreadable()
    {
        const string json = """{"version":1,"presets":[{"id":"6F9619FF-8B86-D011-B42D-00CF4FC964FF","name":"Forest"}]}""";

        Assert.Null(JsonDefaults.TryDeserialize<DockThemePresetLibrary>(json));
    }

    [Fact]
    public void TheStoredFormIsTheMacs()
    {
        var preset = new UserDockThemePreset
        {
            Id = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"),
            Name = "Forest",
            Colours = Colours(),
        };

        var json = JsonDefaults.Serialize(new DockThemePresetLibrary([preset]));

        Assert.Equal(
            "{\"version\":1,\"presets\":[{\"id\":\"6F9619FF-8B86-D011-B42D-00CF4FC964FF\",\"name\":\"Forest\","
            + "\"colours\":{\"backgroundTop\":\"#06381C\",\"backgroundBottom\":\"#0E5C30\",\"number\":\"#FFFFFF\",\"label\":\"#EAFBF1\"}}]}",
            json);
        Assert.Equal(new DockThemePresetLibrary([preset]), JsonDefaults.Deserialize<DockThemePresetLibrary>(json));
        Assert.Equal(DockThemePresetLibrary.CurrentVersion, JsonDefaults.Deserialize<DockThemePresetLibrary>("{}")!.Version);
    }

    [Fact]
    public void RefusalsSayWhy()
    {
        Assert.Equal("Give the preset a name.", DockPresetNameError.Empty.Message());
        Assert.Equal("Names can be up to 40 characters.", DockPresetNameError.TooLong.Message());
        Assert.Equal("There is already a theme with that name.", DockPresetNameError.Duplicate.Message());
    }
}
