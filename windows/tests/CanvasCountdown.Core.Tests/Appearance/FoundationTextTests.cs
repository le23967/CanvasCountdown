namespace CanvasCountdown.Tests.Appearance;

/// <summary>
/// The string rules names and release notes depend on, pinned to what
/// Foundation answered on a Mac for the same input.
/// </summary>
public sealed class FoundationTextTests
{
    [Theory]
    [InlineData("Straße", "STRASSE", true)]
    [InlineData("Straße", "strasse", true)]
    [InlineData("café", "CAFÉ", true)]
    [InlineData("Café", "café", true)]
    [InlineData("ﬁne", "FINE", true)]
    [InlineData("İstanbul", "istanbul", false)]
    [InlineData("ΣΑΣ", "σας", true)]
    [InlineData("Dark", "dark ", false)]
    [InlineData("a​b", "ab", false)]
    [InlineData("ｄａｒｋ", "dark", false)]
    [InlineData("Ａ", "a", false)]
    [InlineData("ı", "i", false)]
    [InlineData("ı", "I", false)]
    [InlineData("ſ", "s", true)]
    [InlineData("µ", "μ", true)]
    [InlineData("K", "k", true)]
    [InlineData("Ǆ", "ǆ", true)]
    [InlineData("ŉ", "ʼN", true)]
    [InlineData("Green Gradient", "green gradient", true)]
    public void CaseInsensitiveComparisonMatchesFoundation(string left, string right, bool same)
    {
        Assert.Equal(same, FoundationText.CaseInsensitiveEquals(left, right));
        Assert.Equal(same, FoundationText.CaseInsensitiveEquals(right, left));
    }

    [Theory]
    [InlineData("🎓 Exams", 7)]
    [InlineData("👨‍👩‍👧‍👦", 1)]
    [InlineData("é", 1)]
    [InlineData("🇦🇺🇦🇺", 2)]
    [InlineData("abc", 3)]
    public void CharactersAreCountedAsSwiftCountsThem(string text, int count)
    {
        Assert.Equal(count, FoundationText.CharacterCount(text));
    }

    [Fact]
    public void TrimmingWhitespaceLeavesLineBreaks()
    {
        Assert.Equal("\rFirst\r", FoundationText.TrimmingWhitespace(" \t\rFirst\r  "));
        Assert.Equal("First", FoundationText.TrimmingWhitespaceAndNewlines(" \t\rFirst\r\n  "));
    }

    [Fact]
    public void APrefixNeverSplitsACharacter()
    {
        Assert.Equal("🎓 E", FoundationText.Prefix("🎓 Exams", 3));
        Assert.Equal("abc", FoundationText.Prefix("abc", 10));
    }
}
