using System.Text;

namespace GitManager.Services.CommitMessages;

public static class SummaryTextBatches
{
    // 优先按整行拆分，超长单行也完整保留；不拆开 UTF-16 代理对。
    public static IEnumerable<string> Split(string text, int limit)
    {
        if (limit < 2) throw new InvalidOperationException("总结分批长度配置过小。");
        if (text.Length == 0) { yield return ""; yield break; }
        for (int offset = 0; offset < text.Length;)
        {
            int length = Math.Min(limit, text.Length - offset);
            if (offset + length < text.Length)
            {
                int newline = text.LastIndexOf('\n', offset + length - 1, length);
                if (newline >= offset) length = newline - offset + 1;
                else if (char.IsHighSurrogate(text[offset + length - 1]) && char.IsLowSurrogate(text[offset + length])) length--;
            }
            yield return text.Substring(offset, length);
            offset += length;
        }
    }

    public static IReadOnlyList<string> Pack(IEnumerable<string> blocks, int limit, string prefix)
    {
        var batches = new List<string>();
        var current = new StringBuilder(prefix);
        foreach (string block in blocks)
        {
            if (prefix.Length + block.Length > limit) throw new InvalidOperationException("单个总结片段超过输入上限，无法完整发送。");
            if (current.Length + block.Length > limit)
            {
                batches.Add(current.ToString());
                current.Clear().Append(prefix);
            }
            current.Append(block);
        }
        if (current.Length > prefix.Length) batches.Add(current.ToString());
        return batches;
    }
}
