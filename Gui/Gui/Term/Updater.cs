using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Gui.Term;

internal enum UpdateResult
{
    UpToDate,
    UpdateAvailable,
    Failed,
}

/// <summary>The outcome of a release check: whether a newer version exists, plus its tag and page.</summary>
internal readonly record struct UpdateCheck(UpdateResult Status, string Version, string Url);

/// <summary>
/// Checks GitHub's releases endpoint for a newer build. Kept free of UI so the network
/// work and version comparison can be tested independently of a window.
/// </summary>
internal static class Updater
{
    private const string LatestApi = "https://api.github.com/repos/shizengzhou/Gui/releases/latest";
    private const string ListApi = "https://api.github.com/repos/shizengzhou/Gui/releases?per_page=10";

    /// <summary>A launch check only runs when the last one is older than this.</summary>
    private static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(24);

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Gui-Updater");
        client.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>
    /// Reads the newest release and compares it to this build. "stable" reads the latest
    /// non-prerelease; "tip" includes pre-releases, matching Ghostty's update channels.
    /// </summary>
    internal static async Task<UpdateCheck> CheckAsync(string currentVersion, string channel = "stable")
    {
        try
        {
            string json = await Client
                .GetStringAsync(channel == "tip" ? ListApi : LatestApi)
                .ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);

            JsonElement release = channel == "tip"
                ? FirstRelease(document.RootElement)
                : document.RootElement;

            if (!release.TryGetProperty("tag_name", out JsonElement tag) ||
                tag.ValueKind != JsonValueKind.String)
            {
                return new UpdateCheck(UpdateResult.Failed, string.Empty, string.Empty);
            }

            string latest = (tag.GetString() ?? string.Empty).TrimStart('v', 'V');
            if (latest.Length == 0) return new UpdateCheck(UpdateResult.Failed, string.Empty, string.Empty);

            string url = release.TryGetProperty("html_url", out JsonElement page) &&
                         page.ValueKind == JsonValueKind.String
                ? page.GetString() ?? string.Empty
                : string.Empty;

            return Compare(latest, currentVersion) > 0
                ? new UpdateCheck(UpdateResult.UpdateAvailable, latest, url)
                : new UpdateCheck(UpdateResult.UpToDate, latest, url);
        }
        catch (Exception error)
        {
            Diag.LogException("updater.check", error);
            return new UpdateCheck(UpdateResult.Failed, string.Empty, string.Empty);
        }
    }

    /// <summary>True when no check has run in the last 24 hours, so the launch check is due.</summary>
    internal static bool AutoCheckDue()
    {
        DateTimeOffset? last = LastCheck();
        return last is null || DateTimeOffset.UtcNow - last.Value >= AutoCheckInterval;
    }

    /// <summary>Records when a check finished, so later launches within a day skip it.</summary>
    internal static void RecordCheck()
    {
        try
        {
            string path = CheckStampPath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception error)
        {
            Diag.LogException("updater.recordCheck", error);
        }
    }

    private static DateTimeOffset? LastCheck()
    {
        try
        {
            string path = CheckStampPath();
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path).Trim();
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch (Exception error)
        {
            Diag.LogException("updater.lastCheck", error);
            return null;
        }
    }

    private static string CheckStampPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "gui", "update-check");
    }

    private static JsonElement FirstRelease(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) return root[0];
        return default;
    }

    /// <summary>Positive when <paramref name="left"/> is newer. Compares numeric dot segments.</summary>
    private static int Compare(string left, string right)
    {
        string[] a = left.Split('.');
        string[] b = right.Split('.');
        int count = Math.Max(a.Length, b.Length);
        for (int i = 0; i < count; i++)
        {
            int x = LeadingNumber(i < a.Length ? a[i] : string.Empty);
            int y = LeadingNumber(i < b.Length ? b[i] : string.Empty);
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <summary>Reads the leading decimal run of a segment, ignoring any pre-release suffix.</summary>
    private static int LeadingNumber(string segment)
    {
        int value = 0;
        bool any = false;
        foreach (char c in segment)
        {
            if (c < '0' || c > '9') break;
            value = (value * 10) + (c - '0');
            any = true;
        }
        return any ? value : 0;
    }
}
