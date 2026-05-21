using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Xml;

namespace SayTomorrowUtility
{
    internal static class SystemInfoCollector
    {
        private const string Unknown = "н/д";

        public static SystemInfoSnapshot Collect()
        {
            SystemInfoSnapshot snapshot = new SystemInfoSnapshot();
            snapshot.IsLaptop = IsLaptop();
            Add(snapshot, "Устройство", "Тип устройства", snapshot.IsLaptop ? "Ноутбук" : "ПК");

            CollectCpu(snapshot);
            CollectGpu(snapshot);
            CollectMemory(snapshot);
            CollectDisks(snapshot);

            if (snapshot.IsLaptop)
            {
                CollectLaptopDisplay(snapshot);
                CollectBattery(snapshot);
            }

            CollectPorts(snapshot);
            return snapshot;
        }

        private static void CollectCpu(SystemInfoSnapshot snapshot)
        {
            int index = 1;
            foreach (ManagementObject cpu in Query(@"root\CIMV2", "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
            {
                string prefix = index == 1 ? string.Empty : " #" + index.ToString(CultureInfo.InvariantCulture);
                Add(snapshot, "Процессор", "Название ЦП" + prefix, Clean(cpu["Name"]));
                Add(snapshot, "Процессор", "Количество ядер" + prefix, Clean(cpu["NumberOfCores"]));
                Add(snapshot, "Процессор", "Количество потоков" + prefix, Clean(cpu["NumberOfLogicalProcessors"]));
                Add(snapshot, "Процессор", "Максимальная частота (WMI)" + prefix, FormatMHz(cpu["MaxClockSpeed"]));
                index++;
            }

            if (index == 1)
                Add(snapshot, "Процессор", "Информация", Unknown);
        }

        private static void CollectGpu(SystemInfoSnapshot snapshot)
        {
            Dictionary<string, string> dxdiagMemory = ReadDxdiagDedicatedMemory();
            List<string> cards = new List<string>();

            foreach (ManagementObject gpu in Query(@"root\CIMV2", "SELECT Name, AdapterRAM FROM Win32_VideoController"))
            {
                string name = Clean(gpu["Name"]);
                if (name == Unknown)
                    continue;

                string vram = FindDxdiagMemory(name, dxdiagMemory);
                if (string.IsNullOrEmpty(vram))
                    vram = FormatBytes(ToUInt64(gpu["AdapterRAM"]));

                cards.Add(name + " — VRAM: " + (string.IsNullOrEmpty(vram) ? Unknown : vram));
            }

            Add(snapshot, "Видеокарты", "Количество видеокарт", cards.Count.ToString(CultureInfo.InvariantCulture));
            if (cards.Count == 0)
            {
                Add(snapshot, "Видеокарты", "Информация", Unknown);
                return;
            }

            for (int i = 0; i < cards.Count; i++)
                Add(snapshot, "Видеокарты", "Видеокарта " + (i + 1).ToString(CultureInfo.InvariantCulture), cards[i]);
        }

        private static void CollectMemory(SystemInfoSnapshot snapshot)
        {
            foreach (ManagementObject os in Query(@"root\CIMV2", "SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem"))
            {
                ulong kib = ToUInt64(os["TotalVisibleMemorySize"]);
                Add(snapshot, "Оперативная память", "Суммарный объем", kib == 0 ? Unknown : FormatBytes(kib * 1024));
                break;
            }

            int index = 1;
            foreach (ManagementObject module in Query(@"root\CIMV2", "SELECT Manufacturer, Capacity, Speed, ConfiguredClockSpeed, DeviceLocator, BankLabel FROM Win32_PhysicalMemory"))
            {
                string manufacturer = Clean(module["Manufacturer"]);
                string capacity = FormatBytes(ToUInt64(module["Capacity"]));
                string speed = FormatMHz(module["ConfiguredClockSpeed"]);
                if (speed == Unknown)
                    speed = FormatMHz(module["Speed"]);

                string location = Clean(module["DeviceLocator"]);
                if (location == Unknown)
                    location = Clean(module["BankLabel"]);

                string value = string.Format("{0}, {1}, рабочая скорость: {2}", manufacturer, capacity, speed);
                if (location != Unknown)
                    value = location + ": " + value;

                Add(snapshot, "Оперативная память", "Плашка " + index.ToString(CultureInfo.InvariantCulture), value);
                index++;
            }

            if (index == 1)
                Add(snapshot, "Оперативная память", "Плашки", Unknown);
        }

        private static void CollectDisks(SystemInfoSnapshot snapshot)
        {
            int index = 1;
            foreach (ManagementObject disk in Query(@"root\CIMV2", "SELECT Index, Model, Caption, Manufacturer, Size, InterfaceType, MediaType, PNPDeviceID FROM Win32_DiskDrive"))
            {
                if (IsExternalUsbDisk(disk))
                    continue;

                int number = ToInt32(disk["Index"], -1);
                StorageDetails details = ReadStorageDetails(number);
                string model = FirstKnown(Clean(disk["Model"]), Clean(disk["Caption"]));
                string vendor = FirstKnown(Clean(disk["Manufacturer"]), GuessVendor(model));
                string size = FormatBytes(ToUInt64(disk["Size"]));
                string type = ClassifyDisk(details.BusType, details.MediaType, model, Clean(disk["MediaType"]));

                Add(snapshot, "Системные диски", "Диск " + index.ToString(CultureInfo.InvariantCulture), string.Format("{0}, {1}, {2}, {3}", type, size, vendor, model));
                index++;
            }

            if (index == 1)
                Add(snapshot, "Системные диски", "Диски", "Не найдено внутренних накопителей или WMI недоступен");
        }

        private static void CollectLaptopDisplay(SystemInfoSnapshot snapshot)
        {
            Screen screen = Screen.PrimaryScreen;
            if (screen != null)
            {
                Rectangle bounds = screen.Bounds;
                Add(snapshot, "Матрица ноутбука", "Разрешение", bounds.Width.ToString(CultureInfo.InvariantCulture) + " × " + bounds.Height.ToString(CultureInfo.InvariantCulture));
                Add(snapshot, "Матрица ноутбука", "Частота обновления", GetPrimaryRefreshRate());
            }
            else
            {
                Add(snapshot, "Матрица ноутбука", "Разрешение", Unknown);
                Add(snapshot, "Матрица ноутбука", "Частота обновления", Unknown);
            }

            string diagonal = ReadMonitorDiagonal();
            Add(snapshot, "Матрица ноутбука", "Диагональ", diagonal);
        }

        private static void CollectBattery(SystemInfoSnapshot snapshot)
        {
            double design = 0;
            double full = 0;

            foreach (ManagementObject item in Query(@"root\WMI", "SELECT DesignedCapacity FROM BatteryStaticData"))
            {
                design = ToDouble(item["DesignedCapacity"]);
                if (design > 0)
                    break;
            }

            foreach (ManagementObject item in Query(@"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"))
            {
                full = ToDouble(item["FullChargedCapacity"]);
                if (full > 0)
                    break;
            }

            if (design > 0 && full > 0 && full <= design * 1.2)
            {
                double wear = Math.Max(0, Math.Min(100, 100 - (full / design * 100)));
                Add(snapshot, "АКБ", "Износ АКБ", wear.ToString("0.#", CultureInfo.InvariantCulture) + "%");
                Add(snapshot, "АКБ", "Полная/проектная емкость", full.ToString("0", CultureInfo.InvariantCulture) + " / " + design.ToString("0", CultureInfo.InvariantCulture) + " mWh");
                return;
            }

            Add(snapshot, "АКБ", "Износ АКБ", "недоступно: контроллер не отдал проектную/полную емкость");
        }

        private static void CollectPorts(SystemInfoSnapshot snapshot)
        {
            int index = 1;
            foreach (ManagementObject port in Query(@"root\CIMV2", "SELECT ExternalReferenceDesignator, InternalReferenceDesignator, PortType, ConnectorType FROM Win32_PortConnector"))
            {
                string name = FirstKnown(Clean(port["ExternalReferenceDesignator"]), Clean(port["InternalReferenceDesignator"]));
                string portType = FormatPortType(port["PortType"]);
                string connector = FormatConnectorType(port["ConnectorType"]);
                if (name == Unknown && portType == Unknown && connector == Unknown)
                    continue;

                Add(snapshot, "Разъемы", "Порт " + index.ToString(CultureInfo.InvariantCulture), string.Format("{0}; тип: {1}; коннектор: {2}", name, portType, connector));
                index++;
            }

            if (index == 1)
                Add(snapshot, "Разъемы", "Информация", "Контроллер/BIOS не отдал список физических разъемов через Win32_PortConnector");
        }

        private static bool IsLaptop()
        {
            foreach (ManagementObject cs in Query(@"root\CIMV2", "SELECT PCSystemType FROM Win32_ComputerSystem"))
            {
                int type = ToInt32(cs["PCSystemType"], 0);
                if (type == 2)
                    return true;
            }

            foreach (ManagementObject enclosure in Query(@"root\CIMV2", "SELECT ChassisTypes FROM Win32_SystemEnclosure"))
            {
                ushort[] chassis = enclosure["ChassisTypes"] as ushort[];
                if (chassis == null)
                    continue;

                foreach (ushort type in chassis)
                {
                    if (type == 8 || type == 9 || type == 10 || type == 14 || type == 30 || type == 31 || type == 32)
                        return true;
                }
            }

            return SystemInformation.PowerStatus.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery;
        }

        private static Dictionary<string, string> ReadDxdiagDedicatedMemory()
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            string file = Path.Combine(Path.GetTempPath(), "saytomorrow_dxdiag_" + Guid.NewGuid().ToString("N") + ".xml");

            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo("dxdiag.exe", "/whql:off /x \"" + file + "\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    process.Start();
                    process.WaitForExit(15000);
                }

                if (!File.Exists(file))
                    return result;

                XmlDocument document = new XmlDocument();
                document.Load(file);
                XmlNodeList nodes = document.SelectNodes("//DisplayDevice");
                if (nodes == null)
                    return result;

                foreach (XmlNode node in nodes)
                {
                    string name = ReadXmlText(node, "CardName");
                    string memory = FirstKnown(ReadXmlText(node, "DedicatedMemory"), ReadXmlText(node, "DisplayMemory"));
                    if (name == Unknown || memory == Unknown)
                        continue;

                    ulong bytes = ParseMegabytes(memory);
                    result[NormalizeName(name)] = bytes > 0 ? FormatBytes(bytes) : memory;
                }
            }
            catch
            {
            }
            finally
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }

