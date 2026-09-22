// Licensed to the .NET Foundation and Contributors.
// See LICENSE file in the project root for full license information.
//
// Adapted from nanoFramework.IoT.Device/devices/HomeAssistant/samples/Wireless80211.cs.
// Only change of substance: logging goes through Console.WriteLine rather than
// Debug.WriteLine, because Debug.WriteLine is DEBUG-conditional and would go silent
// in a Release build of a deployed device.

using nanoFramework.Networking;
using System;
using System.Device.Wifi;
using System.Net.NetworkInformation;
using System.Threading;

namespace Reliability
{
    /// <summary>
    /// Manages the ESP32 Wi-Fi station (STA) interface.
    /// Credentials are persisted in <see cref="Wireless80211Configuration"/> (platform flash) so that
    /// the firmware owns re-association and <see cref="WifiNetworkHelper.Reconnect"/> only has to wait
    /// for a valid IP, both after a dropout and on every subsequent boot.
    /// </summary>
    internal static class Wireless80211
    {
        /// <summary>
        /// Returns <c>true</c> when STA is enabled and has a non-empty SSID stored.
        /// </summary>
        public static bool IsEnabled()
        {
            Wireless80211Configuration wconf = GetConfiguration();
            if (wconf == null)
            {
                return false;
            }

            bool enabled = (wconf.Options & Wireless80211Configuration.ConfigurationOptions.Enable)
                == Wireless80211Configuration.ConfigurationOptions.Enable;
            return enabled && !string.IsNullOrEmpty(wconf.Ssid);
        }

        /// <summary>
        /// Returns <c>true</c> when the station interface holds a valid unicast IPv4 address.
        /// This is the cheap health probe used by the connectivity maintenance loop.
        /// </summary>
        public static bool IsConnected()
        {
            NetworkInterface ni = GetInterface();
            if (ni == null)
            {
                return false;
            }

            string ip = ni.IPv4Address;
            return !string.IsNullOrEmpty(ip) && ip != "0.0.0.0";
        }

        /// <summary>
        /// Waits for the firmware's automatic STA reconnect to produce a valid IP.
        /// Does NOT actively send credentials - relies on WifiReconnectionKind.Automatic
        /// being set by a previous call to <see cref="SaveCredentials"/> or <see cref="Configure"/>.
        /// </summary>
        /// <param name="timeoutMs">Maximum wait time in milliseconds (default 15 s).</param>
        /// <param name="requiresDateTime">When <c>true</c>, blocks until SNTP has set the system clock.
        /// Only needed by applications that read <see cref="DateTime"/> for their own logic (e.g. a
        /// time-of-day schedule); most callers can leave this <c>false</c>.</param>
        public static bool Reconnect(int timeoutMs = 15_000, bool requiresDateTime = false)
        {
            Console.WriteLine("Wi-Fi reconnecting with stored credentials...");
            bool success = WifiNetworkHelper.Reconnect(
                requiresDateTime: requiresDateTime,
                token: new CancellationTokenSource(timeoutMs).Token);
            Console.WriteLine("Wi-Fi reconnect result: " + success);
            return success;
        }

        /// <summary>
        /// Persists SSID and password to the platform's Wireless80211Configuration so that
        /// <see cref="Reconnect"/> will work after a dropout and after the next reboot.
        /// Does NOT actively connect.
        /// </summary>
        public static bool SaveCredentials(string ssid, string password)
        {
            if (string.IsNullOrEmpty(ssid))
            {
                return false;
            }

            Wireless80211Configuration wconf = GetConfiguration();
            if (wconf == null)
            {
                Console.WriteLine("Wireless80211.SaveCredentials: no STA configuration found.");
                return false;
            }

            wconf.Options = Wireless80211Configuration.ConfigurationOptions.AutoConnect
                | Wireless80211Configuration.ConfigurationOptions.Enable;
            wconf.Ssid = ssid;
            wconf.Password = password ?? string.Empty;
            wconf.SaveConfiguration();

            Console.WriteLine("Wi-Fi credentials saved for SSID=" + ssid);
            return true;
        }

        /// <summary>
        /// Saves credentials to platform flash AND actively connects via WifiNetworkHelper.ConnectDhcp.
        /// Falls back to a direct WifiAdapter.Connect when ConnectDhcp returns a stale cached result.
        /// Used only as a first-boot fallback: a device that has never associated has nothing for
        /// <see cref="Reconnect"/> to wait on.
        /// </summary>
        public static bool Configure(string ssid, string password, bool requiresDateTime = false)
        {
            if (string.IsNullOrEmpty(ssid))
            {
                return false;
            }

            WifiAdapter[] adapters = WifiAdapter.FindAllAdapters();
            if (adapters == null || adapters.Length == 0)
            {
                Console.WriteLine("Wireless80211.Configure: no Wi-Fi adapters found.");
                return false;
            }

            WifiAdapter wa = adapters[0];
            wa.Disconnect();
            WifiNetworkHelper.Disconnect();

            Wireless80211Configuration wconf = GetConfiguration();
            if (wconf != null)
            {
                wconf.Options = Wireless80211Configuration.ConfigurationOptions.AutoConnect
                    | Wireless80211Configuration.ConfigurationOptions.Enable;
                wconf.Ssid = ssid;
                wconf.Password = password ?? string.Empty;
                wconf.SaveConfiguration();
            }

            WifiNetworkHelper.Disconnect();

            bool success = WifiNetworkHelper.ConnectDhcp(
                ssid,
                password ?? string.Empty,
                WifiReconnectionKind.Automatic,
                requiresDateTime: requiresDateTime,
                token: new CancellationTokenSource(30_000).Token);

            if (!success)
            {
                // ConnectDhcp sometimes returns false when credentials were already stored
                // and the helper believes it is still connected. Try a direct adapter call.
                wa.Disconnect();
                WifiConnectionResult res = wa.Connect(
                    ssid,
                    WifiReconnectionKind.Automatic,
                    password ?? string.Empty);
                success = res.ConnectionStatus == WifiConnectionStatus.Success;
                Console.WriteLine("Wi-Fi direct connect: " + res.ConnectionStatus);
            }

            Console.WriteLine("Wi-Fi configure success=" + success);
            return success;
        }

        /// <summary>
        /// Gets the wireless station configuration bound to the current STA interface.
        /// </summary>
        /// <returns>The station configuration, or null when unavailable.</returns>
        public static Wireless80211Configuration GetConfiguration()
        {
            NetworkInterface ni = GetInterface();
            if (ni == null)
            {
                return null;
            }

            Wireless80211Configuration[] configs =
                Wireless80211Configuration.GetAllWireless80211Configurations();
            if (configs == null || configs.Length == 0)
            {
                return null;
            }

            uint id = ni.SpecificConfigId;
            if (id == uint.MaxValue)
            {
                id = 0;
            }

            if (id >= (uint)configs.Length)
            {
                return null;
            }

            return configs[id];
        }

        /// <summary>
        /// Gets the wireless station network interface.
        /// </summary>
        /// <returns>The station interface, or null when not found.</returns>
        public static NetworkInterface GetInterface()
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            if (interfaces == null)
            {
                return null;
            }

            for (int i = 0; i < interfaces.Length; i++)
            {
                if (interfaces[i].NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    return interfaces[i];
                }
            }

            return null;
        }
    }
}
