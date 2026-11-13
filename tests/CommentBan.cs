using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DrivenRides.Core.Tests;

public sealed record CommentUnit(string Path, int Line, string Text, bool Directive, int EndLine)
{
    public string Key => EndLine > Line ? $"{Path}:{Line}-{EndLine}" : $"{Path}:{Line}";
}

public static class CommentBan
{
    public const string ExcusesFile = "docs/comment-excuses.md";
    static readonly string[] SkippedRoots = { "workshop/", "docs/" };

    public static string? Language(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        if (path.EndsWith(".cs")) return "cs";
        if (path.EndsWith(".py") || name == "agent" || name == "mac-agent") return "py";
        if (path.EndsWith(".sh") || name == "mod.conf") return "sh";
        if (path.EndsWith(".ps1")) return "ps";
        if (path.EndsWith(".csproj") || path.EndsWith(".slnx")) return "xml";
        if (name == ".gitignore") return "ignore";
        return null;
    }

    public static IEnumerable<string> Files(string root)
    {
        var git = new ProcessStartInfo("git", "ls-files --cached --others --exclude-standard")
        {
            WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false,
        };
        using var process = Process.Start(git)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("git ls-files failed");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Language(p) != null && !SkippedRoots.Any(p.StartsWith) && File.Exists(System.IO.Path.Combine(root, p)))
            .OrderBy(p => p, StringComparer.Ordinal);
    }

    public static HashSet<string> Allowlist(string markdown)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(markdown, @"(?m)^- `([^`\s]+:\d+(?:-\d+)?)`"))
            set.Add(m.Groups[1].Value);
        return set;
    }

    public static List<string> Violations(string root)
    {
        var allow = Allowlist(File.ReadAllText(System.IO.Path.Combine(root, ExcusesFile)));
        var found = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (string path in Files(root))
            foreach (CommentUnit unit in Units(path, File.ReadAllText(System.IO.Path.Combine(root, path))))
            {
                if (unit.Directive) continue;
                string key = unit.Key;
                found.Add(key);
                if (!allow.Contains(key)) problems.Add($"{key}: comment not allowed: {First(unit.Text)}");
            }
        foreach (string key in allow.Where(k => !found.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
            problems.Add($"{key}: listed in {ExcusesFile} but no comment spans exactly these lines");
        return problems;
    }

    static string First(string text) { string line = text.Split('\n')[0]; return line.Length > 80 ? line[..80] : line; }

    public static List<CommentUnit> Units(string path, string text)
    {
        string lang = Language(path) ?? throw new ArgumentException(path);
        List<(int Start, int End)> spans = lang switch
        {
            "cs" => new CSharpLexer(text).Run(),
            "py" => new PythonLexer(text).Run(),
            "sh" => new ShellLexer(text, powerShell: false).Run(),
            "ps" => new ShellLexer(text, powerShell: true).Run(),
            "xml" => Regex.Matches(text, "<!--.*?-->", RegexOptions.Singleline).Select(m => (m.Index, m.Index + m.Length)).ToList(),
            _ => Regex.Matches(text, @"(?m)^[ \t]*#.*$").Select(m => (m.Index + m.Value.IndexOf('#'), m.Index + m.Length)).ToList(),
        };
        var units = new List<CommentUnit>();
        int lastEndLine = -2;
        bool lastGroupable = false;
        foreach (var (start, end) in spans)
        {
            string body = text[start..end].TrimEnd('\r');
            int line = LineOf(text, start);
            int endLine = LineOf(text, Math.Max(start, end - 1));
            int lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
            if (start == 0) lineStart = 0;
            bool ownLine = text[lineStart..start].Trim().Length == 0;
            bool directive = IsDirective(lang, body, line);
            bool groupable = ownLine && !directive && !body.StartsWith("/*") && !body.StartsWith("<#") && !body.StartsWith("<!--");
            if (groupable && lastGroupable && line == lastEndLine + 1)
            {
                CommentUnit last = units[^1];
                units[^1] = last with { Text = last.Text + "\n" + body.Trim(), EndLine = endLine };
            }
            else units.Add(new CommentUnit(path, line, body.Trim(), directive, endLine));
            lastEndLine = endLine;
            lastGroupable = groupable;
        }
        return units;
    }

    static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }

    public static bool IsDirective(string lang, string body, int line)
    {
        string s = body.Trim();
        if (line == 1 && s.StartsWith("#!")) return true;
        return lang switch
        {
            "py" => Regex.IsMatch(s, @"^#\s*(-\*-|type:|noqa|pylint:)"),
            "sh" => Regex.IsMatch(s, @"^#\s*shellcheck\s"),
            "ps" => Regex.IsMatch(s, @"^#[Rr]equires\b"),
            _ => false,
        };
    }

    sealed class CSharpLexer
    {
        readonly string t;
        readonly List<(int, int)> spans = new();
        public CSharpLexer(string text) { t = text; }
        public List<(int, int)> Run() { Code(0, false); return spans; }

        int LineEnd(int i) { int j = t.IndexOf('\n', i); return j < 0 ? t.Length : j; }

        int Code(int i, bool interpolation)
        {
            int depth = 0;
            while (i < t.Length)
            {
                char c = t[i];
                if (interpolation && c == '{') depth++;
                if (interpolation && c == '}') { if (depth == 0) return i; depth--; }
                if (At(i, "//")) { int j = LineEnd(i); spans.Add((i, j)); i = j; continue; }
                if (At(i, "/*")) { int j = t.IndexOf("*/", i + 2, StringComparison.Ordinal); j = j < 0 ? t.Length : j + 2; spans.Add((i, j)); i = j; continue; }
                if (c == '#' && t[(t.LastIndexOf('\n', Math.Max(0, i - 1)) + 1)..i].Trim().Length == 0)
                {
                    int j = LineEnd(i);
                    int k = t.IndexOf("//", i, j - i, StringComparison.Ordinal);
                    if (k >= 0) spans.Add((k, j));
                    i = j; continue;
                }
                if (c == '\'')
                {
                    i++;
                    while (i < t.Length && t[i] != '\'') i += t[i] == '\\' ? 2 : 1;
                    i++; continue;
                }
                int prefix = StringPrefix(i);
                if (prefix >= 0)
                {
                    string p = t.Substring(i, prefix);
                    if (At(i + prefix, "\"\"\"")) throw new NotSupportedException($"raw string literal at offset {i}");
                    bool verbatim = p.Contains('@'), interpolated = p.Contains('$');
                    i += prefix + 1;
                    while (i < t.Length)
                    {
                        char s = t[i];
                        if (s == '"') { if (verbatim && At(i, "\"\"")) { i += 2; continue; } i++; break; }
                        if (s == '\\' && !verbatim) { i += 2; continue; }
                        if (interpolated && s == '{') { if (At(i, "{{")) { i += 2; continue; } i = Code(i + 1, true) + 1; continue; }
                        i++;
                    }
                    continue;
                }
                i++;
            }
            return i;
        }

        int StringPrefix(int i)
        {
            if (t[i] == '"') return 0;
            bool wordBefore = i > 0 && (char.IsLetterOrDigit(t[i - 1]) || t[i - 1] == '_');
            if (wordBefore) return -1;
            if ((At(i, "$@\"") || At(i, "@$\""))) return 2;
            if (At(i, "$\"") || At(i, "@\"")) return 1;
            return -1;
        }

        bool At(int i, string s) => string.CompareOrdinal(t, i, s, 0, s.Length) == 0;
    }

    sealed class PythonLexer
    {
        readonly string t;
        public PythonLexer(string text) { t = text; }

        public List<(int, int)> Run()
        {
            var spans = new List<(int, int)>();
            int i = 0;
            while (i < t.Length)
            {
                char c = t[i];
                if (c == '#') { int j = t.IndexOf('\n', i); j = j < 0 ? t.Length : j; spans.Add((i, j)); i = j; continue; }
                Match m = Regex.Match(t[i..Math.Min(t.Length, i + 5)], "^([rRbBfFuU]{0,2})('''|\"\"\"|'|\")");
                bool wordBefore = i > 0 && (char.IsLetterOrDigit(t[i - 1]) || t[i - 1] == '_');
                if (m.Success && !(m.Groups[1].Length > 0 && wordBefore))
                {
                    string quote = m.Groups[2].Value;
                    i += m.Length;
                    while (i < t.Length)
                    {
                        if (t[i] == '\\') { i += 2; continue; }
                        if (string.CompareOrdinal(t, i, quote, 0, quote.Length) == 0) { i += quote.Length; break; }
                        i++;
                    }
                    continue;
                }
                i++;
            }
            return spans;
        }
    }

    sealed class ShellLexer
    {
        readonly string t;
        readonly bool ps;
        readonly List<(int, int)> spans = new();
        readonly List<string> heredocs = new();
        public ShellLexer(string text, bool powerShell) { t = text; ps = powerShell; }
        public List<(int, int)> Run() { Code(0, false); return spans; }

        bool At(int i, string s) => string.CompareOrdinal(t, i, s, 0, s.Length) == 0;
        bool WordStart(int i) => i == 0 || (ps ? " \t\n;|&(){}" : " \t\n;|&()").IndexOf(t[i - 1]) >= 0;

        int Code(int i, bool nested)
        {
            int depth = 0;
            char escape = ps ? '`' : '\\';
            while (i < t.Length)
            {
                char c = t[i];
                if (nested && c == '(') depth++;
                if (nested && c == ')') { if (depth == 0) return i; depth--; }
                if (c == escape) { i += 2; continue; }
                if (ps && At(i, "<#")) { int j = t.IndexOf("#>", i, StringComparison.Ordinal); j = j < 0 ? t.Length : j + 2; spans.Add((i, j)); i = j; continue; }
                if (ps && (At(i, "@'") || At(i, "@\"")))
                {
                    int j = t.IndexOf("\n" + t[i + 1] + "@", i, StringComparison.Ordinal);
                    i = j < 0 ? t.Length : j + 3; continue;
                }
                if (c == '\'')
                {
                    i++;
                    while (i < t.Length) { if (ps && At(i, "''")) { i += 2; continue; } if (t[i] == '\'') { i++; break; } i++; }
                    continue;
                }
                if (c == '"') { i = DoubleQuoted(i + 1); continue; }
                if (c == '#' && WordStart(i)) { int j = t.IndexOf('\n', i); j = j < 0 ? t.Length : j; spans.Add((i, j)); i = j; continue; }
                if (!ps && At(i, "<<<")) { i += 3; continue; }
                if (!ps)
                {
                    Match m = Regex.Match(t[i..Math.Min(t.Length, i + 80)], "^<<-?\\s*(['\"]?)(\\w+)\\1");
                    if (m.Success) { heredocs.Add(m.Groups[2].Value); i += m.Length; continue; }
                }
                if (c == '\n' && heredocs.Count > 0)
                {
                    i++;
                    foreach (string end in heredocs)
                        while (i < t.Length)
                        {
                            int j = t.IndexOf('\n', i); j = j < 0 ? t.Length : j;
                            string line = t[i..j];
                            i = j + 1;
                            if (line.Trim() == end) break;
                        }
                    heredocs.Clear(); continue;
                }
                i++;
            }
            return i;
        }

        int DoubleQuoted(int i)
        {
            char escape = ps ? '`' : '\\';
            while (i < t.Length)
            {
                char c = t[i];
                if (c == escape) { i += 2; continue; }
                if (ps && At(i, "\"\"")) { i += 2; continue; }
                if (c == '"') return i + 1;
                if (At(i, "$(")) { i = Code(i + 2, true) + 1; continue; }
                if (!ps && c == '`') { int j = t.IndexOf('`', i + 1); i = j < 0 ? t.Length : j + 1; continue; }
                i++;
            }
            return i;
        }
    }
}
