# Proposal

## Why

The house alarm (a Texecom panel, already bridged to MQTT via texecom2mqtt and shown by `AlarmStatus`) has no way to be raised on demand from Home Assistant. There is no wireless or remote panic/personal-attack input: the only way to raise the alarm deliberately is at a keypad. A small ESP32 device that closes a dry contact on the panel's panic zone whenever it receives an MQTT event lets any Home Assistant dashboard, phone widget, voice command, or automation (for example a wireless panic button) raise the alarm.

## What Changes

- Add a new nanoFramework ESP32 project, `PanicTrigger`, alongside the existing device projects (`AlarmSilencer`, `AlarmStatus`, `GateSensor`, ...).
- The device announces itself to Home Assistant over MQTT discovery as a **Panic** button entity. A press command on that button's MQTT command topic is the trigger event.
- On a valid trigger the device closes a relay output wired to the alarm panel's panic zone for a short, fixed pulse, then releases it. The panel latches the alarm itself; the device never holds the contact.
- The device is fail-safe against accidental triggering: the relay is released at startup, retained MQTT messages are never treated as triggers, only the exact press payload is accepted, and connectivity loss or recovery never energises the relay.
- The device reports whether the relay is currently closed through a read-only "Panic Active" binary sensor, and re-publishes that state after every MQTT (re)connect and Home Assistant restart.
- The project reuses the shared `Reliability` harness for Wi-Fi/MQTT recovery, periodic GC and reboot-by-deep-sleep, and follows the same `Secrets.cs` / `Secrets.example.cs` convention as the other projects.
- Register the project in `HomeIoT.slnx` and add a README documenting the wiring and the false-trigger precautions.

No existing project is modified and nothing is **BREAKING**.

## Capabilities

### New Capabilities
- `panic-trigger`: Receiving an MQTT panic command and pulsing a relay output wired to the alarm panel's panic zone, including the safeguards against false triggering and the state reporting back to Home Assistant.

### Modified Capabilities
<!-- None: openspec/specs/ has no existing capabilities. -->

## Impact

- **New code**: `PanicTrigger/` (`PanicTrigger.nfproj`, `Program.cs`, `Properties/AssemblyInfo.cs`, `Secrets.example.cs`, `packages.config`, `README.md`). `Secrets.cs` is created locally from the example and stays gitignored.
- **Modified files**: `HomeIoT.slnx` (add the project). No change to `Reliability` or to the Home Assistant library.
- **Dependencies**: only packages the other projects already use (`nanoFramework.M2Mqtt`, `System.Device.Gpio`, `System.Threading`, the `Reliability` project, and the sibling `nanoFramework.IoT.Device` Home Assistant library). No new NuGet packages.
- **Systems**: the MQTT broker (one more client and one more discovered device), Home Assistant (one button and one binary sensor), and the Texecom panel, which gains one wired input that must be configured as a personal-attack/panic zone.
- **Hardware**: an ESP32 board, a relay (or opto/transistor output) whose idle state at ESP32 reset is "open", and wiring to a spare panic zone. The wiring is done by the owner and is documented but not verified by software.
