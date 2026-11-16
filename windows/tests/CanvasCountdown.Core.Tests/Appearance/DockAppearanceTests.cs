namespace CanvasCountdown.Tests.Appearance;

/// <summary>
/// Countdown appearance preferences: bounded sizing that cannot clip, readable
/// colours, working presets, and immediate application to the live tile.
/// </summary>
public sealed class DockAppearanceTests
{
    private const string AppearanceKey = "CanvasCountdown.settings.dockAppearance";

    private static readonly double[] TileSizes = [32, 48, 64, 128, 256, 1_024];
    private static readonly string[] Values = ["0", "7", "21", "157", "1024", DockTileLayout.Placeholder];

    // Sizing

    [Fact]
    public void EverySizeAndWeightCombinationFitsEveryValue()
    {
        foreach (var size in TileSizes)
        {
            foreach (var numberSize in Enum.GetValues<DockNumberSize>())
            {
                foreach (var weight in Enum.GetValues<DockNumberWeight>())
                {
                    foreach (var value in Values)
                    {
                        var layout = DockTileLayout.Make(new TileRect(0, 0, size, size), value, numberSize, weight);
                        var ink = DockTileLayout.CountdownInkSize(value, layout.CountdownFontSize, weight);

                        Assert.True(
                            ink.Width <= layout.CountdownRect.Width + 0.5,
                            $"{value} at {numberSize.Title()}/{weight.Title()}/{size} overflows");
                        Assert.True(
                            ink.Height <= layout.CountdownRect.Height + 0.5,
                            $"{value} at {numberSize.Title()}/{weight.Title()}/{size} overflows");
                        Assert.True(
                            layout.CountdownRect.MaxY <= layout.TileRect.MaxY + 0.5,
                            "The number band must stay inside the tile");
                    }
                }
            }
        }
    }

    [Fact]
    public void LargerSizePreferencesProduceLargerNumbers()
    {
        var previous = 0.0;
        foreach (var numberSize in Enum.GetValues<DockNumberSize>())
        {
            var layout = DockTileLayout.Make(new TileRect(0, 0, 128, 128), "7", numberSize);

            Assert.True(
                layout.CountdownFontSize > previous,
                $"{numberSize.Title()} should not be smaller than the size below it");
            previous = layout.CountdownFontSize;
        }
    }

    [Fact]
    public void MediumKeepsTheDocumentedCentralProportion()
    {
        var layout = DockTileLayout.Make(new TileRect(0, 0, 128, 128), "7", DockNumberSize.Medium);
        var fraction = layout.CountdownRect.Height / layout.TileRect.Height;

        Assert.InRange(fraction, 0.60, 0.70);
    }

    [Fact]
    public void AdaptiveScalingBetweenDigitLengthsSurvivesEverySizePreference()
    {
        foreach (var numberSize in Enum.GetValues<DockNumberSize>())
        {
            var one = FontSize("7", numberSize);
            var two = FontSize("21", numberSize);
            var three = FontSize("157", numberSize);
            var four = FontSize("1024", numberSize);

            Assert.True(one > two, numberSize.Title());
            Assert.True(two > three, numberSize.Title());
            Assert.True(three > four, numberSize.Title());
        }
    }

    // Colour and contrast

    /// <summary>
    /// The themes the app ships are readable. Nothing enforces that at runtime
    /// any more, so it is worth checking the ones with no user behind them.
    /// </summary>
    [Fact]
    public void EveryPresetIsReadable()
    {
        foreach (var preset in DockThemePresets.Selectable)
        {
            var appearance = DockAppearance.Applying(preset, DockAppearance.Defaults);

            Assert.True(appearance.HasSufficientContrast, $"{preset.Title()} does not meet the contrast floor");
        }
    }

    /// <summary>
    /// The colour wells do what they say. Picking white means white, even on
    /// white: the countdown belongs to whoever is looking at it, and a
    /// guideline is a reason to mention something rather than to overrule it.
    /// </summary>
    [Fact]
    public void LowContrastColoursAreKeptExactlyAsChosen()
    {
        var appearance = DockAppearance.Defaults with
        {
            Colours = DockAppearance.Defaults.Colours with
            {
                BackgroundTop = "#FFFFFF",
                BackgroundBottom = "#FFFFFF",
                Number = "#FFFFF0",
                Label = "#FEFEFE",
            },
        };

        Assert.False(appearance.HasSufficientContrast, "Settings still has something true to say about these");
        Assert.Equal("#FFFFF0", appearance.Colours.Number);
        Assert.Equal("#FEFEFE", appearance.Colours.Label);
        Assert.Equal("#FFFFF0", appearance.NumberColor.HexString);
        Assert.Equal("#FEFEFE", appearance.LabelColor.HexString);
    }

