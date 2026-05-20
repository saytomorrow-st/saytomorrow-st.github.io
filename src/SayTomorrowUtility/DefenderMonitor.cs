using System;
using System.Diagnostics;
using System.Linq;
using System.Management;

namespace SayTomorrowUtility
{
    internal static class DefenderMonitor
    {
        public static DefenderStatus Query()
        {
            DefenderStatus status = new DefenderStatus();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\Defender",
                    "SELECT AMServiceEnabled, AntivirusEnabled, RealTimeProtectionEnabled FROM MSFT_MpComputerStatus"))
                {
                    foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
                    {
                        status.Available = true;
                        status.ProductName = "Microsoft Defender Antivirus";
                        status.ServiceEnabled = ToNullableBool(item["AMServiceEnabled"]);
                        status.AntivirusEnabled = ToNullableBool(item["AntivirusEnabled"]);
                        status.RealTimeProtectionEnabled = ToNullableBool(item["RealTimeProtectionEnabled"]);
                        return status;
                    }
                }
            }
            catch (Exception ex)
            {
                status.Error = ex.Message;
            }

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    @"root\SecurityCenter2",
                    "SELECT displayName FROM AntiVirusProduct"))
                {
                    string product = string.Empty;
                    foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
                    {
                        string name = Convert.ToString(item["displayName"]);
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            if (product.Length > 0)
                                product += ", ";
                            product += name.Trim();
                        }
                    }

                    if (product.Length > 0)
                    {
                        status.Available = true;
                        status.ProductName = product;
                    }
                }
            }
            catch
            {
            }

            return status;
        }

        public static void OpenWindowsSecurity()
        {
            try
            {
                Process.Start(new ProcessStartInfo("windowsdefender:") { UseShellExecute = true });
                return;
            }
            catch
            {
            }

            Process.Start(new ProcessStartInfo("ms-settings:windowsdefender") { UseShellExecute = true });
        }

        private static bool? ToNullableBool(object value)
        {
            if (value == null)
                return null;

            try
            {
                return Convert.ToBoolean(value);
            }
            catch
            {
                return null;
            }
        }
    }
}
