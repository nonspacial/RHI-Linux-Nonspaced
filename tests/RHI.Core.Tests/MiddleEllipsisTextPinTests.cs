using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Xunit;

namespace RHI.Core.Tests;

/// <summary>
/// MiddleEllipsisText depends on Avalonia 12.1.2 TextBlock internals (see the comment on the class). These fail on an
/// Avalonia upgrade or when the internals move, so the class is re-checked rather than silently mis-rendering.
/// </summary>
public sealed class MiddleEllipsisTextPinTests
{
    [Fact]
    public void AvaloniaIsThePinnedVersion()
    {
        var version = typeof(TextBlock).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        Assert.StartsWith("12.1.2", version);
    }

    [Fact]
    public void TextBlockInternalsStillExist()
    {
        var constraint = typeof(TextBlock).GetField("_constraint", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(constraint);
        Assert.True(constraint!.IsFamily, "_constraint must stay protected");
        Assert.Equal(typeof(Size), constraint.FieldType);

        var runCache = typeof(TextBlock).GetField("_textRunCache", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(runCache);

        var create = typeof(TextBlock).GetMethod("CreateTextLayout", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string)]);
        Assert.NotNull(create);
        Assert.True(create!.IsVirtual && create.IsFamily);

        var invalidate = typeof(TextBlock).GetMethod("InvalidateTextLayout", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes);
        Assert.NotNull(invalidate);
    }
}
