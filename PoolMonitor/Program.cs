using nanoFramework.Device.OneWire;
using nanoFramework.Hardware.Esp32;
using nanoFramework.HomeAssistant;
using nanoFramework.M2Mqtt.Messages;
using Reliability;
using System;
using System.Device.Adc;
using System.Device.Gpio;
using System.Text;
using System.Threading;

namespace PoolMonitor
{
    public class Program
    {
        private const int MqttPort = 1883;

        // dfRobot ORP sensor is connected to an ADC pin on the ESP32.
        // Adjust this to match your wiring (e.g., GPIO 34 = ADC channel 6 on ESP32).
        private const int OrpAdcChannel = 6;

        // DS18B20 is wired to UART3's pins but driven as a 1-Wire bus, not as a UART.
        private const int OneWireRxPin = 16;
        private const int OneWireTxPin = 17;

        private const int ReadIntervalMs = 60_000 * 5;

        // DS18B20 commands
        private const byte SkipRomCommand = 0xCC;
        private const byte ConvertTemperatureCommand = 0x44;
        private const byte ReadScratchpadCommand = 0xBE;

        // Relay module is active-low (same convention as AlarmSilencer's relay): Low energizes
        // the relay, High releases it.
        private const int PoolPumpRelayPin = 13;

        // nanoFramework has no timezone database, so the schedule is interpreted against a fixed
        // offset from UTC. South Africa Standard Time does not observe daylight saving.
        private const int UtcOffsetHours = 2;

        private const string DefaultPoolPumpOnTime = "11:00:00";
        private const string DefaultPoolPumpOffTime = "13:00:00";
        private const int ScheduleCheckIntervalMs = 20_000;

        private static HomeAssistantClient _homeAssistant;
        private static HomeAssistantNumber _temperatureSensor;
        private static HomeAssistantNumber _orpSensor;
        private static HomeAssistantSwitch _poolPumpRelay;
        private static HomeAssistantTime _poolPumpOnTime;
        private static HomeAssistantTime _poolPumpOffTime;

        private static AdcChannel _orpChannel;
        private static OneWireHost _oneWire;
        private static GpioPin _poolPumpRelayPin;

        // Tracks the local calendar day (as yyyymmdd) each schedule edge last fired on, so a
        // once-a-minute-resolution check triggers each edge exactly once per day instead of on
        // every tick where the clock happens to match.
        private static int _lastOnTriggerDay = -1;
        private static int _lastOffTriggerDay = -1;

        // Cache of the last successfully read values, republished on reconnect by PublishAllState
        // since sensor reads happen on their own timer rather than on demand.
        private static double _lastTemperatureC = double.MinValue;
        private static double _lastOrpMv = double.MinValue;

        public static void Main()
        {
            try
            {
                Console.WriteLine("PoolMonitor starting...");

                InitializeHomeAssistantComponent();
                InitializeSensors();
                InitializePoolPumpRelay();

                // requiresDateTime: true, so DateTime.UtcNow is valid by the time the schedule
                // loop starts checking it below.
                ReliabilityHarness.Configure(_homeAssistant, PublishAllState, requiresDateTime: true);
                ReliabilityHarness.Start(Secrets.WifiSsid, Secrets.WifiPassword);

                new Thread(SensorReadLoop).Start();
                new Thread(PoolPumpScheduleLoop).Start();

                Console.WriteLine("PoolMonitor is running.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Global exception: " + ex.Message);
                ReliabilityHarness.RebootByTimedDeepSleep("Unhandled exception in Main.");
            }

            ReliabilityHarness.RunForegroundIdleLoop();
        }

        private static void InitializeSensors()
        {
            var adcController = new AdcController();
            _orpChannel = adcController.OpenChannel(OrpAdcChannel);

            Configuration.SetPinFunction(OneWireRxPin, DeviceFunction.COM3_RX);
            Configuration.SetPinFunction(OneWireTxPin, DeviceFunction.COM3_TX);
            _oneWire = new OneWireHost();
        }

        private static void InitializePoolPumpRelay()
        {
            var gpio = new GpioController();
            _poolPumpRelayPin = gpio.OpenPin(PoolPumpRelayPin, PinMode.Output);
            _poolPumpRelayPin.Write(PinValue.Low); // Ensure the relay is off initially
        }

        private static void SensorReadLoop()
        {
            while (true)
            {
                try
                {
                    ReadAndPublishSensors();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Sensor read error: " + ex.Message);
                }

                Thread.Sleep(ReadIntervalMs);
            }
        }

        private static void ReadAndPublishSensors()
        {
            double orpMv = ReadOrpMillivolts(_orpChannel);
            if (orpMv > 0)
            {
                _lastOrpMv = orpMv;
                PublishOrp(orpMv);
            }

            double temperatureC = ReadTemperature(_oneWire);
            if (temperatureC != double.MinValue && temperatureC > 0)
            {
                _lastTemperatureC = temperatureC;
                PublishTemperature(temperatureC);
            }
        }

