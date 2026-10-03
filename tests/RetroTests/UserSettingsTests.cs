namespace RetroTests;

public class UserSettingsTests
{
    [Theory]
    [InlineData("https://search.example/?q=%s", "https://search.example/?q=%s")]
    [InlineData("https://search.example/?q={query}", "https://search.example/?q=%s")]
    [InlineData("https://search.example/?q={searchTerms}", "https://search.example/?q=%s")]
    public void SearchTemplatePreservesCustomProvider(string input, string expected)
    {
        Assert.True(Retro96.UserSettings.TryNormalizeSearchTemplate(input, out string normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://search.example/")]
    public void SearchTemplateWithoutQueryPlaceholderIsRejected(string input)
    {
        Assert.False(Retro96.UserSettings.TryNormalizeSearchTemplate(input, out _));
        Assert.Equal(Retro96.UserSettings.DefaultSearchUrl,
            Retro96.UserSettings.NormalizeSearchTemplate(input));
    }
}
