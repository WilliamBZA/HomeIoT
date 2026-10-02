# Design

## Context

See `proposal.md` for the motivation and `specs/panic-trigger/spec.md` for the required behavior. This section covers only what shapes the approach.

**How the existing devices are built** (observed in `AlarmSilencer`, `AlarmStatus`, `GateSensor`):

- Each device is a nanoFramework console app. `Main()` creates a `HomeAssistantClient`, registers entities, calls `ReliabilityHarness.Configure(client, onReconnected)` then `ReliabilityHarness.Start(ssid, password)`, and ends in `ReliabilityHarness.RunForegroundIdleLoop()`. Any exception in `Main` goes to `RebootByTimedDeepSleep`.
- `Reliability` owns Wi-Fi/MQTT recovery. After 3 consecutive failed recoveries it reboots the board through a 1-second deep sleep, so **reset-path GPIO behavior is part of normal operation, not a rare event**.
- `HomeAssistantClient` connects with `cleanSession: true`, auto-subscribes to every entity's command topic plus `homeassistant/status` on each connect, and publishes online + discovery. Subscribing delivers any **retained** message on those topics to the device immediately.
- For a command message the client first routes the payload to the matching entity (`SetState` → `OnStateChange`), then raises the raw `MqttMessageReceived` callback with the full `MqttMsgPublishEventArgs`. Only the raw callback exposes `Retain`.
- `AddButton` is a stateless command entity (every payload notifies), and the closest existing analogue is `AlarmSilencer` (MQTT command → GPIO relay). `AlarmSilencer` does not check `Retain`, drives the pin from the MQTT thread, and publishes state optimistically; those are the patterns this design deliberately does not copy.
- Project layout, `Secrets.example.cs`/gitignored `Secrets.cs`, `packages.config`, the `.nfproj` reference set and `HomeIoT.slnx` registration are consistent across projects and are copied from `AlarmSilencer`.

**Environment constraints:** nanoFramework (a small subset of .NET; no `switch` on larger string sets, no general-purpose JSON), limited heap, and no automated test harness in this repo. Verification of this change is manual, against a real board and broker.

## Goals / Non-Goals

**Goals:**
- A false alarm should be harder to cause than a real one: stale messages, restarts and connectivity events must be unable to close the contact.
- The contact can never be left closed by a firmware fault, a dropped connection or a slow publish.
- Match the existing project structure so the device is maintained like the others.

**Non-Goals:**
- Replacing a certified personal-attack device, keypad PA, or monitored alarm path. This is a convenience trigger; if the device is offline a press is lost (Home Assistant shows the button unavailable).
- A physical button on the ESP32, a local confirm step, or a lockout/cool-down between pulses.
- A raw, non-Home-Assistant trigger topic. Other event sources (a wireless panic button, a phone, voice) reach the device by pressing the Panic entity from a Home Assistant automation, or by publishing the press payload straight to its command topic.
- Changes to `Reliability`, the Home Assistant library, or the alarm panel's programming.
- Authenticating individual MQTT publishers beyond what the broker already enforces.

## Decisions

### D1. New project `PanicTrigger`, not a second entity on `AlarmSilencer`
Home Assistant device `panic_trigger` / "Panic Trigger", one project folder, one firmware.

*Why:* The repo is one project per device, and the two functions are unrelated safety-wise: a bug or reflash for the silencer must not be able to touch the panic input. Separate firmware also lets the panic relay board be chosen for fail-open behavior without constraining the silencer.
*Alternative:* add a second switch to `AlarmSilencer` to reuse the board. Rejected for the coupling above; it is cheap to revisit.

### D2. Trigger is a Home Assistant MQTT `button` entity
`AddButton("panic_trigger_button", "Panic")` with the default `PRESS` payload. Its auto-generated command topic is the trigger event, and the firmware logs that topic at startup so it can be used from `mosquitto_pub` or automations.

*Why:* A button is stateless, which matches "do one thing now". It is auto-subscribed and auto-discovered, so there is no extra configuration, and Home Assistant already provides dashboards, mobile widgets, voice assistants and automations on top of it.
*Alternatives:* a `switch` (like `AlarmSilencer`) implies a persistent on/off state that does not exist here; a hand-configured raw topic adds an unauthenticated-by-convention trigger path and invites payload-format mistakes.

### D3. Decide on the raw MQTT callback, not `OnStateChange`
The trigger is handled in the `onMqttMessageReceived` callback (the same hook `AlarmSilencer`/`GateSensor` use). It trims the payload and requires **all** of: topic equals the button's `CommandTopic`, `e.Retain` is false, payload equals exactly `PRESS`. The button entity's own `OnStateChange` is not subscribed to.

*Why:* `Retain` is only visible on the raw event args, and the retained-message guard is the single most important false-alarm protection (see Risks). The library still routes the payload to the button's `SetState`, which with no `OnStateChange` listener only stores a string.
*Alternative:* `button.OnStateChange` is simpler but cannot distinguish a stale retained `PRESS` replayed on every reconnect from a fresh press.

### D4. Pulse is released by a one-shot timer; the MQTT thread never sleeps
State is a `_pulseActive` flag guarded by a lock. On trigger:

1. If `_pulseActive`, return (ignores repeats; spec: no extension, no queue).
2. Otherwise write the closed level, set `_pulseActive`, and arm a single reused `System.Threading.Timer` for `PulseDurationMs` (2000 ms).
3. Publish `Panic Active = ON` after leaving the lock.

