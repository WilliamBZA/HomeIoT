using nanoFramework.HomeAssistant;
using nanoFramework.M2Mqtt.Messages;
using Reliability;
using System;
using System.Device.Gpio;
using System.Text;
using System.Threading;

namespace AlarmStatus
{
    public class Program
    {
        private const int MqttPort = 1883;

        // Published directly by the texecom2mqtt bridge - not this device's own discovery
        // entities, so these are plain topic strings rather than HomeAssistantClient-generated ones.
        private const string AlarmAvailabilityTopic = "texecom2mqtt/status";
        private const string AlarmAreaStateTopic = "texecom2mqtt/area/house_alarm";

        // texecom2mqtt area "status" values (texecom2mqtt-hassio docs).
        private const string StatusDisarmed = "disarmed";
        private const string StatusInExit = "in_exit";
        private const string StatusInEntry = "in_entry";
        private const string StatusFullArmed = "full_armed";
        private const string StatusPartArmed1 = "part_armed_";
        private const string StatusTriggered = "triggered";

        // LED wiring - adjust if the physical wiring differs.
        private const int RedLedPin = 19;
        private const int GreenLedPin = 8;
        private const int YellowLedPin = 1;

        private const int LedPollIntervalMs = 200;
        private const int SlowBlinkPeriodMs = 1000; // Connecting / Unavailable: one full flash per second.
        private const int FastBlinkPeriodMs = 500; // Triggered: one full flash every 500ms.

        private enum AlarmLedState
        {
            Connecting,
            Unavailable,
            Unarmed,
            Arming,
            Armed,
            Triggered
        }

        private static HomeAssistantClient _homeAssistant;

        private static GpioPin _redLed;
        private static GpioPin _yellowLed;
        private static GpioPin _greenLed;

        // Last known values from texecom2mqtt. Null until the retained message has arrived
        // after the current (re)connect's subscribe.
        private static string _bridgeStatus;
        private static string _areaStatus;

        public static void Main()
        {
            try
            {
                Console.WriteLine("AlarmStatus starting...");

                InitializeLeds();
                InitializeHomeAssistantComponent();

                // Started before ReliabilityHarness.Start(), which blocks on the initial Wi-Fi
                // handshake (up to ~20s) before returning: starting the LED thread first is what
                // makes the yellow "connecting" flash actually visible during that wait, not just
                // once Wi-Fi is already up.
                new Thread(LedLoop).Start();

                ReliabilityHarness.Configure(_homeAssistant, OnReconnected);
                ReliabilityHarness.Start(Secrets.WifiSsid, Secrets.WifiPassword);

                Console.WriteLine("AlarmStatus is running.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Global exception: " + ex.Message);
                ReliabilityHarness.RebootByTimedDeepSleep("Unhandled exception in Main.");
            }

            ReliabilityHarness.RunForegroundIdleLoop();
        }

        private static void InitializeLeds()
        {
            var gpio = new GpioController();
            _redLed = gpio.OpenPin(RedLedPin, PinMode.Output);
            _yellowLed = gpio.OpenPin(YellowLedPin, PinMode.Output);
            _greenLed = gpio.OpenPin(GreenLedPin, PinMode.Output);
            SetLeds(false, false, false);
        }

        private static void SetLeds(bool red, bool yellow, bool green)
        {
            _redLed.Write(red ? PinValue.High : PinValue.Low);
            _yellowLed.Write(yellow ? PinValue.High : PinValue.Low);
            _greenLed.Write(green ? PinValue.High : PinValue.Low);
        }

