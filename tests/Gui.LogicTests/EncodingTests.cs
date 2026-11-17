using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// The repository stores its text sources as UTF-8 with a byte order mark and CRLF line
/// endings. Mixing the two up produces diffs that look like whole-file rewrites, so the
/// rule is checked here instead of by eye.
/// </summary>
public class EncodingTests
{
    private static readonly string[] Extensions = { ".cs", ".csproj", ".slnx", ".md", ".xaml" };

    private static readonly string[] SkipDirectories =
    {
        "bin", "obj", ".git", ".vs", "Assets", ".codebuddy", "node_modules", ".screenshots",
    };

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Gui.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("Could not find the repository root (Gui.slnx).");
    }

    private static IEnumerable<string> SourceFiles()
    {
        string root = RepositoryRoot();

        // The skip list has to apply while walking, not after: .git holds worktree
        // administrative directories that a plain recursive enumeration dies on ("access to
        // the path ... is denied"), and bin/obj/node_modules are not worth reading at all.
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();

            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                string name = Path.GetFileName(child);
                if (SkipDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                pending.Push(child);
            }

            foreach (string path in Directory.EnumerateFiles(directory))
            {
                string extension = Path.GetExtension(path);
                if (!Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;

                yield return Path.GetRelativePath(root, path);
            }
        }
    }

    [Fact]
    public void EverySourceFileUsesUtf8WithABomAndCrlf()
    {
        var problems = new List<string>();
        int checkedFiles = 0;

        foreach (string relative in SourceFiles())
        {
            checkedFiles++;
            byte[] bytes = File.ReadAllBytes(Path.Combine(RepositoryRoot(), relative));

            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            int lines = 0;
            int crlf = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != (byte)'\n') continue;
                lines++;
                if (i > 0 && bytes[i - 1] == (byte)'\r') crlf++;
            }

            if (!bom) problems.Add($"{relative}: missing UTF-8 BOM");
            if (lines != crlf) problems.Add($"{relative}: {lines - crlf} line(s) without CRLF");
        }

        Assert.True(checkedFiles > 0, "No source files were found to check.");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
