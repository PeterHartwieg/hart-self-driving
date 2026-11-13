using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace DrivenRides.Core.Tests;

public sealed class MarkdownBanTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DrivenRides.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("DrivenRides.slnx");
    }

    [Fact]
    public void BranchAddsNoNewMarkdown()
    {
        string baseRef = Environment.GetEnvironmentVariable("DR_MD_BASE") ?? MarkdownBan.DefaultBase;
        string? eventPath = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH");
        string? payload = eventPath != null && File.Exists(eventPath) ? File.ReadAllText(eventPath) : null;
        var labels = MarkdownBan.Labels(payload);
        var problems = MarkdownBan.Violations(RepoRoot(), baseRef, labels);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void RulesForAddsCopiesAndRenames()
    {
        var baseFiles = new HashSet<string> { "README.md", "docs/a.md", "tools/x.txt" };
        var none = Array.Empty<string>();
        Assert.Empty(MarkdownBan.Violations(new[] { new MarkdownChange('A', null, "Core/X.cs"), new MarkdownChange('R', "docs/a.md", "docs/b.mdx") }, baseFiles, none));
        Assert.Single(MarkdownBan.Violations(new[] { new MarkdownChange('A', null, "docs/new.md") }, baseFiles, none));
        Assert.Single(MarkdownBan.Violations(new[] { new MarkdownChange('C', "README.md", "docs/copy.md") }, baseFiles, none));
        Assert.Single(MarkdownBan.Violations(new[] { new MarkdownChange('R', "tools/x.txt", "docs/x.md") }, baseFiles, none));
        Assert.Single(MarkdownBan.Violations(new[] { new MarkdownChange('A', null, "notes.MDX") }, baseFiles, none));
    }

    [Fact]
    public void DocsApprovedLabelInPayloadAllowsNewMarkdown()
    {
        var payload = "{\"pull_request\":{\"labels\":[{\"name\":\"bug\"},{\"name\":\"docs-approved\"}]}}";
        var labels = MarkdownBan.Labels(payload);
        Assert.Contains("docs-approved", labels);
        Assert.Empty(MarkdownBan.Violations(new[] { new MarkdownChange('A', null, "docs/plan.md") }, new HashSet<string>(), labels));
        Assert.DoesNotContain("docs-approved", MarkdownBan.Labels("{\"pull_request\":{\"labels\":[{\"name\":\"bug\"}]}}"));
    }

    [Fact]
    public void SyntheticRepoFailsOnAddedMarkdownAndPassesWithout()
    {
        string root = Path.Combine(Path.GetTempPath(), "md-ban-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        try
        {
            MarkdownBan.Git(root, "init -q -b base");
            MarkdownBan.Git(root, "config user.email t@t");
            MarkdownBan.Git(root, "config user.name t");
            File.WriteAllText(Path.Combine(root, "docs/old.md"), "old\n");
            MarkdownBan.Git(root, "add -A");
            MarkdownBan.Git(root, "commit -q -m base");
            MarkdownBan.Git(root, "checkout -q -b work");

            File.AppendAllText(Path.Combine(root, "docs/old.md"), "edit\n");
            File.WriteAllText(Path.Combine(root, "code.cs"), "int a;\n");
            MarkdownBan.Git(root, "commit -q -am edit");
            MarkdownBan.Git(root, "add code.cs");
            MarkdownBan.Git(root, "commit -q -m code");
            Assert.Empty(MarkdownBan.Violations(root, "base", Array.Empty<string>()));

            File.WriteAllText(Path.Combine(root, "docs/new.md"), "new\n");
            Assert.Single(MarkdownBan.Violations(root, "base", Array.Empty<string>()));
            MarkdownBan.Git(root, "add -A");
            MarkdownBan.Git(root, "commit -q -m new");
            Assert.Equal("docs/new.md", Assert.Single(MarkdownBan.Violations(root, "base", Array.Empty<string>())).Split(':')[0]);
            Assert.Empty(MarkdownBan.Violations(root, "base", new[] { MarkdownBan.Label }));

            File.Delete(Path.Combine(root, "docs/new.md"));
            Assert.Equal("docs/new.md", Assert.Single(MarkdownBan.Violations(root, "base", Array.Empty<string>())).Split(':')[0]);
            MarkdownBan.Git(root, "checkout -q -- docs/new.md");

            MarkdownBan.Git(root, "config core.quotePath true");
            File.WriteAllText(Path.Combine(root, "docs/\u00fcber.md"), "x\n");
            Assert.Equal("docs/\u00fcber.md", MarkdownBan.Violations(root, "base", Array.Empty<string>()).Select(p => p.Split(':')[0]).Single(p => p != "docs/new.md"));
            MarkdownBan.Git(root, "add -A");
            MarkdownBan.Git(root, "commit -q -m uber");
            Assert.Contains("docs/\u00fcber.md", MarkdownBan.Violations(root, "base", Array.Empty<string>()).Select(p => p.Split(':')[0]));
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
}