        private static void PublishTemperature(double temperatureC)
        {
            bool connected = _homeAssistant != null && _homeAssistant.IsConnected;

            // Report whether it actually went out. PublishState no-ops silently while MQTT is
            // down, so an unqualified log line here would imply a working connection that
            // isn't there.
            Console.WriteLine("Water temperature is " + temperatureC.ToString("F2") + "C"
                + (connected ? " (published)" : " (MQTT down, not published)"));

            _temperatureSensor.PublishState(temperatureC.ToString("F2"));
        }

        private static void PublishOrp(double orpMv)
        {
            bool connected = _homeAssistant != null && _homeAssistant.IsConnected;

            Console.WriteLine("ORP is " + orpMv.ToString("F1") + " mV"
                + (connected ? " (published)" : " (MQTT down, not published)"));

            _orpSensor.PublishState(orpMv.ToString("F1"));
        }

        private static void PublishAllState()
        {
            if (_homeAssistant == null || !_homeAssistant.IsConnected)
            {
                return;
            }

            if (_lastTemperatureC != double.MinValue)
            {
                _temperatureSensor.PublishState(_lastTemperatureC.ToString("F2"));
            }

            if (_lastOrpMv != double.MinValue)
            {
                _orpSensor.PublishState(_lastOrpMv.ToString("F1"));
            }

            _poolPumpRelay.PublishState(_poolPumpRelay.State);

            // Home Assistant does not resend the last command on reconnect. Re-subscribing to our
            // own retained schedule state topics is what lets a user's on/off times set from the UI
            // survive a reboot: the broker replays the last retained value the moment we subscribe.
            _homeAssistant.Subscribe(
                new[] { _poolPumpOnTime.StateTopic, _poolPumpOffTime.StateTopic },
                new[] { MqttQoSLevel.AtLeastOnce, MqttQoSLevel.AtLeastOnce });
        }

        private static void SetPoolPumpRelay(bool on)
        {
            _poolPumpRelayPin.Write(on ? PinValue.High : PinValue.Low);
            _poolPumpRelay.PublishState(on ? "ON" : "OFF");

            bool connected = _homeAssistant != null && _homeAssistant.IsConnected;
            Console.WriteLine("Pool pump relay set to " + (on ? "ON" : "OFF")
                + (connected ? " (published)" : " (MQTT down, not published)"));
        }

        private static void PoolPumpScheduleLoop()
        {
            // Give MQTT a moment to redeliver the retained on/off time state after connect, so the
            // very first tick's self-heal check below runs against the user's actual schedule
            // rather than the hardcoded default.
            Thread.Sleep(5000);

            bool firstTick = true;
            while (true)
            {
                try
                {
                    RunPoolPumpScheduleTick(firstTick);
                    firstTick = false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Pool pump schedule check error: " + ex.Message);
                }

                Thread.Sleep(ScheduleCheckIntervalMs);
            }
        }

        private static void RunPoolPumpScheduleTick(bool enforceCurrentWindow)
        {
            if (!HomeAssistantTime.TryParse(_poolPumpOnTime.State, out int onHour, out int onMinute)
                || !HomeAssistantTime.TryParse(_poolPumpOffTime.State, out int offHour, out int offMinute))
            {
                return;
            }

            int onMinutes = (onHour * 60) + onMinute;
            int offMinutes = (offHour * 60) + offMinute;

            DateTime localNow = DateTime.UtcNow.AddHours(UtcOffsetHours);
            int nowMinutes = (localNow.Hour * 60) + localNow.Minute;
            int today = (localNow.Year * 10000) + (localNow.Month * 100) + localNow.Day;

            // Self-heals the relay to match the schedule right after boot (including the
            // deep-sleep reboots ReliabilityHarness uses for recovery), so a reboot that happens
            // mid-window doesn't leave the pump off until the next day's on-time.
            if (enforceCurrentWindow)
            {
                SetPoolPumpRelay(IsWithinPoolPumpSchedule(onMinutes, offMinutes, nowMinutes));
            }

            if (nowMinutes == onMinutes && _lastOnTriggerDay != today)
            {
                SetPoolPumpRelay(true);
                _lastOnTriggerDay = today;
            }

            if (nowMinutes == offMinutes && _lastOffTriggerDay != today)
            {
                SetPoolPumpRelay(false);
                _lastOffTriggerDay = today;
            }
        }

        /// <summary>
        /// Whether the relay should be on right now, treating [on, off) as the run window and
        /// handling a schedule that wraps past midnight (e.g. on 22:00, off 06:00).
        /// </summary>
        private static bool IsWithinPoolPumpSchedule(int onMinutes, int offMinutes, int nowMinutes)
        {
            if (onMinutes == offMinutes)
            {
                return false;
            }

            if (onMinutes < offMinutes)
            {
                return nowMinutes >= onMinutes && nowMinutes < offMinutes;
            }

            return nowMinutes >= onMinutes || nowMinutes < offMinutes;
        }

