using System;
using System.Text;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// The OSC 133 marks the injected PowerShell wrapper writes: a command is running between
/// C and D, and D closes it with the shell's exit code.
/// </summary>
public class ShellIntegrationTests
{
    private static void Feed(ShellMarkParser parser, string text) =>
        parser.Feed(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void InjectionEmitsTheMarksAndHonorsTheMode()
    {
        const string prefix = "pwsh.exe -NoExit -EncodedCommand ";

        Assert.Equal("pwsh.exe", ShellIntegration.Inject("pwsh.exe", "none", ShellIntegrationFeatures.Default));
        Assert.Equal("pwsh.exe", ShellIntegration.Inject("pwsh.exe", "bash", ShellIntegrationFeatures.Default));

        string injected = ShellIntegration.Inject("pwsh.exe", "detect", ShellIntegrationFeatures.Default);
        Assert.StartsWith(prefix, injected);
        string script = Encoding.Unicode.GetString(
            Convert.FromBase64String(injected[prefix.Length..]));

        Assert.Contains("]133;A", script);
        Assert.Contains("]133;B", script);
        Assert.Contains("]133;C", script);
        Assert.Contains("]133;D;$status", script);
        Assert.Contains("]7;file:///", script);
        Assert.Contains("$global:__guiCursor = $true", script);
        Assert.Contains("$global:__guiTitle = $true", script);

        string disabled = ShellIntegration.Inject("pwsh.exe", "powershell",
            new ShellIntegrationFeatures(false, false, false, false, false, false, false));
        string trimmed = Encoding.Unicode.GetString(Convert.FromBase64String(disabled[prefix.Length..]));
        Assert.Contains("$global:__guiCursor = $false", trimmed);
        Assert.Contains("$global:__guiTitle = $false", trimmed);
    }

    [Fact]
    public void MarksTrackACommandLifetime()
    {
        var parser = new ShellMarkParser();
        (int Exit, TimeSpan Duration)? finished = null;
        parser.CommandFinished = (exit, duration) => finished = (exit, duration);

        Feed(parser, "\u001b]133;A\u0007prompt\u001b]133;B\u0007");
        Assert.False(parser.CommandRunning);

        Feed(parser, "\u001b]133;C\u0007output");
        Assert.True(parser.CommandRunning);

        Feed(parser, "\u001b]133;D;7\u0007");
        Assert.False(parser.CommandRunning);
        Assert.NotNull(finished);
        Assert.Equal(7, finished!.Value.Exit);
        Assert.True(finished.Value.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public void MarksSplitAcrossReadsAreStillParsed()
    {
        var parser = new ShellMarkParser();
        int exit = -1;
        parser.CommandFinished = (code, _) => exit = code;

        Feed(parser, "\u001b]13");
        Feed(parser, "3;C\u0007");
        Assert.True(parser.CommandRunning);

        Feed(parser, "\u001b]133;D");
        Feed(parser, ";3\u0007");
        Assert.False(parser.CommandRunning);
        Assert.Equal(3, exit);
    }

    [Fact]
    public void OtherOscSequencesAreIgnored()
    {
        var parser = new ShellMarkParser();
        bool finished = false;
        parser.CommandFinished = (_, _) => finished = true;

        Feed(parser, "\u001b]7;file:///C:/temp\u0007");
        Feed(parser, "\u001b]0;title\u0007");

        Assert.False(parser.CommandRunning);
        Assert.False(finished);
    }

    [Fact]
    public void ANewPromptClosesAClostCommand()
    {
        var parser = new ShellMarkParser();
        Feed(parser, "\u001b]133;C\u0007");
        Assert.True(parser.CommandRunning);

        Feed(parser, "\u001b]133;A\u0007");
        Assert.False(parser.CommandRunning);
    }
}
