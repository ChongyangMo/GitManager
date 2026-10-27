# GitManager 项目规则

## 发布

- 发布只输出到 `publish\win-x64`，命令：
  `dotnet publish .\GitManager.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64 --nologo`
- 禁止在 `publish\` 下新建其他目录（如 `xxx-update`、`xxx-release`、`xxx-debug`）。
- 覆盖 `publish\win-x64` 前不做备份，禁止创建 `win-x64.before-*`、`before-*`、`backup-*` 目录，也不要往 `artifacts\release-backups` 放备份。需要回退时用 git 历史重新发布。
- 发布时如果 `GitManager.exe` 正在运行导致文件被占用，先结束该进程，发布完成后再启动 `publish\win-x64\GitManager.exe`。
- 以往记忆中“发布到单独目录”“替换前保留带时间戳的备份”的做法已废止，以本文件为准。
