using RHI.Desktop.ViewModels;

namespace RHI.Core.Tests;

public sealed class OneShotStatusNoticeTests
{
    [Fact]
    public void Decorate_AppendsNoticeToTheNextStatusOnly()
    {
        var notice = new OneShotStatusNotice();

        var shown = notice.Raise("Token rejected");

        Assert.Same(shown, notice.Decorate(shown)); // displaying the notice itself does not consume it
        Assert.Equal("Installed ReShade · Token rejected", notice.Decorate("Installed ReShade"));
        Assert.Equal("Scan complete", notice.Decorate("Scan complete"));
    }

    [Fact]
    public void Decorate_WithoutNotice_ReturnsSameInstance()
    {
        var notice = new OneShotStatusNotice();
        const string status = "Ready";

        Assert.Same(status, notice.Decorate(status));
    }
}
