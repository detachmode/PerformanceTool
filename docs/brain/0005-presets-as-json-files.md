# 0005. Presets stored as one JSON file each

- Status: Accepted
- Date: 2026-11-20

## What
`PresetStore` (`Services/PresetStore.cs`) saves a preset (name + `LoadPlan`) as `<slug>.json` in the
directory from config `Presets:Directory` (default `presets`, relative to the content root). Writes go to a
temp file, then an atomic move. Saving with an existing name overwrites it. JSON uses relaxed escaping so
the files stay readable.

## Why
- Presets are small and rare; a database would be overhead with no benefit.
- Plain files can be reviewed, diffed, committed and shared, and a CI pipeline or CLI can read the same
  files without a running web app (see `docs/plan.md`).
- One file per preset avoids write conflicts between presets.

## Consequences
- Two names that slug to the same file overwrite each other ("My Test" and "my-test").
- No locking across processes beyond the atomic move; fine for a few users, revisit for heavy concurrent use.
- The preset format *is* `LoadPlan`. Changing `LoadPlan` changes the file format: keep it backwards
  compatible (new fields need defaults).
