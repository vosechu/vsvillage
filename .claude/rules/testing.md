---
description: How to write and run unit tests for VS Village. Applies when editing anything under VSVillage.Tests/.
globs: VSVillage.Tests/**
---

# VS Village testing convention

VS mods have no integration-test harness — behavior is verified by loading the mod
in-game and reading the logs. `VSVillage.Tests` (xUnit, net10.0) covers **only pure
decision logic** extracted into plain classes.

## The boundary (the one rule that matters)

A test may reference VS **value types** (e.g. `BlockPos`). A test must **never** construct
or invoke `ICoreAPI`, an `IWorldAccessor`, an `EntityAgent`, a `BlockEntity`, or the
pathfinder. The compiler does not enforce this — you must. If logic needs a world, it is
not unit-testable here; verify it in-game instead. Keep the testable decision (e.g. "how
many items to move given need N, available M") in a plain method that takes plain values.

## Conventions

- One test class per context: `ClassUnderTest_When_<context>`.
- Test methods are the "it": `It_<expected_behavior>`.
- Built-in `Assert` only (`Assert.True/Equal/False/Null`). No third-party assertion library.
- Table-driven cases use `[Theory]` + `[InlineData(...)]`.
- Namespace: `VsVillage.Tests`.

## Running

    VINTAGE_STORY="<game install>" dotnet test VSVillage.Tests/VSVillage.Tests.csproj

`VINTAGE_STORY` is required even for tests — `VintagestoryAPI.dll` (value types) resolves
from it. On macOS the install is `/Applications/Vintage Story.app`.

## Gotcha

The test project references `VintagestoryAPI.dll` **copy-local** (no `<Private>false>`),
unlike the mod project. Tests load VS value types at runtime; the test assembly is never
packaged, so copying is correct. Do not "fix" this to match the mod's `Private=false`.
