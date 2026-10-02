using nanoFramework.HomeAssistant;
using nanoFramework.M2Mqtt.Messages;
using Reliability;
using System;
using System.Device.Gpio;
using System.Text;
using System.Threading;

namespace PanicTrigger
{
    public class Program
    {
        private const int MqttPort = 1883;

        // Relay wiring - adjust if the physical wiring differs. Only the relay's normally-open
        // contact goes to the alarm panel's panic zone, so the zone is only ever triggered while
        // a pulse is running (see README.md).
        private const int PanicRelayPin = 8;

        // true: High closes the relay and idle is Low. Flip for an active-low relay module, but
        // bench-test its behavior across resets first (README.md): a floating pin must read as open.
        private const bool RelayActiveHigh = true;

        // Long enough for the panel's zone response time, short enough that an accidental press is brief.
        private const int PulseDurationMs = 2000;

        // Home Assistant's button press payload. Anything else on the command topic is ignored.
        private const string PressPayload = "PRESS";

        // Guards the relay pin and _pulseActive. Never held across any MQTT call, so a stalled
        // broker connection can never delay opening the relay.
        private static readonly object PulseLock = new object();

        // Serializes state publishes so the retained Panic Active value always ends up matching
        // the relay even if a press and a release publish at the same time.
        private static readonly object PublishLock = new object();

        private static HomeAssistantClient _homeAssistant;
        private static HomeAssistantButton _panicButton;
        private static HomeAssistantSwitch _panicActiveSensor;

        private static GpioPin _relayPin;
        private static Timer _pulseTimer;
        private static bool _pulseActive;

        public static void Main()
        {
            try
            {
                // First thing, before the console, timers, Wi-Fi or MQTT: drive the relay to its
                // open level so the firmware is never what holds the panic zone closed.
                InitializeRelay();

                Console.WriteLine("PanicTrigger starting...");
                Console.WriteLine("Panic relay open.");

                // Created disarmed up front so the one thing that can fail while arming a pulse
                // fails here at boot rather than mid-pulse.
                _pulseTimer = new Timer(OnPulseTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);

                InitializeHomeAssistantComponent();

                ReliabilityHarness.Configure(_homeAssistant, PublishAllState);
                ReliabilityHarness.Start(Secrets.WifiSsid, Secrets.WifiPassword);

                Console.WriteLine("PanicTrigger is running.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Global exception: " + ex.Message);
                ReliabilityHarness.RebootByTimedDeepSleep("Unhandled exception in Main.");
            }

            ReliabilityHarness.RunForegroundIdleLoop();
        }

        private static void InitializeRelay()
        {
            var gpio = new GpioController();
            _relayPin = gpio.OpenPin(PanicRelayPin, PinMode.Output);
            _relayPin.Write(RelayLevel(false));
        }

        private static PinValue RelayLevel(bool closed)
        {
            return closed == RelayActiveHigh ? PinValue.High : PinValue.Low;
        }

        #region Pulse

        /// <summary>
        /// Closes the relay and schedules its release. Returns false, leaving the relay open,
        /// if a pulse is already running (repeat presses neither extend nor queue a pulse) or if
        /// the release could not be scheduled.
        /// </summary>
        private static bool TryStartPulse()
        {
            lock (PulseLock)
            {
                if (_pulseActive)
                {
                    Console.WriteLine("Panic press ignored: a pulse is already in progress.");
                    return false;
                }

                _relayPin.Write(RelayLevel(true));
                _pulseActive = true;

                try
                {
                    _pulseTimer.Change(PulseDurationMs, Timeout.Infinite);
                }
                catch (Exception ex)
                {
                    // Never leave the contact closed without a release scheduled.
                    _relayPin.Write(RelayLevel(false));
                    _pulseActive = false;
                    Console.WriteLine("Panic pulse aborted, could not schedule the release: " + ex.Message);
                    return false;
                }

                return true;
            }
        }

        private static void OnPulseTimerElapsed(object state)
        {
            ReleasePulse();

            Console.WriteLine("Panic pulse ended, relay open.");

            // Only after the relay is open, so a slow or failing publish cannot delay the release.
            PublishPanicActiveState();
        }

