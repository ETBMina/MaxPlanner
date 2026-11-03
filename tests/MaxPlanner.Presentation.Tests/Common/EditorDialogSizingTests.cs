using AwesomeAssertions;
using MaxPlanner.Presentation.Common.Dialogs;

namespace MaxPlanner.Presentation.Tests.Common;

public sealed class EditorDialogSizingTests
{
    [Theory]
    [InlineData(1920, 840)]
    [InlineData(1008, 840)]
    [InlineData(1007, 640)]
    [InlineData(800, 640)]
    [InlineData(688, 640)]
    [InlineData(640, 592)]
    [InlineData(500, 452)]
    [InlineData(300, 320)]
    public void Dialog_width_depends_only_on_the_window(double windowWidth, double expected) =>
        EditorDialogSizing.DialogWidth(windowWidth).Should().Be(expected);

    [Fact]
    public void Content_width_is_the_dialog_width_less_its_padding() =>
        EditorDialogSizing.ContentWidth(1200).Should().Be(792);

    [Theory]
    [InlineData(1200, 800, false)]
    [InlineData(639, 800, true)]
    [InlineData(1200, 599, true)]
    [InlineData(640, 600, false)]
    public void Small_or_short_windows_get_a_full_size_dialog(double width, double height, bool expected) =>
        EditorDialogSizing.IsFullSize(width, height).Should().Be(expected);
}
