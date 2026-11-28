# Command Reference

All commands are verb-first, stateless, and non-interactive. Pass `-` for any `<file|->` argument to read JSON from `stdin`.

| Command | Syntax | Description |
| :--- | :--- | :--- |
| `prime` | `mby prime` *(or `mby` / `mby --help` / `mby -h`)* | Print compact AI-agent grammar card |
| `help` | `mby <cmd> --help` *(or `mby <cmd> -h` / `mby help <cmd>`)* | Print command-specific usage, prerequisites, flags, and examples |
| `spec` | `mby spec [type\|@group...] [--icons] [--nested]` | Print schemas with exact limits (grouped by `@content` vs. `@other`; `--icons` adds SVG icons, `--nested` adds nested item schemas) |
| `apply` | `mby apply [addr] <file\|dir\|-> [-o dir] [--build dir] [--verify] [--static-only] [--timeout n] [--export plat[:path]] [--dry-run] [--quiet\|--verbose]` | Create workspace or append to `session`/`component`/`quiz` (`[addr]` cannot target blocks/questions); writes drafts and returns `data.lint` (use `--build`/`--export`/`check` for exit-`2` lint gating) |
| `patch` | `mby patch <addr> [file\|-] [--set k=v] [--move idx:shift\|exchange]` | Surgically update fields or reorder by ID, `#path`, or `#ref` |
| `get` | `mby get <addr> [--view outline\|full\|ids\|spec]` | Inspect entity by ID, `#path`, `#ref`, or direct workspace folder path (`outline` default; `spec` emits canonical `apply`-compatible JSON + parallel `idMap`) |
| `ls` | `mby ls [dir] [--all]` | List workspaces in CWD + `.meloberry/workspaces/` (or all globally registered workspaces with `--all`) |
| `rm` | `mby rm <addr>` | Delete workspace, component, block, or question immediately |
| `check` | `mby check <addr> [--quiet\|--verbose]` | Run mandatory lint validation |
| `build` | `mby build <addr\|spec\|dir\|-> [-o dir] [--verify] [--static-only] [--ephemeral] [--timeout n] [--preview] [--no-wait]` | Lint and generate WebApp with optional headless/static runtime verification |
| `batch` | `mby batch <file\|->` | Execute JSON operations array atomically in one transaction |
| `pkg` | `mby pkg export <wsId> <desktop\|android> [outPath] [--include-layout]`<br>`mby pkg import <file> [destDir]` | Export `.mbypkg` v2.0 archive (defaults to project root or parent dir; `[outPath]` ending in `/` or `\` or an existing dir writes `<dir>/<wsId>.mbypkg`) or import `v1.0`/`v2.0` (defaults to CWD) |
| `layout` | `mby layout get [mcq\|flashcard\|article]`<br>`mby layout set <mcq\|flashcard\|article> <prop> <val>`<br>`mby layout reset [mcq\|flashcard\|article] [prop...]`<br>`mby layout edit [target] [--no-wait]` | Query bounds, update or reset global layout profiles, or serve GUI layout editor (`article-text`, `image`, `mcq-question`, `mcq-options`, `flashcard`, `callout`, `table`, `fill-blank`, `assertion-reason`, `spot-error`, `diff`, `mechanism`, `relabel`) |
| `analyze` | `mby analyze <webDir\|report.json> [--headless] [--static-only] [--timeout n] [--all] [--live] [--no-wait]` | Run zero-touch headless/static WebApp verification or analyze captured telemetry reports |
| `version` | `mby version` *(or `-v` / `--version`)* | Print CLI version, .NET runtime, and supported `data.capabilities` (in `--json`) |

## Spec Classification (`mby spec`)

Topics are split into **Session & Quiz Content Specs** (`scope: "content"`) and **Other Specs** (`scope: "other"`). Query individual topics or `@group` selectors *(quote `'@group'` in PowerShell because `@word` is PowerShell's splatting operator)*:

| Tier (`scope`) | Subcategory (`category`) | Group Selector(s) | Implemented Topics (28) |
| :--- | :--- | :--- | :--- |
| **Session & Quiz Content Specs** (`content`) | `container` | `@content` / `@containers` | `session`, `article`, `quiz` |
| | `block` | `@content` / `@blocks` | `text`, `image`, `callout`, `table`, `diagram-explore`, `reveal` |
| | `question` | `@content` / `@questions` | `mcq`, `flashcard`, `fill-blank`, `assertion-reason`, `spot-error`, `diff`, `mechanism`, `relabel`, `case`, `classify`, `match`, `numeric` |
| **Other Specs** (`other`) | `format` | `@other` / `@formats` | `markdown`, `layouts` |
| | `workflow` | `@other` / `@workflows` | `patch`, `batch`, `pkg`, `build`, `analyze` |
