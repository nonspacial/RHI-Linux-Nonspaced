using RHI.Desktop.ViewModels;

namespace RHI.Core.Tests;

/// <summary>#309 (upstream 2.9.0): links in the message of the day can be clicked; only http/https links become links.</summary>
public sealed class MotdLinkTests
{
    private static (string Text, string? Url)[] Parts(string text) => MotdText.Segments(text).Select(s => (s.Text, s.Url)).ToArray();

    [Fact]
    public void PlainText_IsOneRun_LineBreaksKept()
        => Assert.Equal([("Welcome to RHI.\nEnjoy.", (string?)null)], Parts("Welcome to RHI.\nEnjoy."));

    [Fact]
    public void AMarkdownLink_ShowsItsText_AndOpensItsAddress()
        => Assert.Equal(
            [("Read the ", (string?)null), ("guide", "https://github.com/RankFTW/RHI/wiki"), (" first.", null)],
            Parts("Read the [guide](https://github.com/RankFTW/RHI/wiki) first."));

    [Fact]
    public void ABareAddress_IsALink_WithoutTheSentencesFullStop()
        => Assert.Equal(
            [("Join us at ", (string?)null), ("https://discord.gg/abc", "https://discord.gg/abc"), (".", null)],
            Parts("Join us at https://discord.gg/abc."));

    [Theory]
    [InlineData("[open](file:///etc/passwd)", "open")]
    [InlineData("[run](javascript:alert(1))", "run")]
    [InlineData("[x](ftp://example.com/a)", "x")]
    [InlineData("[creds](https://user:pass@example.com/)", "creds")]
    [InlineData("[rel](/relative/path)", "rel")]
    public void OnlyWebLinks_BecomeLinks_AnythingElseIsItsTextOnly(string text, string expectedStart)
    {
        var parts = MotdText.Segments(text);
        Assert.All(parts, part => Assert.Null(part.Url));
        Assert.StartsWith(expectedStart, string.Concat(parts.Select(p => p.Text)));
        Assert.DoesNotContain("file:", string.Concat(parts.Select(p => p.Text)));
    }

    [Fact]
    public void SeveralLinks_InOrder()
        => Assert.Equal(
            [("a", (string?)"http://a.example/"), (" and ", null), ("http://b.example/x", "http://b.example/x")],
            Parts("[a](http://a.example) and http://b.example/x"));

    [Fact]
    public void Empty_HasNoRuns() => Assert.Empty(MotdText.Segments(""));

    [Fact]
    public void TheDialogText_HasItsLinksAsButtons_ThatOpenThroughTheOpener()
    {
        string? opened = null;
        var block = RHI.Desktop.Views.MotdView.Build("See [the notes](https://example.org/notes) or [this](file:///x).", url => opened = url);

        var inlines = block.Inlines!.ToArray();
        var link = Assert.Single(inlines.OfType<Avalonia.Controls.Documents.InlineUIContainer>());
        var button = Assert.IsType<Avalonia.Controls.Button>(link.Child);
        Assert.Equal("the notes", ((Avalonia.Controls.TextBlock)button.Content!).Text);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal("https://example.org/notes", opened);
        Assert.Equal(" or this.", string.Concat(inlines.OfType<Avalonia.Controls.Documents.Run>().Skip(1).Select(run => run.Text)));
    }
}
