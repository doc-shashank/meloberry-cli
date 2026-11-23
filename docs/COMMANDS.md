# Command Reference

All commands are verb-first, stateless, and non-interactive. Pass `-` for any `<file|->` argument to read JSON from `stdin`.

| Command | Syntax | Description |
| :--- | :--- | :--- |
| `prime` | `mby prime` *(or `mby` / `mby --help` / `mby -h`)* | Print compact AI-agent grammar card |
| `help` | `mby <cmd> --help` *(or `mby <cmd> -h` / `mby help <cmd>`)* | Print command-specific usage, prerequisites, flags, and examples |
| `spec` | `mby spec [type...]` | Print schemas with exact limits (`session`, `article`, `quiz`, `text`, `image`, `mcq`, `flashcard`, `layouts`, `callout`, `markdown`, `analyze`, `pkg`, `build`, `batch`, `patch`) |
| `apply` | `mby apply [addr] <file\|dir\|-> [-o dir] [--build dir] [--verify] [--static-only] [--timeout n] [--export plat[:path]] [--dry-run] [--quiet\|--verbose]` | Create workspace or append components/blocks/questions from JSON, Markdown, or directory |
| `patch` | `mby patch <addr> [file\|-] [--set k=v] [--move idx:shift\|exchange]` | Surgically update fields or reorder by ID, `#path`, or `#ref` |
| `get` | `mby get <addr> [--view outline\|full\|ids]` | Inspect entity (`outline` default) |
| `ls` | `mby ls [dir] [--all]` | List workspaces in CWD + `.meloberry/workspaces/` (or all globally registered workspaces with `--all`) |
| `rm` | `mby rm <addr>` | Delete workspace, component, block, or question immediately |
| `check` | `mby check <addr> [--quiet\|--verbose]` | Run mandatory lint validation |
| `build` | `mby build <addr\|spec\|dir\|-> [-o dir] [--verify] [--static-only] [--ephemeral] [--timeout n] [--preview] [--no-wait]` | Lint and generate WebApp with optional headless/static runtime verification |
| `batch` | `mby batch <file\|->` | Execute JSON operations array atomically in one transaction |
| `pkg` | `mby pkg export <wsId> <desktop\|android> [outPath] [--include-layout]`<br>`mby pkg import <file> [destDir]` | Export `.mbypkg` v1.0 archive (defaults to workspace project root or parent dir) or import (defaults to CWD) |
| `layout` | `mby layout get [mcq\|flashcard\|article]`<br>`mby layout set <mcq\|flashcard\|article> <prop> <val>`<br>`mby layout edit [target] [--no-wait]` | Query bounds, update global layout profile, or serve GUI layout editor (`article-text`, `image`, `mcq-question`, `mcq-options`, `flashcard`) |
| `analyze` | `mby analyze <webDir\|report.json> [--headless] [--static-only] [--timeout n] [--all] [--live] [--no-wait]` | Run zero-touch headless/static WebApp verification or analyze captured telemetry reports |
| `version` | `mby version` *(or `-v` / `--version`)* | Print CLI version and .NET runtime |
