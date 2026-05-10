# Codex 对话同步 (CodexSyncWizard)

**让 Codex CLI 和 Codex Desktop 的所有历史对话，都能在你的中转站继续聊。**

一键归并 + 跨平台 + GUI/CLI 双模式。

![Windows](https://img.shields.io/badge/Windows-10%2F11-blue)
![macOS](https://img.shields.io/badge/macOS-10.14%2B-lightgrey)
![Linux](https://img.shields.io/badge/Linux-x64-orange)
![License](https://img.shields.io/badge/License-MIT-green)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)

---

## 这工具解决什么

Codex（CLI 或 Desktop）每次切换登录方式（OAuth ↔ 中转 ↔ 别的代理）后：

- 老对话**还在**，但点进去续聊会报「`Model provider XXX not found`」或上游 401/502
- 因为对话存了原始 `model_provider` 字段，而切了之后那个 provider 在 config.toml 里没定义了

这工具把所有对话的 provider 字段统一改成你指定的「主渠道」，让续聊全部走你的中转站。

## 主要功能

- **一键归并** — 主窗口顶部按钮，把所有 provider 的对话全合并到选定的目标
- **细粒度迁移** — 按对话 / 按项目 / 按 source（CLI / Desktop / exec）勾选迁移
- **provider 仪表板** — 卡片视图，每渠道对话数 + 在用标记，hover 浮出「设为默认」
- **CLI 模式** — 同一个 exe 也是命令行工具，无参数 GUI、有参数 CLI（见下）
- **嵌入命令面板** — GUI 里直接调用 CLI 子命令，输入框 + 历史 + 输出区
- **批量操作窗口** — 图形化按 provider 批量迁移 / 删除
- **工作区管理** — 把对话所属的项目一键加入 Codex Desktop 左侧栏（避免对话「藏起来」）
- **后台监听** — 托盘模式监听 cc-switch / config.toml 切换，自动归并
- **智能还原** — 兼容 Dailin521/codex-provider-sync 的紧凑备份格式
- **每次操作前自动备份** — 「高级」里可一键还原

## 下载

到 [Releases](https://github.com/cjhfff/codex-sync-wizard/releases) 选你的平台：

| 平台 | 下载文件 |
|------|---------|
| Windows 10/11 x64 | `CodexSyncWizard-vX.Y.Z-win-x64.zip` |
| macOS Intel + Apple Silicon | `CodexSyncWizard-vX.Y.Z-macos.zip` |
| Linux x64 | `CodexSyncWizard-vX.Y.Z-linux-x64.zip` |

均为单文件自包含，**无需装 .NET 运行时**。

### Windows
解压双击 `CodexSyncWizard.exe`。SmartScreen 弹「保护了你的电脑」的话点「更多信息 → 仍要运行」（未购代码签名证书）。

### macOS
解压选对应你芯片的 `.app`：
- Apple 芯（M1/M2/M3）→ `CodexSyncWizard-AppleSilicon.app`
- Intel → `CodexSyncWizard-Intel.app`

第一次打开 Finder 右键 `.app` → 选「打开」绕过 Gatekeeper（未付费 Apple Developer 签名）。或终端跑：
```bash
xattr -cr CodexSyncWizard-AppleSilicon.app
```

### Linux
```bash
unzip CodexSyncWizard-vX.Y.Z-linux-x64.zip
chmod +x codex-sync-wizard
./codex-sync-wizard
```

## 用法

### GUI（推荐普通用户）
1. 双击启动，自动扫描 `~/.codex`
2. 顶部「**一键归并到主渠道**」选目标 → 点按钮
3. 完成。重启 Codex 客户端即可

### CLI（脚本/批处理/服务器）
```bash
codex-sync scan                                # 看现状
codex-sync providers                           # 列已定义的 provider
codex-sync list --provider OpenAI              # 列对话
codex-sync migrate --from openai --to custom   # 一个 provider 全迁
codex-sync migrate --all-to custom --yes       # 全部归到一个
codex-sync delete --provider sub2api --yes     # 删除某 provider
codex-sync register-workspace ~/HermesAgent    # 加入 Codex 工作区
codex-sync set-default custom                  # 改 config.toml 默认
codex-sync restore --list                      # 列备份
codex-sync restore --apply <name>              # 还原备份
codex-sync smart-restore                       # 还原 Dailin521 紧凑备份
codex-sync help                                # 完整命令清单
```

GUI 内也能跑 — 主窗口底部「命令」按钮打开命令面板。

## 数据安全

- 每次操作前自动备份到 `~/.codex/backups_state/provider-sync/`
- 默认保留最近 5 份
- 「高级 → 备份列表」一键还原
- 智能还原能叠加 Dailin521 旧工具留下的紧凑备份

## 与 cc-switch 联动

[cc-switch](https://github.com/farion1231/cc-switch) 是常用的 Codex provider 切换器。本工具：
- 自动检测 cc-switch 是否安装
- 「高级」可开启**后台监听**：cc-switch 切完自动合并对话历史，零点击
- 写入 config.toml 时检测 cc-switch 是否在跑，避免被覆盖

## 系统要求

- **Windows** 10 / 11 x64
- **macOS** 10.14+ (Mojave) Intel 或 Apple Silicon
- **Linux** x64（任意主流发行版）

## 从源码构建

```bash
git clone https://github.com/cjhfff/codex-sync-wizard
cd codex-sync-wizard/CodexSyncWizard.Avalonia

# 选目标平台
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true

# Mac: -r osx-arm64 / -r osx-x64
# Linux: -r linux-x64
```

## 致谢

灵感来自 [Dailin521/codex-provider-sync](https://github.com/Dailin521/codex-provider-sync) — 第一个解决这痛点的工具。本工具用 Avalonia 跨平台重写 + 扩充批量选择 / 项目分组 / cc-switch 联动 / CLI 模式 / 工作区注册。

## License

MIT
