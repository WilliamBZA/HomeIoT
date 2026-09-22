using nanoFramework.HomeAssistant;
using nanoFramework.Hardware.Esp32;
using System;
using System.Net.NetworkInformation;
using System.Threading;

namespace Reliability
{
    /// <summary>
    /// Owns Wi-Fi/MQTT connectivity recovery, periodic GC, and watchdog-style reboot-by-deep-sleep
    /// for a nanoFramework application's Main(). Keeps this operational plumbing out of the
    /// application wiring so it stays readable.
    /// </summary>
    public static class ReliabilityHarness
    {
        /// <summary>
        /// Invoked after every successful MQTT (re)connect, so the application can resync any
        /// state that may have changed while disconnected.
        /// </summary>
        public delegate void ReconnectedHandler();

        private const int InitialWifiTimeoutMs = 20_000;
        private const int WifiReconnectTimeoutMs = 30_000;
        private const int HealthCheckIntervalMs = 5_000;
        private const int MqttReconnectDelayMs = 1_000;
        private const int MaxMqttReconnectAttempts = 3;
        private const int MaxConsecutiveRecoveryFailures = 3;
        private const int RecoveryDeepSleepSeconds = 1;
        private const int ForegroundIdleSleepMs = 1_000;
        private const int RegularGcIntervalMs = 30_000;

        private static readonly object ConnectivityLock = new object();

        private static HomeAssistantClient _homeAssistant;
        private static ReconnectedHandler _onReconnected;
        private static bool _requiresDateTime;

        private static bool _mqttReconnectRequested;
        private static int _consecutiveRecoveryFailures;
        private static long _nextGcTicks;

        /// <summary>
        /// Wires the harness to the application's Home Assistant client and the callback used to
        /// resync state after a successful (re)connect. Must be called before <see cref="Start"/>.
        /// </summary>
        /// <param name="homeAssistant">The application's Home Assistant/MQTT client.</param>
        /// <param name="onReconnected">Invoked after every successful MQTT connect, so an application
        /// can republish state that may have changed while disconnected.</param>
        /// <param name="requiresDateTime">When <c>true</c>, every Wi-Fi (re)connect blocks until SNTP
        /// has set the system clock. Only needed by applications that read <see cref="DateTime"/> for
        /// their own logic (e.g. a time-of-day schedule); most callers can leave this <c>false</c>.</param>
        public static void Configure(HomeAssistantClient homeAssistant, ReconnectedHandler onReconnected, bool requiresDateTime = false)
        {
            _homeAssistant = homeAssistant;
            _onReconnected = onReconnected;
            _requiresDateTime = requiresDateTime;
        }

        /// <summary>
        /// Performs the initial Wi-Fi and MQTT bring-up, then starts the connectivity maintenance
        /// thread. Must be called after <see cref="Configure"/>.
        /// </summary>
        /// <param name="ssid">Wi-Fi SSID to fall back to with an active connect if the initial
        /// reconnect fails (e.g. a device that has never associated).</param>
        /// <param name="password">Wi-Fi password for the active-connect fallback.</param>
        public static void Start(string ssid, string password)
        {
            if (!Wireless80211.Reconnect(InitialWifiTimeoutMs, _requiresDateTime))
            {
                // Nothing to reconnect to on a device that has never associated: fall back to
                // an active connect. Steady-state recovery never needs this.
                Console.WriteLine("Initial Wi-Fi reconnect failed. Trying an active connect.");
                Wireless80211.Configure(ssid, password, _requiresDateTime);
            }

            LogNetworkAddress();

            // Let the maintenance loop own the first MQTT connect too, so startup and
            // recovery follow exactly the same path.
            RequestMqttReconnect("startup");
            new Thread(MqttMaintenanceLoop).Start();
        }

        public static void RunForegroundIdleLoop()
        {
            while (true)
            {
                Thread.Sleep(ForegroundIdleSleepMs);
                RunPeriodicGarbageCollection(false);
            }
        }

        private static void RunPeriodicGarbageCollection(bool force)
        {
            long nowTicks = DateTime.UtcNow.Ticks;

            if (!force && nowTicks < _nextGcTicks)
            {
                return;
            }

            try
            {
                nanoFramework.Runtime.Native.GC.Run(true);
            }
            catch
            {
            }

            _nextGcTicks = nowTicks + (RegularGcIntervalMs * TimeSpan.TicksPerMillisecond);
        }

        private static void LogNetworkAddress()
        {
            NetworkInterface ni = Wireless80211.GetInterface();
            Console.WriteLine("Wi-Fi IP address: " + (ni == null ? "unknown" : ni.IPv4Address));
        }