    [Fact]
    public void ContrastRatioMatchesKnownValues()
    {
        var white = SrgbColor.FromHex("#FFFFFF")!.Value;
        var black = SrgbColor.FromHex("#000000")!.Value;

        Assert.Equal(21, SrgbColor.ContrastRatio(white, black), 0.01);
        Assert.Equal(1, SrgbColor.ContrastRatio(white, white), 0.01);
    }

    [Fact]
    public void HexRoundTrip()
    {
        var colour = SrgbColor.FromHex("#21448C");

        Assert.NotNull(colour);
        Assert.Equal("#21448C", colour.Value.HexString);
        Assert.Null(SrgbColor.FromHex("not a colour"));
    }

    [Fact]
    public void PresetsChangeColoursButKeepSizePreferences()
    {
        var appearance = DockAppearance.Defaults with
        {
            NumberSize = DockNumberSize.ExtraLarge,
            NumberWeight = DockNumberWeight.Bold,
        };

        var themed = DockAppearance.Applying(DockThemePreset.Dark, appearance);

        Assert.Equal(DockNumberSize.ExtraLarge, themed.NumberSize);
        Assert.Equal(DockNumberWeight.Bold, themed.NumberWeight);
        Assert.Equal(DockThemePreset.Dark, themed.Preset);
        Assert.Equal(DockThemePreset.Dark.Colours(), themed.Colours);
    }

    // Rendering

    /// <summary>
    /// The Mac rendered the tile and looked for red pixels. Here the drawing
    /// is the Windows renderer's; what this module owns is that the plan it
    /// executes carries the chosen colour to the number.
    /// </summary>
    [Fact]
    public void RenderedTileUsesTheChosenNumberColour()
    {
        var appearance = DockAppearance.Defaults with
        {
            Colours = DockAppearance.Defaults.Colours with
            {
                BackgroundTop = "#000000",
                BackgroundBottom = "#000000",
                Number = "#FF0000",
            },
        };

        var plan = DockTileRenderPlan.Make(appearance, 21, "DAYS", 128);

        Assert.Equal("#FF0000", plan.Countdown.Color.HexString);
        Assert.Equal("21", plan.Countdown.Text);
        Assert.Equal("#000000", plan.GradientTopColor.HexString);
        Assert.Equal("#000000", plan.GradientBottomColor.HexString);
    }

    // Persistence

    [Fact]
    public void AppearanceIsPersistedAndAppliedToTheTileImmediately()
    {
        using var file = new PreferencesFile();
        var appearance = DockAppearance.Defaults with
        {
            NumberSize = DockNumberSize.ExtraLarge,
            Preset = DockThemePreset.Dark,
            Colours = DockThemePreset.Dark.Colours()!,
        };
        file.Open().Set(AppearanceKey, appearance);

        var reloaded = file.Open().Get(AppearanceKey, DockAppearance.Defaults);

        Assert.Equal(DockNumberSize.ExtraLarge, reloaded.NumberSize);
        Assert.Equal(DockThemePreset.Dark, reloaded.Preset);
        Assert.Equal(DockThemePreset.Dark.Colours()!.Number, reloaded.Colours.Number);
        Assert.Equal(appearance, reloaded);
    }

    // Windows additions

    /// <summary>The values a Mac computed for the shipped themes.</summary>
    [Theory]
    [InlineData(DockThemePreset.DefaultBlue, 6.226174441509967, 5.492257841603996)]
    [InlineData(DockThemePreset.DaysMatter, 3.9160689400486777, 5.836759475659064)]
    [InlineData(DockThemePreset.Dark, 14.726389464047934, 6.411759093359636)]
    [InlineData(DockThemePreset.HighContrast, 21.0, 19.555999999999997)]
    public void ContrastOfEveryPresetMatchesTheMac(DockThemePreset preset, double number, double label)
    {
        var appearance = DockAppearance.Applying(preset, DockAppearance.Defaults);

        Assert.Equal(number, appearance.NumberContrastRatio, 9);
        Assert.Equal(label, appearance.LabelContrastRatio, 9);
    }

    [Fact]
    public void BlendingMatchesTheMac()
    {
        Assert.Equal("#808080", SrgbColor.Blend(SrgbColor.Black, SrgbColor.White).HexString);
        Assert.Equal("#2F5FAF", DockAppearance.Defaults.AverageBackgroundColor.HexString);
    }

