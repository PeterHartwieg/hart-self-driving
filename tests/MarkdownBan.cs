using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DrivenRides.Core.Tests;

public sealed record MarkdownChange(char Status, string? From, string To);

public static class MarkdownBan
{
    public const string Label = "docs-approved";
    public const string DefaultBase = "origin/main";

    public static bool IsMarkdown(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase);

    public static List<string> Violations(IEnumerable<MarkdownChange> changes, ISet<string> baseFiles, IEnumerable<string> labels)
    {
        if (labels.Contains(Label, StringComparer.Ordinal)) return new List<string>();
        var problems = new List<string>();
        foreach (var c in changes)
        {
            if (!IsMarkdown(c.To) || baseFiles.Contains(c.To)) continue;
            bool grandfatheredRename = c.Status == 'R' && c.From != null && IsMarkdown(c.From) && baseFiles.Contains(c.From);
            if (grandfatheredRename) continue;
            string how = c.Status switch { 'R' => $"renamed from {c.From}", 'C' => $"copied from {c.From}", _ => "added" };
            problems.Add($"{c.To}: new markdown file ({how}) needs the {Label} label");
        }
        return problems;
    }

    public static List<string> Labels(string? eventJson, string? labelList)
    {
        var labels = new List<string>();
        if (!string.IsNullOrWhiteSpace(labelList))
            labels.AddRange(labelList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(eventJson)) return labels;
        using var doc = JsonDocument.Parse(eventJson);
        if (doc.RootElement.TryGetProperty("pull_request", out var pr) && pr.TryGetProperty("labels", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var l in list.EnumerateArray())
                if (l.TryGetProperty("name", out var name) && name.GetString() is string s) labels.Add(s);
        return labels;
    }

    public static List<MarkdownChange> Parse(string nameStatus)
    {
        var changes = new List<MarkdownChange>();
        foreach (string line in nameStatus.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\t');
            char status = parts[0][0];
            changes.Add(parts.Length >= 3 ? new MarkdownChange(status, parts[1], parts[2]) : new MarkdownChange(status, null, parts[1]));
        }
        return changes;
    }

    public static List<string> Violations(string root, string baseRef, IEnumerable<string> labels)
    {
        string mergeBase = Git(root, $"merge-base {baseRef} HEAD").Trim();
        var baseFiles = new HashSet<string>(Git(root, $"ls-tree -r --name-only {mergeBase}").Split('\n', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        var changes = Parse(Git(root, $"diff --name-status -M -C --diff-filter=ACR {mergeBase}"));
        foreach (string path in Git(root, "ls-files --others --exclude-standard").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            changes.Add(new MarkdownChange('A', null, path));
        return Violations(changes, baseFiles, labels);
    }

    public static string Git(string root, string args)
    {
        var info = new ProcessStartInfo("git", args) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {args} failed: {error}");
        return output;
    }
}