        #region Connectivity

        public static void RequestMqttReconnect(string reason)
        {
            _mqttReconnectRequested = true;
            Console.WriteLine("MQTT reconnect requested: " + reason);
        }

        public static void OnHomeAssistantConnectionClosed(object sender, EventArgs e)
        {
            // Raised on the MQTT client's own thread: flag it and get out, never reconnect inline.
            Console.WriteLine("MQTT connection closed event received.");
            RequestMqttReconnect("connection closed event");
        }

        private static bool TryConnectMqtt()
        {
            if (!Wireless80211.IsEnabled())
            {
                Console.WriteLine("Skipping MQTT connect: Wi-Fi configuration is incomplete.");
                return false;
            }

            if (!Wireless80211.IsConnected())
            {
                Console.WriteLine("Skipping MQTT connect: station is not connected.");
                return false;
            }

            try
            {
                // LWT is auto-generated from the device ID.
                bool connected = _homeAssistant.Connect();

                if (connected)
                {
                    _mqttReconnectRequested = false;
                    _onReconnected?.Invoke();
                    Console.WriteLine("MQTT connected.");
                }

                return connected;
            }
            catch (Exception ex)
            {
                Console.WriteLine("MQTT connect failed: " + ex.Message);
                return false;
            }
        }

        private static void MqttMaintenanceLoop()
        {
            while (true)
            {
                try
                {
                    if (ShouldRecoverConnectivity())
                    {
                        if (RecoverConnectivity())
                        {
                            _consecutiveRecoveryFailures = 0;
                        }
                        else
                        {
                            RegisterRecoveryFailure("Unable to recover connectivity.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    RegisterRecoveryFailure("MQTT maintenance error: " + ex.Message);
                }

                Thread.Sleep(HealthCheckIntervalMs);
            }
        }

        private static bool ShouldRecoverConnectivity()
        {
            if (!Wireless80211.IsEnabled())
            {
                return true;
            }

            if (_mqttReconnectRequested)
            {
                return true;
            }

            if (!Wireless80211.IsConnected())
            {
                return true;
            }

            if (!_homeAssistant.IsConnected)
            {
                return true;
            }

            return false;
        }

        private static bool RecoverConnectivity()
        {
            lock (ConnectivityLock)
            {
                if (!Wireless80211.IsConnected())
                {
                    Console.WriteLine("STA disconnected. Trying reconnect...");
                    if (!Wireless80211.Reconnect(WifiReconnectTimeoutMs, _requiresDateTime))
                    {
                        Console.WriteLine("Wi-Fi reconnect failed.");
                        return false;
                    }

                    LogNetworkAddress();
                }

                for (int attempt = 1; attempt <= MaxMqttReconnectAttempts; attempt++)
                {
                    if (TryConnectMqtt())
                    {
                        return true;
                    }

                    Console.WriteLine("MQTT reconnect attempt " + attempt + "/" + MaxMqttReconnectAttempts + " failed.");
                    Thread.Sleep(MqttReconnectDelayMs);
                }

                // Keep running and retry on next maintenance cycle.
                _mqttReconnectRequested = true;
                Console.WriteLine("MQTT still unreachable. Will retry in maintenance loop.");
                return false;
            }
        }

        private static void RegisterRecoveryFailure(string message)
        {
            _consecutiveRecoveryFailures++;
            Console.WriteLine(message + " Consecutive failures: " + _consecutiveRecoveryFailures);

            if (_consecutiveRecoveryFailures >= MaxConsecutiveRecoveryFailures)
            {
                RebootByTimedDeepSleep("Too many connectivity failures.");
            }
        }

        public static void RebootByTimedDeepSleep(string reason)
        {
            try
            {
                Console.WriteLine("Recovery reboot requested: " + reason);

                if (_homeAssistant != null)
                {
                    // Publishes a retained "offline" so HA marks the device unavailable straight
                    // away instead of waiting out the keep-alive. A no-op if the broker is the
                    // thing that is unreachable, in which case the LWT covers it.
                    _homeAssistant.Disconnect();
                }

                Thread.Sleep(1000);
                Sleep.EnableWakeupByTimer(TimeSpan.FromSeconds(RecoveryDeepSleepSeconds));
                Sleep.StartDeepSleep();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Deep sleep reboot failed: " + ex.Message);
            }

            // In case deep sleep fails, keep running rather than exiting the app.
            RunForegroundIdleLoop();
        }

        #endregion
    }
}