        private static void LedLoop()
        {
            bool blinkPhaseOn = true;

            while (true)
            {
                try
                {
                    AlarmLedState state = DetermineLedState();

                    switch (state)
                    {
                        case AlarmLedState.Unarmed:
                            SetLeds(false, false, true);
                            Thread.Sleep(LedPollIntervalMs);
                            break;

                        case AlarmLedState.Arming:
                            SetLeds(false, true, false);
                            Thread.Sleep(LedPollIntervalMs);
                            break;

                        case AlarmLedState.Armed:
                            SetLeds(true, false, false);
                            Thread.Sleep(LedPollIntervalMs);
                            break;

                        case AlarmLedState.Connecting:
                            blinkPhaseOn = !blinkPhaseOn;
                            SetLeds(false, blinkPhaseOn, false);
                            Thread.Sleep(SlowBlinkPeriodMs / 2);
                            break;

                        case AlarmLedState.Unavailable:
                            blinkPhaseOn = !blinkPhaseOn;
                            SetLeds(blinkPhaseOn, blinkPhaseOn, blinkPhaseOn);
                            Thread.Sleep(SlowBlinkPeriodMs / 2);
                            break;

                        case AlarmLedState.Triggered:
                            blinkPhaseOn = !blinkPhaseOn;
                            SetLeds(blinkPhaseOn, false, false);
                            Thread.Sleep(FastBlinkPeriodMs / 2);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("LED loop error: " + ex.Message);
                    Thread.Sleep(LedPollIntervalMs);
                }
            }
        }

        /// <summary>
        /// Derives what the LEDs should show from Wi-Fi/MQTT connectivity plus the last known
        /// texecom2mqtt bridge availability and alarm area status. Any state that cannot be
        /// confidently resolved (not yet connected, bridge offline, status not yet received, or
        /// an unrecognized status string) falls back to a visible "not sure" indication rather
        /// than guessing at armed/unarmed.
        /// </summary>
        private static AlarmLedState DetermineLedState()
        {
            if (_homeAssistant == null || !_homeAssistant.IsConnected)
            {
                return AlarmLedState.Connecting;
            }

            if (_bridgeStatus != "online")
            {
                return AlarmLedState.Unavailable;
            }

            string status = _areaStatus;
            if (status == null)
            {
                return AlarmLedState.Connecting;
            }

            // Deliberately if/else rather than switch-on-string: nanoFramework's mscorlib does not
            // reliably support the string-hashing helper the C# compiler emits for larger string
            // switches, so a chain of string comparisons is the safe choice here.
            if (status == StatusDisarmed)
            {
                return AlarmLedState.Unarmed;
            }

            if (status == StatusInExit)
            {
                return AlarmLedState.Arming;
            }

            if (status == StatusFullArmed || status.Contains(StatusPartArmed1) || status == StatusInEntry)
            {
                return AlarmLedState.Armed;
            }

            if (status == StatusTriggered)
            {
                return AlarmLedState.Triggered;
            }

            return AlarmLedState.Unavailable;
        }

        private static void OnReconnected()
        {
            // texecom2mqtt's topics aren't this device's own discovery entities, so they aren't
            // covered by HomeAssistantClient's auto-subscribe on connect - subscribe explicitly.
            // Re-subscribing on every (re)connect makes the broker redeliver each topic's retained
            // value immediately, since a fresh MQTT session drops any previous subscription.
            _homeAssistant.Subscribe(
                new[] { AlarmAvailabilityTopic, AlarmAreaStateTopic },
                new[] { MqttQoSLevel.AtLeastOnce, MqttQoSLevel.AtLeastOnce });
        }

        private static void InitializeHomeAssistantComponent()
        {
            var device = new HomeAssistantDeviceInfo("alarm_status", "Alarm Status", "ESP32");

            _homeAssistant = new HomeAssistantClient(
                device,
                Secrets.MqttBroker,
                MqttPort,
                mqttUsername: Secrets.MqttUsername,
                mqttPassword: Secrets.MqttPassword,
                onMqttMessageReceived: OnHomeAssistantMessageReceived,
                onMqttConnectionClosed: ReliabilityHarness.OnHomeAssistantConnectionClosed);
        }

        private static void OnHomeAssistantMessageReceived(object sender, MqttMsgPublishEventArgs e)
        {
            try
            {
                string topic = e.Topic;
                string payload = Encoding.UTF8.GetString(e.Message, 0, e.Message.Length).Trim();

                if (topic == AlarmAvailabilityTopic)
                {
                    _bridgeStatus = payload;
                    Console.WriteLine("texecom2mqtt bridge status: " + payload);
                }
                else if (topic == AlarmAreaStateTopic)
                {
                    string status = ExtractJsonStringField(payload, "status");
                    if (status == null)
                    {
                        Console.WriteLine("Could not parse alarm area status from payload: " + payload);
                        return;
                    }

                    _areaStatus = status;
                    Console.WriteLine("Alarm area status: " + status);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("MQTT message processing error: " + ex.Message);
                ReliabilityHarness.RequestMqttReconnect("message handler exception");
            }
        }

        /// <summary>
        /// Pulls a single string field's value out of a flat JSON object, e.g. "status" from
        /// {"id":"A","name":"House Alarm","number":1,"status":"disarmed"}. Good enough for
        /// texecom2mqtt's flat area payloads; not a general-purpose JSON parser.
        /// </summary>
        private static string ExtractJsonStringField(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            string key = "\"" + fieldName + "\"";
            int keyIndex = json.IndexOf(key);
            if (keyIndex < 0)
            {
                return null;
            }

            int colonIndex = json.IndexOf(':', keyIndex + key.Length);
            if (colonIndex < 0)
            {
                return null;
            }

            int valueStart = json.IndexOf('"', colonIndex + 1);
            if (valueStart < 0)
            {
                return null;
            }

            int valueEnd = json.IndexOf('"', valueStart + 1);
            if (valueEnd < 0)
            {
                return null;
            }

            return json.Substring(valueStart + 1, valueEnd - valueStart - 1);
        }
    }
}
