## Summary

<!-- What does this change, and why? Link the issue or proposal: Fixes #123 -->

## Checklist

- [ ] `dotnet build` and `dotnet test` pass locally
- [ ] Output changes are covered by a snapshot case, and the `*.verified.*` diffs were reviewed
- [ ] New diagnostics have a code in the right range, a hint, and a test
- [ ] `LANG.md` is updated for language changes
- [ ] `CHANGELOG.md` has a line under `[Unreleased]` for user-visible changes
- [ ] The sample builds with `flagstaff build` (generator or MSBuild changes)
- [ ] No `Generated/` or `*.received.*` files are committed
