# AI Agent Playbook

Minimal-token execution rules for driving `mby` deterministically.

## Conventions

1. **Discover lazily**: Run `mby prime` once (~350 tokens). Query `mby spec <type...>` only for needed schemas (e.g. `mby spec article mcq`).
2. **Use `MBY_AGENT=1` or `--json`**: Receive single-line minified JSON on stdout/stderr.
3. **Pipe via `stdin` (`-`)**: Avoid temporary JSON files when authoring or patching.
4. **Address with `#path` or `#ref`**: Set `"ref": "slug"` in JSON specs, or address positionally via `<wsId>#c1.b2` / `<wsId>#c2.q1`.
5. **Inspect with `outline` first**: `mby get <wsId> --json` defaults to compact snippets; pass `--view full` only when full text is needed.

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
