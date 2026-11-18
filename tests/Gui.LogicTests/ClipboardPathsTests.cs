using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

public class ClipboardPathsTests
{
    [Fact]
    public void Quote_wraps_path_in_double_quotes()
    {
        Assert.Equal("\"C:\\Users\\szz\\file.txt\"", ClipboardPaths.Quote("C:\\Users\\szz\\file.txt"));
    }

    [Fact]
    public void Join_spaces_multiple_paths()
    {
        Assert.Equal(
            "\"C:\\a.txt\" \"D:\\my file.txt\"",
            ClipboardPaths.Join(new[] { "C:\\a.txt", "D:\\my file.txt" }));
    }

    [Fact]
    public void Join_returns_empty_for_no_paths()
    {
        Assert.Equal(string.Empty, ClipboardPaths.Join(System.Array.Empty<string>()));
    }
}
