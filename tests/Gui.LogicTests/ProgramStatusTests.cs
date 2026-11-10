using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// The OSC 7501 records: one per id, replaced wholesale, cleared with their children, and
/// ranked so a person sees the record that matters most.
/// </summary>
public class ProgramStatusTests
{
    private static ProgramStatusInfo Info(
        ProgramState state,
        string id = "",
        string app = "",
        string title = "",
        string message = "",
        int progress = -1,
        ProgramKind kind = ProgramKind.None) =>
        new(state, kind, progress, id, app, title, message);

    [Fact]
    public void AReportReplacesItsRecordWholesale()
    {
        var tracker = new ProgramStatusTracker();

        Assert.True(tracker.Apply(Info(ProgramState.Working, progress: 10)));
        Assert.Equal(10, tracker.Current!.Value.Progress);

        Assert.True(tracker.Apply(Info(ProgramState.Working, progress: 60)));
        Assert.Equal(60, tracker.Current!.Value.Progress);

        // The new report carries no percentage, so the old one must not survive.
        Assert.True(tracker.Apply(Info(ProgramState.Done)));
        Assert.Equal(-1, tracker.Current!.Value.Progress);
    }

    [Fact]
    public void ABlockedReportOutranksEverythingElse()
    {
        var tracker = new ProgramStatusTracker();
        tracker.Apply(Info(ProgramState.Working, id: "build", progress: 20));
        tracker.Apply(Info(ProgramState.Done, id: "deploy"));
        tracker.Apply(Info(ProgramState.Error, id: "other"));
        tracker.Apply(Info(ProgramState.Blocked, id: "ask", kind: ProgramKind.Permission));

        ProgramStatusInfo current = tracker.Current!.Value;
        Assert.Equal(ProgramState.Blocked, current.State);
        Assert.Equal(ProgramKind.Permission, current.Kind);
        Assert.Equal("ask", current.Id);
    }

    [Fact]
    public void TheNewestReportWinsAmongEquals()
    {
        var tracker = new ProgramStatusTracker();
        tracker.Apply(Info(ProgramState.Working, id: "first"));
        tracker.Apply(Info(ProgramState.Working, id: "second"));

        Assert.Equal("second", tracker.Current!.Value.Id);

        tracker.Apply(Info(ProgramState.Working, id: "first", progress: 5));
        Assert.Equal("first", tracker.Current!.Value.Id);
    }

    [Fact]
    public void ClearingAnIdRemovesItsChildren()
    {
        var tracker = new ProgramStatusTracker();
        tracker.Apply(Info(ProgramState.Working, id: "build"));
        tracker.Apply(Info(ProgramState.Working, id: "build/test"));
        tracker.Apply(Info(ProgramState.Working, id: "deploy/us-east"));

        // "build" is not the visible record, so the surface does not have to refresh.
        Assert.False(tracker.Clear("build"));
        Assert.Equal("deploy/us-east", tracker.Current!.Value.Id);

        // An empty id is the full reset a terminal sends with ESC c.
        Assert.True(tracker.Clear(string.Empty));
        Assert.Null(tracker.Current);
    }

    [Fact]
    public void ARecordInheritsTheAppOfItsNearestAncestor()
    {
        var tracker = new ProgramStatusTracker();
        tracker.Apply(Info(ProgramState.Working, app: "terraform"));
        tracker.Apply(Info(ProgramState.Working, id: "us-east"));
        Assert.Equal("terraform", tracker.Current!.Value.App);

        tracker.Apply(Info(ProgramState.Working, id: "build", app: "cargo"));
        tracker.Apply(Info(ProgramState.Working, id: "build/test"));
        Assert.Equal("cargo", tracker.Current!.Value.App);

        // A record that names its own app keeps it.
        tracker.Apply(Info(ProgramState.Working, id: "build/tests", app: "ctest"));
        Assert.Equal("ctest", tracker.Current!.Value.App);
    }

    [Fact]
    public void ANewPromptDropsBusyRecordsButKeepsTheOutcome()
    {
        var tracker = new ProgramStatusTracker();
        tracker.Apply(Info(ProgramState.Working, id: "a"));
        tracker.Apply(Info(ProgramState.Blocked, id: "b"));
        tracker.Apply(Info(ProgramState.Error, id: "c"));
        tracker.Apply(Info(ProgramState.Done, id: "d"));

        Assert.True(tracker.ClearBusy());
        Assert.Equal(ProgramState.Error, tracker.Current!.Value.State);

        // Looking at the surface acknowledges the outcome.
        Assert.True(tracker.Acknowledge());
        Assert.Null(tracker.Current);

        // Nothing left to remove: the caller can skip its refresh.
        Assert.False(tracker.Acknowledge());
        Assert.False(tracker.ClearBusy());
    }

    [Fact]
    public void IdleReportsAreStoredButRankLast()
    {
        var tracker = new ProgramStatusTracker();
        tracker.Apply(Info(ProgramState.Idle, id: "rest"));

        Assert.Equal(ProgramState.Idle, tracker.Current!.Value.State);

        tracker.Apply(Info(ProgramState.Done, id: "job"));
        Assert.Equal("job", tracker.Current!.Value.Id);
    }

    [Fact]
    public void ControlAndDirectionCharactersAreStripped()
    {
        var tracker = new ProgramStatusTracker();
        tracker.Apply(Info(
            ProgramState.Blocked,
            app: "deploy\u202E",
            message: "Apply\u202E? \u0007"));

        ProgramStatusInfo current = tracker.Current!.Value;
        Assert.Equal("Apply?", current.Message);
        Assert.Equal("deploy", current.App);
    }

    [Fact]
    public void TheOldestRecordFallsOffPastTheLimit()
    {
        var tracker = new ProgramStatusTracker();
        for (int index = 0; index < 70; index++)
            tracker.Apply(Info(ProgramState.Done, id: "job" + index, title: "job" + index));

        // The newest 64 survive, so the six oldest are gone.
        Assert.Equal(64, tracker.Count);

        // "job0" was dropped, so clearing it changes nothing.
        Assert.False(tracker.Clear("job0"));
        Assert.Equal(64, tracker.Count);

        Assert.True(tracker.Clear("job69"));
        Assert.Equal("job68", tracker.Current!.Value.Id);
    }
}
