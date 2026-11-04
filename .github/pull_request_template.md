## What and why

<!-- What does this change, and which issue / roadmap item does it address? -->

## Tests

<!-- Which test failed before this change and passes now? Paste the summary lines. -->

## Checklist

- [ ] A failing test was added first (or this is a pure refactor / documentation change).
- [ ] `python -B -m unittest discover -s tests` passes.
- [ ] `tests/PolicyTests` passes (C# changes; see docs/BUILDING.md).
- [ ] `python -B tools/leak_scan.py . --tracked` reports 0 findings.
- [ ] No EA / SI assets, extracted data, interop assemblies, binaries or personal data are included.
- [ ] Clean-room: no code read, copied or ported from GPL-licensed modding tools; the contribution is MIT.
- [ ] README / docs updated if user-visible behaviour changed.
