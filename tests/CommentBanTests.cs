using System.IO;
using System.Linq;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class CommentBanTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DrivenRides.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("DrivenRides.slnx");
    }

    [Fact]
    public void RepositoryHasOnlyDirectivesAndListedExcuses()
    {
        var problems = CommentBan.Violations(RepoRoot());
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void PlainCommentIsFoundButStringsAndDirectivesAreNot()
    {
        const string cs = "#if DEBUG\nvar a = \"// no\"; var b = @\"/* no */\"; var c = $\"{(x ? \"//\" : \"\")}\";\n#endif\n// new comment\nint d = 1; /* inline */\n";
        var units = CommentBan.Units("Core/X.cs", cs);
        Assert.Equal(new[] { 4, 5 }, units.Select(u => u.Line));
        Assert.All(units, u => Assert.False(u.Directive));

        var py = CommentBan.Units("tools/x.py", "#!/usr/bin/env python3\ns = '# no'\nx = 1  # yes\n");
        Assert.Equal(new[] { (1, true), (3, false) }, py.Select(u => (u.Line, u.Directive)));

        var sh = CommentBan.Units("tools/x.sh", "#!/usr/bin/env bash\necho \"$# ${#x} '#'\" a#b\ncat <<'EOF'\n# body\nEOF\n# yes\n");
        Assert.Equal(new[] { (1, true), (6, false) }, sh.Select(u => (u.Line, u.Directive)));

        var ps = CommentBan.Units("tools/x.ps1", "$s = @'\n# body\n'@\n<# block #>\n$t = \"`#\" # yes\n");
        Assert.Equal(new[] { 4, 5 }, ps.Select(u => u.Line));
    }

    [Fact]
    public void ConsecutiveLineCommentsFormOneUnit()
    {
        var units = CommentBan.Units("Core/X.cs", "// one\n// two\nint a;\n// three\n");
        Assert.Equal(new[] { 1, 4 }, units.Select(u => u.Line));
    }

    [Fact]
    public void AllowlistReadsOnlyListedPathLines()
    {
        var allow = CommentBan.Allowlist("# Title\n\n- `Core/X.cs:4` workaround\n- `Core/Z.cs:2-3` todo\nnot `Core/Y.cs:9`\n");
        Assert.Equal(new[] { "Core/X.cs:4", "Core/Z.cs:2-3" }, allow);
    }

    [Fact]
    public void NewPlainCommentFailsWhileListedExcuseAndDirectivePass()
    {
        string root = Path.Combine(Path.GetTempPath(), "comment-ban-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Core"));
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        try
        {
            File.WriteAllText(Path.Combine(root, "Core/X.cs"), "#pragma warning disable CS0168\n// excuse: for now\nint a;\n");
            File.WriteAllText(Path.Combine(root, "tools.sh"), "#!/usr/bin/env bash\necho hi\n");
            File.WriteAllText(Path.Combine(root, CommentBan.ExcusesFile), "- `Core/X.cs:2` temporary\n");
            Git(root, "init -q");
            Assert.Empty(CommentBan.Violations(root));

            File.AppendAllText(Path.Combine(root, "Core/X.cs"), "// explains a\n");
            var problems = CommentBan.Violations(root);
            Assert.Equal("Core/X.cs:4", Assert.Single(problems).Split(": ")[0]);

            File.WriteAllText(Path.Combine(root, "Core/X.cs"), "#pragma warning disable CS0168\n// excuse: for now\n// new plain explanation\nint a;\n");
            problems = CommentBan.Violations(root);
            Assert.Equal(new[] { "Core/X.cs:2-3", "Core/X.cs:2" }, problems.Select(p => p.Split(": ")[0]));

            File.WriteAllText(Path.Combine(root, "Core/X.cs"), "int a;\n");
            Assert.Contains("listed in", Assert.Single(CommentBan.Violations(root)));
        }
        finally { Directory.Delete(root, true); }
    }

    static void Git(string dir, string args)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git", args) { WorkingDirectory = dir, UseShellExecute = false };
        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
