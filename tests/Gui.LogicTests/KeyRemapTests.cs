using System;
using System.IO;
using System.Linq;
using System.Text;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// "key-remap": modifier remapping, parsed from the from=to lines Ghostty documents. The table
/// is one-way and never transitive, and a sided name only covers that side.
/// </summary>
public class KeyRemapTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "gui-remap-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // A leftover temp directory must not fail the test run.
        }
    }

    private Config Load(string content)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "config");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return Config.Load(path);
    }

    private static KeyRemap Parse(string entry)
    {
        Assert.True(KeyRemapTable.TryParse(entry, out KeyRemap remap, out string error), error);
        return remap;
    }

    [Fact]
    public void AGenericLineCoversBothSides()
    {
        KeyRemap remap = Parse("ctrl=super");

        Assert.Equal(KeyModifier.Ctrl, remap.From);
        Assert.Equal(KeyModifier.Super, remap.To);
        Assert.True(remap.Generic);

        KeyRemap[] table = { remap };
        Assert.Equal(KeyModifier.Super, KeyRemapTable.Match(table, KeyModifier.Ctrl, false));
        Assert.Equal(KeyModifier.Super, KeyRemapTable.Match(table, KeyModifier.Ctrl, true));

        // The key that was remapped onto keeps acting as itself, and so does every other key.
        Assert.Equal(KeyModifier.Super, KeyRemapTable.Match(table, KeyModifier.Super, false));
        Assert.Equal(KeyModifier.Alt, KeyRemapTable.Match(table, KeyModifier.Alt, false));
    }

    [Fact]
    public void ASidedLineCoversOneSideOnly()
    {
        KeyRemap remap = Parse("left_control=right_alt");

        Assert.Equal(KeyModifier.Ctrl, remap.From);
        Assert.Equal(KeyModifier.Alt, remap.To);
        Assert.False(remap.Generic);

        KeyRemap[] table = { remap };
        Assert.Equal(KeyModifier.Alt, KeyRemapTable.Match(table, KeyModifier.Ctrl, false));
        Assert.Equal(KeyModifier.Ctrl, KeyRemapTable.Match(table, KeyModifier.Ctrl, true));
    }

    [Theory]
    [InlineData("control=super", KeyModifier.Ctrl, KeyModifier.Super)]
    [InlineData("option=shift", KeyModifier.Alt, KeyModifier.Shift)]
    [InlineData("cmd=ctrl", KeyModifier.Super, KeyModifier.Ctrl)]
    [InlineData("command=ctrl", KeyModifier.Super, KeyModifier.Ctrl)]
    [InlineData("win=ctrl", KeyModifier.Super, KeyModifier.Ctrl)]
    [InlineData(" Shift = Alt ", KeyModifier.Shift, KeyModifier.Alt)]
    public void TheNamesGhosttyUsesAreAccepted(string entry, KeyModifier from, KeyModifier to)
    {
        KeyRemap remap = Parse(entry);
        Assert.Equal(from, remap.From);
        Assert.Equal(to, remap.To);
    }

    [Theory]
    [InlineData("ctrl")]
    [InlineData("ctrl=")]
    [InlineData("=super")]
    [InlineData("ctrl=hyper")]
    [InlineData("bottom_ctrl=alt")]
    public void BadLinesAreRejected(string entry)
    {
        Assert.False(KeyRemapTable.TryParse(entry, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void RemapsAreNotTransitive()
    {
        // Ghostty: with these two lines Alt acts as Ctrl, never as Super.
        KeyRemap[] table = { Parse("ctrl=super"), Parse("alt=ctrl") };

        Assert.Equal(KeyModifier.Ctrl, KeyRemapTable.Match(table, KeyModifier.Alt, false));
        Assert.Equal(KeyModifier.Super, KeyRemapTable.Match(table, KeyModifier.Ctrl, false));
    }

    [Fact]
    public void ASidedLineBeatsAGenericOne()
    {
        KeyRemap[] table = { Parse("ctrl=alt"), Parse("left_ctrl=shift") };

        Assert.Equal(KeyModifier.Shift, KeyRemapTable.Match(table, KeyModifier.Ctrl, false));
        Assert.Equal(KeyModifier.Alt, KeyRemapTable.Match(table, KeyModifier.Ctrl, true));
    }

    [Fact]
    public void TheLastLineWins()
    {
        KeyRemap[] table = { Parse("ctrl=alt"), Parse("ctrl=shift") };
        Assert.Equal(KeyModifier.Shift, KeyRemapTable.Match(table, KeyModifier.Ctrl, false));
    }

    [Fact]
    public void AConfigKeepsTheValidLinesAndReportsTheRest()
    {
        Config config = Load("key-remap = ctrl=super\nkey-remap = ctrl=hyper\n");

        Assert.Single(config.KeyRemaps);
        Assert.Equal(KeyModifier.Super, config.KeyRemaps[0].To);

        ConfigDiagnostic problem = config.Diagnostics.Single(d => d.Key == "key-remap");
        Assert.Equal(ConfigSeverity.Warning, problem.Severity);
        Assert.Contains("unknown modifier", problem.Message);
    }

    [Fact]
    public void AnUnsetOptionLeavesEveryKeyAlone()
    {
        Config config = Load("font-size = 12\n");

        Assert.Empty(config.KeyRemaps);
        Assert.Equal(KeyModifier.Ctrl, KeyRemapTable.Match(config.KeyRemaps, KeyModifier.Ctrl, false));
        Assert.Equal(KeyModifier.Super, KeyRemapTable.Match(config.KeyRemaps, KeyModifier.Super, true));
    }
}
