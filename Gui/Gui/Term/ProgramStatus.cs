using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Gui.Term;

/// <summary>What a program says it is doing in an OSC 7501 report.</summary>
public enum ProgramState
{
    /// <summary>At rest, waiting for the user's next instruction.</summary>
    Idle = 0,

    /// <summary>Running on its own; the report may carry a percentage.</summary>
    Working = 1,

    /// <summary>Finished a piece of work the user has not looked at yet.</summary>
    Done = 2,

    /// <summary>Can't continue until the user does something; see <see cref="ProgramKind"/>.</summary>
    Blocked = 3,

    /// <summary>Failed and stopped.</summary>
    Error = 4,
}

/// <summary>What a blocked program needs from the user.</summary>
public enum ProgramKind
{
    /// <summary>The program did not say, or the state is not blocked.</summary>
    None = 0,

    /// <summary>Approval to do something.</summary>
    Permission = 1,

    /// <summary>An answer the user has to type.</summary>
    Question = 2,

    /// <summary>A login, token or other credential.</summary>
    Auth = 3,
}

/// <summary>
/// One program status report with its text already decoded and sanitized. A missing
/// percentage is -1, and an absent text is the empty string.
/// </summary>
public readonly record struct ProgramStatusInfo(
    ProgramState State,
    ProgramKind Kind,
    int Progress,
    string Id,
    string App,
    string Title,
    string Message);

/// <summary>
/// The OSC 7501 records of one surface. The specification keeps one record per id (an
/// empty id is the program itself), replaces a record wholesale with each report, removes
/// an id together with every record beneath it, and lets a record inherit the app of its
/// nearest ancestor. <see cref="Current"/> answers the record that matters most to a
/// person: a blocked program first, then a failure, then work in progress, then a
/// finished job.
/// </summary>
internal sealed class ProgramStatusTracker
{
    /// <summary>The specification allows at least 64 records; older ones fall off above that.</summary>
    private const int MaxRecords = 64;

    private readonly Dictionary<string, Record> _records = new(StringComparer.Ordinal);
    private long _sequence;

    private sealed class Record
    {
        public ProgramStatusInfo Info;
        public long Sequence;
    }

    /// <summary>How many records are kept; the cap keeps a busy program bounded.</summary>
    internal int Count => _records.Count;

    /// <summary>The report a person should see, or null when the program reported nothing.</summary>
    public ProgramStatusInfo? Current
    {
        get
        {
            ProgramStatusInfo? best = null;
            int bestRank = -1;
            long bestSequence = -1;
            foreach (Record record in _records.Values)
            {
                int rank = Rank(record.Info.State);
                if (rank < bestRank) continue;
                if (rank == bestRank && record.Sequence < bestSequence) continue;
                best = record.Info;
                bestRank = rank;
                bestSequence = record.Sequence;
            }
            return best;
        }
    }

    /// <summary>Stores a report. Returns true when the visible record changed.</summary>
    public bool Apply(ProgramStatusInfo info)
    {
        ProgramStatusInfo? before = Current;

        string app = Clean(info.App);
        if (app.Length == 0) app = InheritedApp(info.Id);
        info = info with { App = app, Title = Clean(info.Title), Message = Clean(info.Message) };

        if (!_records.TryGetValue(info.Id, out Record? record))
        {
            record = new Record();
            _records[info.Id] = record;
        }
        record.Info = info;
        record.Sequence = ++_sequence;
        Trim();

        return !Same(before, Current);
    }

    /// <summary>
    /// Removes a record and everything beneath it. An empty id removes every record, which
    /// is what a full terminal reset reports.
    /// </summary>
    public bool Clear(string id)
    {
        ProgramStatusInfo? before = Current;

        if (id.Length == 0)
        {
            _records.Clear();
        }
        else
        {
            _records.Remove(id);
            string prefix = id + "/";
            List<string>? doomed = null;
            foreach (string key in _records.Keys)
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                (doomed ??= new List<string>()).Add(key);
            }
            if (doomed is not null)
            {
                foreach (string key in doomed) _records.Remove(key);
            }
        }

        return !Same(before, Current);
    }

    /// <summary>
    /// A new shell prompt means the previous command is over, so nothing is still working
    /// or waiting. Finished and failed records stay until the user has seen them.
    /// </summary>
    public bool ClearBusy() => RemoveWhere(record =>
        record.Info.State is ProgramState.Working or ProgramState.Blocked);

    /// <summary>The user looked at the surface: drop the finished and failed records.</summary>
    public bool Acknowledge() => RemoveWhere(record =>
        record.Info.State is ProgramState.Done or ProgramState.Error);

    /// <summary>Drops every record (a full reset or a surface that goes away).</summary>
    public bool Reset()
    {
        if (_records.Count == 0) return false;
        _records.Clear();
        return true;
    }

    private bool RemoveWhere(Func<Record, bool> remove)
    {
        ProgramStatusInfo? before = Current;
        List<string>? doomed = null;
        foreach (KeyValuePair<string, Record> pair in _records)
        {
            if (!remove(pair.Value)) continue;
            (doomed ??= new List<string>()).Add(pair.Key);
        }
        if (doomed is null) return false;
        foreach (string key in doomed) _records.Remove(key);
        return !Same(before, Current);
    }

    /// <summary>A child record without an app takes the one from its nearest ancestor.</summary>
    private string InheritedApp(string id)
    {
        int slash = id.LastIndexOf('/');
        while (slash > 0)
        {
            if (_records.TryGetValue(id[..slash], out Record? parent) && parent.Info.App.Length > 0)
                return parent.Info.App;
            slash = id.LastIndexOf('/', slash - 1);
        }
        return _records.TryGetValue(string.Empty, out Record? root) ? root.Info.App : string.Empty;
    }

    private void Trim()
    {
        while (_records.Count > MaxRecords)
        {
            string? oldest = null;
            long oldestSequence = long.MaxValue;
            foreach (KeyValuePair<string, Record> pair in _records)
            {
                if (pair.Value.Sequence >= oldestSequence) continue;
                oldest = pair.Key;
                oldestSequence = pair.Value.Sequence;
            }
            if (oldest is null) return;
            _records.Remove(oldest);
        }
    }

    private static int Rank(ProgramState state) => state switch
    {
        ProgramState.Blocked => 4,
        ProgramState.Error => 3,
        ProgramState.Working => 2,
        ProgramState.Done => 1,
        _ => 0,
    };

    private static bool Same(ProgramStatusInfo? left, ProgramStatusInfo? right) =>
        left is null ? right is null : right is not null && left.Value == right.Value;

    /// <summary>
    /// Drops control and bidi-formatting characters: the text is shown outside the terminal,
    /// where a direction override could make one program look like another. The parser has
    /// already validated the encoding and length.
    /// </summary>
    private static string Clean(string text)
    {
        if (text.Length == 0) return string.Empty;

        StringBuilder? cleaned = null;
        for (int index = 0; index < text.Length; index++)
        {
            char value = text[index];
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(value);
            bool drop = category is UnicodeCategory.Control or UnicodeCategory.Format;
            if (drop)
            {
                cleaned ??= new StringBuilder(text.Length).Append(text, 0, index);
                continue;
            }
            cleaned?.Append(value);
        }

        return (cleaned?.ToString() ?? text).Trim();
    }
}
