# STS2 Context Coach v0.1.3

## English

### Install
Download `Sts2ContextCoach-v0.1.3.zip` from this release and extract into `<Slay the Spire 2>/mods/Sts2ContextCoach/`. Ensure [BaseLib](https://github.com/Alchyr/BaseLib-StS2) is enabled.

### What changed
- **Embedded metadata:** Card, relic, and keyword JSON ship inside the DLL so the game does not pick up stray `*.json` files under `mods/` as extra manifests. The release zip contains `Sts2ContextCoach.dll`, `Sts2ContextCoach.json`, `contextcoach.config`, and `result_cleaned.csv` only.
- **Optional LLM coaching:** Batch scheduling, debouncing, in-game API key and model settings (`LlmSettingsPanel`), deck-profile summaries for long runs, and optional transcript logging when enabled in config.
- **Telemetry and exports:** Structured run logging under `%AppData%/Roaming/SlayTheSpire2/Sts2ContextCoach/` plus export helpers for sharing diagnostics.
- **Heuristics and state:** Broader reward/shop probing, shop economy context, combat-screen heuristics, and glossary-backed keyword hints for LLM payloads.
- **Quality:** xUnit regression tests for scoring and metadata; metadata load logging avoids Godot-backed `Log` during `dotnet test` on Windows (no more test-host access violations).
- **Docs:** README contributor map, bilingual release-note guidance in `docs/GITHUB_RELEASE_GUIDE.md`, and `docs/REPO_MEMORY_BANK.md` for maintainers.

---

## 简体中文

### 安装
从本 Release 下载 `Sts2ContextCoach-v0.1.3.zip`，解压到《杀戮尖塔 2》的 `mods/Sts2ContextCoach/` 目录，并确保已启用 [BaseLib](https://github.com/Alchyr/BaseLib-StS2)。

### 更新内容
- **元数据嵌入：** 卡牌、遗物与关键词数据打包进 DLL，避免在 `mods/` 下额外放置 `*.json` 被游戏误识别为其它 mod 清单。本 zip 仅包含 `Sts2ContextCoach.dll`、`Sts2ContextCoach.json`、`contextcoach.config` 与 `result_cleaned.csv`。
- **可选 LLM 辅助：** 支持请求合并与防抖、游戏内 API Key/模型设置面板、长跑下的卡组摘要，以及在配置开启时的对话转写日志。
- **遥测与导出：** 在 `%AppData%/Roaming/SlayTheSpire2/Sts2ContextCoach/` 写入结构化运行日志，并提供导出以便分享诊断信息。
- **启发式与状态：** 扩展奖励/商店探测、商店经济上下文、战斗界面启发式，以及为 LLM 载荷提供关键词表提示。
- **质量与测试：** 增加 xUnit 回归测试；元数据加载日志在 `dotnet test` 下不再触发 Godot 相关 `Log` 静态初始化，修复 Windows 上测试进程偶发崩溃。
- **文档：** README 贡献者速览表、`docs/GITHUB_RELEASE_GUIDE.md` 中的中英 Release 说明建议，以及维护者向的 `docs/REPO_MEMORY_BANK.md`。
