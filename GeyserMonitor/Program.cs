using nanoFramework.Device.OneWire;
using nanoFramework.Hardware.Esp32;
using nanoFramework.HomeAssistant;
using nanoFramework.M2Mqtt.Messages;
using Reliability;
using System;
using System.Collections;
using System.Text;
using System.Threading;

namespace GeyserMonitor
{
    public class Program
    {
        private const int MqttPort = 1883;

        // DS18B20s are wired to UART3's pins but driven as a 1-Wire bus, not as a UART.
        private const int OneWireRxPin = 16;
        private const int OneWireTxPin = 17;

        private const int ReadIntervalMs = 60_000 * 5;
        private const int ConversionDelayMs = 750; // 12-bit resolution conversion time.

        // DS18B20 commands
        private const byte SkipRomCommand = 0xCC;
        private const byte MatchRomCommand = 0x55;
        private const byte ConvertTemperatureCommand = 0x44;
        private const byte ReadScratchpadCommand = 0xBE;
        private const int RomCodeLength = 8;
        private const int ScratchpadLength = 9; // 2 temp bytes + 6 unused + CRC.

        // Physical location for each probe, in the order probes should be assigned once sorted
        // by ROM code (see DiscoverProbes). Rename these to match where each probe actually ends
        // up; the console log at startup prints each probe's ROM code next to the label it was
        // assigned so the mapping can be verified/adjusted during commissioning.
        private static readonly string[] ProbeIds = { "geyser_top", "geyser_bottom", "geyser_inlet", "geyser_outlet" };
        private static readonly string[] ProbeLabels = { "Geyser Top", "Geyser Bottom", "Geyser Inlet", "Geyser Outlet" };

        private static HomeAssistantClient _homeAssistant;
        private static HomeAssistantNumber[] _temperatureSensors;

        private static OneWireHost _oneWire;
        private static byte[][] _probeRomCodes;

        // Cache of the last successfully read values, republished on reconnect by PublishAllState
        // since sensor reads happen on their own timer rather than on demand.
        private static double[] _lastTemperaturesC;

        public static void Main()
        {
            try
            {
                Console.WriteLine("GeyserMonitor starting...");

                InitializeHomeAssistantComponent();
                InitializeSensors();

                ReliabilityHarness.Configure(_homeAssistant, PublishAllState);
                ReliabilityHarness.Start(Secrets.WifiSsid, Secrets.WifiPassword);

                new Thread(SensorReadLoop).Start();

                Console.WriteLine("GeyserMonitor is running.");
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
            Configuration.SetPinFunction(OneWireRxPin, DeviceFunction.COM3_RX);
            Configuration.SetPinFunction(OneWireTxPin, DeviceFunction.COM3_TX);
            _oneWire = new OneWireHost();

            DiscoverProbes();
        }

        /// <summary>
        /// Enumerates every DS18B20 on the bus and sorts them by ROM code so that discovery order
        /// - and therefore the probe-to-label assignment below - stays the same across reboots for
        /// an unchanged set of physical sensors. Logs each ROM code next to the label it was given
        /// so the assignment can be verified (or a probe swapped to a different <see cref="ProbeLabels"/>
        /// slot) during commissioning.
        /// </summary>
        private static void DiscoverProbes()
        {
            ArrayList devices = _oneWire.FindAllDevices();

            var romCodes = new byte[devices.Count][];
            for (int i = 0; i < devices.Count; i++)
            {
                romCodes[i] = (byte[])devices[i];
            }

            SortRomCodesAscending(romCodes);

            Console.WriteLine("DS18B20: found " + romCodes.Length + " device(s) on the 1-Wire bus.");
            for (int i = 0; i < romCodes.Length; i++)
            {
                Console.WriteLine("  [" + i + "] " + RomCodeToString(romCodes[i]) + " -> " + LabelFor(i));
            }

            if (romCodes.Length != ProbeLabels.Length)
            {
                Console.WriteLine("WARNING: expected " + ProbeLabels.Length + " DS18B20 probes but found "
                    + romCodes.Length + ". Check wiring - readings will only be published for the "
                    + "probes that were actually found.");
            }

            _probeRomCodes = romCodes;
        }

        private static void SortRomCodesAscending(byte[][] romCodes)
        {
            // Small fixed set (typically 4 probes): a simple insertion sort is plenty, and keeps
            // the comparison logic obvious.
            for (int i = 1; i < romCodes.Length; i++)
            {
                byte[] current = romCodes[i];
                int j = i - 1;
                while (j >= 0 && CompareRomCodes(romCodes[j], current) > 0)
                {
                    romCodes[j + 1] = romCodes[j];
                    j--;
                }

                romCodes[j + 1] = current;
            }
        }

        private static int CompareRomCodes(byte[] a, byte[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return a[i] - b[i];
                }
            }

            return 0;
        }

