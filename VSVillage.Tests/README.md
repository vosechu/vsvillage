# VS Village unit tests & complexity ratchet (fork-only)

Fast, local, no-game checks — the default quality tier. Distinct from the behavioral golden harness
in `VSVillage.TestHarness/`, which boots a server + client. Neither ships in the release zip.

## Unit tests (`VSVillage.Tests`, xUnit)

Cover **only pure decision logic** pulled into plain classes — how many items to move, which animal
to feed, whether a claim has expired. The boundary rule (full version in `.claude/rules/testing.md`):
a test may reference VS **value types** (`BlockPos`) but must **never** construct an `ICoreAPI`,
`IWorldAccessor`, `EntityAgent`, `BlockEntity`, or the pathfinder. Logic that needs a world isn't
unit-testable here — verify it in-game or in the harness.

Run:

    VINTAGE_STORY="/Applications/Vintage Story.app" dotnet test VSVillage.Tests/VSVillage.Tests.csproj

`$VINTAGE_STORY` is required even for tests — `VintagestoryAPI.dll` (value types) resolves from it.

## Complexity ratchet (`scripts/complexity-ratchet.rb`)

A code-quality gate over `VSVillage/src`: sums every method's cyclomatic complexity (via `lizard`)
into one number and compares it to the single value in `metrics/complexity-baseline`. It fails if
the total rose, and ratchets the baseline down when the total drops — so total complexity can only
decrease over time. It reports *that* complexity got worse, not which method; run
`lizard VSVillage/src -w` to see the worst offenders. Needs `lizard` (`pipx install lizard`).

    scripts/complexity-ratchet.rb          # check + ratchet down; exit 1 if the total rose
    scripts/complexity-ratchet.rb --check  # read-only gate (CI); exit 1 if the total rose, never writes
    scripts/complexity-ratchet.rb --bless  # accept the current total as the new baseline (after review)

Run the default form before committing a C# change, and commit the one-line baseline change
alongside it. Adding a genuinely new feature raises the total — `--bless` to accept it.
