# Meloberry CLI (`mby` v2.0.0)

Stateless, AI-agent-first compiler for deterministic study helper WebApps. Agents supply content and organization via JSON, Markdown (`.md` + frontmatter), or modular directories; `mby` owns themes, layout bounds, validation, and rendering.

## Docs

- [COMMANDS.md](COMMANDS.md) — Command reference & two-tier `spec` classification (`@content` vs. `@other`)
- [AI_AGENT_PLAYBOOK.md](AI_AGENT_PLAYBOOK.md) — Minimal-token agent workflows
- [JSON_AND_PACKAGES.md](JSON_AND_PACKAGES.md) — Strict JSON/Markdown specs, surgical patching & `.mbypkg` v2.0
- [LAYOUTS_LINT_AND_WEBAPP.md](LAYOUTS_LINT_AND_WEBAPP.md) — Layout bounds, lint rules, search & headless verification

## Entity IDs & Addressing

| Prefix | Entity | Persistence |
| :--- | :--- | :--- |
| `Session_<YYYYMMDD_HHMMSS_fff>` | Study Session | `<dir>/<id>/<id>.db` + `assets/` |
| `Component_<YYYYMMDD_HHMMSS_fff>` | Session Component (`article` \| `quiz`) | Parent session SQLite DB |
| `ArtComp_<YYYYMMDD_HHMMSS_fff>` | Article Block (`text` \| `image` \| `callout` \| `table`) | Parent session SQLite DB |
| `Set_<YYYYMMDD_HHMMSS_fff>` | Standalone Quiz (`mcq` \| `flashcard` \| `mixed`) | `<dir>/<id>/<id>.db` + `assets/` |
| `Q_<YYYYMMDD_HHMMSS_fff>` | Question (`mcq` \| `flashcard` \| `fill-blank` \| `assertion-reason` \| `spot-error` \| `diff` \| `mechanism` \| `relabel`) | Parent quiz/component |

Unless `-o` is specified, new workspace DB folders default under `.meloberry/workspaces/<id>/`, while `ls` discovers both CWD and `.meloberry/workspaces/`. Component `ref` slugs (`^[a-z0-9][a-z0-9_-]{0,31}$`) are deterministically auto-generated from `title`/filename if omitted.

All read (`get`), `patch`, `check`, and `rm` commands accept either a direct ID or a workspace path/slug (`apply [addr]` targets a `session`, `component`, or standalone `quiz` only — use `patch` for blocks or questions):
- `<sessionId>#c<n>` — 1-based component in session
- `<sessionId>#c<n>.b<m>` — 1-based block in article component
- `<sessionId>#c<n>.q<m>` — 1-based question in quiz component
- `<setId>#q<m>` — 1-based question in standalone quiz (`Set_*` only; use `<sessionId>#c<n>.q<m>` inside sessions)
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
| `1` | General I/O, SQLite, cancellation, or unexpected CLI error (always emits JSON error envelope when `--json` / `MBY_AGENT=1`) |
| `2` | Validation, JSON/Markdown schema, bounds, or `Lint.Failed` |
| `3` | Target workspace, entity, file, or topic not found |
| `4` | Conflict (`Address.DuplicateRef`, `Package.TargetExists`, etc.) |
