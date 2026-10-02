# Spec Delta

## Purpose

Lets the owner raise the house alarm panel's panic (personal attack) input on demand by sending an MQTT event from Home Assistant, by pulsing a dry-contact output wired to that input. It must never raise the alarm as a side effect of restarts, stale messages or connectivity changes.

## ADDED Requirements

### Requirement: Panic control is discoverable in Home Assistant
The device SHALL announce itself to Home Assistant through MQTT discovery as a device named "Panic Trigger" that exposes a "Panic" button and a read-only "Panic Active" binary sensor. The device SHALL publish its availability so that Home Assistant shows both entities as unavailable whenever the device is offline.

#### Scenario: Entities appear after the device connects
- **WHEN** the device connects to the MQTT broker
- **THEN** it publishes discovery for the Panic button and the Panic Active sensor, and publishes its availability as online

#### Scenario: Unexpected disconnect marks the entities unavailable
- **WHEN** the device loses power or its broker connection drops without a clean disconnect
- **THEN** the broker publishes the device's availability as offline and Home Assistant shows both entities as unavailable

### Requirement: A valid press command pulses the panic relay
When the relay is open and the device receives the Panic button's press command, the device SHALL close the relay output immediately, hold it closed for the pulse duration (2 seconds as shipped), and then open it. The device SHALL NOT keep the relay closed after the pulse.

#### Scenario: Press while idle
- **WHEN** the press payload is received, not retained, on the Panic button's command topic while the relay is open
- **THEN** the relay closes at once and opens again after the pulse duration

### Requirement: Only genuine, current press commands trigger the relay
The device SHALL close the relay only for a non-retained message carrying exactly the press payload on the Panic button's command topic. It SHALL NOT close the relay for a retained message, for any other payload, or for a message on any other topic.

#### Scenario: Retained press is ignored
- **WHEN** a retained message with the press payload is delivered to the Panic button's command topic, for example when the device subscribes after a reconnect
- **THEN** the relay stays open

#### Scenario: Unexpected payload is ignored
- **WHEN** a non-retained message on the Panic button's command topic carries any payload other than the exact press payload (such as `ON`, `press` or an empty payload)
- **THEN** the relay stays open

#### Scenario: Other topics are ignored
- **WHEN** a message arrives on any topic other than the Panic button's command topic, including Home Assistant's online/offline status topic
- **THEN** the relay stays open

### Requirement: Repeated presses do not extend or stack the pulse
While the relay is closed, the device SHALL ignore further press commands: it SHALL NOT extend the running pulse and SHALL NOT queue another pulse to follow it.

#### Scenario: Second press during a pulse
- **WHEN** a valid press arrives while the relay is closed from an earlier press
- **THEN** the relay still opens when the original pulse ends, and no further pulse follows

#### Scenario: Press after the pulse has ended
- **WHEN** a valid press arrives after the relay has opened again
- **THEN** a new pulse is produced

### Requirement: The relay stays open unless a pulse is in progress
The device SHALL hold the relay open from the earliest point at which its firmware controls the output, including after power-up, reset and recovery reboots. The device SHALL NOT close the relay as a result of Wi-Fi or MQTT connecting, disconnecting or reconnecting, of a broker restart, or of a Home Assistant restart.

#### Scenario: Startup leaves the relay open
- **WHEN** the device powers up or reboots, including a recovery reboot triggered by repeated connectivity failures
- **THEN** the relay stays open until a valid press command is received

#### Scenario: Connectivity changes leave the relay open
- **WHEN** Wi-Fi drops and reconnects, the broker restarts, or Home Assistant restarts and announces that it is online
- **THEN** the relay stays open

### Requirement: Every pulse ends
The device SHALL open the relay when the pulse duration elapses regardless of whether MQTT is connected, whether publishing state succeeds, or whether an error occurs while handling the command.

#### Scenario: MQTT is lost during a pulse
- **WHEN** the broker connection drops while the relay is closed
- **THEN** the relay still opens when the pulse duration elapses

#### Scenario: State publishing fails
- **WHEN** publishing the Panic Active state fails or is skipped because MQTT is down
- **THEN** the relay still opens when the pulse duration elapses

### Requirement: Relay state is reported to Home Assistant
The Panic Active sensor SHALL be ON while the relay is closed and OFF while it is open. The device SHALL publish a change whenever the relay closes or opens. After every MQTT connect, and when Home Assistant announces that it has come online, the device SHALL republish its discovery information and the actual current relay state.

#### Scenario: Pulse is reflected in Home Assistant
- **WHEN** a valid press produces a pulse
- **THEN** Panic Active becomes ON when the relay closes and OFF when it opens

#### Scenario: Stale state is corrected after a reboot
- **WHEN** the device reboots while the relay was closed and the broker still holds a retained Panic Active state of ON
- **THEN** after the device reconnects it publishes Panic Active as OFF

#### Scenario: Home Assistant restarts
- **WHEN** Home Assistant publishes that it is online
- **THEN** the device republishes its discovery information, its availability and the current Panic Active state

### Requirement: The device recovers from outages without attention and never replays missed presses
After a Wi-Fi or broker outage the device SHALL re-establish connectivity without manual intervention and accept press commands again. A press command published while the device was offline SHALL NOT produce a pulse once the device reconnects.

#### Scenario: Press after recovery works
- **WHEN** the broker was unreachable and has become reachable again, and a valid press is then published
- **THEN** the device produces a pulse

#### Scenario: Press during an outage is not replayed
- **WHEN** a press command is published while the device is offline and the device later reconnects
- **THEN** no pulse is produced on reconnect