        private static void ReleasePulse()
        {
            try
            {
                lock (PulseLock)
                {
                    _relayPin.Write(RelayLevel(false));
                    _pulseActive = false;
                }
            }
            catch (Exception ex)
            {
                // Last resort if the pin cannot be driven: a deep-sleep reboot floats it, which
                // the relay stage treats as open (README.md).
                Console.WriteLine("Failed to open the panic relay: " + ex.Message);
                ReliabilityHarness.RebootByTimedDeepSleep("Failed to open the panic relay.");
            }
        }

        #endregion

        #region Home Assistant

        private static void InitializeHomeAssistantComponent()
        {
            var device = new HomeAssistantDeviceInfo("panic_trigger", "Panic Trigger", "ESP32");

            _homeAssistant = new HomeAssistantClient(
                device,
                Secrets.MqttBroker,
                MqttPort,
                mqttUsername: Secrets.MqttUsername,
                mqttPassword: Secrets.MqttPassword,
                onMqttMessageReceived: OnHomeAssistantMessageReceived,
                onMqttConnectionClosed: ReliabilityHarness.OnHomeAssistantConnectionClosed);

            _panicButton = _homeAssistant.AddButton("panic_trigger_button", "Panic", PressPayload);
            _panicActiveSensor = _homeAssistant.AddBinarySensor("panic_active", "Panic Active", deviceClass: HomeAssistantDeviceClass.Safety);

            Console.WriteLine("Panic command topic: '" + _panicButton.CommandTopic + "'");
        }

        private static void OnHomeAssistantMessageReceived(object sender, MqttMsgPublishEventArgs e)
        {
            try
            {
                string topic = e.Topic;
                string payload = e.Message == null ? string.Empty : Encoding.UTF8.GetString(e.Message, 0, e.Message.Length).Trim();

                if (topic == HomeAssistantTopics.StatusTopic)
                {
                    // Used to detect a Home Assistant restart and re-publish discovery/state.
                    if (_homeAssistant.IsHomeAssistantOnlineEvent(topic, payload))
                    {
                        Console.WriteLine("HA came online, re-publishing discovery and state.");
                        _homeAssistant.PublishOnline();
                        _homeAssistant.PublishDiscovery();
                        PublishAllState();
                    }
                }
                else if (topic == _panicButton.CommandTopic)
                {
                    HandlePanicCommand(e.Retain, payload);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("MQTT message processing error: " + ex.Message);
                ReliabilityHarness.RequestMqttReconnect("message handler exception");
            }
        }

        /// <summary>
        /// Handled here, on the raw MQTT callback, rather than through the button's OnStateChange,
        /// because only the raw event args say whether the message was retained.
        /// </summary>
        private static void HandlePanicCommand(bool retained, string payload)
        {
            // A retained message is a stale copy the broker replays on every (re)subscribe,
            // never a fresh press.
            if (retained)
            {
                Console.WriteLine("Ignoring retained message on the panic command topic.");
                return;
            }

            if (payload != PressPayload)
            {
                Console.WriteLine("Ignoring unexpected payload on the panic command topic: '" + payload + "'");
                return;
            }

            if (!TryStartPulse())
            {
                return;
            }

            Console.WriteLine("Panic pulse started (" + PulseDurationMs + " ms).");
            PublishPanicActiveState();
        }

        private static void PublishAllState()
        {
            if (_homeAssistant == null || !_homeAssistant.IsConnected || _panicActiveSensor == null)
            {
                return;
            }

            PublishPanicActiveState();
        }

        /// <summary>
        /// Publishes the relay's actual current state (not the state the caller expects), so
        /// whichever publish runs last leaves the retained value correct. A no-op while MQTT is
        /// down; PublishAllState() resyncs on the next successful connect.
        /// </summary>
        private static void PublishPanicActiveState()
        {
            try
            {
                lock (PublishLock)
                {
                    bool active;
                    lock (PulseLock)
                    {
                        active = _pulseActive;
                    }

                    _panicActiveSensor.PublishState(active ? "ON" : "OFF");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Panic Active publish failed: " + ex.Message);
            }
        }

        #endregion
    }
}
