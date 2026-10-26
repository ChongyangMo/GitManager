namespace GitManager.Services.CommitMessages;

internal sealed class CommitContentBatchBuilder(QwenOptions options)
{
    private const string Prefix = "请总结以下真实 Git 改动。同一文件的多个片段共享文件统计，不要重复累加。\n";
    private readonly List<string> _blocks = [];

    public void AddFile(string details, string statistics, string patch)
    {
        string header = "\n文件：" + details + "\n统计：" + statistics + "\n差异或新增内容：\n";
        int limit = Math.Min(options.MaxFileCharacters, options.MaxInputCharacters - Prefix.Length - header.Length - 80);
        if (limit < 2) throw new InvalidOperationException("文件路径信息超过总结输入限制，请增大输入长度配置。");
        var parts = SummaryTextBatches.Split(patch, limit).ToArray();
        for (int index = 0; index < parts.Length; index++)
            _blocks.Add(header + (parts.Length > 1 ? $"文件片段 {index + 1}/{parts.Length}：\n" : "") + parts[index] + "\n");
    }

    public IReadOnlyList<string> Build() => SummaryTextBatches.Pack(_blocks, options.MaxInputCharacters, Prefix);
}
