# Design

## Context

See `proposal.md` for why `PoolMonitor` fails to build. What shapes the fix:

**What `PoolMonitor` needs from the Time entity** (all in `PoolMonitor/Program.cs`):

- Two entities, `pool_pump_on_time` and `pool_pump_off_time`, created with `AddTime(id, name, "11:00:00" / "13:00:00")` (lines 361-362).
- A way to turn a payload or entity state string into an hour and minute, used in four places: the schedule tick on `State` (lines 233-234), the retained-state resync (401) and the command handler (416). A string that does not parse is ignored; in the tick it makes the tick return without touching the relay.
- The schedule compares whole minutes only; seconds are never used.

**What the library offers now** (`devices/HomeAssistant`, commit `2b490ecef`):

- `AddTime(id, name, TimeSpan initialValue)` and `AddTime(id, name)`; a negative value throws `ArgumentOutOfRangeException`.
- `HomeAssistantTime.Value` (a `TimeSpan`, `TimeSpan.Zero` when the state does not parse) and `SetValue(TimeSpan)`, which publishes `HH:MM:SS`.
- The parser is `private`. There is no public equivalent of the old `TryParse(string, out int, out int)`.
- Even the first committed version (`e178a6d9a`) was `TimeSpan`-based, so `PoolMonitor` was written against a shape that was never committed, not against something that was later removed.
- nanoFramework's `TimeSpan` has no `Parse`/`TryParse` in the CoreLibrary this repo uses (only `Int32.TryParse`), which is why the library carries its own parser.

**Accepted payloads.** Home Assistant's time entity sends `HH:MM:SS`. The library's parser also accepts `H:MM`/`HH:MM`, ignores fractional seconds, trims whitespace, and requires hour 0-23 and minute and second 0-59. The removed `TryParse` was never committed, so its exact accepted formats are an assumption (the same ones).

## Goals / Non-Goals

**Goals:**
- `PoolMonitor` compiles against the library as committed, and `HomeIoT.slnx` builds with 0 errors.
- No change to what `PoolMonitor` does at runtime: same entity IDs, topics, default schedule, accepted and rejected payloads, and schedule logic.

**Non-Goals:**
- Changing the Home Assistant library, which is another repository.
- Reworking `PoolMonitor` onto `Value`/`SetValue`, or changing how schedule state strings are stored and published.
- The latent issue described under Risks.

## Decisions

### D1. Adapt `PoolMonitor`; do not change the library
`PoolMonitor` is the only consumer, and the library's current API is deliberate: the second commit this morning exists to harden it. Whether to expose a public parser from a library bound for an upstream project is the library maintainer's call, not something to slip in to make a consumer compile.

*Alternative:* add a public `TryParse(string, out int, out int)` to `HomeAssistantTime`. About ten lines, and no duplicated logic, but it grows the library's public surface in another repo to restore an API that was never committed. Left as an open question.

### D2. A private validator in `PoolMonitor` with the old call shape
Add `TryParseScheduleTime(string text, out int hour, out int minute)` to `PoolMonitor` with the same shape as the missing `TryParse`, so the four call sites change only by name and the surrounding logic is untouched. It accepts exactly what the library's own parser accepts (see Context); seconds are validated but not returned, since the schedule works in minutes. It uses `int.TryParse`, which this CoreLibrary has, and `string.Split`/`IndexOf`, as the library's parser does.

*Why not `entity.Value`:* it cannot validate an inbound payload before it is stored, and it returns `TimeSpan.Zero` for an unparseable state, which is indistinguishable from a valid 00:00. Using it in the tick would turn today's "skip the tick when the state is unparseable" into "treat the schedule as midnight", which is a behavior change.

### D3. Defaults become `TimeSpan` values
Replace the `const string` defaults with `static readonly TimeSpan` fields (`new TimeSpan(11, 0, 0)` and `new TimeSpan(13, 0, 0)`) and pass them to `AddTime`. The library formats a `TimeSpan` as `Pad(Hours):Pad(Minutes):Pad(Seconds)`, which gives `11:00:00` and `13:00:00`, exactly the strings used before, so the initial entity state is unchanged. Values are non-negative, so the new negative-value guard cannot fire.

### D4. State strings stay as they are
Inbound payloads are still stored and published verbatim (`SetState(payload)` on resync, `PublishState(payload)` on a command). Canonicalising them through `SetValue(TimeSpan)` would be tidier but would change what is published for inputs such as `7:30`, which is outside the scope of a compile fix.

### D5. Verification
There is no test project in the repo, and the runtime behavior needs a board. Verification is: every project builds on its own and as a solution with 0 errors and 0 warnings; a review that the `PoolMonitor` diff touches only the lines named in this design; and an optional on-device smoke test by the owner.

## Risks / Trade-offs

- **The library is still moving.** It changed twice this morning. → The last task rebuilds everything against whatever the library is at that moment; if it has changed, stop and re-plan rather than patching around it.
- **A second copy of the parsing rules.** `PoolMonitor`'s validator duplicates the library's private parser and could drift from it. → Accepted for now; if the library gains a public parser (open question), delete the helper.
- **Behavior parity is argued, not run.** Nothing here can be executed without the board. → The change is confined to the pieces above, and the schedule math and relay handling are not touched. The smoke test covers it on the device.
- **Observed, not fixed (existing behavior, not caused by this change).** The library routes every command payload to `entity.SetState(payload)` *before* `PoolMonitor`'s handler runs, and `SetState` stores the raw string. So an invalid payload published to a schedule command topic (for example by hand with `mosquitto_pub`; Home Assistant's own entity only sends valid times) is rejected and logged by `PoolMonitor`, but the entity's state is already replaced by it. The next schedule ticks then fail to parse and return early, so the relay is left alone until a valid time arrives or the retained state is replayed on reconnect. This follows from the library as committed. Worth a follow-up change; deliberately not folded into this one.

## Open Questions

- Should the library expose a public parser so consumers like `PoolMonitor` do not need their own? Deferrable: adopting it later means deleting the helper and renaming four call sites.
