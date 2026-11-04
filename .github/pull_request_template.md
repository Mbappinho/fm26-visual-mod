### Summary of Changes

<!-- Describe what this pull request changes, why it is needed, and link any relevant issue or roadmap item. -->

---

### Test Coverage & Verification

<!-- Which test failed before this change and passes now? Paste the test runner summary lines below. -->

```bat
# Paste test output here
```

---

### Contribution Checklist

- [ ] **Test-First:** A failing test was introduced first (or this is a pure refactor / documentation update).
- [ ] **Python Suite:** `python -B -m unittest discover -s tests` passes with 0 errors.
- [ ] **Policy Tests:** `dotnet run --project tests/PolicyTests/PolicyTests.csproj` passes (for C# changes).
- [ ] **Zero Leaks:** `python -B tools/leak_scan.py . --tracked` reports **0 findings**.
- [ ] **Asset Embargo:** No EA / SI assets, extracted packs, interop assemblies, binaries, or personal data are included.
- [ ] **Clean-Room:** No code read, copied, or ported from GPL modding tools; code is original and MIT.
- [ ] **Documentation:** `README.md` or `docs/` updated if user-visible behavior changed.
