# Proposal

## Why

`PoolMonitor` no longer compiles, so `HomeIoT.slnx` cannot be built as a whole: 8 errors, all from `HomeAssistantTime` and `AddTime`. This morning's commits to the sibling Home Assistant library (`e178a6d9a` "Add Time entity", `2b490ecef` "Do not allow negative timespan values") gave the Time entity a `TimeSpan`-based API. `PoolMonitor` was written against an earlier, never-committed shape of that entity: a public static `TryParse(string, out int hour, out int minute)` and `AddTime(id, name, string initialValue)`. Neither exists in the committed library.

## What Changes

- `PoolMonitor/Program.cs` is adapted to the library's current Time API, with no change to pool pump behavior:
  - the two `AddTime` calls pass a `TimeSpan` default (11:00 and 13:00, the same values as today's string defaults) instead of a string;
  - the four `HomeAssistantTime.TryParse(...)` calls use a small private validator in `PoolMonitor`, because the library's own parser is now private and no public equivalent exists.
- Every project in `HomeIoT.slnx` is built, individually and as a solution, against the library at its current commit, and the result is recorded. Observed today: `Reliability`, `AlarmSilencer`, `AlarmStatus`, `GateSensor`, `GeyserMonitor` and `PanicTrigger` already build with 0 errors and 0 warnings; `PoolMonitor` is the only failure. They are re-checked at the end because the library is still moving.
- No change to the Home Assistant library, to `Reliability`, or to any other project.

## Capabilities

### New Capabilities
<!-- None. This restores buildability and keeps `PoolMonitor`'s behavior as it is; no requirement is added. -->

### Modified Capabilities
<!-- None. `openspec/specs/` has no capabilities, and the pool pump behavior does not change, so this change sets `skip_specs: true` in its `.openspec.yaml`. -->

## Impact

- **Code**: `PoolMonitor/Program.cs` only (the two `AddTime` calls, the four parse call sites, and one new private helper).
- **Dependencies**: the Home Assistant library in the sibling repo `C:\Work\nanoFramework.IoT.Device`, at `2b490ecef`. It is outside this repository and under active change, so the fix is pinned to the API as committed today.
- **Runtime**: none intended. The entities keep the same IDs, topics and default schedule, so Home Assistant sees no difference and the retained schedule state on the broker stays valid.
- **Other work in flight**: `add-panic-trigger` also builds as part of the solution but touches different files; no overlap.