        private static string RomCodeToString(byte[] romCode)
        {
            string s = string.Empty;
            for (int i = 0; i < romCode.Length; i++)
            {
                s += romCode[i].ToString("X2");
            }

            return s;
        }

        private static string LabelFor(int index)
        {
            return index < ProbeLabels.Length ? ProbeLabels[index] : "Unassigned probe " + index;
        }

        private static void SensorReadLoop()
        {
            while (true)
            {
                try
                {
                    ReadAndPublishTemperatures();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Sensor read error: " + ex.Message);
                }

                Thread.Sleep(ReadIntervalMs);
            }
        }

        private static void ReadAndPublishTemperatures()
        {
            if (_probeRomCodes == null || _probeRomCodes.Length == 0)
            {
                Console.WriteLine("No DS18B20 probes discovered; skipping read.");
                return;
            }

            if (!StartTemperatureConversion())
            {
                return;
            }

            for (int i = 0; i < _probeRomCodes.Length; i++)
            {
                try
                {
                    double temperatureC = ReadTemperature(_probeRomCodes[i]);
                    if (temperatureC == double.MinValue)
                    {
                        continue;
                    }

                    _lastTemperaturesC[i] = temperatureC;
                    PublishTemperature(i, temperatureC);
                }
                catch (Exception ex)
                {
                    // One probe's failure (bad CRC, no response) shouldn't stop the others on the
                    // bus from being read and published.
                    Console.WriteLine(LabelFor(i) + " read error: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Broadcasts Convert T to every device on the bus via Skip ROM, so all probes start
        /// converting at once and the 750 ms conversion wait only has to happen once per cycle
        /// instead of once per probe.
        /// </summary>
        private static bool StartTemperatureConversion()
        {
            if (!_oneWire.TouchReset())
            {
                Console.WriteLine("DS18B20: no response on 1-Wire bus reset; skipping this read cycle.");
                return false;
            }

            _oneWire.WriteByte(SkipRomCommand);
            _oneWire.WriteByte(ConvertTemperatureCommand);
            Thread.Sleep(ConversionDelayMs);
            return true;
        }

        /// <summary>
        /// Reads one probe's converted temperature by addressing it individually with Match ROM -
        /// required once more than one device shares the bus, since Skip ROM would make every
        /// probe answer the scratchpad read at the same time.
        /// </summary>
        private static double ReadTemperature(byte[] romCode)
        {
            if (!_oneWire.TouchReset())
            {
                Console.WriteLine("DS18B20 " + RomCodeToString(romCode) + ": no response on 1-Wire bus reset.");
                return double.MinValue;
            }

            _oneWire.WriteByte(MatchRomCommand);
            for (int i = 0; i < romCode.Length; i++)
            {
                _oneWire.WriteByte(romCode[i]);
            }

            _oneWire.WriteByte(ReadScratchpadCommand);

            var scratchpad = new byte[ScratchpadLength];
            for (int i = 0; i < scratchpad.Length; i++)
            {
                scratchpad[i] = _oneWire.ReadByte();
            }

            if (ComputeCrc8(scratchpad, scratchpad.Length - 1) != scratchpad[scratchpad.Length - 1])
            {
                Console.WriteLine("DS18B20 " + RomCodeToString(romCode) + ": scratchpad CRC mismatch, discarding reading.");
                return double.MinValue;
            }

            // DS18B20 returns a 16-bit signed value in 1/16 degree increments.
            var raw = (scratchpad[1] << 8) | scratchpad[0];

            // Handle negative temperatures (two's complement)
            if ((raw & 0x8000) != 0)
            {
                raw = (int)((uint)raw | 0xFFFF0000);
            }

            return raw / 16.0;
        }

        /// <summary>
        /// Dallas/Maxim CRC-8 (poly 0x31, reflected as 0x8C), used to validate the scratchpad read
        /// so a noisy multi-drop bus doesn't get a corrupted reading published as real data.
        /// </summary>
        private static byte ComputeCrc8(byte[] data, int length)
        {
            byte crc = 0;
            for (int i = 0; i < length; i++)
            {
                byte inputByte = data[i];
                for (int bit = 0; bit < 8; bit++)
                {
                    byte mix = (byte)((crc ^ inputByte) & 0x01);
                    crc >>= 1;
                    if (mix != 0)
                    {
                        crc ^= 0x8C;
                    }

                    inputByte >>= 1;
                }
            }

            return crc;
        }

        private static void PublishTemperature(int index, double temperatureC)
        {
            bool connected = _homeAssistant != null && _homeAssistant.IsConnected;

            // Report whether it actually went out. PublishState no-ops silently while MQTT is
            // down, so an unqualified log line here would imply a working connection that
            // isn't there.
            Console.WriteLine(LabelFor(index) + " temperature is " + temperatureC.ToString("F2") + "C"
                + (connected ? " (published)" : " (MQTT down, not published)"));

            _temperatureSensors[index].PublishState(temperatureC.ToString("F2"));
        }

        private static void PublishAllState()
        {
            if (_homeAssistant == null || !_homeAssistant.IsConnected || _temperatureSensors == null)
            {
                return;
            }

            for (int i = 0; i < _temperatureSensors.Length; i++)
            {
                if (_lastTemperaturesC[i] != double.MinValue)
                {
                    _temperatureSensors[i].PublishState(_lastTemperaturesC[i].ToString("F2"));
                }
            }
        }

        private static void InitializeHomeAssistantComponent()
        {
            var device = new HomeAssistantDeviceInfo("geyser_monitor", "Geyser Monitor", "ESP32");

            _homeAssistant = new HomeAssistantClient(
                device,
                Secrets.MqttBroker,
                MqttPort,
                mqttUsername: Secrets.MqttUsername,
                mqttPassword: Secrets.MqttPassword,
                onMqttMessageReceived: OnHomeAssistantMessageReceived,
                onMqttConnectionClosed: ReliabilityHarness.OnHomeAssistantConnectionClosed);

            _temperatureSensors = new HomeAssistantNumber[ProbeLabels.Length];
            _lastTemperaturesC = new double[ProbeLabels.Length];
            for (int i = 0; i < ProbeLabels.Length; i++)
            {
                _temperatureSensors[i] = _homeAssistant.AddSensor(ProbeIds[i], ProbeLabels[i], "°C", HomeAssistantDeviceClass.Temperature);
                _lastTemperaturesC[i] = double.MinValue;
            }
        }

        private static void OnHomeAssistantMessageReceived(object sender, MqttMsgPublishEventArgs e)
        {
            string topic = e.Topic;

            try
            {
                if (topic == HomeAssistantTopics.StatusTopic)
                {
                    string payload = Encoding.UTF8.GetString(e.Message, 0, e.Message.Length).Trim();

                    // Used to detect a Home Assistant restart and re-publish discovery/state.
                    if (_homeAssistant.IsHomeAssistantOnlineEvent(topic, payload))
                    {
                        Console.WriteLine("HA came online, re-publishing discovery and state.");
                        _homeAssistant.PublishOnline();
                        _homeAssistant.PublishDiscovery();
                        PublishAllState();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("MQTT message processing error: " + ex.Message);
                ReliabilityHarness.RequestMqttReconnect("message handler exception");
            }
        }
    }
}
