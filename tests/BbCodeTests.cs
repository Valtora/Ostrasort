using Ostrasort;
using Xunit;

namespace Ostrasort.Tests;

/// <summary>
/// The Description column feeds on Steam Workshop / mod_info BBCode. These pin the
/// cleanup: tags drop to their inner text, lists become bullets, entities decode,
/// and Flatten collapses the result to a single line for the (trimmed) cell.
/// </summary>
public class BbCodeTests
{
    [Fact]
    public void StripsInlineFormattingTags()
    {
        Assert.Equal("Bold and italic text",
            BbCode.ToPlainText("[b]Bold[/b] and [i]italic[/i] text"));
    }

    [Fact]
    public void KeepsLinkTextDropsUrl()
    {
        Assert.Equal("See the wiki here.",
            BbCode.ToPlainText("See the wiki [url=https://example.com]here[/url]."));
    }

    [Fact]
    public void ListItemsBecomeBullets()
    {
        var text = BbCode.ToPlainText("[list][*]First[*]Second[/list]");
        Assert.Contains("• First", text);
        Assert.Contains("• Second", text);
    }

    [Fact]
    public void DecodesHtmlEntities()
    {
        Assert.Equal("Fixes & tweaks", BbCode.ToPlainText("Fixes &amp; tweaks"));
    }

    [Fact]
    public void DropsImageTagsWholesale()
    {
        Assert.Equal("Before after",
            BbCode.ToPlainText("Before [img]https://example.com/x.png[/img] after"));
    }

    [Fact]
    public void FlattenCollapsesToOneLine()
    {
        Assert.Equal("A B C", BbCode.Flatten("A\n\nB   C"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInputYieldsEmptyString(string? raw)
    {
        Assert.Equal("", BbCode.ToPlainText(raw));
        Assert.Equal("", BbCode.Flatten(raw));
    }
}
