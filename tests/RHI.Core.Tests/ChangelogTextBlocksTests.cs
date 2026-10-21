using RHI.Desktop.Views;

namespace RHI.Core.Tests;

/// <summary>
/// #219: in Help → What's new a bare <c>&gt;</c> line (Markdown's paragraph break inside a blockquote) ends the paragraph
/// or list item like an empty line, so a paragraph after a blockquote list is its own block, not part of the last bullet.
/// Bullet continuation lines still join their bullet and the <c>&gt; [!…]</c> admonition tag stays hidden.
/// </summary>
public sealed class ChangelogTextBlocksTests
{
    [Fact]
    public void ParagraphAfterABlockquoteList_IsItsOwnBlock_AndTheBulletKeepsItsContinuation()
    {
        const string markdown = """
            > [!WARNING]
            > Before you go back, do this:
            >
            > - First bullet.
            > - Last bullet starts here
            >   and goes on here.
            >
            > Following paragraph starts here
            > and ends here.
            """;

        var blocks = ChangelogText.Blocks(markdown);

        Assert.Equal(new (int, string)[]
        {
            (0, "Before you go back, do this:"),
            (0, "• First bullet."),
            (0, "• Last bullet starts here and goes on here."),
            (0, "Following paragraph starts here and ends here.")
        }, blocks);
    }

    [Fact]
    public void TwoBlockquoteParagraphs_SplitByABareMarker_AreTwoBlocks()
    {
        var blocks = ChangelogText.Blocks("> One paragraph\n> on two lines.\n>\n> Another one.\n");

        Assert.Equal(new (int, string)[] { (0, "One paragraph on two lines."), (0, "Another one.") }, blocks);
    }

    [Fact]
    public void TheEmbeddedChangelog_TheWave6ParagraphStartsItsOwnBlock_NoBulletHoldsIt()
    {
        const string paragraph = "The wave-6 build forgets the NVIDIA settings";
        var blocks = ChangelogText.Blocks(ChangelogText.Load());

        var own = Assert.Single(blocks, block => block.Text.Contains(paragraph, StringComparison.Ordinal));
        Assert.Equal(0, own.Level);
        Assert.StartsWith(paragraph, own.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(blocks, block => block.Text.StartsWith('•') && block.Text.Contains(paragraph, StringComparison.Ordinal));
        // The bullet before it still ends with its own continuation line.
        Assert.Contains(blocks, block => block.Text.StartsWith("• A RenoDX add-on installed from a dropped or downloaded file", StringComparison.Ordinal)
                                         && block.Text.EndsWith("for the game.", StringComparison.Ordinal));
    }
}
