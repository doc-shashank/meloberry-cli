# Command Reference

All commands are verb-first, stateless, and non-interactive. Pass `-` for any `<file|->` argument to read JSON from `stdin`.

| Command | Syntax | Description |
| :--- | :--- | :--- |
| `prime` | `mby prime [--topics <t1,t2\|@grp>] [--container session\|article\|quiz] [--format card\|agents-md] [--cli-prefix cmd] [--authoring-format fmt] [--workspace dir] [-o file]` | Print compact AI-agent grammar card (`card`) or generate targeted `AGENTS.md` playbook with token counts (`agents-md`) |
| `help` | `mby <cmd> --help` *(or `mby <cmd> -h` / `mby help <cmd>`)* | Print command-specific usage, prerequisites, flags, and examples |
| `spec` | `mby spec [type\|@group...] [--icons] [--nested] [--templates]` | Print schemas with exact limits (grouped by `@content` vs. `@other`; `--icons` SVGs, `--nested` sub-schemas, `--templates` markdown hints & starters) |
| `apply` | `mby apply [addr] <file\|dir\|-> [-o dir] [--at idx] [--build dir] [--all-themes] [--signal] [--signal-dir dir] [--verify] [--static-only] [--timeout n] [--export plat[:path]] [--dry-run] [--quiet\|--verbose]` | Create workspace or append to `session`/`component`/`quiz` (`--at` inserts at 1-based index); writes drafts and returns `data.lint` |
| `patch` | `mby patch <addr> [file\|-] [--set k=v] [--move idx:shift\|exchange] [--to <targetComp> [--at idx]] [--order id1,id2,...] [--md] [--subtype type] [--image path]` | Surgically update fields, smart-wrap raw Markdown fragments (`--md`), move blocks/questions across components (`--to`/`--at`), or apply exact ordering (`--order`) |
| `clone` | `mby clone <addr> [--to <targetAddr>] [--at idx] [--ref slug]` | Duplicate component/block/question in place or into another compatible target with auto-deduped `ref` slugs |
| `get` | `mby get <addr> [--view outline\|full\|ids\|spec\|markdown] [-o dir]` | Inspect entity by ID, `#path`, `#ref`, or direct workspace folder path (`markdown` projects Modular Markdown, copying assets when `-o` is given) |
| `ls` | `mby ls [dir] [--all\|--web]` | List workspaces in CWD + `.meloberry/workspaces/` (`--all` global workspaces; `--web` compiled WebApp bundles + telemetry counts) |
| `rm` | `mby rm <addr>` | Delete workspace, component, block, or question immediately |
| `check` | `mby check <addr> [--quiet\|--verbose]` | Run mandatory lint validation |
| `asset` | `mby asset add <addr\|--dir d> <file> \| ls <addr> \| prune <addr> [--dry-run]` | Stage content-hashed workspace/staging assets, audit references, or prune orphans |
| `scaffold` | `mby scaffold <blueprint\|-> [-o dir] [--format md\|json] [--apply] [--placeholders]` | Materialize modular Markdown/JSON skeletons across all 19 types from `WorkflowBlueprint`, `.mbypreset`, or `PromptDraft` |
| `build` | `mby build <addr\|spec\|dir\|-> [-o dir] [--all-themes] [--signal] [--signal-dir dir] [--verify] [--static-only] [--ephemeral] [--timeout n] [--preview] [--no-wait]` | Lint and generate WebApp with optional 4-theme bundle + `postMessage` bridge (`--all-themes`), atomic `.mby-signal.txt` (`--signal`), and verification |
| `batch` | `mby batch <file\|->` | Execute JSON operations array atomically (`apply`, `patch`, `move`, `reorder`, `clone`, `rm`, `check`, `asset-add`, `asset-prune`, `scaffold`, `build`, `pkg`, `layout`) |
| `pkg` | `mby pkg export <wsId> <desktop\|android> [outPath] [--include-layout]`<br>`mby pkg import <file> [destDir]` | Export `.mbypkg` v2.0 archive (defaults to project root or parent dir; `[outPath]` ending in `/` or `\` or an existing dir writes `<dir>/<wsId>.mbypkg`) or import `v1.0`/`v2.0` (defaults to CWD) |
| `layout` | `mby layout get [mcq\|flashcard\|article]`<br>`mby layout set <mcq\|flashcard\|article> <prop> <val>`<br>`mby layout reset [mcq\|flashcard\|article] [prop...]`<br>`mby layout edit [target] [--no-wait]` | Query bounds, update or reset global layout profiles, or serve GUI layout editor |
| `analyze` | `mby analyze <webDir\|report.json> [--headless] [--static-only] [--timeout n] [--ingest f\|-] [--all] [--live] [--no-wait]` | Run zero-touch headless/static WebApp verification, ingest external telemetry payloads (`--ingest`), or analyze captured telemetry reports |
| `version` | `mby version` *(or `-v` / `--version`)* | Print CLI version, .NET runtime, and supported `data.capabilities` (in `--json`) |

## Spec Classification (`mby spec`)

Topics are split into **Session & Quiz Content Specs** (`scope: "content"`) and **Other Specs** (`scope: "other"`). Query individual topics or `@group` selectors *(quote `'@group'` in PowerShell because `@word` is PowerShell's splatting operator)*:

| Tier (`scope`) | Subcategory (`category`) | Group Selector(s) | Implemented Topics (29) |
| :--- | :--- | :--- | :--- |
| **Session & Quiz Content Specs** (`content`) | `container` | `@content` / `@containers` | `session`, `article`, `quiz` |
| | `block` | `@content` / `@blocks` | `text`, `image`, `callout`, `link`, `table`, `diagram-explore`, `reveal` |
| | `question` | `@content` / `@questions` | `mcq`, `flashcard`, `fill-blank`, `assertion-reason`, `spot-error`, `diff`, `mechanism`, `relabel`, `case`, `classify`, `match`, `numeric` |
| **Other Specs** (`other`) | `format` | `@other` / `@formats` | `markdown`, `layouts` |
| | `workflow` | `@other` / `@workflows` | `patch`, `batch`, `pkg`, `build`, `analyze` |