        /// <summary>
        /// Reads the raw ADC value from the ORP sensor and converts to millivolts.
        /// Takes multiple samples and averages to reduce noise.
        /// </summary>
        private static double ReadOrpMillivolts(AdcChannel channel)
        {
            const int sampleCount = 100;
            var total = 0;

            for (var i = 0; i < sampleCount; i++)
            {
                total += channel.ReadValue();
                Thread.Sleep(10);
            }

            return total / (double)sampleCount;
        }

        /// <summary>
        /// Reads the temperature from a DS18B20 sensor on the 1-Wire bus.
        /// Assumes a single DS18B20 is connected (uses Skip ROM).
        /// </summary>
        private static double ReadTemperature(OneWireHost oneWire)
        {
            if (!oneWire.TouchReset())
            {
                Console.WriteLine("DS18B20: No device found on 1-Wire bus");
                return double.MinValue;
            }

            // Skip ROM (only one device on the bus) and start temperature conversion
            oneWire.WriteByte(SkipRomCommand);
            oneWire.WriteByte(ConvertTemperatureCommand);

            // Wait for conversion to complete (750ms for 12-bit resolution)
            Thread.Sleep(750);

            // Read the scratchpad
            oneWire.TouchReset();
            oneWire.WriteByte(SkipRomCommand);
            oneWire.WriteByte(ReadScratchpadCommand);

            var lsb = (byte)oneWire.ReadByte();
            var msb = (byte)oneWire.ReadByte();

            // Convert raw value to temperature in Celsius
            // DS18B20 returns a 16-bit signed value in 1/16 degree increments
            var raw = (msb << 8) | lsb;

            // Handle negative temperatures (two's complement)
            if ((raw & 0x8000) != 0)
            {
                raw = (int)((uint)raw | 0xFFFF0000);
            }

            return raw / 16.0;
        }

        private static void InitializeHomeAssistantComponent()
        {
            var device = new HomeAssistantDeviceInfo("pool_controller", "Pool Control", "ESP32");

            _homeAssistant = new HomeAssistantClient(
                device,
                Secrets.MqttBroker,
                MqttPort,
                mqttUsername: Secrets.MqttUsername,
                mqttPassword: Secrets.MqttPassword,
                onMqttMessageReceived: OnHomeAssistantMessageReceived,
                onMqttConnectionClosed: ReliabilityHarness.OnHomeAssistantConnectionClosed);

            _temperatureSensor = _homeAssistant.AddSensor("water_temperature", "Water Temperature", "°C", HomeAssistantDeviceClass.Temperature);
            _orpSensor = _homeAssistant.AddSensor("orp", "ORP", "mV", HomeAssistantDeviceClass.Voltage);

            _poolPumpRelay = _homeAssistant.AddSwitch("pool_pump", "Pool Pump");
            _poolPumpOnTime = _homeAssistant.AddTime("pool_pump_on_time", "Pool Pump On Time", DefaultPoolPumpOnTime);
            _poolPumpOffTime = _homeAssistant.AddTime("pool_pump_off_time", "Pool Pump Off Time", DefaultPoolPumpOffTime);
        }

        private static void OnHomeAssistantMessageReceived(object sender, MqttMsgPublishEventArgs e)
        {
            string topic = e.Topic;

            try
            {
                string payload = Encoding.UTF8.GetString(e.Message, 0, e.Message.Length).Trim();

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
                else if (topic == _poolPumpRelay.CommandTopic)
                {
                    SetPoolPumpRelay(payload == "ON");
                }
                else if (topic == _poolPumpOnTime.CommandTopic)
                {
                    SetPoolPumpScheduleTime(_poolPumpOnTime, "on", payload);
                }
                else if (topic == _poolPumpOffTime.CommandTopic)
                {
                    SetPoolPumpScheduleTime(_poolPumpOffTime, "off", payload);
                }
                else if (topic == _poolPumpOnTime.StateTopic || topic == _poolPumpOffTime.StateTopic)
                {
                    // Retained-state resync after a (re)connect: see PublishAllState. Only updates
                    // the in-memory value, doesn't re-publish, so this can't loop.
                    HomeAssistantTime target = topic == _poolPumpOnTime.StateTopic ? _poolPumpOnTime : _poolPumpOffTime;
                    if (HomeAssistantTime.TryParse(payload, out _, out _))
                    {
                        target.SetState(payload);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("MQTT message processing error: " + ex.Message);
                ReliabilityHarness.RequestMqttReconnect("message handler exception");
            }
        }

        private static void SetPoolPumpScheduleTime(HomeAssistantTime entity, string label, string payload)
        {
            if (!HomeAssistantTime.TryParse(payload, out _, out _))
            {
                Console.WriteLine("Ignoring invalid pool pump " + label + "-time: '" + payload + "'");
                return;
            }

            entity.PublishState(payload);
            Console.WriteLine("Pool pump " + label + "-time set to " + payload);
        }
    }
}
