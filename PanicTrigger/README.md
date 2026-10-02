# PanicTrigger

Lets Home Assistant raise the house alarm panel's panic (personal attack) input on demand.
The device shows up in Home Assistant as a **Panic** button; pressing it (from a dashboard,
a phone widget, a voice command, or any automation, e.g. a wireless panic button) sends an
MQTT event, and the ESP32 answers by closing a relay contact wired to a panic zone on the
panel for a short pulse. The panel latches the alarm itself - this device never holds the
contact closed.

> **This is a convenience trigger, not a certified personal-attack device.** If the device
> is offline (power, Wi-Fi, broker) a press is lost - Home Assistant shows the button as
> unavailable in that case - and a press is never replayed once the device comes back.
> Keep the panel's keypad PA and any monitored path as the primary way to raise an alarm.

Follows the same structure as the other projects in this solution: `Reliability` owns
Wi-Fi/MQTT recovery and watchdog-style reboot, `Secrets.cs` (gitignored) holds credentials,
and `Program.cs` wires everything to Home Assistant via `nanoFramework.HomeAssistant`.

## Parts list

- ESP32 dev board
- A relay stage with a **normally-open (NO)** dry contact, whose input reads as *off* when
  the ESP32 pin is floating (see [Wiring](#wiring-and-why-it-must-fail-open)). A small
  NPN/MOSFET driving a reed or signal relay, with a pull-down on its input, is ideal.
  Cheap opto-isolated relay boards vary widely in how they behave while the ESP32 resets,
  so test the one you use.
- Cable to a spare zone on the alarm panel, configured as a personal-attack/panic zone
  (follow the panel's installer manual for the zone type and the loop/end-of-line
  termination it expects; the contact simply takes the place of a button in that loop)

## Wiring, and why it must fail open

```
  ESP32 GPIO19 ──► relay stage input ──► relay coil          (pull-down on the input)
                                          │
                                          └─ NO contact ──► panic zone terminals on the panel
                                                            (in series with whatever end-of-line
                                                             resistor the zone requires)
```

The contact must be **open whenever the firmware is not actively pulsing**. That includes
the moments the firmware does *not* control the pin: power-up, every hardware reset, and
every recovery reboot (`ReliabilityHarness` reboots the board through a 1-second deep sleep
after repeated connectivity failures, so this happens in normal operation, not just at
power-on). In those moments an ESP32 pin is high-impedance, so:

- Use the relay's **normally-open** contact only, so an unpowered or de-energised relay
  means "no alarm".
- Give the relay stage's input a **pull-down** (or use a driver that reads a floating input
  as off), so a floating pin cannot energise it.
- Avoid ESP32 strapping pins and pins that toggle during boot for your board variant. The
  default GPIO19 is only a placeholder copied from `AlarmSilencer`: on some variants (for
  example ESP32-C3) it is also a USB pin, so confirm it on the board you use.
- Prefer an **active-high** stage (the default): the pin idles low, so "open" is also what a
  pull-down gives you while the pin floats. If you must use an active-low module, set
  `RelayActiveHigh = false` at the top of `Program.cs`, and bench-test it across resets
  *before* connecting the panel.

At startup the firmware drives the relay open as the very first thing `Main` does, before
the console, timers, Wi-Fi or MQTT, and logs `Panic relay open.`.

### Pulse length and the panel's zone response time

A press closes the contact for `PulseDurationMs` (2000 ms) and then opens it. Repeat presses
during a pulse are ignored; they neither extend it nor queue another one. Set the panic
zone's response time on the panel **below** the pulse length, otherwise the panel may
ignore the closure. If you change `PulseDurationMs`, update the panel to match.

## Configuration

Copy `Secrets.example.cs` to `Secrets.cs` in this folder (gitignored) and fill in your
Wi-Fi and MQTT broker details.

The relay pin, polarity and pulse length are constants at the top of `Program.cs`
(`PanicRelayPin`, `RelayActiveHigh`, `PulseDurationMs`).

## Home Assistant entities

| Entity | Type | Notes |
|---|---|---|
| Panic (`panic_trigger_button`) | Button | Pressing it sends the `PRESS` payload to the command topic. |
| Panic Active (`panic_active`) | Binary sensor, device class `safety` | `ON` while the contact is closed, `OFF` otherwise. Republished with the real relay state after every MQTT connect and whenever Home Assistant restarts. |

Both belong to the **Panic Trigger** device and go *unavailable* when it is offline.

## Triggering it

The trigger is a plain MQTT event: a non-retained `PRESS` on the Panic button's command
topic. The device logs the exact topic at startup (`Panic command topic: '...'`); it is
expected to look like `nanoframework/panic-trigger/panic_trigger_button/set`.

From Home Assistant, press the **Panic** button entity, e.g. from an automation that reacts
to a wireless panic button:

```yaml
action: button.press
target:
  entity_id: button.panic_trigger_panic   # use the entity id shown in your Home Assistant
```

Or publish directly (this is what Home Assistant does under the hood):

```
mosquitto_pub -h <broker> -u <user> -P <password> -t '<command-topic>' -m PRESS
```

### What the device accepts

A pulse is produced only for a message that is **all** of: on the Panic button's command
topic, **not retained**, and exactly `PRESS`. Anything else is ignored and logged: a
retained message, any other payload (`press`, `ON`, empty), any other topic.

**Never publish the command with the retain flag.** The device ignores retained copies, which
is what stops a stale message being replayed on every reconnect, but a retained `PRESS`
published while the device is already connected is delivered as a live message and will
pulse once. If one ever gets stuck on the broker, clear it with
`mosquitto_pub -t '<command-topic>' -r -n`.

### Restrict who can publish to it

Anyone who can publish to that topic can trigger the alarm, and the link is plain MQTT on
port 1883 like the other devices. Limit publish rights on the command topic to the Home
Assistant user with a broker ACL. For Mosquitto, along the lines of:

```
user homeassistant
topic write <command-topic>
```

with every other user denied write access to it. Check your broker's ACL syntax and test
that an unauthorised user really is refused before relying on it.

## Before connecting it to the alarm panel

Leave the contact disconnected from the panel (a meter or LED across the NO contact is
enough) until the fail-open behavior has been seen to hold: 20 hardware resets, 5 power
cycles, a re-flash, and one forced recovery reboot (make the broker unreachable until the
log shows `Recovery reboot requested` and the board restarts). The contact must never
close in any of them. Then, when testing against the panel, put the system in a mode that
will not sound sirens or call the monitoring station, or warn the monitoring company first.

## Reliability

Same recovery model as the other projects here: `ReliabilityHarness` owns Wi-Fi/MQTT
reconnect and reboots the device (via timed deep sleep) after repeated connectivity
failures or an unhandled exception in `Main`. Connectivity changes, broker or Home
Assistant restarts and reboots never close the relay; only a valid press does. The relay
is opened by a one-shot timer, before any state is published, so a stalled MQTT connection
cannot hold the contact closed.
