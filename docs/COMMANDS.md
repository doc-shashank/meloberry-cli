# Command Reference

All commands are verb-first, stateless, and non-interactive. Pass `-` for any `<file|->` argument to read JSON from `stdin`.

| Command | Syntax | Description |
| :--- | :--- | :--- |
| `prime` | `mby prime` *(or `mby` / `mby --help`)* | Print compact AI-agent grammar card |
| `spec` | `mby spec [type...]` | Print micro-schema for `session`, `article`, `quiz`, `text`, `image`, `mcq`, `flashcard`, `layouts` |
| `apply` | `mby apply [addr] <file\|-> [-o dir] [--build dir] [--export plat:path] [--dry-run]` | Create workspace or append components/blocks/questions from JSON |
| `patch` | `mby patch <addr> [file\|-] [--set k=v] [--move idx:shift\|exchange]` | Surgically update fields or reorder by ID, `#path`, or `#ref` |
| `get` | `mby get <addr> [--view outline\|full\|ids]` | Inspect entity (`outline` default) |
| `ls` | `mby ls [dir]` | List discovered workspaces |
| `rm` | `mby rm <addr>` | Delete workspace, component, block, or question immediately |
| `check` | `mby check <addr>` | Run mandatory lint validation |
| `build` | `mby build <addr\|spec.json\|-> [-o dir] [--preview] [--no-wait]` | Lint and generate WebApp (supports one-shot JSON/`stdin`) |
| `batch` | `mby batch <file\|->` | Execute JSON operations array atomically in one transaction |
| `pkg` | `mby pkg export <wsId> <desktop\|android> [out] [--include-layout]`<br>`mby pkg import <file> [destDir]` | Export or import `.mbypkg` v1.0 archive |
| `layout` | `mby layout get [mcq\|flashcard\|article]`<br>`mby layout set <type> <prop> <val>`<br>`mby layout edit [target] [--no-wait]` | Query bounds, update global layout profile, or serve GUI layout editor |
| `analyze` | `mby analyze <webDir> [--all] [--live] [--no-wait]` | Summarize `reports/*.analysis.json` or capture live browser telemetry |
| `version` | `mby version` *(or `-v` / `--version`)* | Print CLI version and .NET runtime |
