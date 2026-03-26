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
