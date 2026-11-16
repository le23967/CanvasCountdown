namespace CanvasCountdown.Tests.Assistant;

/// <summary>
/// Local model management. No test reaches a real model server; a stub
/// answers instead.
/// </summary>
public sealed class LocalModelManagerTests
{
    private static readonly Uri LocalBase = new("http://localhost:11434/v1");

    [Fact]
    public void ApiRootIsDerivedFromTheChatAddress()
    {
        // The chat endpoint ends in /v1; the management API sits at the root.
        Assert.Equal("http://localhost:11434/", OllamaModelManager.ApiRoot(new Uri("http://localhost:11434/v1")).AbsoluteUri);

        // Without /v1 the address is left as it is. .NET writes an address with
        // no path with its slash, where Foundation leaves it off; it is the same
        // address either way.
        var bare = new Uri("http://localhost:11434");
        Assert.Equal(bare, OllamaModelManager.ApiRoot(bare));
        Assert.Equal("http://localhost:11434/", OllamaModelManager.ApiRoot(bare).AbsoluteUri);
    }

    [Fact]
    public void SuggestedModelsAreNamedWithApproximateSizes()
    {
        Assert.False(SuggestedModel.All.IsEmpty);
        foreach (var model in SuggestedModel.All)
        {
            Assert.NotEmpty(model.Name);
            Assert.True(
                model.ApproximateSize.Contains("about", StringComparison.OrdinalIgnoreCase),
                "Sizes are approximate and must be described that way");
            Assert.NotEmpty(model.Note);
        }
    }

    [Fact]
    public void SuggestionsAreOrderedSmallestFirst()
    {
        // A student on a laptop cares about the download before the quality.
        Assert.Equal("llama3.2:3b", SuggestedModel.All[0].Name);
    }

    [Fact]
    public void SetupCommandsAreTheRealOnes()
    {
        Assert.Equal("ollama serve", OllamaSetup.ServeCommand);
        Assert.Equal("ollama pull llama3.1:8b", OllamaSetup.PullCommand("llama3.1:8b"));
        Assert.StartsWith("https://", OllamaSetup.DownloadPage, StringComparison.Ordinal);

        // On Windows the server is installed with winget, not Homebrew, and the
        // install comes before starting it.
        Assert.Equal("winget install Ollama.Ollama", OllamaSetup.WingetCommand);
        Assert.Equal([OllamaSetup.WingetCommand, OllamaSetup.ServeCommand], OllamaSetup.SetupCommands);
        Assert.DoesNotContain(OllamaSetup.SetupCommands, command => command.Contains("brew", StringComparison.Ordinal));
    }

    [Fact]
    public void ModelSizeIsShownInHumanUnits()
    {
        var model = new LocalModel { Name = "llama3.1:8b", SizeBytes = 4_700_000_000 };

        Assert.True(
            model.SizeDescription(AssistantFixtures.EnglishUS).Contains("GB", StringComparison.Ordinal),
            "A multi-gigabyte download should not be printed in bytes");
    }

    [Fact]
    public void SuggestedModelListIsOfferedButNotEnforced()
    {
        Assert.False(AssistantService.Groq.Models.IsEmpty);

        // A name outside the list is still accepted, so a new release does not
        // have to wait for an app update.
        var settings = AssistantSettings.Applying(AssistantProvider.Cloud, AssistantSettings.Defaults) with
        {
            Model = "some-new-model-that-does-not-exist-yet",
        };
        Assert.Equal("some-new-model-that-does-not-exist-yet", settings.Model);

        // The same for an address no suggestion mentions.
        settings = settings with { BaseUrl = "https://ai.example.edu/v1" };
        Assert.Null(AssistantService.Matching(settings.BaseUrl));
        Assert.False(settings.StaysOnThisMachine);
    }

    // What the panel shows, against a stub

    [Fact]
    public async Task UnreachableServerReportsNoModels()
    {
        var inventory = await LocalModelInventory.RefreshAsync(new StubModelManager(reachable: false), LocalBase);

        Assert.False(inventory.IsReachable);
        Assert.Empty(inventory.Installed);
    }

    [Fact]
    public async Task InstalledModelsAreListed()
    {
        var manager = new StubModelManager(
            reachable: true,
            new LocalModel { Name = "llama3.1:8b", SizeBytes = 4_700_000_000 },
            new LocalModel { Name = "llama3.2:3b", SizeBytes = 2_000_000_000 });

        var inventory = await LocalModelInventory.RefreshAsync(manager, LocalBase);

        Assert.True(inventory.IsReachable);
        Assert.Equal(["llama3.1:8b", "llama3.2:3b"], inventory.Installed.Select(model => model.Name));
    }
}
