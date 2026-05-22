Publish a new tiktop release. The version number is in $ARGUMENTS (e.g. "1.2.3").

If $ARGUMENTS is empty, ask the user for the version before proceeding.

Steps — execute in order, stop and report if any step fails:

## 1. Validate working tree
Run `git status --short`. If there are uncommitted changes (other than tiktop/tiktop.csproj which will be modified), warn the user and ask whether to continue.

## 2. Determine changelog range
Run `git tag --sort=-v:refname | head -1` to find the previous tag.
- If a previous tag exists, range is `{prev_tag}..HEAD`
- If no previous tag exists, use the full history

## 3. Collect commits for changelog
Run:
```
git log {range} --pretty=format:"%s" --no-merges
```
Group commits by conventional-commit prefix into these sections (skip section if empty):
- **Features** — lines starting with `feat:`
- **Fixes** — lines starting with `fix:`
- **Performance** — lines starting with `perf:`
- **Refactoring** — lines starting with `refactor:`
- **Other** — everything else (skip `chore:` lines unless meaningful)

Format as a markdown bullet list per section. Strip the prefix (`feat: `, `fix: `, etc.) from each item. Capitalize first letter.

**Language:** Write all changelog entries in English. If a commit message is in Czech or another language, translate it to English before including it.

## 4. Update version in csproj
Edit `tiktop/tiktop.csproj`: change `<Version>...</Version>` to `<Version>{version}</Version>`.

## 5. Stage and commit
```
git add tiktop/tiktop.csproj
git commit -m "chore: release v{version}"
```

## 6. Create annotated tag
```
git tag -a v{version} -m "v{version}"
```

## 7. Show summary and ask for confirmation before push
Display:
- New version: v{version}
- Previous tag: {prev_tag} (or "first release")
- Changelog preview (the markdown from step 3)
- Actions about to happen: push commit + tag → GitHub Actions will build and attach binaries to the release

Ask: "Push and create GitHub release? (yes/no)"

## 8. Push
```
git push
git push origin v{version}
```

## 9. Create GitHub release

Append a Downloads section to the changelog. Asset URLs follow the pattern
`https://github.com/danikf/tiktop/releases/download/v{version}/{filename}`:

```
{changelog_markdown}

## Downloads
| Platform | Download |
|----------|----------|
| Windows (x64) | [tiktop.exe](https://github.com/danikf/tiktop/releases/download/v{version}/tiktop.exe) |
| Linux (x64) | [tiktop](https://github.com/danikf/tiktop/releases/download/v{version}/tiktop) |
| macOS (x64) | [tiktop-macos](https://github.com/danikf/tiktop/releases/download/v{version}/tiktop-macos) |
```

Then create the release:
```
gh release create v{version} \
  --title "v{version}" \
  --notes "{full_notes_with_downloads}" \
  --draft=false
```

Report the release URL when done.
