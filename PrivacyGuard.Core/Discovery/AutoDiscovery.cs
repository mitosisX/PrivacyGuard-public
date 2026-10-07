using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PrivacyGuard.Core.Discovery;

/// <summary>A value found on this machine that the user probably wants hidden.</summary>
public sealed record DiscoveredValue(string Category, string Value);

/// <summary>Reads identifying values from the machine so protection works with zero setup.</summary>
public static class AutoDiscovery
{
    public static IReadOnlyList<DiscoveredValue> Discover()
    {
        var found = new List<DiscoveredValue>();

        Add(found, "Windows username", Environment.UserName);
        Add(found, "Computer name", Environment.MachineName);

        var domain = Environment.UserDomainName;
        if (!string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            Add(found, "Windows domain", domain);

        foreach (var ssid in GetWifiNames())
            Add(found, "Wi-Fi network", ssid);

        foreach (var ip in GetLocalAddresses())
            Add(found, "Local IP address", ip);

        // De-duplicate by value, keeping the first category.
        return found
            .GroupBy(v => v.Value, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static void Add(List<DiscoveredValue> list, string category, string? value)
    {
        value = value?.Trim();
        if (!string.IsNullOrEmpty(value) && value.Length >= 3)
            list.Add(new DiscoveredValue(category, value));
    }

    private static IEnumerable<string> GetLocalAddresses()
    {
        var addresses = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        addresses.Add(ua.Address.ToString());
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No adapters available; nothing to discover.
        }
        return addresses;
    }

    /// <summary>Names of the Wi-Fi network(s) the PC is currently connected to.</summary>
    private static IEnumerable<string> GetWifiNames()
    {
        var names = new List<string>();
        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return names;

            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(3000)) return names;

            // Matches "SSID : Name" but not "BSSID : aa:bb:..." (the line must start with SSID).
            foreach (Match m in Regex.Matches(output, @"^\s*SSID\s*:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant))
                names.Add(m.Groups[1].Value);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // netsh unavailable; skip Wi-Fi discovery.
        }
        return names;
    }
}
