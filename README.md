# Codex 对话同步 (CodexSyncWizard)

让 Codex 在不同 model_provider（官方登录 / sub2api / 自建中转 / 其他渠道）之间切换时，**历史对话依然全都看得到**。

跨渠道迁移、按项目筛选、批量删除、cc-switch 联动，全在一个绿色版 GUI 里。

![Windows](https://img.shields.io/badge/Windows-10%2F11-blue)
![License](https://img.shields.io/badge/License-MIT-green)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)

---

## 解决什么问题

Codex 切换登录方式（OAuth 官方账户 ↔ sub2api ↔ 别的中转）后，**之前的对话就消失了** —— 因为对话的 `model_provider` 字段还是老值，新渠道下查询不到。

这工具会：
1. 扫描 `~/.codex/sessions/`（含归档）+ `state_5.sqlite` 的 `threads` 表
2. 按 model_provider 分组列出所有对话
3. 让你**精挑细选**要迁移哪些对话 / 哪个项目，目标渠道点选即可
4. 自动备份再修改，万一不对一键还原

## 截图

待补。

## 主要功能

- **仪表板**：所有渠道一目了然，每个渠道一张卡片显示对话数 / 数据库记录 / 是否「在用」
- **对话浏览**：点渠道卡片打开浏览窗口，按项目（cwd）分组，标题来自 SQLite 自动生成
- **选中迁移**：每条对话 / 每个项目可独立勾选，下拉选目标渠道一键迁移
- **删除**：清理某个渠道下的对话
- **设为默认**：把 config.toml 顶层 `model_provider` 改成指定渠道（hover provider 卡片浮出链接）
- **后台监听**：勾选后最小化到托盘，cc-switch 一切换就自动合并对话历史
- **智能还原**：兼容 [Dailin521/codex-provider-sync](https://github.com/Dailin521/codex-provider-sync) 的紧凑备份格式
- **自动备份 / 还原**：每次操作前自动备份，可一键回滚
- **拖入文件夹**：把 `.codex` 目录拖到窗口直接切换路径

## 系统要求

- Windows 10 / 11 x64
- 已安装 [Codex CLI](https://github.com/openai/codex) 或 Codex Desktop

不需要 .NET 运行时（自包含单文件 exe）。

## 下载与使用

去 [Releases](https://github.com/cjhfff/codex-sync-wizard/releases) 下载最新 `CodexSyncWizard-vX.Y.Z-win-x64.zip`，解压后双击 `CodexSyncWizard.exe` 即可。

首次运行 Windows 可能弹「保护了你的电脑」（SmartScreen），点「更多信息 → 仍要运行」即可。

## 使用流程

1. 打开 app，自动扫描 `~/.codex`
2. 在仪表板上点任一渠道卡片
3. 在弹出的对话浏览窗口里勾选要迁移的对话（或整个项目）
4. 底部下拉选目标渠道 → 点「迁移选中」
5. 重启 Codex 客户端，对话历史就在新渠道里了

## 数据安全

- 每次操作前自动备份到 `~/.codex/backups_state/provider-sync/`
- 默认保留最近 5 份
- 所有改动可通过「高级 → 备份列表」一键还原

## 与 cc-switch 联动

如果你装了 [cc-switch](https://github.com/farion1231/cc-switch)，本工具会自动检测并显示当前 cc-switch 选定的渠道。

开启「高级 → 后台监听」后，cc-switch 切换 provider 时本工具会**自动合并对话历史**到新渠道，零点击。

## 从源码构建

```bash
git clone https://github.com/cjhfff/codex-sync-wizard
cd codex-sync-wizard/CodexSyncWizard.Avalonia
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
```

Mac / Linux 构建：把 `-r win-x64` 改成 `-r osx-arm64` / `-r osx-x64` / `-r linux-x64` 即可（UI 用 Avalonia 跨平台）。

## 致谢

灵感来自 [Dailin521/codex-provider-sync](https://github.com/Dailin521/codex-provider-sync) — 第一个解决这个痛点的工具。本工具重写了 GUI、扩充了批量选择 / 项目分组 / cc-switch 联动等能力。

## License

MIT
