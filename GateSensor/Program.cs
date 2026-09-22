using nanoFramework.HomeAssistant;
using nanoFramework.M2Mqtt.Messages;
using Reliability;
using System;
using System.Device.Gpio;
using System.Text;

namespace GateSensor
{
    public class Program
    {
        private const int MqttPort = 1883;

        private const int GateReedPin = 21;
        private const int GateMotorReedPin = 18;
        private const int ReedDebounceMs = 150;

        private static HomeAssistantClient _homeAssistant;
        private static HomeAssistantSwitch _gateSensor;
        private static HomeAssistantSwitch _gateMotorSensor;

        private static GpioPin _gateReedSwitch;
        private static GpioPin _gateMotorReedSwitch;

        public static void Main()
        {
            try
            {
                Console.WriteLine("GateSensor starting...");

                InitializeHomeAssistantComponent();
                InitializeSensors();

                ReliabilityHarness.Configure(_homeAssistant, PublishAllState);
                ReliabilityHarness.Start(Secrets.WifiSsid, Secrets.WifiPassword);

                Console.WriteLine("GateSensor is running.");
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
            var gpio = new GpioController();
            _gateReedSwitch = OpenReedSwitch(gpio, GateReedPin);
            _gateMotorReedSwitch = OpenReedSwitch(gpio, GateMotorReedPin);

            _gateReedSwitch.ValueChanged += (s, e) =>
            {
                PublishSensorState("Gate", _gateReedSwitch, _gateSensor);
            };

            _gateMotorReedSwitch.ValueChanged += (s, e) =>
            {
                PublishSensorState("Gate Motor Box", _gateMotorReedSwitch, _gateMotorSensor);
            };
        }

        private static GpioPin OpenReedSwitch(GpioController gpio, int pinNumber)
        {
            var pin = gpio.OpenPin(pinNumber, PinMode.InputPullDown);
            pin.DebounceTimeout = TimeSpan.FromMilliseconds(ReedDebounceMs);
            return pin;
        }

        private static string ReadSensorState(GpioPin pin)
        {
            return pin.Read() == PinValue.High ? "OFF" : "ON";
        }

        private static void PublishSensorState(string label, GpioPin pin, HomeAssistantSwitch sensor)
        {
            if (pin == null || sensor == null)
            {
                return;
            }

            string state = ReadSensorState(pin);
            bool connected = _homeAssistant != null && _homeAssistant.IsConnected;

            // Report whether it actually went out. PublishState no-ops silently while MQTT is
            // down, so an unqualified log line here would imply a working connection that
            // isn't there.
            Console.WriteLine(label + " is " + (state == "ON" ? "open" : "closed")
                + (connected ? " (published)" : " (MQTT down, not published)"));

            // PublishAllState() resyncs on the next successful connect.
            sensor.PublishState(state);
        }

        private static void PublishAllState()
        {
            if (_homeAssistant == null || !_homeAssistant.IsConnected || _gateReedSwitch == null || _gateMotorReedSwitch == null)
            {
                return;
            }

            _gateSensor.PublishState(ReadSensorState(_gateReedSwitch));
            _gateMotorSensor.PublishState(ReadSensorState(_gateMotorReedSwitch));
        }

        private static void InitializeHomeAssistantComponent()
        {
            var device = new HomeAssistantDeviceInfo("gate_sensor", "Gate Sensor", "ESP32");

            _homeAssistant = new HomeAssistantClient(
                device,
                Secrets.MqttBroker,
                MqttPort,
                mqttUsername: Secrets.MqttUsername,
                mqttPassword: Secrets.MqttPassword,
                onMqttMessageReceived: OnHomeAssistantMessageReceived,
                onMqttConnectionClosed: ReliabilityHarness.OnHomeAssistantConnectionClosed);

            _gateSensor = _homeAssistant.AddBinarySensor("gate_reed_sensor", "Gate", deviceClass: HomeAssistantDeviceClass.GarageDoor);
            _gateMotorSensor = _homeAssistant.AddBinarySensor("gate_motor_reed_sensor", "Gate Motor", deviceClass: HomeAssistantDeviceClass.GarageDoor);
        }

        private static void OnHomeAssistantMessageReceived(object sender, MqttMsgPublishEventArgs e)
        {
            // Used to detect a Home Assistant restart and re-publish discovery/state.
            string topic = e.Topic;

            try
            {
                if (topic == HomeAssistantTopics.StatusTopic)
                {
                    string payload = Encoding.UTF8.GetString(e.Message, 0, e.Message.Length).Trim();

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
