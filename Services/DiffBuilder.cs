using System.Text.RegularExpressions;
using GitManager.Models;

namespace GitManager.Services;

public static partial class DiffBuilder
{
    public static List<DiffRow> Build(FileComparison comparison)
    {
        string[] before = Lines(comparison.Before), after = Lines(comparison.After);
        List<DiffRow> rows = [];
        int oldIndex = 0, newIndex = 0;
        List<(int Number, string Text)> removed = [], added = [];
        void Flush()
        {
            for (int i = 0; i < Math.Max(removed.Count, added.Count) && rows.Count < 10000; i++)
            {
                var oldLine = i < removed.Count ? removed[i] : (0, "");
                var newLine = i < added.Count ? added[i] : (0, "");
                rows.Add(new(oldLine.Item1 == 0 ? "" : oldLine.Item1.ToString(), oldLine.Item2, newLine.Item1 == 0 ? "" : newLine.Item1.ToString(), newLine.Item2, i < removed.Count, i < added.Count));
            }
            removed.Clear(); added.Clear();
        }
        void UnchangedUntil(int oldEnd, int newEnd)
        {
            while (oldIndex < oldEnd && newIndex < newEnd && rows.Count < 10000)
                rows.Add(new((oldIndex + 1).ToString(), before[oldIndex++], (newIndex + 1).ToString(), after[newIndex++]));
        }
        if (comparison.Patch.Length > 0)
        {
            bool inHunk = false;
            foreach (string line in Lines(comparison.Patch))
            {
                var match = Hunk().Match(line);
                if (match.Success)
                {
                    Flush();
                    int oldStart = Math.Max(0, int.Parse(match.Groups[1].Value) - 1), newStart = Math.Max(0, int.Parse(match.Groups[2].Value) - 1);
                    UnchangedUntil(Math.Min(oldStart, before.Length), Math.Min(newStart, after.Length));
                    oldIndex = oldStart; newIndex = newStart; inHunk = true;
                    continue;
                }
                if (!inHunk || line.Length == 0 || rows.Count >= 10000) continue;
                if (line[0] == '-') removed.Add((++oldIndex, line[1..]));
                else if (line[0] == '+') added.Add((++newIndex, line[1..]));
                else if (line[0] == ' ')
                {
                    Flush();
                    rows.Add(new((++oldIndex).ToString(), line[1..], (++newIndex).ToString(), line[1..]));
                }
            }
            Flush();
        }
        if (before.SequenceEqual(after) || comparison.Patch.Length > 0) UnchangedUntil(before.Length, after.Length);
        while (oldIndex < before.Length && rows.Count < 10000) { removed.Add((oldIndex + 1, before[oldIndex++])); }
        while (newIndex < after.Length && rows.Count < 10000) { added.Add((newIndex + 1, after[newIndex++])); }
        Flush();
        if (rows.Count >= 10000) rows.Add(new("", "仅预览前 10000 行", "", "仅预览前 10000 行"));
        return rows;
    }

    private static string[] Lines(string text)
    {
        if (text.Length == 0) return [];
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }
    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex Hunk();
}
