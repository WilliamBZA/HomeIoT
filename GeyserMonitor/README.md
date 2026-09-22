# GeyserMonitor

Reads 4 DS18B20 temperature probes on a single 1-Wire bus and publishes each as a
Home Assistant temperature sensor over MQTT. Intended for monitoring geyser (hot water
cylinder) temperature at multiple points - e.g. top vs. bottom of the tank, and inlet vs.
outlet pipe temperature - to see stratification and usage/heat-loss at a glance.

Follows the same structure as the other projects in this solution: `Reliability` owns
Wi-Fi/MQTT recovery and watchdog-style reboot, `Secrets.cs` (gitignored) holds credentials,
and `Program.cs` wires sensors to Home Assistant via `nanoFramework.HomeAssistant`.

## Parts list

- ESP32 dev board
- 4x DS18B20 temperature probes (waterproof stainless steel probe type recommended for
  strapping to pipework/tank)
- 1x 4.7 kΩ resistor (single pull-up resistor for the whole bus - not one per probe)

## Wiring the DS18B20s to the ESP32

Each DS18B20 has three wires: **GND** (black), **DATA** (yellow), **VDD** (red). All 4
probes share the same 1-Wire bus, so all 4 GND wires, all 4 DATA wires, and all 4 VDD wires
are commoned together respectively - it's one bus with 4 devices on it, not 4 separate buses.

This project uses external power (VDD tied to 3.3V) rather than parasite power, since
driving 4 probes through a single parasite-power line is more prone to brownout during
conversion. That means each probe needs all 3 wires connected, not just 2.

Three shared rails run to all 4 probes: 3.3V, GND, and DATA (with one pull-up resistor).
The ESP32's RX and TX pins are shorted together and both tap into the DATA rail:

```
 3.3V rail   ──────┬───────┬────────┬────────┬────────┬───────  (to VDD on every probe)
                    │      │        │        │        │
                  4.7kΩ  Probe1   Probe2   Probe3   Probe4
                    │      │        │        │        │
 DATA rail   ───────┴──────┴────────┴────────┴────────┴───────  (to DATA on every probe)
                    │
        GPIO16 (RX) ┤
        GPIO17 (TX) ┴── shorted together, both wired to the DATA rail above

 GND rail    ───────────────┬────────┬────────┬────────┬──────  (to GND on every probe,
                           Probe1   Probe2   Probe3   Probe4      and to ESP32 GND)
```

In short, per probe:

| DS18B20 wire | Connects to |
|---|---|
| VDD (red) | 3.3V rail (shared) |
| GND (black) | GND rail (shared) |
| DATA (yellow) | Shared DATA bus, GPIO16/17 (shared) |

Plus one 4.7 kΩ resistor between the shared DATA bus and the 3.3V rail (pull-up - only
needed once for the whole bus, not per probe).

### Why RX and TX are shorted together

nanoFramework's 1-Wire driver (`nanoFramework.Device.OneWire`) uses a UART to get precise
bus timing, so it needs a UART's RX and TX pins physically shorted together and both wired
to the DATA line - see `Configuration.SetPinFunction` calls in `InitializeSensors()` in
`Program.cs`, which assign GPIO16/17 to UART3 (COM3) for this purpose.

### A note on GPIO16/17 and PSRAM boards

GPIO16/17 are also used internally for PSRAM on ESP32-WROVER-based boards. If your board is
a WROVER (rather than plain WROOM), those pins won't be available - pick two other free
GPIOs instead and update `OneWireRxPin`/`OneWireTxPin` at the top of `Program.cs`.

## Identifying which probe is which

DS18B20s don't have a way to tell them apart physically - each one just has a factory-set
64-bit ROM code baked in. On startup, `Program.cs` enumerates every probe found on the bus,
sorts them by ROM code (so the order is stable across reboots for the same physical set of
probes), and assigns them to `ProbeLabels` in that order:

```csharp
private static readonly string[] ProbeLabels = { "Geyser Top", "Geyser Bottom", "Geyser Inlet", "Geyser Outlet" };
```

The console log at startup prints each probe's ROM code next to the label it was assigned,
e.g.:

```
DS18B20: found 4 device(s) on the 1-Wire bus.
  [0] 28FF641E9A160413 -> Geyser Top
  [1] 28FF7A2B9B160457 -> Geyser Bottom
  [2] 28FFA0119C160522 -> Geyser Inlet
  [3] 28FFC3449D1606A1 -> Geyser Outlet
```

To confirm (or fix) which physical probe ended up with which label, connect one probe at a
time while watching the console output, note its ROM code, then reconnect all 4 - the same
ROM code will always sort into the same position as long as no probe is added, removed, or
replaced. If you'd rather assign labels deliberately instead of relying on sort order, note
each probe's ROM code from the log and reorder `ProbeLabels`/`ProbeIds` to match.

If fewer than 4 probes are found (bad connection, broken probe), a warning is logged and
readings are only published for the probes actually present - the missing slot isn't
reported at all rather than being reported with a stale or fabricated value.

## Configuration

Copy `Secrets.example.cs` to `Secrets.cs` in this folder (gitignored) and fill in your
Wi-Fi and MQTT broker details.

## Home Assistant entities

Each probe is published as a `sensor.*` entity with device class `temperature`, using the
IDs in `ProbeIds` (`geyser_top`, `geyser_bottom`, `geyser_inlet`, `geyser_outlet` by
default). Rename `ProbeIds`/`ProbeLabels` together if you want different entity IDs -
changing the ID after Home Assistant has already discovered the entity will create a new
one rather than renaming the old one.

## Reliability

Same recovery model as the other projects here: `ReliabilityHarness` owns Wi-Fi/MQTT
reconnect and reboots the device (via timed deep sleep) after repeated connectivity
failures or an unhandled exception in `Main`. On top of that, this project treats each
probe independently during a read cycle - a bad CRC or no response from one probe is
logged and skipped without affecting the other 3, and the last good reading per probe is
cached and republished after an MQTT reconnect (see `PublishAllState`).
