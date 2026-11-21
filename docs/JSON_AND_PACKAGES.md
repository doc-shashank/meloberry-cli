# JSON Specs, Patching & `.mbypkg` Format

## Strict JSON Rules

- UTF-8 JSON with camelCase keys; unrecognized properties fail with exit `2` (`Json.ParseError` + typo `hint` when applicable).
- `imagePath` fields must be relative paths confined to the JSON file directory (or CWD when reading `stdin`), validated via image magic bytes (`.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, `.bmp`, `.svg`) and copied to `<workspace>/assets/`.
- Optional `ref` slugs (`^[a-z0-9][a-z0-9_-]{0,31}$`) must be unique within a workspace.

## Declarative Session Spec (`apply` / `build`)

```json
{
  "kind": "session",
  "name": "Physics: Kinematics",
  "description": "Motion and acceleration review",
  "components": [
    {
      "kind": "article",
      "ref": "intro",
      "title": "Equations of Motion",
      "blocks": [
        { "kind": "text", "text": "# Kinematics\n\n$$v = u + at$$" },
        { "kind": "image", "imagePath": "images/velocity.png" }
      ]
    },
    {
      "kind": "quiz",
      "ref": "check",
      "title": "Quick Check",
      "quizType": "mixed",
      "questions": [
        {
          "type": "mcq",
          "text": "SI unit of acceleration?",
          "imagePath": "images/accel.png",
          "explanation": "Velocity changes in $m/s$ per second.",
          "isMultiCorrect": false,
          "maxOptions": 4,
          "options": [
            { "text": "$m/s^2$", "isCorrect": true },
            { "text": "$m/s$", "isCorrect": false }
          ]
        },
        {
          "type": "flashcard",
          "front": { "text": "Binary search complexity?", "position": "belowImage" },
          "back": { "text": "$O(\\log n)$" }
        }
      ]
    }
  ]
}
```

## Surgical Patch (`patch`)

- `session` / `quiz`: `{ name?, description? }`
- `component`: `{ title?, ref?, layoutOverrides? }`
- `articleBlock` (`text`): `{ text?, ref? }` | (`image`): `{ imagePath?, ref? }`
- `question` (`mcq`): `{ text?, imagePath?, explanation?, isMultiCorrect?, maxOptions?, options?, ref?, layoutOverrides? }`
- `question` (`flashcard`): `{ front?: { text?, imagePath?, position? }, back?: { ... }, ref?, layoutOverrides? }`
- Set any `layoutOverrides.<key>` to `null` to remove an override; pass `--move <index>:<shift|exchange>` to reorder.

## `.mbypkg` Package Format (`v1.0`)

ZIP archive produced after mandatory `check`:
- `manifest.json`: `packageVersion` (`"1.0"`), `kind` (`session`\|`quiz`), `id`, `exportedAtUtc`, `assets[]`, `targetPlatform` (`desktop`\|`android`)
- `workspace.json`: serialized components/blocks/questions (omits `layoutOverrides` unless `--include-layout` is passed)
- `layouts.json`: global profiles (included only with `--include-layout`)
- `assets/<filename>`: deduped verified media files

`pkg import` enforces matching single-segment IDs, ZIP entry confinement, and `50 MB` per-entry / `250 MB` total decompressed caps via atomic staging.
