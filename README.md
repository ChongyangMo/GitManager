# GitManager

使用 WPF 与 .NET 10 编写的 Windows Git 管理器，直接调用本机 Git，可离线查看改动、提交版本和恢复代码。

## 使用

1. 启动 `GitManager.exe`，点击“添加仓库”选择已有仓库的根目录，也可新建或克隆仓库。
2. 在“改动”中查看并排差异，勾选文件、填写说明后点击“提交到本地”。提交只保存所选文件，不会自动推送。
3. 在“历史”中选择提交和文件，查看差异，或恢复指定文件、整个版本。最多加载当前分支最近 300 条提交。
4. 恢复前，相关未提交修改会保存为 Git stash，可通过“恢复点”取回。恢复不重写历史；检查恢复后的改动，再提交为新版本。
5. 需要同步时，通过“远程”设置 origin 地址，再执行获取、拉取或推送。拉取采用快进方式，推送不使用强制覆盖。

切换分支、拉取和取回恢复点前，需先处理当前修改。仓库中存在合并或变基等未完成操作时，应先完成该操作。恢复点不会自动删除；有冲突时请保留记录并解决冲突。

## 配置

安装 Git for Windows，默认程序路径为 `C:\Program Files\Git\cmd\git.exe`，也可在“设置”中修改。远程认证使用 Git 自身的凭据管理器；不要把令牌或密码填入仓库地址。

用户配置与日志保存在当前用户的 `AppData\Local\GitManager` 下，分别为 `settings.json` 和 `logs`。不需要配置环境变量。设置中填写的提交姓名和邮箱仅写入当前仓库，不修改全局身份。

快捷键：`F5` 刷新、`Ctrl+O` 添加仓库、`Ctrl+Enter` 提交。

## 构建

安装 .NET 10 SDK，在 Windows 上执行：

```powershell
dotnet build .\GitManager.csproj -c Release
dotnet publish .\GitManager.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
```

发布目录中的 `GitManager.exe` 可直接启动，Git 仍需在本机安装。文本差异预览支持不超过 2 MiB 的 UTF-8 文件，最多显示 10000 行；二进制文件只显示状态。

## 目录

- `Models/`：仓库、提交、文件与差异数据。
- `Services/`：Git 进程调用、仓库读写、恢复、对比、配置与日志。
- `ViewModels/`：仓库、改动与历史的界面状态。
- `Views/`：差异查看器和通用对话框。
- `Themes/`：沿用 AutoCopy 的配色和通用控件样式。
- `Checks/`：使用真实临时仓库验证本地 Git 流程。
