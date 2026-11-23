# Command Reference

All commands are verb-first, stateless, and non-interactive. Pass `-` for any `<file|->` argument to read JSON from `stdin`.

| Command | Syntax | Description |
| :--- | :--- | :--- |
| `prime` | `mby prime` *(or `mby` / `mby --help`)* | Print compact AI-agent grammar card |
| `help` | `mby <cmd> --help` *(or `mby help <cmd>`)* | Print command-specific usage, prerequisites, flags, and examples |
| `spec` | `mby spec [type...]` | Print schemas with exact limits (`session`, `article`, `quiz`, `text`, `image`, `callout`, `markdown`, `mcq`, `flashcard`, `layouts`, `analyze`, `pkg`, `build`) |
| `apply` | `mby apply [addr] <file\|dir\|-> [-o dir] [--build dir] [--verify] [--export plat:path] [--dry-run] [--quiet\|--verbose]` | Create workspace or append components/blocks/questions from JSON, Markdown, or directory |
| `patch` | `mby patch <addr> [file\|-] [--set k=v] [--move idx:shift\|exchange]` | Surgically update fields or reorder by ID, `#path`, or `#ref` |
| `get` | `mby get <addr> [--view outline\|full\|ids]` | Inspect entity (`outline` default) |
| `ls` | `mby ls [dir]` | List discovered workspaces (scans CWD and `.meloberry/workspaces/`) |
| `rm` | `mby rm <addr>` | Delete workspace, component, block, or question immediately |
| `check` | `mby check <addr> [--quiet\|--verbose]` | Run mandatory lint validation |
| `build` | `mby build <addr\|spec\|dir\|-> [-o dir] [--verify] [--static-only] [--timeout n] [--preview] [--no-wait]` | Lint and generate WebApp with optional headless/static runtime verification |
| `batch` | `mby batch <file\|->` | Execute JSON operations array atomically in one transaction |
| `pkg` | `mby pkg export <wsId> <desktop\|android> [out] [--include-layout]`<br>`mby pkg import <file> [destDir]` | Export or import `.mbypkg` v1.0 archive |
| `layout` | `mby layout get [mcq\|flashcard\|article]`<br>`mby layout set <type> <prop> <val>`<br>`mby layout edit [target] [--no-wait]` | Query bounds, update global layout profile, or serve GUI layout editor |
| `analyze` | `mby analyze <webDir> [--headless] [--static-only] [--timeout n] [--all] [--live] [--no-wait]` | Run zero-touch headless/static WebApp verification or analyze captured telemetry reports |
| `version` | `mby version` *(or `-v` / `--version`)* | Print CLI version and .NET runtime |