            return result;
        }

        private static string FindDxdiagMemory(string gpuName, Dictionary<string, string> dxdiagMemory)
        {
            string normalized = NormalizeName(gpuName);
            foreach (KeyValuePair<string, string> pair in dxdiagMemory)
            {
                if (normalized.Contains(pair.Key) || pair.Key.Contains(normalized))
                    return pair.Value;
            }

            return string.Empty;
        }

        private static string ReadXmlText(XmlNode node, string name)
        {
            XmlNode child = node.SelectSingleNode(name);
            if (child == null || string.IsNullOrWhiteSpace(child.InnerText))
                return Unknown;

            return child.InnerText.Trim();
        }

        private static ulong ParseMegabytes(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            string digits = new string(text.Where(delegate(char c) { return char.IsDigit(c); }).ToArray());
            ulong mb;
            if (!ulong.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out mb))
                return 0;

            return mb * 1024UL * 1024UL;
        }

        private static StorageDetails ReadStorageDetails(int number)
        {
            StorageDetails details = new StorageDetails();
            if (number < 0)
                return details;

            foreach (ManagementObject disk in Query(@"root\Microsoft\Windows\Storage", "SELECT BusType FROM MSFT_Disk WHERE Number=" + number.ToString(CultureInfo.InvariantCulture)))
            {
                details.BusType = ToInt32(disk["BusType"], 0);
                break;
            }

            foreach (ManagementObject disk in Query(@"root\Microsoft\Windows\Storage", "SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceId='" + number.ToString(CultureInfo.InvariantCulture) + "'"))
            {
                details.MediaType = ToInt32(disk["MediaType"], 0);
                break;
            }

            return details;
        }

        private static string ClassifyDisk(int busType, int storageMediaType, string model, string win32MediaType)
        {
            if (busType == 17)
                return "NVMe SSD";

            bool isSsd = storageMediaType == 4 || ContainsAny(model, "ssd", "solid state");
            bool isHdd = storageMediaType == 3 || ContainsAny(win32MediaType, "hard disk");

            if (busType == 11 || busType == 3)
            {
                if (isSsd)
                {
                    if (ContainsAny(model, "m.2", "m2"))
                        return "M.2 SATA SSD";

                    return "SATA SSD";
                }

                if (isHdd)
                    return "SATA HDD";

                return "SATA накопитель";
            }

            if (isSsd)
                return "SSD";

            if (isHdd)
                return "HDD";

            return BusTypeName(busType) + " накопитель";
        }

        private static bool IsExternalUsbDisk(ManagementObject disk)
        {
            string interfaceType = Clean(disk["InterfaceType"]);
            string mediaType = Clean(disk["MediaType"]);
            string pnp = Clean(disk["PNPDeviceID"]);

            return string.Equals(interfaceType, "USB", StringComparison.OrdinalIgnoreCase)
                   || ContainsAny(mediaType, "removable", "external")
                   || ContainsAny(pnp, "USBSTOR", "USB\\");
        }

        private static string ReadMonitorDiagonal()
        {
            foreach (ManagementObject monitor in Query(@"root\WMI", "SELECT MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams WHERE Active=True"))
            {
                double widthCm = ToDouble(monitor["MaxHorizontalImageSize"]);
                double heightCm = ToDouble(monitor["MaxVerticalImageSize"]);
                if (widthCm <= 0 || heightCm <= 0)
                    continue;

                double diagonalInches = Math.Sqrt(widthCm * widthCm + heightCm * heightCm) / 2.54;
                return diagonalInches.ToString("0.#", CultureInfo.InvariantCulture) + "\"";
            }

            return Unknown;
        }

        private static string GetPrimaryRefreshRate()
        {
            DEVMODE mode = new DEVMODE();
            mode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (EnumDisplaySettings(null, -1, ref mode) && mode.dmDisplayFrequency > 0)
                return mode.dmDisplayFrequency.ToString(CultureInfo.InvariantCulture) + " Гц";

            return Unknown;
        }

        private static IEnumerable<ManagementObject> Query(string scope, string query)
        {
            List<ManagementObject> result = new List<ManagementObject>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query))
                {
                    foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
                        result.Add(item);
                }
            }
            catch
            {
            }

            return result;
        }

        private static void Add(SystemInfoSnapshot snapshot, string section, string name, string value)
        {
            snapshot.Rows.Add(new InfoRow(section, name, string.IsNullOrWhiteSpace(value) ? Unknown : value.Trim()));
        }

        private static string Clean(object value)
        {
            if (value == null)
                return Unknown;

            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text))
                return Unknown;

            return text.Trim();
        }

        private static ulong ToUInt64(object value)
        {
            if (value == null)
                return 0;

            try { return Convert.ToUInt64(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static int ToInt32(object value, int fallback)
        {
            if (value == null)
                return fallback;

            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static double ToDouble(object value)
        {
            if (value == null)
                return 0;

            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static string FormatMHz(object value)
        {
            int mhz = ToInt32(value, 0);
            if (mhz <= 0)
                return Unknown;

            if (mhz >= 1000)
                return (mhz / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " ГГц (" + mhz.ToString(CultureInfo.InvariantCulture) + " МГц)";

            return mhz.ToString(CultureInfo.InvariantCulture) + " МГц";
        }

        private static string FormatBytes(ulong bytes)
        {
            if (bytes == 0)
                return Unknown;

            string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return value.ToString(unit >= 3 ? "0.#" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
        }

        private static string FirstKnown(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value) && value != Unknown)
                    return value;
            }

            return Unknown;
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            if (string.IsNullOrWhiteSpace(value) || value == Unknown)
                return false;

            foreach (string needle in needles)
            {
                if (value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static string NormalizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            StringBuilder builder = new StringBuilder(value.Length);
            foreach (char c in value.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                    builder.Append(c);
            }

            return builder.ToString();
        }

        private static string GuessVendor(string model)
        {
            if (ContainsAny(model, "samsung")) return "Samsung";
            if (ContainsAny(model, "kingston")) return "Kingston";
            if (ContainsAny(model, "western digital", "wd ", "wds")) return "Western Digital";
            if (ContainsAny(model, "seagate", "st")) return "Seagate";
            if (ContainsAny(model, "toshiba", "kioxia")) return "Kioxia/Toshiba";
            if (ContainsAny(model, "crucial", "micron")) return "Crucial/Micron";
            if (ContainsAny(model, "intel")) return "Intel";
            if (ContainsAny(model, "sk hynix", "hynix")) return "SK hynix";
            if (ContainsAny(model, "adata", "xpg")) return "ADATA";
            if (ContainsAny(model, "sandisk")) return "SanDisk";
            return Unknown;
        }

        private static string BusTypeName(int busType)
        {
            switch (busType)
            {
                case 1: return "SCSI";
                case 2: return "ATAPI";
                case 3: return "ATA";
                case 7: return "USB";
                case 8: return "RAID";
                case 10: return "SAS";
                case 11: return "SATA";
                case 14: return "Virtual";
                case 17: return "NVMe";
                default: return Unknown;
            }
        }

        private static string FormatPortType(object value)
        {
            int type = ToInt32(value, -1);
            switch (type)
            {
                case 0: return "None";
                case 1: return "Parallel Port XT/AT Compatible";
                case 2: return "Parallel Port PS/2";
                case 3: return "Parallel Port ECP";
                case 4: return "Parallel Port EPP";
                case 5: return "Parallel Port ECP/EPP";
                case 6: return "Serial Port XT/AT Compatible";
                case 7: return "Serial Port 16450 Compatible";
                case 8: return "Serial Port 16550 Compatible";
                case 9: return "Serial Port 16550A Compatible";
                case 10: return "SCSI Port";
                case 11: return "MIDI Port";
                case 12: return "Joystick Port";
                case 13: return "Keyboard Port";
                case 14: return "Mouse Port";
                case 15: return "SSA SCSI";
                case 16: return "USB";
                case 17: return "FireWire";
                case 18: return "PCMCIA Type I";
                case 19: return "PCMCIA Type II";
                case 20: return "PCMCIA Type III";
                case 21: return "CardBus";
                case 22: return "Access Bus Port";
                case 23: return "SCSI II";
                case 24: return "SCSI Wide";
                case 25: return "PC-98";
                case 26: return "PC-98-Hireso";
                case 27: return "PC-H98";
                case 28: return "Video Port";
                case 29: return "Audio Port";
                case 30: return "Modem Port";
                case 31: return "Network Port";
                case 32: return "SATA";
                case 33: return "SAS";
                case 34: return "MFDP";
                case 35: return "Thunderbolt";
                default: return Unknown;
            }
        }

        private static string FormatConnectorType(object value)
        {
            ushort[] values = value as ushort[];
            if (values == null || values.Length == 0)
                return Unknown;

            List<string> names = new List<string>();
            foreach (ushort type in values)
                names.Add(type.ToString(CultureInfo.InvariantCulture));

            return string.Join(", ", names.ToArray());
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
        }

        private sealed class StorageDetails
        {
            public int BusType { get; set; }
            public int MediaType { get; set; }
        }
    }
}
