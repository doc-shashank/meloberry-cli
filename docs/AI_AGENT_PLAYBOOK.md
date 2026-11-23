# AI Agent Playbook

Minimal-token execution rules for driving `mby` deterministically.

## Conventions

1. **Discover lazily**: Run `mby prime` once or `mby <cmd> --help`. Query `mby spec <type...>` (including `callout`, `markdown`, `analyze`, `pkg`, `build`, `batch`, `patch`) for exact limits and schemas.
2. **Use `MBY_AGENT=1` or `--json`**: Receive single-line minified JSON (`--quiet` omits large ID arrays).
3. **Prefer Markdown & Modular Directories**: Author `./articles/*.md` (YAML frontmatter + `![alt](path "caption")` + `:::callout` + GFM tables) + quiz `.json` files instead of escaping long prose inside JSON strings.
4. **Address with `#path` or `#ref`**: Component `ref` values auto-slugify cleanly when omitted, or set explicit `"ref": "slug"` (`<=32` chars).
5. **Close QA in one command**: Run `mby build ./articles/ -o ./dist_web --verify --json` or `mby analyze ./dist_web --headless --json` for zero-touch runtime verification.

## Recipes

### One-Shot Build from Stdin
```bash
mby build - -o ./dist_web --json << 'EOF'
{
  "kind": "session",
  "name": "Kinematics",
  "components": [
    { "kind": "article", "ref": "notes", "blocks": [{ "kind": "text", "text": "# Motion\n$$v = u + at$$" }] },
    { "kind": "quiz", "quizType": "mcq", "questions": [{ "type": "mcq", "text": "SI unit of force?", "options": [{ "text": "N", "isCorrect": true }, { "text": "J" }] }] }
  ]
}
EOF
```

### Surgical Patch & Rebuild
```bash
mby get Session_2026... --json
mby patch Session_2026...#c2.q1 --set text="SI unit of acceleration?" --set explanation="Velocity per second." --json
mby build Session_2026... -o ./dist_web --json
```

### Atomic Multi-Step Batch (`$0.id` / `$prev.id`)
```bash
mby batch - --json << 'EOF'
[
  { "op": "apply", "spec": { "kind": "quiz", "quizType": "mcq", "name": "Temp" } },
  { "op": "patch", "addr": "$0.id", "data": { "name": "Final Quiz" } },
  { "op": "apply", "addr": "$0.id", "spec": { "questions": [{ "type": "mcq", "text": "2+2?", "options": [{ "text": "4", "isCorrect": true }, { "text": "3" }] }] } },
  { "op": "build", "addr": "$0.id", "out": "./quiz_web" }
]
EOF
```
