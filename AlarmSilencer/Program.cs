using nanoFramework.HomeAssistant;
using nanoFramework.M2Mqtt.Messages;
using Reliability;
using System;
using System.Device.Gpio;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace AlarmSilencer
{
    public class Program
    {
        private const int MqttPort = 1883;

        private const int SilencerRelayPin = 19;

        private static HomeAssistantClient _homeAssistant;
        private static HomeAssistantSwitch _silencerRelayPin;
        private static GpioPin _relayPin;

        public static void Main()
        {
            try
            {
                Console.WriteLine("Alarm Silencer starting...");

                var gpio = new GpioController();
                _relayPin = gpio.OpenPin(SilencerRelayPin, PinMode.Output);
                _relayPin.Write(PinValue.Low); // Ensure the relay is off initially

                InitializeHomeAssistantComponent();

                ReliabilityHarness.Configure(_homeAssistant,
                    () => {
                        _silencerRelayPin.PublishState("ON");
                    });
                ReliabilityHarness.Start(Secrets.WifiSsid, Secrets.WifiPassword);

                Console.WriteLine("Alarm Silencer is running.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Global exception: " + ex.Message);
                ReliabilityHarness.RebootByTimedDeepSleep("Unhandled exception in Main.");
            }

            ReliabilityHarness.RunForegroundIdleLoop();
        }

        private static void InitializeHomeAssistantComponent()
        {
            var device = new HomeAssistantDeviceInfo("alarm_silencer", "Alarm Silencer", "ESP32");

            _homeAssistant = new HomeAssistantClient(
                device,
                Secrets.MqttBroker,
                MqttPort,
                mqttUsername: Secrets.MqttUsername,
                mqttPassword: Secrets.MqttPassword,
                onMqttMessageReceived: OnHomeAssistantMessageReceived,
                onMqttConnectionClosed: ReliabilityHarness.OnHomeAssistantConnectionClosed);

            _silencerRelayPin = _homeAssistant.AddSwitch("alarm_silencer_switch", "Siren");
        }

        private static void OnHomeAssistantMessageReceived(object sender, MqttMsgPublishEventArgs e)
        {
            string payload = Encoding.UTF8.GetString(e.Message, 0, e.Message.Length).Trim();

            if (e.Topic == _silencerRelayPin.CommandTopic)
            {
                if (payload == "ON")
                {
                    _relayPin.Write(PinValue.Low);
                    Console.WriteLine("Relay turned ON.");
                    _silencerRelayPin.PublishState("ON");
                }
                else if (payload == "OFF")
                {
                    _relayPin.Write(PinValue.High);
                    Console.WriteLine("Relay turned OFF.");
                    _silencerRelayPin.PublishState("OFF");
                }
            }
            Console.WriteLine($"Silencer state topic: '{_silencerRelayPin.StateTopic}'");
            Console.WriteLine($"Message received on topic '{e.Topic}' and body: '{payload}'");
        }
    }
}
