# Meloberry CLI (`mby`)

Stateless, AI-agent-first compiler for deterministic study helper WebApps. Agents supply content and organization via JSON, Markdown (`.md` + frontmatter), or modular directories; `mby` owns themes, layout bounds, validation, and rendering.

## Docs

- [COMMANDS.md](COMMANDS.md) — Command reference
- [AI_AGENT_PLAYBOOK.md](AI_AGENT_PLAYBOOK.md) — Minimal-token agent workflows
- [JSON_AND_PACKAGES.md](JSON_AND_PACKAGES.md) — Strict JSON/Markdown specs & `.mbypkg` v1.0
- [LAYOUTS_LINT_AND_WEBAPP.md](LAYOUTS_LINT_AND_WEBAPP.md) — Layout bounds, lint rules, search & headless verification

## Entity IDs & Addressing

| Prefix | Entity | Persistence |
| :--- | :--- | :--- |
| `Session_<YYYYMMDD_HHMMSS_fff>` | Study Session | `<dir>/<id>/<id>.db` + `assets/` |
| `Component_<YYYYMMDD_HHMMSS_fff>` | Session Component (`article` \| `quiz`) | Parent session SQLite DB |
| `ArtComp_<YYYYMMDD_HHMMSS_fff>` | Article Block (`text` \| `image` \| `callout`) | Parent session SQLite DB |
| `Set_<YYYYMMDD_HHMMSS_fff>` | Standalone Quiz (`mcq` \| `flashcard` \| `mixed`) | `<dir>/<id>/<id>.db` + `assets/` |
| `Q_<YYYYMMDD_HHMMSS_fff>` | Question (`mcq` \| `flashcard`) | Parent quiz/component |

Unless `-o` is specified, new workspace DB folders default under `.meloberry/workspaces/<id>/`, while `ls` discovers both CWD and `.meloberry/workspaces/`. Component `ref` slugs (`^[a-z0-9][a-z0-9_-]{0,31}$`) are deterministically auto-generated from `title`/filename if omitted.

All read, patch, lint, and delete commands accept either a direct ID or a workspace path/slug:
- `<wsId>#c<n>` — 1-based component in session
- `<wsId>#c<n>.b<m>` — 1-based block in article component
- `<wsId>#c<n>.q<m>` — 1-based question in quiz component
- `<setId>#q<m>` — 1-based question in standalone quiz
- `<wsId>#<ref>` — entity with matching `ref` slug (`^[a-z0-9][a-z0-9_-]{0,31}$`)

## Global Flags & Environment

| Flag / Env | Effect |
| :--- | :--- |
| `--json` | Minified single-line JSON on stdout/stderr |
| `--app-data <dir>` | Override `app_settings.db` & `.mby_id_clock` directory |
| `--quiet` / `--verbose` | Minimal summary output vs full lists/reports |
| `-h`, `--help` | Print prime grammar card (or command-specific help when paired with `<cmd>`) |
| `-v`, `--version` | Print CLI version and .NET runtime |
| `--` | End of options (required before positional arguments starting with `-`; unrecognized short/long flags before `--` fail with `Cli.ParseError`, while bare `-` reads `stdin`) |
| `MBY_AGENT=1` | Implicitly enables `--json` (also accepts `true`) |
| `MBY_APP_DATA=<dir>` | Default `--app-data` directory |
| `MBY_BROWSER_PATH=<path>` | Explicit Edge/Chrome executable path for `--headless` / `--verify` |

Command-specific output flags: `-o`, `--out <dir>` is supported only on `apply` and `build` (`pkg` uses positional `[outPath]` / `[destDir]`).

## JSON Envelope & Exit Codes

Success (`exit 0`, stdout): `{"success":true,"data":{...}}`  
Failure (`exit != 0`, stderr): `{"success":false,"error":{"code":"...","description":"...","type":"...","path":"...","hint":"..."},"data":{...}}` *(optional `data` carries structured diagnostic reports for `check`, `analyze --headless/--static-only`, and `build --verify`)*

| Code | Meaning |
| :--- | :--- |
| `0` | Success |
| `1` | General I/O or SQLite error |
| `2` | Validation, JSON schema, bounds, or `Lint.Failed` |
| `3` | Target workspace, entity, file, or topic not found |
| `4` | Conflict (`Address.DuplicateRef`, `Package.TargetExists`, etc.) |
