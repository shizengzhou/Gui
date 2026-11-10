using System;
using System.Collections.Generic;
using System.IO;
using Gui.Cli;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// Covers "+new-tab": the IPC wire format both ends share, the pipe name, and the argument
/// parsing that turns a command line into an IpcProtocol.Request.
/// </summary>
public class CliNewTabTests
{
    [Fact]
    public void ProtocolRoundTripsARequest()
    {
        var request = new IpcProtocol.Request
        {
            Action = "new-tab",
            WorkingDirectory = @"C:\dev\motr",
            Command = "pwsh -NoLogo",
            Title = "build",
        };

        IpcProtocol.Request? parsed = IpcProtocol.ParseRequest(IpcProtocol.Serialize(request));

        Assert.NotNull(parsed);
        Assert.Equal("new-tab", parsed!.Action);
        Assert.Equal(@"C:\dev\motr", parsed.WorkingDirectory);
        Assert.Equal("pwsh -NoLogo", parsed.Command);
        Assert.Equal("build", parsed.Title);
    }

    [Fact]
    public void ProtocolOmitsAbsentFieldsAndReadsThemBackAsNull()
    {
        string json = IpcProtocol.Serialize(new IpcProtocol.Request { Action = "new-tab" });

        Assert.DoesNotContain("workingDirectory", json);
        IpcProtocol.Request? parsed = IpcProtocol.ParseRequest(json);
        Assert.NotNull(parsed);
        Assert.Null(parsed!.WorkingDirectory);
        Assert.Null(parsed.Command);
    }

    [Fact]
    public void ProtocolRejectsMalformedJson()
    {
        Assert.Null(IpcProtocol.ParseRequest("{not json"));
        Assert.Null(IpcProtocol.ParseResponse("[]"));
    }

    [Fact]
    public void ResponseRoundTrips()
    {
        IpcProtocol.Response? ok = IpcProtocol.ParseResponse(
            IpcProtocol.Serialize(new IpcProtocol.Response { Ok = true }));
        Assert.NotNull(ok);
        Assert.True(ok!.Ok);
        Assert.Null(ok.Error);

        IpcProtocol.Response? failed = IpcProtocol.ParseResponse(
            IpcProtocol.Serialize(new IpcProtocol.Response { Ok = false, Error = "no window is open" }));
        Assert.Equal("no window is open", failed!.Error);
    }

    [Fact]
    public void PipeNameCarriesClassUserAndSession()
    {
        string name = IpcProtocol.PipeName();

        Assert.StartsWith("gui-terminal-" + IpcProtocol.DefaultClass + "-", name);
        Assert.Contains(Environment.UserName.ToLowerInvariant(), name);
        Assert.NotEqual(name, IpcProtocol.PipeName("Other"));
    }

    [Fact]
    public void PipeNameFoldsCharactersAPipeCannotCarry()
    {
        string name = IpcProtocol.PipeName(@"my\class name");

        Assert.DoesNotContain(@"\", name);
        Assert.DoesNotContain(" ", name);
    }

    private static NewTab.Options Parse(params string[] args) =>
        NewTab.Parse(new List<string>(args), @"C:\work");

    [Fact]
    public void DefaultsTheDirectoryToWhereTheCommandRan()
    {
        NewTab.Options options = Parse();

        Assert.Null(options.Error);
        Assert.Equal(@"C:\work", options.WorkingDirectory);
        Assert.Null(options.Command);
    }

    [Fact]
    public void DashETakesEveryFollowingArgument()
    {
        NewTab.Options options = Parse("--title=build", "-e", "pwsh", "-NoLogo", "-Command", "echo hi");

        Assert.Null(options.Error);
        Assert.Equal("build", options.Title);
        Assert.Equal("pwsh -NoLogo -Command \"echo hi\"", options.Command);
    }

    [Fact]
    public void WorkingDirectoryResolvesAgainstTheCurrentDirectory()
    {
        NewTab.Options options = Parse("--working-directory=sub/dir");

        Assert.Null(options.Error);
        Assert.Equal(Path.GetFullPath(Path.Combine(@"C:\work", "sub/dir")), options.WorkingDirectory);
    }

    [Fact]
    public void InheritKeepsTheConfiguredDirectory()
    {
        NewTab.Options options = Parse("--working-directory=inherit");

        Assert.Null(options.Error);
        Assert.Null(options.WorkingDirectory);
    }

    [Fact]
    public void HomeExpandsToTheUserProfile()
    {
        NewTab.Options options = Parse("--working-directory=home");

        Assert.Null(options.Error);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), options.WorkingDirectory);
    }

    [Fact]
    public void SurfaceIdOnlyAcceptsTheFocusedSurface()
    {
        Assert.Null(Parse("--surface-id=0").Error);
        Assert.NotNull(Parse("--surface-id=7").Error);
    }

    [Fact]
    public void UnknownOptionIsAnError()
    {
        NewTab.Options options = Parse("--bogus=1");

        Assert.NotNull(options.Error);
        Assert.Contains("--bogus", options.Error!);
    }

    [Fact]
    public void MissingValueIsAnError()
    {
        Assert.NotNull(Parse("--working-directory").Error);
        Assert.NotNull(Parse("--command").Error);
    }

    [Fact]
    public void HelpWins()
    {
        Assert.True(Parse("--help", "--bogus").Help);
    }
}