    /// <summary>What <c>NSColor(hex:)</c> and <c>NSColorHex.isValid</c> answered on a Mac.</summary>
    [Theory]
    [InlineData("#21448C", "#21448C", true)]
    [InlineData("21448c", "#21448C", true)]
    [InlineData("  #21448C\n", "#21448C", true)]
    [InlineData("#12 34 56", "#123456", false)]
    [InlineData("+12345", "#012345", true)]
    [InlineData("-00000", "#000000", true)]
    [InlineData("-12345", null, false)]
    [InlineData("#1234567", null, false)]
    [InlineData("#12345", null, false)]
    [InlineData("##123456", "#123456", true)]
    [InlineData("#GGGGGG", null, false)]
    [InlineData("0x1234", null, false)]
    [InlineData("١٢٣٤٥٦", null, false)]
    public void HexIsReadExactlyAsTheMacReadsIt(string text, string? colour, bool isValid)
    {
        Assert.Equal(colour, SrgbColor.FromHex(text)?.HexString);
        Assert.Equal(isValid, ColorHex.IsValid(text));
    }

    [Fact]
    public void ColoursThatCannotBeReadFallBackAsTheMacDid()
    {
        var appearance = DockAppearance.Defaults with
        {
            Colours = new DockColours { BackgroundTop = "?", BackgroundBottom = "", Number = "blue", Label = "#12" },
        };

        Assert.Equal(SrgbColor.SystemBlue, appearance.BackgroundTopColor);
        Assert.Equal(SrgbColor.SystemBlue, appearance.BackgroundBottomColor);
        Assert.Equal(SrgbColor.White, appearance.NumberColor);
        Assert.Equal(SrgbColor.White, appearance.LabelColor);
    }

    [Fact]
    public void TheStoredFormUsesTheMacKeys()
    {
        var id = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
        var appearance = DockAppearance.Defaults with { Preset = DockThemePreset.Custom, UserPresetId = id };

        var json = JsonDefaults.Serialize(appearance);

        Assert.Equal(
            "{\"preset\":\"custom\",\"numberSize\":\"medium\",\"numberWeight\":\"heavy\","
            + "\"colours\":{\"backgroundTop\":\"#21448C\",\"backgroundBottom\":\"#3D7AD1\",\"number\":\"#FFFFFF\",\"label\":\"#EAF1FF\"},"
            + "\"userPresetID\":\"6F9619FF-8B86-D011-B42D-00CF4FC964FF\"}",
            json);
        Assert.Equal(appearance, JsonDefaults.Deserialize<DockAppearance>(json));
        Assert.DoesNotContain("userPresetID", JsonDefaults.Serialize(DockAppearance.Defaults), StringComparison.Ordinal);
    }

    /// <summary>
    /// Swift's decoder refuses an appearance it cannot fully read, and the
    /// store falls back to the defaults rather than half-loading one.
    /// </summary>
    [Theory]
    [InlineData("{\"preset\":\"neon\",\"numberSize\":\"medium\",\"numberWeight\":\"heavy\",\"colours\":{\"backgroundTop\":\"#000000\",\"backgroundBottom\":\"#000000\",\"number\":\"#FFFFFF\",\"label\":\"#FFFFFF\"}}")]
    [InlineData("{\"preset\":\"dark\",\"numberSize\":\"medium\",\"numberWeight\":\"heavy\"}")]
    [InlineData("{\"preset\":\"dark\",\"numberSize\":\"medium\",\"numberWeight\":\"heavy\",\"colours\":{\"backgroundTop\":null,\"backgroundBottom\":\"#000000\",\"number\":\"#FFFFFF\",\"label\":\"#FFFFFF\"}}")]
    [InlineData("{\"preset\":\"Dark\",\"numberSize\":\"medium\",\"numberWeight\":\"heavy\",\"colours\":{\"backgroundTop\":\"#000000\",\"backgroundBottom\":\"#000000\",\"number\":\"#FFFFFF\",\"label\":\"#FFFFFF\"}}")]
    [InlineData("\"not an appearance\"")]
    public void AnAppearanceThatCannotBeReadFallsBackToTheDefaults(string stored)
    {
        Assert.Null(JsonDefaults.TryDeserialize<DockAppearance>(stored));

        using var document = System.Text.Json.JsonDocument.Parse(stored);
        var store = JsonPreferencesStore.InMemory();
        store.Set(AppearanceKey, document.RootElement);
        Assert.Equal(DockAppearance.Defaults, store.Get(AppearanceKey, DockAppearance.Defaults));
    }

    private static double FontSize(string text, DockNumberSize numberSize)
        => DockTileLayout.Make(new TileRect(0, 0, 128, 128), text, numberSize).CountdownFontSize;
}
