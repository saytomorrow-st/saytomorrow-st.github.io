using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace SayTomorrowUtility
{
    internal sealed class AutotuneTaskDefinition
    {
        public AutotuneTaskDefinition(string id, string title, Func<AutotuneResult> check, Func<AutotuneResult> run, bool destructive = false)
        {
            Id = id;
            Title = title;
            Check = check;
            Run = run;
            Destructive = destructive;
        }

        public string Id { get; private set; }
        public string Title { get; private set; }
        public Func<AutotuneResult> Check { get; private set; }
        public Func<AutotuneResult> Run { get; private set; }
        public bool Destructive { get; private set; }
    }

    internal sealed class AutotuneResult
    {
        public AutotuneResult(bool done, string status, string details = "")
        {
            Done = done;
            Status = status;
            Details = details;
        }

        public bool Done { get; private set; }
        public string Status { get; private set; }
        public string Details { get; private set; }
    }

    internal static class AutotuneManager
    {
        public static List<AutotuneTaskDefinition> CreateTasks()
        {
            return new List<AutotuneTaskDefinition>
            {
                new AutotuneTaskDefinition("network", "Сеть", CheckNetwork, RunNetwork),
                new AutotuneTaskDefinition("drivers", "Обновления драйверов", CheckDriverUpdatesDisabled, RunDisableDriverUpdates),
                new AutotuneTaskDefinition("office", "Установка Office", CheckOfficeInstalled, RunOfficeInstall),
                new AutotuneTaskDefinition("redists", "Библиотеки", CheckRedistsInstalled, RunRedistsInstall),
                new AutotuneTaskDefinition("wallpaper", "Обои", CheckWallpaper, RunWallpaper),
                new AutotuneTaskDefinition("darktheme", "Темная тема", CheckDarkTheme, RunDarkTheme),
                new AutotuneTaskDefinition("rudesktop", "RuDesktop", CheckRuDesktop, RunRuDesktopInstall),
                new AutotuneTaskDefinition("activation", "Активация", CheckActivation, CheckActivation),
                new AutotuneTaskDefinition("rawdisks", "Диски", CheckRawDisks, RunPartitionRawDisks, true)
            };
        }

        public static string ExtraDirectory
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "extra"); }
        }

        public static string WallpapersDirectory
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wallpapers"); }
        }

        private static string WifiConfigPath
        {
            get { return Path.Combine(ExtraDirectory, "wifi-password.txt"); }
        }

        private static AutotuneResult CheckNetwork()
        {
            if (HasConnectedNetwork())
                return new AutotuneResult(true, "Выполнено", "Сеть уже подключена: " + GetConnectedNetworkSummary());

            if (!HasWifiAdapter())
                return new AutotuneResult(false, "Нет подключения", "Wi‑Fi модуль не найден, LAN тоже не подключен.");

            WifiConfig wifiConfig = ReadWifiConfig();
            if (!wifiConfig.IsValid)
                return new AutotuneResult(false, "Нет данных Wi‑Fi", "Создай файл extra\\wifi-password.txt: первая строка — SSID, вторая строка — пароль.");

            return new AutotuneResult(false, "Требуется подключение", "Wi‑Fi модуль найден, сеть не подключена.");
        }

        private static AutotuneResult RunNetwork()
        {
            AutotuneResult current = CheckNetwork();
            if (current.Done)
                return current;

            if (!HasWifiAdapter())
                return new AutotuneResult(false, "Пропущено", "Wi‑Fi модуль не найден.");

            WifiConfig wifiConfig = ReadWifiConfig();
            if (!wifiConfig.IsValid)
                return new AutotuneResult(false, "Нет данных Wi‑Fi", "Создай файл extra\\wifi-password.txt: первая строка — SSID, вторая строка — пароль.");

            string profilePath = Path.Combine(Path.GetTempPath(), "saytomorrow_wifi.xml");
            string xml =
                "<?xml version=\"1.0\"?>" +
                "<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">" +
                "<name>" + EscapeXml(wifiConfig.Ssid) + "</name>" +
                "<SSIDConfig><SSID><name>" + EscapeXml(wifiConfig.Ssid) + "</name></SSID></SSIDConfig>" +
                "<connectionType>ESS</connectionType><connectionMode>auto</connectionMode>" +
                "<MSM><security><authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>" +
                "<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>" + EscapeXml(wifiConfig.Password) + "</keyMaterial></sharedKey>" +
                "</security></MSM></WLANProfile>";

                File.WriteAllText(profilePath, xml, Encoding.UTF8);
            try
            {
                RunProcess("netsh", "wlan add profile filename=\"" + profilePath + "\" user=all", 30000);
                ProcessResult connect = RunProcess("netsh", "wlan connect name=\"" + wifiConfig.Ssid + "\" ssid=\"" + wifiConfig.Ssid + "\"", 30000);
                return new AutotuneResult(connect.ExitCode == 0, connect.ExitCode == 0 ? "Запущено" : "Ошибка", connect.Output);
            }
            finally
            {
                try { File.Delete(profilePath); } catch { }
            }
        }

        private static AutotuneResult CheckDriverUpdatesDisabled()
        {
            object searchOrder = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching", "SearchOrderConfig", null);
            object policy = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", null);
            bool disabled = ToInt(searchOrder) == 0 && ToInt(policy) == 1;
            return new AutotuneResult(disabled, disabled ? "Выполнено" : "Не выполнено", disabled ? "Драйверы исключены из Windows Update." : "Будут изменены системные параметры обновления драйверов.");
        }

        private static AutotuneResult RunDisableDriverUpdates()
        {
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching"))
                key.SetValue("SearchOrderConfig", 0, RegistryValueKind.DWord);

            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate"))
                key.SetValue("ExcludeWUDriversInQualityUpdate", 1, RegistryValueKind.DWord);

            return CheckDriverUpdatesDisabled();
        }

        private static AutotuneResult CheckOfficeInstalled()
        {
            OfficeInstallInfo info = GetOfficeInstallInfo();
            return new AutotuneResult(info.Installed, info.Installed ? "Выполнено" : "Не установлено", info.Installed ? "Office найден." : "Ожидается setup.exe и configuration*.xml в extra или extra\\Office.");
        }

        private static AutotuneResult RunOfficeInstall()
        {
            string setup = FindOfficeSetup();
            if (string.IsNullOrEmpty(setup))
                return new AutotuneResult(false, "Нет установщика", "Положи setup.exe из Office Deployment Tool в extra или extra\\Office.");

            string config = FindOfficeConfiguration(setup);
            if (string.IsNullOrEmpty(config))
                return new AutotuneResult(false, "Нет конфигурации", "Положи configuration*.xml рядом с setup.exe, в extra или extra\\Office.");

            string args = "/configure \"" + config + "\"";
            ProcessResult result = RunProcess(setup, args, 60 * 60 * 1000);
            return new AutotuneResult(result.ExitCode == 0, result.ExitCode == 0 ? "Выполнено" : "Ошибка установки", result.Output);
        }

        private static AutotuneResult CheckRedistsInstalled()
        {
            bool vc = RegistryKeyExists(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64") || RegistryKeyExists(@"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86");
            bool xna = RegistryKeyExists(@"SOFTWARE\Microsoft\XNA\Framework\v4.0") || RegistryKeyExists(@"SOFTWARE\WOW6432Node\Microsoft\XNA\Framework\v4.0");
            bool openAl = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenAL32.dll"));
            bool directX = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "d3dx9_43.dll"))
                || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "d3dx9_43.dll"));
            bool dotNet40 = RegistryKeyExists(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full")
                || RegistryKeyExists(@"SOFTWARE\WOW6432Node\Microsoft\NET Framework Setup\NDP\v4\Full");
            bool done = vc && xna && openAl && directX && dotNet40;
            return new AutotuneResult(done, done ? "Выполнено" : "Частично/не выполнено", "DirectX: " + BoolText(directX) + "; .NET 4.x: " + BoolText(dotNet40) + "; VC++: " + BoolText(vc) + "; XNA 4.0: " + BoolText(xna) + "; OpenAL: " + BoolText(openAl));
        }

        private static AutotuneResult RunRedistsInstall()
        {
            if (!Directory.Exists(ExtraDirectory))
                return new AutotuneResult(false, "Нет папки extra", "Создай папку extra рядом с exe и положи туда redist-установщики.");

            List<string> logs = new List<string>();
            RunDirectXInstaller(logs);
            RunInstallerIfFound(logs, new[] { "ndp481", "ndp48", "dotnetfx40", "dotnetfx_full_x86_x64" }, "/q /norestart");
            RunInstallerIfFound(logs, new[] { "oalinst", "openal" }, "/S");
            RunInstallerIfFound(logs, new[] { "vc_redist.x64", "vcredist_x64", "visualcpp", "visual-cpp" }, "/quiet /norestart");
            RunInstallerIfFound(logs, new[] { "vc_redist.x86", "vcredist_x86" }, "/quiet /norestart");
            RunMsiInstallerIfFound(logs, new[] { "xnafx40", "xnafx40_redist" });
            RunInstallerIfFound(logs, new[] { "verify-redist", "verify_redist" }, "/quiet /norestart");
            RunInstallerIfFound(logs, new[] { "offline redist", "offline-redist", "aio-runtimes" }, "/silent /norestart");

            AutotuneResult check = CheckRedistsInstalled();
            return new AutotuneResult(check.Done, check.Done ? "Выполнено" : "Запущено/частично", string.Join(Environment.NewLine, logs.ToArray()));
        }

        private static AutotuneResult CheckWallpaper()
        {
            string current = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string;
            bool done = !string.IsNullOrEmpty(current) && current.IndexOf(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SayTomorrowWallpapers"), StringComparison.OrdinalIgnoreCase) >= 0;
            return new AutotuneResult(done, done ? "Выполнено" : "Не выполнено", done ? current : "Будет выбран случайный файл из wallpapers.");
        }

        private static AutotuneResult RunWallpaper()
        {
            if (!Directory.Exists(WallpapersDirectory))
                return new AutotuneResult(false, "Нет папки wallpapers", "Создай папку wallpapers рядом с exe.");

            string[] files = Directory.GetFiles(WallpapersDirectory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => HasExtension(f, ".jpg", ".jpeg", ".png", ".bmp")).ToArray();
            if (files.Length == 0)
                return new AutotuneResult(false, "Нет обоев", "В папке wallpapers нет jpg/png/bmp файлов.");

            string targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SayTomorrowWallpapers");
            Directory.CreateDirectory(targetDir);
            string source = files[new Random().Next(files.Length)];
            string target = Path.Combine(targetDir, Path.GetFileName(source));
            File.Copy(source, target, true);

            bool ok = SystemParametersInfo(20, 0, target, 0x01 | 0x02);
            return new AutotuneResult(ok, ok ? "Выполнено" : "Ошибка", target);
        }

        private static AutotuneResult CheckDarkTheme()
        {
            int apps = ToInt(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", null));
            int system = ToInt(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", null));
            bool done = apps == 0 && system == 0;
            return new AutotuneResult(done, done ? "Выполнено" : "Не выполнено", done ? "Темная тема уже включена." : "Будет изменена тема приложений и системы.");
        }

        private static AutotuneResult RunDarkTheme()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                key.SetValue("AppsUseLightTheme", 0, RegistryValueKind.DWord);
                key.SetValue("SystemUsesLightTheme", 0, RegistryValueKind.DWord);
            }

            return CheckDarkTheme();
        }

        private static AutotuneResult CheckRuDesktop()
        {
            bool installed = RegistryKeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RuDesktop") || RegistryKeyExists(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\RuDesktop") || ProgramFilesContains("RuDesktop");
            return new AutotuneResult(installed, installed ? "Выполнено" : "Не установлено", installed ? "RuDesktop найден." : "Ожидается установщик в extra.");
        }

        private static AutotuneResult RunRuDesktopInstall()
        {
            string installer = FindFile(ExtraDirectory, "*.exe", "rudesktop", "ru-desktop", "ru desktop");
            if (string.IsNullOrEmpty(installer))
                installer = FindFile(ExtraDirectory, "*.msi", "rudesktop", "ru-desktop", "ru desktop");
            if (string.IsNullOrEmpty(installer))
                return new AutotuneResult(false, "Нет установщика", "Положи установщик RuDesktop в папку extra.");

            ProcessResult result = HasExtension(installer, ".msi")
                ? RunProcess("msiexec.exe", "/i \"" + installer + "\" /qn /norestart", 30 * 60 * 1000)
                : RunProcess(installer, "/S /quiet /norestart", 30 * 60 * 1000);
            bool success = IsInstallerSuccess(result.ExitCode);
            return new AutotuneResult(success, success ? "Выполнено" : "Ошибка установки", result.Output);
        }

        private static AutotuneResult CheckActivation()
        {
            bool windowsActivated = IsWindowsActivated();
            OfficeInstallInfo office = GetOfficeInstallInfo();
            if (!office.Installed)
            {
                string noOfficeDetails = "Windows: " + (windowsActivated ? "активирована" : "не активирована") + "; Office: не установлен.";
                return new AutotuneResult(windowsActivated, windowsActivated ? "Выполнено" : "Не активировано", noOfficeDetails);
            }

            bool officeActivated = IsOfficeActivated(office);
            bool done = windowsActivated && officeActivated;
            string details = "Windows: " + (windowsActivated ? "активирована" : "не активирована") + "; Office: " + (officeActivated ? "активирован" : "не активирован") + ".";
            return new AutotuneResult(done, done ? "Выполнено" : "Не активировано", details);
        }

        private static AutotuneResult CheckRawDisks()
        {
            string script = "Get-Disk | Where-Object { $_.PartitionStyle -eq 'RAW' -and $_.BusType -ne 'USB' } | Select-Object -ExpandProperty Number";
            ProcessResult result = RunProcess("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + script + "\"", 60000);
            if (result.ExitCode != 0)
                return new AutotuneResult(false, "Ошибка проверки", result.Output);

            bool hasRaw = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Any(IsInteger);
            return new AutotuneResult(!hasRaw, hasRaw ? "Есть пустые диски" : "Выполнено", hasRaw ? result.Output.Trim() : "RAW-диски не найдены.");
        }

        private static AutotuneResult RunPartitionRawDisks()
        {
            string script =
                "$ErrorActionPreference='Stop';" +
                "$disks=Get-Disk | Where-Object { $_.PartitionStyle -eq 'RAW' -and $_.BusType -ne 'USB' -and $_.IsSystem -eq $false -and $_.IsBoot -eq $false };" +
                "foreach($d in $disks){" +
                "$label=if($d.MediaType -eq 'HDD'){'HDD'}else{'SSD'};" +
                "$d | Initialize-Disk -PartitionStyle GPT -PassThru | New-Partition -UseMaximumSize -AssignDriveLetter | Format-Volume -FileSystem NTFS -NewFileSystemLabel $label -Confirm:$false -Force" +
                "};" +
                "$disks | Select-Object Number,FriendlyName,MediaType | Format-Table -AutoSize | Out-String";
            ProcessResult result = RunProcess("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + script + "\"", 30 * 60 * 1000);
            return new AutotuneResult(result.ExitCode == 0, result.ExitCode == 0 ? "Выполнено" : "Ошибка", result.Output);
        }

        private static bool HasConnectedNetwork()
        {
            foreach (ManagementObject adapter in Query(@"root\CIMV2", "SELECT NetConnectionStatus, PhysicalAdapter FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True"))
            {
                if (ToInt(adapter["NetConnectionStatus"]) == 2)
                    return true;
            }
            return false;
        }

        private static string GetConnectedNetworkSummary()
        {
            List<string> names = new List<string>();
            foreach (ManagementObject adapter in Query(@"root\CIMV2", "SELECT Name, NetConnectionStatus, PhysicalAdapter FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True"))
            {
                if (ToInt(adapter["NetConnectionStatus"]) == 2)
                    names.Add(Convert.ToString(adapter["Name"]));
            }
            return names.Count == 0 ? "нет" : string.Join(", ", names.ToArray());
        }

        private static bool HasWifiAdapter()
        {
            foreach (ManagementObject adapter in Query(@"root\CIMV2", "SELECT Name, AdapterType, PhysicalAdapter FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True"))
            {
                string name = Convert.ToString(adapter["Name"]);
                string type = Convert.ToString(adapter["AdapterType"]);
                if (ContainsAny(name, "wi-fi", "wifi", "wireless", "wlan", "802.11") || ContainsAny(type, "wireless", "802.11"))
                    return true;
            }
            return false;
        }

        private static OfficeInstallInfo GetOfficeInstallInfo()
        {
            OfficeInstallInfo info = new OfficeInstallInfo();
            string clickToRunPath = ReadRegistryString(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration", "ClientFolder")
                ?? ReadRegistryString(@"SOFTWARE\WOW6432Node\Microsoft\Office\ClickToRun\Configuration", "ClientFolder");

            if (!string.IsNullOrEmpty(clickToRunPath) && Directory.Exists(clickToRunPath))
            {
                info.Installed = true;
                info.RootPath = clickToRunPath;
                return info;
            }

            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };

            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root))
                    continue;

                string officeRoot = Path.Combine(root, "Microsoft Office");
                if (!Directory.Exists(officeRoot))
                    continue;

                info.Installed = true;
                info.RootPath = officeRoot;
                return info;
            }

            return info;
        }

        private static string CheckOfficeActivationText(OfficeInstallInfo office)
        {
            if (office != null && !string.IsNullOrEmpty(office.RootPath) && Directory.Exists(office.RootPath))
            {
                string ospp = Directory.GetFiles(office.RootPath, "ospp.vbs", SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrEmpty(ospp))
                    return RunProcess("cscript.exe", "//Nologo \"" + ospp + "\" /dstatus", 60000).Output;
            }

            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };

            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root))
                    continue;

                string officeRoot = Path.Combine(root, "Microsoft Office");
                if (!Directory.Exists(officeRoot))
                    continue;

                string ospp = Directory.GetFiles(officeRoot, "ospp.vbs", SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrEmpty(ospp))
                    return RunProcess("cscript.exe", "//Nologo \"" + ospp + "\" /dstatus", 60000).Output;
            }

            return "ospp.vbs не найден";
        }

        private static bool IsWindowsActivated()
        {
            foreach (ManagementObject license in Query(@"root\CIMV2", "SELECT LicenseStatus, PartialProductKey, ApplicationID, Description FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL"))
            {
                string applicationId = Convert.ToString(license["ApplicationID"]);
                string description = Convert.ToString(license["Description"]);
                if (ToInt(license["LicenseStatus"]) == 1
                    && string.Equals(applicationId, "55c92734-d682-4d71-983e-d6ec3f16059f", StringComparison.OrdinalIgnoreCase)
                    && ContainsAny(description, "Windows"))
                    return true;
            }

            ProcessResult windows = RunProcess("cscript.exe", "//Nologo \"%windir%\\system32\\slmgr.vbs\" /xpr", 60000);
            string output = windows.Output ?? string.Empty;
            if (ContainsAny(output, "not activated", "не актив", "не удалось"))
                return false;

            return ContainsAny(output, "permanently activated", "activated", "активирован", "активирована");
        }

        private static bool IsOfficeActivated(OfficeInstallInfo office)
        {
            foreach (ManagementObject license in Query(@"root\CIMV2", "SELECT LicenseStatus, PartialProductKey, ApplicationID, Name, Description FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL"))
            {
                string applicationId = Convert.ToString(license["ApplicationID"]);
                string name = Convert.ToString(license["Name"]);
                string description = Convert.ToString(license["Description"]);
                if (ToInt(license["LicenseStatus"]) == 1
                    && (string.Equals(applicationId, "0ff1ce15-a989-479d-af46-f275c6370663", StringComparison.OrdinalIgnoreCase)
                        || ContainsAny(name, "Office")
                        || ContainsAny(description, "Office")))
                    return true;
            }

            string officeStatus = CheckOfficeActivationText(office);
            if (ContainsAny(officeStatus, "---LICENSED---", "LICENSE STATUS:  LICENSED", "лицензирован", "активирован"))
                return true;

            return false;
        }

        private static string FindOfficeSetup()
        {
            string[] preferredDirs =
            {
                Path.Combine(ExtraDirectory, "Office"),
                Path.Combine(ExtraDirectory, "office"),
                ExtraDirectory
            };

            foreach (string directory in preferredDirs)
            {
                string setup = Path.Combine(directory, "setup.exe");
                if (File.Exists(setup) && LooksLikeOfficeSetup(setup))
                    return setup;
            }

            foreach (string setup in Directory.Exists(ExtraDirectory) ? Directory.GetFiles(ExtraDirectory, "setup.exe", SearchOption.AllDirectories) : new string[0])
            {
                if (LooksLikeOfficeSetup(setup))
                    return setup;
            }

            return string.Empty;
        }

        private static string FindOfficeConfiguration(string setup)
        {
            string setupDir = Path.GetDirectoryName(setup);
            string[] directories = new[] { setupDir, Path.Combine(ExtraDirectory, "Office"), Path.Combine(ExtraDirectory, "office"), ExtraDirectory }
                .Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (string directory in directories)
            {
                string direct = Path.Combine(directory, "configuration.xml");
                if (File.Exists(direct))
                    return direct;

                string match = Directory.GetFiles(directory, "configuration*.xml", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (!string.IsNullOrEmpty(match))
                    return match;
            }

            string recursive = Directory.Exists(ExtraDirectory) ? Directory.GetFiles(ExtraDirectory, "configuration*.xml", SearchOption.AllDirectories).FirstOrDefault() : null;
            return recursive ?? string.Empty;
        }

        private static bool LooksLikeOfficeSetup(string setup)
        {
            string directory = Path.GetDirectoryName(setup);
            if (string.IsNullOrEmpty(directory))
                return false;

            if (Directory.GetFiles(directory, "configuration*.xml", SearchOption.TopDirectoryOnly).Length > 0)
                return true;

            if (Directory.GetDirectories(directory, "Office", SearchOption.TopDirectoryOnly).Length > 0)
                return true;

            return setup.IndexOf("officedeploymenttool", StringComparison.OrdinalIgnoreCase) >= 0
                || directory.IndexOf("office", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void RunInstallerIfFound(List<string> logs, string[] aliases, string arguments)
        {
            string installer = FindFile(ExtraDirectory, "*.exe", aliases);
            if (string.IsNullOrEmpty(installer))
            {
                logs.Add("Не найдено: " + string.Join("/", aliases));
                return;
            }

            ProcessResult result = RunProcess(installer, arguments, 30 * 60 * 1000);
            logs.Add(Path.GetFileName(installer) + ": exit " + result.ExitCode.ToString(CultureInfo.InvariantCulture));
        }

        private static void RunDirectXInstaller(List<string> logs)
        {
            string installer = FindFile(ExtraDirectory, "*.exe", "directx", "dxwebsetup", "dxsetup");
            if (string.IsNullOrEmpty(installer))
            {
                logs.Add("Не найдено: DirectX");
                return;
            }

            if (string.Equals(Path.GetFileName(installer), "DXSETUP.exe", StringComparison.OrdinalIgnoreCase))
            {
                ProcessResult direct = RunProcess(installer, "/silent", 20 * 60 * 1000);
                logs.Add("DirectX: exit " + direct.ExitCode.ToString(CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(direct.Output) ? string.Empty : " " + direct.Output));
                return;
            }

            string extractDir = Path.Combine(Path.GetTempPath(), "saytomorrow_directx_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extractDir);
            try
            {
                ProcessResult extract = RunProcess(installer, "/Q /T:\"" + extractDir + "\"", 10 * 60 * 1000);
                if (extract.ExitCode != 0)
                {
                    logs.Add(Path.GetFileName(installer) + ": extract exit " + extract.ExitCode.ToString(CultureInfo.InvariantCulture) + " " + extract.Output);
                    return;
                }

                string dxsetup = Directory.GetFiles(extractDir, "DXSETUP.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (string.IsNullOrEmpty(dxsetup))
                {
                    logs.Add(Path.GetFileName(installer) + ": DXSETUP.exe не найден после распаковки");
                    return;
                }

                ProcessResult install = RunProcess(dxsetup, "/silent", 20 * 60 * 1000);
                logs.Add("DirectX: exit " + install.ExitCode.ToString(CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(install.Output) ? string.Empty : " " + install.Output));
            }
            finally
            {
                try { Directory.Delete(extractDir, true); } catch { }
            }
        }

        private static void RunMsiInstallerIfFound(List<string> logs, string[] aliases)
        {
            string installer = FindFile(ExtraDirectory, "*.msi", aliases);
            if (string.IsNullOrEmpty(installer))
            {
                logs.Add("Не найдено: " + string.Join("/", aliases));
                return;
            }

            ProcessResult result = RunProcess("msiexec.exe", "/i \"" + installer + "\" /qn /norestart", 30 * 60 * 1000);
            logs.Add(Path.GetFileName(installer) + ": exit " + result.ExitCode.ToString(CultureInfo.InvariantCulture));
        }

        private static bool IsInstallerSuccess(int exitCode)
        {
            return exitCode == 0 || exitCode == 3010 || exitCode == 1641;
        }

        private static string FindFile(string root, string pattern, params string[] aliases)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return string.Empty;

            foreach (string file in Directory.GetFiles(root, pattern, SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file).ToLowerInvariant();
                foreach (string alias in aliases)
                {
                    if (string.IsNullOrEmpty(alias) || name.IndexOf(alias.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase) >= 0)
                        return file;
                }
            }

            return string.Empty;
        }

        private static bool RegistryKeyExists(string path)
        {
            RegistryKey root = path.StartsWith("SOFTWARE\\WOW6432Node", StringComparison.OrdinalIgnoreCase) ? Registry.LocalMachine : Registry.LocalMachine;
            using (RegistryKey key = root.OpenSubKey(path))
                return key != null;
        }

        private static string ReadRegistryString(string path, string name)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path))
                    return key == null ? null : key.GetValue(name) as string;
            }
            catch
            {
                return null;
            }
        }

        private static bool ProgramFilesContains(string folderName)
        {
            string[] roots = { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
            foreach (string root in roots)
            {
                if (!string.IsNullOrEmpty(root) && Directory.Exists(Path.Combine(root, folderName)))
                    return true;
            }
            return false;
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

        private static ProcessResult RunProcess(string fileName, string arguments, int timeoutMs)
        {
            ProcessResult result = new ProcessResult();
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(fileName, Environment.ExpandEnvironmentVariables(arguments))
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
                    StandardErrorEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
                };

                using (Process process = Process.Start(info))
                {
                    if (process == null)
                        return new ProcessResult { ExitCode = -1, Output = "Не удалось запустить процесс." };

                    StringBuilder output = new StringBuilder();
                    StringBuilder error = new StringBuilder();
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null)
                            output.AppendLine(e.Data);
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null)
                            error.AppendLine(e.Data);
                    };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        return new ProcessResult { ExitCode = -2, Output = "Таймаут: " + fileName };
                    }
                    process.WaitForExit();

                    result.ExitCode = process.ExitCode;
                    result.Output = (output.ToString() + Environment.NewLine + error.ToString()).Trim();
                }
            }
            catch (Exception ex)
            {
                result.ExitCode = -1;
                result.Output = ex.Message;
            }

            return result;
        }

        private static WifiConfig ReadWifiConfig()
        {
            try
            {
                if (!File.Exists(WifiConfigPath))
                    return new WifiConfig();

                string[] lines = File.ReadAllLines(WifiConfigPath, Encoding.UTF8);
                if (lines.Length < 2)
                    return new WifiConfig();

                return new WifiConfig { Ssid = lines[0].Trim(), Password = lines[1].Trim() };
            }
            catch
            {
                return new WifiConfig();
            }
        }

        private sealed class WifiConfig
        {
            public string Ssid { get; set; }
            public string Password { get; set; }

            public bool IsValid
            {
                get { return !string.IsNullOrWhiteSpace(Ssid) && !string.IsNullOrWhiteSpace(Password); }
            }
        }

        private sealed class OfficeInstallInfo
        {
            public bool Installed { get; set; }
            public string RootPath { get; set; }
        }

        private static int ToInt(object value)
        {
            if (value == null)
                return -1;

            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return -1; }
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            foreach (string needle in needles)
            {
                if (value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool HasExtension(string file, params string[] extensions)
        {
            string ext = Path.GetExtension(file);
            foreach (string expected in extensions)
            {
                if (string.Equals(ext, expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string BoolText(bool value)
        {
            return value ? "да" : "нет";
        }

        private static string EscapeXml(string value)
        {
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private static bool IsInteger(string value)
        {
            int parsed;
            return int.TryParse(value.Trim(), out parsed);
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool SystemParametersInfo(int action, int param, string value, int flags);

        private sealed class ProcessResult
        {
            public int ExitCode { get; set; }
            public string Output { get; set; }
        }
    }
}
