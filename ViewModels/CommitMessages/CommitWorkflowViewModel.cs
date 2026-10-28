using GitManager.Services;
using GitManager.Services.CommitMessages;

namespace GitManager.ViewModels;

public sealed partial class MainViewModel
{
    public Task CommitAsync()
    {
        if (!CanWriteRepository || _disposed) return Task.CompletedTask;
        CancelCommitSummary();
        return ExecuteAsync("正在保存本地版本", async () =>
        {
            SetSummaryCost(null);
            if (IsMerging) await Git.FinishMergeAsync(Root, CommitMessage);
            else
            {
                var files = (await Git.ReadChangesAsync(Root)).ToArray();
                Replace(Changes, files);
                SelectedChange = files.FirstOrDefault(file => file.Path == SelectedChange?.Path) ?? files.FirstOrDefault();
                Notify(nameof(ChangesTitle));
                foreach (var file in files) file.Selected = true;
                if (files.Length == 0)
                {
                    Status = "没有未提交改动";
                    return;
                }

                Status = "正在总结全部改动";
                var result = await new CommitMessageGenerator(Git, Settings).GenerateAsync(Root, files, _lifetimeCancellation.Token);
                SetSummaryCost(result.Cost);
                var current = await Git.ReadChangesAsync(Root);
                if (!current.Select(file => file.Path).ToHashSet(StringComparer.Ordinal).SetEquals(files.Select(file => file.Path)))
                    throw new InvalidOperationException("改动已变化，请重新提交。");

                CommitMessage = result.Message;
                AppLog.Write($"已全选并通过{result.Source}总结 {files.Length} 个文件，准备提交");
                Status = "正在保存本地版本";
                await Git.CommitAsync(Root, files, result.Message);
            }
            CommitMessage = "";
            await RefreshCoreAsync();
            Status = "已保存到本地";
        });
    }
}
