using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace GitManager.Views;

// 轻量 Markdown 显示：覆盖 README 常见写法（标题、列表、引用、代码块、表格、链接），不追求完整规范。
public static partial class MarkdownDocument
{
    private static readonly FontFamily CodeFont = new("Cascadia Mono,Consolas");

    public static FlowDocument Build(string text)
    {
        var document = new FlowDocument { PagePadding = new Thickness(28, 20, 28, 28), FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 14, LineHeight = 22 };
        document.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();
        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            var block = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
            AddInlines(block.Inlines, string.Join(" ", paragraph));
            document.Blocks.Add(block);
            paragraph.Clear();
        }
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                FlushParagraph();
                string fence = trimmed[..3];
                var code = new StringBuilder();
                while (++i < lines.Length && !lines[i].Trim().StartsWith(fence)) code.AppendLine(lines[i]);
                document.Blocks.Add(CodeBlock(code.ToString().TrimEnd('\n', '\r')));
                continue;
            }
            if (trimmed.Length == 0) { FlushParagraph(); continue; }
            if (HtmlOnly().IsMatch(trimmed))
            {
                FlushParagraph();
                string inner = HtmlTag().Replace(trimmed, "").Trim();
                if (inner.Length > 0) paragraph.Add(inner);
                continue;
            }
            if (Heading().Match(trimmed) is { Success: true } heading)
            {
                FlushParagraph();
                int level = heading.Groups[1].Length;
                var block = new Paragraph { FontWeight = FontWeights.SemiBold, FontSize = level switch { 1 => 26, 2 => 21, 3 => 18, _ => 15 }, Margin = new Thickness(0, level <= 2 ? 18 : 12, 0, 10), LineHeight = double.NaN };
                AddInlines(block.Inlines, heading.Groups[2].Value.TrimEnd('#', ' '));
                if (level <= 2) Underline(block);
                document.Blocks.Add(block);
                continue;
            }
            if (Rule().IsMatch(trimmed))
            {
                FlushParagraph();
                var rule = new Paragraph { Margin = new Thickness(0, 4, 0, 16), FontSize = 1, LineHeight = 1 };
                Underline(rule);
                document.Blocks.Add(rule);
                continue;
            }
            if (trimmed.StartsWith('|'))
            {
                FlushParagraph();
                var table = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('|'); i++)
                    if (!TableDivider().IsMatch(lines[i].Trim())) table.Add(lines[i].Trim());
                i--;
                document.Blocks.Add(TableBlock(table));
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quote = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('>'); i++) quote.Add(lines[i].Trim().TrimStart('>').Trim());
                i--;
                var block = new Paragraph { Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(12, 2, 0, 2), BorderThickness = new Thickness(3, 0, 0, 0) };
                block.SetResourceReference(Block.BorderBrushProperty, "BorderBrush");
                block.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
                AddInlines(block.Inlines, string.Join(" ", quote));
                document.Blocks.Add(block);
                continue;
            }
            if (ListItem().Match(line) is { Success: true } item)
            {
                FlushParagraph();
                int indent = item.Groups[1].Value.Replace("\t", "    ").Length / 2;
                string marker = char.IsDigit(item.Groups[2].Value[0]) ? item.Groups[2].Value : "•";
                string body = item.Groups[3].Value;
                if (TaskBox().Match(body) is { Success: true } task) { marker = task.Groups[1].Value == " " ? "☐" : "☑"; body = task.Groups[2].Value; }
                var block = new Paragraph { Margin = new Thickness(8 + indent * 18, 0, 0, 4), TextIndent = -16, Padding = new Thickness(16, 0, 0, 0) };
                block.Inlines.Add(new Run(marker + " "));
                AddInlines(block.Inlines, body);
                document.Blocks.Add(block);
                continue;
            }
            paragraph.Add(trimmed);
        }
        FlushParagraph();
        return document;
    }

    private static void Underline(Paragraph block)
    {
        block.BorderThickness = new Thickness(0, 0, 0, 1);
        block.Padding = new Thickness(0, 0, 0, 6);
        block.SetResourceReference(Block.BorderBrushProperty, "BorderBrush");
    }

    private static Block CodeBlock(string code)
    {
        var block = new Paragraph(new Run(code)) { FontFamily = CodeFont, FontSize = 12.5, LineHeight = double.NaN, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 12), BorderThickness = new Thickness(1) };
        block.SetResourceReference(Block.BorderBrushProperty, "BorderBrush");
        block.SetResourceReference(TextElement.BackgroundProperty, "PageBrush");
        return block;
    }

    private static Block TableBlock(List<string> rows)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 12), BorderThickness = new Thickness(1, 1, 0, 0) };
        table.SetResourceReference(Table.BorderBrushProperty, "BorderBrush");
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (int index = 0; index < rows.Count; index++)
        {
            string[] cells = rows[index].Trim('|').Split('|');
            while (table.Columns.Count < cells.Length) table.Columns.Add(new TableColumn());
            var row = new TableRow();
            foreach (string value in cells)
            {
                var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = double.NaN };
                AddInlines(paragraph.Inlines, value.Trim());
                var cell = new TableCell(paragraph) { Padding = new Thickness(10, 6, 10, 6), BorderThickness = new Thickness(0, 0, 1, 1) };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "BorderBrush");
                if (index == 0) cell.FontWeight = FontWeights.SemiBold;
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        return table;
    }

    private static void AddInlines(InlineCollection inlines, string text)
    {
        text = HtmlTag().Replace(text, "");
        int position = 0;
        foreach (Match match in InlineToken().Matches(text))
        {
            if (match.Index > position) inlines.Add(new Run(Unescape(text[position..match.Index])));
            position = match.Index + match.Length;
            if (match.Groups["code"].Success)
            {
                var run = new Run(match.Groups["code"].Value) { FontFamily = CodeFont, FontSize = 12.5 };
                run.SetResourceReference(TextElement.BackgroundProperty, "ButtonHoverBrush");
                inlines.Add(run);
            }
            else if (match.Groups["image"].Success)
            {
                if (match.Groups["image"].Value.Length == 0) continue;
                var run = new Run("[" + match.Groups["image"].Value + "]");
                run.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
                inlines.Add(run);
            }
            else if (match.Groups["link"].Success)
            {
                var link = new Hyperlink { ToolTip = match.Groups["url"].Value };
                AddInlines(link.Inlines, match.Groups["link"].Value);
                link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                if (Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                    link.Click += (_, _) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                inlines.Add(link);
            }
            else if (match.Groups["bold"].Success)
            {
                var bold = new Bold();
                AddInlines(bold.Inlines, match.Groups["bold"].Value);
                inlines.Add(bold);
            }
            else if (match.Groups["italic"].Success)
            {
                var italic = new Italic();
                AddInlines(italic.Inlines, match.Groups["italic"].Value);
                inlines.Add(italic);
            }
        }
        if (position < text.Length) inlines.Add(new Run(Unescape(text[position..])));
    }

    private static string Unescape(string text) => Escaped().Replace(text, "$1").Replace("&nbsp;", " ").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex Heading();
    [GeneratedRegex(@"^([-*_])(\s*\1){2,}$")] private static partial Regex Rule();
    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")] private static partial Regex ListItem();
    [GeneratedRegex(@"^\[([ xX])\]\s+(.*)$")] private static partial Regex TaskBox();
    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")] private static partial Regex TableDivider();
    [GeneratedRegex(@"^(<[^>]+>\s*)+$|^<!--.*-->$")] private static partial Regex HtmlOnly();
    [GeneratedRegex(@"<!--.*?-->|</?[a-zA-Z][^>]*>")] private static partial Regex HtmlTag();
    [GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!|])")] private static partial Regex Escaped();
    [GeneratedRegex(@"`(?<code>[^`]+)`|!\[(?<image>[^\]]*)\]\([^)]*\)|\[(?<link>[^\]]+)\]\((?<url>[^)\s]+)[^)]*\)|\*\*(?<bold>.+?)\*\*|__(?<bold>.+?)__|(?<![\w*])\*(?<italic>[^*\s][^*]*?)\*(?![\w*])|(?<!\w)_(?<italic>[^_\s][^_]*?)_(?!\w)")]
    private static partial Regex InlineToken();
}
