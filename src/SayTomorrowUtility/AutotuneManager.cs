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
        private const string WifiSsid = "TBG-SERVICE-RND_WiFi5";

        public static List<AutotuneTaskDefinition> CreateTasks()
        {
            return new List<AutotuneTaskDefinition>
            {
                new AutotuneTaskDefinition("network", "Сеть: подключение Wi‑Fi или статус LAN", CheckNetwork, RunNetwork),
                new AutotuneTaskDefinition("drivers", "Отключить автообновление драйверов из Windows Update", CheckDriverUpdatesDisabled, RunDisableDriverUpdates),
                new AutotuneTaskDefinition("office", "Silent-установка Microsoft Office 2021 RU x64 из extra", CheckOfficeInstalled, RunOfficeInstall),
                new AutotuneTaskDefinition("redists", "Silent-установка DirectX/.NET/VC++/XNA/OpenAL библиотек из extra", CheckRedistsInstalled, RunRedistsInstall),
                new AutotuneTaskDefinition("wallpaper", "Случайные обои из wallpapers", CheckWallpaper, RunWallpaper),
                new AutotuneTaskDefinition("darktheme", "Темная тема Windows", CheckDarkTheme, RunDarkTheme),
                new AutotuneTaskDefinition("rudesktop", "Silent-установка RuDesktop из extra", CheckRuDesktop, RunRuDesktopInstall),
                new AutotuneTaskDefinition("activation", "Проверка активации Windows и Office", CheckActivation, CheckActivation),
                new AutotuneTaskDefinition("rawdisks", "Разметить полностью пустые накопители в NTFS", CheckRawDisks, RunPartitionRawDisks, true)
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

        private static string WifiPasswordPath
        {
            get { return Path.Combine(ExtraDirectory, "wifi-password.txt"); }
        }

        private static AutotuneResult CheckNetwork()
        {
            if (HasConnectedNetwork())
                return new AutotuneResult(true, "Выполнено", "Сеть уже подключена: " + GetConnectedNetworkSummary());

            if (!HasWifiAdapter())
                return new AutotuneResult(false, "Нет подключения", "Wi‑Fi модуль не найден, LAN тоже не подключен.");

            if (!File.Exists(WifiPasswordPath))
                return new AutotuneResult(false, "Нет пароля Wi‑Fi", "Создай файл extra\\wifi-password.txt рядом с exe и положи туда пароль от " + WifiSsid + ".");

            return new AutotuneResult(false, "Требуется подключение", "Wi‑Fi модуль найден, сеть не подключена.");
        }

        private static AutotuneResult RunNetwork()
        {
            AutotuneResult current = CheckNetwork();
            if (current.Done)
                return current;

            if (!HasWifiAdapter())
                return new AutotuneResult(false, "Пропущено", "Wi‑Fi модуль не найден.");

            string wifiPassword = ReadWifiPassword();
            if (string.IsNullOrWhiteSpace(wifiPassword))
                return new AutotuneResult(false, "Нет пароля Wi‑Fi", "Создай файл extra\\wifi-password.txt рядом с exe.");

            string profilePath = Path.Combine(Path.GetTempPath(), "saytomorrow_wifi.xml");
            string xml =
                "<?xml version=\"1.0\"?>" +
                "<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">" +
                "<name>" + EscapeXml(WifiSsid) + "</name>" +
                "<SSIDConfig><SSID><name>" + EscapeXml(WifiSsid) + "</name></SSID></SSIDConfig>" +
                "<connectionType>ESS</connectionType><connectionMode>auto</connectionMode>" +
                "<MSM><security><authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>" +
                "<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>" + EscapeXml(wifiPassword) + "</keyMaterial></sharedKey>" +
                "</security></MSM></WLANProfile>";

            File.WriteAllText(profilePath, xml, Encoding.UTF8);
            try
            {
                RunProcess("netsh", "wlan add profile filename=\"" + profilePath + "\" user=all", 30000);
                ProcessResult connect = RunProcess("netsh", "wlan connect name=\"" + WifiSsid + "\" ssid=\"" + WifiSsid + "\"", 30000);
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
            bool installed = RegistryKeyExists(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration") || RegistryKeyExists(@"SOFTWARE\WOW6432Node\Microsoft\Office\ClickToRun\Configuration");
            return new AutotuneResult(installed, installed ? "Выполнено" : "Не установлено", installed ? "Office Click-to-Run найден." : "Ожидается установщик в extra\\office или extra.");
        }

        private static AutotuneResult RunOfficeInstall()
        {
            string officeDir = Path.Combine(ExtraDirectory, "office");
            string setup = File.Exists(Path.Combine(officeDir, "setup.exe")) ? Path.Combine(officeDir, "setup.exe") : FindFile(ExtraDirectory, "setup.exe", "setup");
            if (string.IsNullOrEmpty(setup))
                return new AutotuneResult(false, "Нет установщика", "Положи Office Deployment Tool setup.exe и configuration.xml в extra\\office.");

            string config = File.Exists(Path.Combine(Path.GetDirectoryName(setup), "configuration.xml")) ? Path.Combine(Path.GetDirectoryName(setup), "configuration.xml") : FindFile(Path.GetDirectoryName(setup), "*.xml", "config");
            string args = string.IsNullOrEmpty(config) ? "/configure configuration.xml" : "/configure \"" + config + "\"";
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
            RunInstallerIfFound(logs, new[] { "directx", "dxsetup" }, "/silent");
            RunInstallerIfFound(logs, new[] { "dotnetfx40", "dotnetfx_full_x86_x64" }, "/q /norestart");
            RunInstallerIfFound(logs, new[] { "oalinst", "openal" }, "/silent");
            RunInstallerIfFound(logs, new[] { "vc_redist.x64", "vcredist_x64", "visualcpp", "visual-cpp" }, "/quiet /norestart");
            RunInstallerIfFound(logs, new[] { "vc_redist.x86", "vcredist_x86" }, "/quiet /norestart");
            RunInstallerIfFound(logs, new[] { "xnafx40", "xnafx40_redist" }, "/quiet /norestart");
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
                return new AutotuneResult(false, "Нет установщика", "Положи установщик RuDesktop в папку extra.");

            ProcessResult result = RunProcess(installer, "/S /quiet /norestart", 30 * 60 * 1000);
            return new AutotuneResult(result.ExitCode == 0, result.ExitCode == 0 ? "Выполнено" : "Ошибка установки", result.Output);
        }

        private static AutotuneResult CheckActivation()
        {
            ProcessResult windows = RunProcess("cscript.exe", "//Nologo \"%windir%\\system32\\slmgr.vbs\" /xpr", 60000);
            bool windowsActivated = windows.Output.IndexOf("permanently activated", StringComparison.OrdinalIgnoreCase) >= 0 || windows.Output.IndexOf("активирована", StringComparison.OrdinalIgnoreCase) >= 0;
            string officeStatus = CheckOfficeActivationText();
            bool officeActivated = officeStatus.IndexOf("LICENSED", StringComparison.OrdinalIgnoreCase) >= 0 || officeStatus.IndexOf("лиценз", StringComparison.OrdinalIgnoreCase) >= 0;
            bool done = windowsActivated && officeActivated;
            string details = "Windows: " + (windowsActivated ? "активирована" : "не подтверждено") + "; Office: " + (officeActivated ? "активирован" : "не подтверждено") + ". Автозапуск активаторов не выполняется.";
            return new AutotuneResult(done, done ? "Выполнено" : "Требуется ручная проверка", details);
        }

        private static AutotuneResult CheckRawDisks()
        {
            string script = "Get-Disk | Where-Object { $_.PartitionStyle -eq 'RAW' -and $_.BusType -ne 'USB' } | Select-Object -ExpandProperty Number";
            ProcessResult result = RunProcess("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + script + "\"", 60000);
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

        private static string CheckOfficeActivationText()
        {
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
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
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

        private static string ReadWifiPassword()
        {
            try
            {
                if (!File.Exists(WifiPasswordPath))
                    return string.Empty;

                return File.ReadAllText(WifiPasswordPath, Encoding.UTF8).Trim();
            }
            catch
            {
                return string.Empty;
            }
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
