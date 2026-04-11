# GitHub Repo + Release Guide

## 0) Before First Push

1. Ensure secrets are not tracked:
   - `tools/data_refresh/config.yaml` should not contain API keys.
   - `.gitignore` includes `tools/data_refresh/config.yaml`.
2. If any key was exposed previously, revoke/regenerate it.

## 1) Create GitHub Repository

1. On GitHub, click **New repository**.
2. Suggested name: `sts2-context-coach`.
3. Choose visibility (private/public).
4. Do **not** add README/gitignore/license in GitHub UI (repo already has files).
5. Click **Create repository**.

## 2) Initialize Local Git + First Push

From repo root:

```powershell
git init
git add .
git commit -m "Initial release-ready STS2 Context Coach"
git branch -M main
git remote add origin https://github.com/<your-user>/<your-repo>.git
git push -u origin main
```

## 3) Build Release Zip

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\release\build-release.ps1
```

Output:
- `release/Sts2ContextCoach-v<version>.zip`

## 4) Create GitHub Release

1. Open your GitHub repo -> **Releases** -> **Draft a new release**.
2. Tag: `v<version>` (match `Sts2ContextCoach.json` version).
3. Title: `STS2 Context Coach v<version>`.
4. Upload zip from `release/`.
5. Suggested notes:
   - Context score + signed reasons
   - Upgrade + enchantment scoring
   - Shop economy handling
   - Chinese localization support
6. Publish release.

## 5) User Installation Snippet (for release notes)

1. Download release zip.
2. Extract into `<Slay the Spire 2>/mods/Sts2ContextCoach/`.
3. Ensure BaseLib is enabled.
4. Launch game.

## 6) Bilingual release notes (English + Chinese)

GitHub Releases support Markdown in the description. A practical pattern is **English first**, then a **简体中文** section with the same bullets.

**English — suggested bullets**

- Version bump and any breaking changes (usually none for this mod).
- Player-visible improvements (overlay, scoring, localization, shop/reward behavior).
- Maintainer-only changes (metadata pipeline, tests, docs) if they affect contributors.

**简体中文 — 建议要点**

- 版本号与兼容性说明（本 mod 通常向后兼容）。
- 玩家可见的改进（界面、评分逻辑、中文文案、商店/奖励等）。
- 维护者相关变更（数据管线、测试、文档），如影响贡献流程可简要说明。

Keep both sections aligned so bilingual players can skim either half and see the same scope.