The timer callback writes the open level and clears `_pulseActive` **first**, then publishes `OFF` inside a try/catch. The timer is created disarmed at startup, before any command can arrive; if arming it throws, the handler opens the relay again immediately and reports the failure.

*Why:* `Thread.Sleep` in the callback would block the MQTT receive thread for 2 s, delaying keep-alive and acks. Releasing before publishing, with publishing wrapped, is what makes "every pulse ends" independent of MQTT health. One reused timer avoids per-press allocations on a small heap. Constructing the timer up front means the failure mode of "can't create the thing that opens the relay" happens at boot, not mid-pulse.
*Alternative:* a worker thread per press (more heap, more code) or a single long-lived worker with a signal (equivalent, more moving parts).

### D5. Active-high output, open written before anything else, explicit fail-open hardware
The output is active-high: closed = `High`, open = `Low`. Immediately after `OpenPin` the firmware writes the open level, before Wi-Fi, MQTT or the timer are touched (`GpioController` setup is the first step of `Main`, as in `AlarmSilencer`). The GPIO number is a named constant (default 19, same as `AlarmSilencer`; to be confirmed against the actual board).

*Why:* Active-high with idle `Low` makes the pin's default output level coincide with "open", so the firmware is never the party driving the contact closed. The README then specifies the hardware half: a relay/transistor stage whose input has a pull-down, using the **normally-open** contact, so that the ESP32's high-impedance state during reset and deep sleep also reads as "open".
*Alternative:* active-low modules (as many cheap opto relay boards are). Rejected as the default because their floating-input behavior varies by module and the first milliseconds of each recovery reboot would then be a gamble. They remain usable if the polarity constant is flipped and the board is bench-tested.

### D6. Report real state, retained, and resync on every connect
A read-only `AddBinarySensor("panic_active", "Panic Active", HomeAssistantDeviceClass.Safety)` is published from the code paths that actually change the pin (`ON` on close, `OFF` on open). `PublishAllState()` publishes the pin-derived state, is passed to `ReliabilityHarness.Configure` and is also called when `IsHomeAssistantOnlineEvent` fires (same shape as `GateSensor`, including re-publishing online and discovery).

*Why:* A retained `ON` left behind by a reboot mid-pulse would otherwise show "panic active" until the next change; republishing the actual state on each connect corrects it. The sensor is also the only confirmation, from Home Assistant's side, that a press produced a closure.
*Alternative:* no state entity (smaller, but a press would have no feedback at all).

### D7. Error handling follows the existing convention
The message callback is wrapped in try/catch; on an exception it logs and calls `ReliabilityHarness.RequestMqttReconnect`, as `GateSensor`/`AlarmStatus` do. An exception in the callback must not be able to affect a pulse that is already running, since the release is owned by the timer (D4).

### D8. Pulse length
`PulseDurationMs = 2000`. It is a named constant, well above typical alarm-panel zone response times and short enough that an accidental press is brief. It must be checked against the zone response time configured for the chosen panic zone when wiring.

## Risks / Trade-offs

- **Relay glitches closed during reset, deep-sleep reboot or flashing → false alarm.** Firmware cannot fully control this; GPIO is high-impedance before `Main` runs. → D5 hardware rules (pull-down, NO contact, avoid strapping/boot-glitching pins), plus a manual bench test in `tasks.md` that resets, power-cycles and forces a recovery reboot while a meter/LED watches the contact. Do not connect the panel until this passes.
- **Retained `PRESS` replays on every reconnect → repeated false alarms.** → D3: retained messages are ignored. This relies on the broker setting the retain flag only on the copy replayed at subscribe time, which MQTT 3.1.1 (what M2Mqtt speaks) requires. Verified by test, not assumed. The guard has a known edge: a retained `PRESS` published while the device is already connected is forwarded with the flag cleared, so it pulses once and is then ignored on every later reconnect. The README therefore says never to publish the command with retain.
- **A press while the device is offline is lost.** Accepted (non-goal): Home Assistant marks the button unavailable via the availability topic, and the spec forbids replaying it later. The panel keypad PA remains the primary mechanism.
- **Anyone who can publish to the broker can trigger the alarm.** Same trust model as `AlarmSilencer`; the link uses plain MQTT on 1883 like the other devices. → README recommends restricting publish rights on the command topic to the Home Assistant user via a broker ACL.
- **QoS 1 redelivery (DUP) can produce a second pulse.** The device subscribes at QoS 1. A redelivered duplicate within the pulse is ignored by D4; one after it yields one more brief pulse. Dropping DUP-flagged messages was considered and rejected because it could drop a genuine press whose first copy was lost. A second brief pulse on an already-latched alarm is harmless.
- **A recovery reboot during a pulse opens the contact early.** The pin goes high-impedance, which is "open" by D5. The panel latches on the first closure, so a shortened pulse still alarms provided it exceeded the zone response time.
- **Timer jitter / GC pauses vary the pulse length slightly.** Irrelevant at a 2 s nominal pulse.
- **Unverified in this environment.** nanoFramework builds need the Visual Studio extension and a board; nothing here was compiled or run. → Tasks end with an explicit on-device verification section.

## Open Questions

- Which ESP32 variant, GPIO number and relay module will be used. This only changes two constants (`PanicRelayPin`, closed level) and the wiring section of the README.
- Which Home Assistant automation(s) press the button (a wireless panic button, a dashboard tile, a voice phrase). That is configured in Home Assistant, outside this repo.
- Which panel zone number/type is used for the input and how it is terminated (for example normally-closed loop or end-of-line resistors). The README will describe the contact only; the panel's installer manual governs the rest.
