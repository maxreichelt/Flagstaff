# CLAUDE.md

Read [AGENTS.md](AGENTS.md) before editing `.flag` files or ViewModels. It covers the check, stubs, build, and run loop, the rules, and common mistakes.
Never edit `Generated/`. Implement `private partial async Task On{X}Async()` in `ViewModels/*.cs`; `flagstaff stubs --update` adds missing ones.
For language details, run `flagstaff spec --section <name>` (or read [LANG.md](LANG.md)) instead of guessing syntax.
Contributor conventions for the compiler (snapshots, adding keywords) are in [CONTRIBUTING.md](CONTRIBUTING.md). `LANG.md` is covered by drift tests (`test/Flagstaff.Tests/LangDocTests.cs`): document every new keyword, prop, diagnostic code, overlay keyword equivalent (Generic Elements), and attached-property family (Locality Rules) there.
