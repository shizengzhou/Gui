using System;
using System.IO;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// "window-save-state" command recovery: a saved "-File" command must be dropped when its
/// script is gone, or the fresh shell exits at once and takes the window with it. The path
/// is resolved against the pane's directory, the same way the shell resolves it.
/// </summary>
public class RestoredCommandTests : IDisposable
{
    private const string Variable = "GUI_RESTORED_COMMAND_TEST_DIR";

    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "gui-restored-command-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Variable, null);
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // A leftover temp directory must not fail the test run.
        }
    }

    private string Script()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "helper.ps1");
        File.WriteAllText(path, "# helper");
        return path;
    }

    [Fact]
    public void CommandWithoutFileSwitchIsLeftAlone()
    {
        Assert.True(RestoredCommand.Usable("pwsh -NoLogo", _dir));
        Assert.True(RestoredCommand.Usable("cmd /c build.bat", _dir));
        Assert.True(RestoredCommand.Usable(string.Empty, _dir));
    }

    [Fact]
    public void ExistingAbsoluteScriptIsUsable()
    {
        string path = Script();
        Assert.True(RestoredCommand.Usable($"pwsh -File \"{path}\"", _dir));
    }

    [Fact]
    public void MissingAbsoluteScriptIsRejected()
    {
        string path = Path.Combine(_dir, "gone.ps1");
        Assert.False(RestoredCommand.Usable($"pwsh -File \"{path}\"", _dir));
    }

    [Fact]
    public void RelativeScriptResolvesAgainstThePaneDirectory()
    {
        Script();
        Assert.True(RestoredCommand.Usable("pwsh -File .\\helper.ps1", _dir));
        Assert.True(RestoredCommand.Usable("pwsh -File helper.ps1", _dir));
    }

    [Fact]
    public void RelativeScriptMissingFromThePaneDirectoryIsRejected()
    {
        Assert.False(RestoredCommand.Usable("pwsh -File .\\helper.ps1", _dir));
    }

    [Fact]
    public void EnvironmentVariablesInThePathAreExpanded()
    {
        Script();
        Environment.SetEnvironmentVariable(Variable, _dir);
        Assert.True(RestoredCommand.Usable($"pwsh -File \"%{Variable}%\\helper.ps1\"", _dir));
    }

    [Fact]
    public void ArgumentsAfterTheScriptDoNotChangeTheVerdict()
    {
        string path = Script();
        Assert.True(RestoredCommand.Usable($"pwsh -File \"{path}\" -Verbose", _dir));
    }

    [Fact]
    public void UnparsableCommandIsLeftAlone()
    {
        // Nothing can be checked when the switch names no path or the quote never closes,
        // so the command survives instead of being thrown away.
        Assert.True(RestoredCommand.Usable("pwsh -File", _dir));
        Assert.True(RestoredCommand.Usable("pwsh -File \"unterminated", _dir));
    }
}
