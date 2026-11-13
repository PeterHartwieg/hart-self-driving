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

    public static List<string> Labels(string? eventJson)
    {
        var labels = new List<string>();
        if (string.IsNullOrWhiteSpace(eventJson)) return labels;
        using var doc = JsonDocument.Parse(eventJson);
        if (doc.RootElement.TryGetProperty("pull_request", out var pr) && pr.TryGetProperty("labels", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var l in list.EnumerateArray())
                if (l.TryGetProperty("name", out var name) && name.GetString() is string s) labels.Add(s);
        return labels;
    }

    public static List<MarkdownChange> Parse(string nameStatusZ)
    {
        var changes = new List<MarkdownChange>();
        string[] fields = nameStatusZ.Split('\0');
        for (int i = 0; i + 1 < fields.Length && fields[i].Length > 0;)
        {
            char status = fields[i][0];
            if ((status == 'R' || status == 'C') && i + 2 < fields.Length)
            {
                changes.Add(new MarkdownChange(status, fields[i + 1], fields[i + 2]));
                i += 3;
            }
            else
            {
                changes.Add(new MarkdownChange(status, null, fields[i + 1]));
                i += 2;
            }
        }
        return changes;
    }

    static IEnumerable<string> Paths(string nulList) => nulList.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    public static List<string> Violations(string root, string baseRef, IEnumerable<string> labels)
    {
        string mergeBase = Git(root, $"merge-base {baseRef} HEAD").Trim();
        var baseFiles = new HashSet<string>(Paths(Git(root, $"ls-tree -r -z --name-only {mergeBase}")), StringComparer.Ordinal);
        var changes = Parse(Git(root, $"diff -z --name-status -M -C --diff-filter=ACR {mergeBase} HEAD"));
        changes.AddRange(Parse(Git(root, "diff -z --name-status -M -C --diff-filter=ACR HEAD")));
        foreach (string path in Paths(Git(root, "ls-files -z --others --exclude-standard")))
            changes.Add(new MarkdownChange('A', null, path));
        return Violations(changes, baseFiles, labels).Distinct(StringComparer.Ordinal).ToList();
    }

    public static string Git(string root, string args)
    {
        var info = new ProcessStartInfo("git", args) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, UseShellExecute = false };
        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {args} failed: {error}");
        return output;
    }
}
