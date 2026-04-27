using N64RecompLauncher.Models;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace N64RecompLauncher.Services
{
    public static class ShortcutHelper
    {
        private static readonly string LauncherSteamTag = N64RecompLauncherProfile.Instance.SteamTag;

        public static void CreateGameShortcut(GameInfo game, string launcherPath, string? cacheDirectory)
        {
            if (string.IsNullOrWhiteSpace(game?.Name))
                throw new ArgumentException("Game name is required.", nameof(game));

            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string? iconPath = PrepareIcon(game, cacheDirectory);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                CreateWindowsShortcut(desktopPath, launcherPath, game, iconPath);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                CreateLinuxDesktopFile(desktopPath, launcherPath, game, iconPath);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                throw new PlatformNotSupportedException("macOS shortcuts not yet implemented");
            }
        }

        public static string AddGameToSteam(GameInfo game, string launcherPath, string? cacheDirectory)
        {
            if (string.IsNullOrWhiteSpace(game?.Name))
                throw new ArgumentException("Game name is required.", nameof(game));

            if (string.IsNullOrWhiteSpace(launcherPath))
                throw new ArgumentException("Launcher path is required.", nameof(launcherPath));

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                throw new PlatformNotSupportedException("Adding non-Steam shortcuts is currently supported on Windows and Linux only.");
            }

            if (IsSteamRunning())
                throw new InvalidOperationException("Steam is still running.");

            return AddGameToSteamInternal(game, launcherPath, cacheDirectory);
        }

        public static string QueueGameAddToSteam(GameInfo game, string launcherPath)
        {
            if (string.IsNullOrWhiteSpace(game?.Name))
                throw new ArgumentException("Game name is required.", nameof(game));

            if (string.IsNullOrWhiteSpace(launcherPath))
                throw new ArgumentException("Launcher path is required.", nameof(launcherPath));

            if (IsRunningUnderSteam())
                throw new InvalidOperationException("Steam is running this launcher, so the shortcut worker would keep Steam from seeing the launcher as closed. Close Steam and run the launcher outside Steam to add shortcuts.");

            var startInfo = new ProcessStartInfo
            {
                FileName = launcherPath,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("--add-steam-shortcut");
            startInfo.ArgumentList.Add(game.Name);
            startInfo.ArgumentList.Add("--wait-for-steam-exit");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the Steam shortcut worker.");

            return $"Queued {game.Name} for Steam. Restart Steam once and the shortcut will be written after Steam fully closes.";
        }

        public static async Task<string> AddGameToSteamFromCliAsync(GameInfo game, string launcherPath, string? cacheDirectory, bool waitForSteamExit)
        {
            if (waitForSteamExit)
            {
                await WaitForSteamExitAsync().ConfigureAwait(false);
            }
            else if (IsSteamRunning())
            {
                throw new InvalidOperationException("Steam must be closed before modifying shortcuts.vdf.");
            }

            return AddGameToSteamInternal(game, launcherPath, cacheDirectory);
        }

        private static string AddGameToSteamInternal(GameInfo game, string launcherPath, string? cacheDirectory)
        {
            string? configDirectory = FindSteamConfigDirectory();
            if (string.IsNullOrWhiteSpace(configDirectory))
            {
                throw new DirectoryNotFoundException("Could not find a Steam userdata config directory. Open Steam at least once on this device first.");
            }

            Directory.CreateDirectory(configDirectory);

            string shortcutsPath = Path.Combine(configDirectory, "shortcuts.vdf");
            string? iconPath = PrepareIcon(game, cacheDirectory);
            var root = File.Exists(shortcutsPath)
                ? ReadSteamShortcuts(shortcutsPath)
                : CreateEmptySteamShortcutsRoot();

            var shortcutsObject = EnsureObject(root, "shortcuts");
            string launchOptions = BuildSteamLaunchOptions(game.Name!);
            string quotedLauncherPath = QuoteSteamPath(launcherPath);
            string startDir = Path.GetDirectoryName(launcherPath) ?? AppContext.BaseDirectory;
            int appId = CalculateSteamShortcutAppId(quotedLauncherPath, game.Name!);

            var existingEntry = FindMatchingSteamEntry(shortcutsObject, game.Name!, launchOptions);
            bool updated = existingEntry != null;

            var shortcutEntry = existingEntry ?? new SteamObject();
            SetShortcutInt(shortcutEntry, "appid", appId);
            SetShortcutString(shortcutEntry, "appname", game.Name!);
            SetShortcutString(shortcutEntry, "exe", quotedLauncherPath);
            SetShortcutString(shortcutEntry, "StartDir", startDir);
            SetShortcutString(shortcutEntry, "icon", iconPath ?? string.Empty);
            SetShortcutString(shortcutEntry, "ShortcutPath", string.Empty);
            SetShortcutString(shortcutEntry, "LaunchOptions", launchOptions);
            SetShortcutInt(shortcutEntry, "IsHidden", 0);
            SetShortcutInt(shortcutEntry, "AllowDesktopConfig", 1);
            SetShortcutInt(shortcutEntry, "AllowOverlay", 1);
            SetShortcutInt(shortcutEntry, "OpenVR", 0);
            SetShortcutInt(shortcutEntry, "Devkit", 0);
            SetShortcutString(shortcutEntry, "DevkitGameID", string.Empty);
            SetShortcutInt(shortcutEntry, "DevkitOverrideAppID", 0);
            SetShortcutInt(shortcutEntry, "LastPlayTime", 0);
            SetShortcutString(shortcutEntry, "FlatpakAppID", string.Empty);
            SetShortcutString(shortcutEntry, "sortas", string.Empty);

            var tags = EnsureObject(shortcutEntry, "tags");
            tags.Properties.Clear();
            tags.Properties.Add(new KeyValuePair<string, SteamValue>("0", new SteamString(LauncherSteamTag)));

            if (!updated)
            {
                shortcutsObject.Properties.Add(new KeyValuePair<string, SteamValue>(
                    GetNextShortcutIndex(shortcutsObject).ToString(),
                    shortcutEntry));
            }

            NormalizeShortcutIndices(shortcutsObject);
            WriteSteamShortcuts(shortcutsPath, root);

            return updated
                ? "Updated the Steam shortcut. Restart Steam or return to Game Mode to refresh your library."
                : "Added the game to Steam. Restart Steam or return to Game Mode to refresh your library.";
        }

        private static string? PrepareIcon(GameInfo game, string? cacheDirectory)
        {
            if (string.IsNullOrWhiteSpace(cacheDirectory))
                return null;

            string iconsDir = Path.Combine(cacheDirectory, "ShortcutIcons");
            Directory.CreateDirectory(iconsDir);

            string? sourcePath = null;

            // Try cached default icon
            if (!string.IsNullOrEmpty(game.IconUrl) && File.Exists(game.IconUrl))
            {
                sourcePath = game.IconUrl;
            }
            // Download from URL if needed
            else if (!string.IsNullOrEmpty(game.DefaultIconUrl) &&
                     (game.DefaultIconUrl.StartsWith("http://") || game.DefaultIconUrl.StartsWith("https://")))
            {
                try
                {
                    var tempIconPath = Path.Combine(iconsDir, $"{game.FolderName}_temp.png");
                    using var client = new System.Net.Http.HttpClient();
                    var iconData = client.GetByteArrayAsync(game.DefaultIconUrl).Result;
                    File.WriteAllBytes(tempIconPath, iconData);
                    sourcePath = tempIconPath;
                }
                catch
                {
                    return null; // No icon available
                }
            }

            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                return null;

            // Convert to ICO for Windows
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string icoPath = Path.Combine(iconsDir, $"{game.FolderName}.ico");

                if (!File.Exists(icoPath))
                {
                    ConvertToIco(sourcePath, icoPath);
                }

                return icoPath;
            }

            // Linux can use PNG directly
            return sourcePath;
        }

        [SupportedOSPlatform("windows")]
        private static void ConvertToIco(string sourcePath, string icoPath)
        {
            try
            {
                using var sourceImage = Image.FromFile(sourcePath);
                using var resizedImage = new Bitmap(sourceImage, new Size(256, 256));
                using var stream = new FileStream(icoPath, FileMode.Create);

                // Write ICO header
                stream.WriteByte(0); stream.WriteByte(0); // Reserved
                stream.WriteByte(1); stream.WriteByte(0); // Type (1 = ICO)
                stream.WriteByte(1); stream.WriteByte(0); // Image count

                // Write ICONDIRENTRY
                stream.WriteByte(0); // Width (0 = 256)
                stream.WriteByte(0); // Height (0 = 256)
                stream.WriteByte(0); // Color palette
                stream.WriteByte(0); // Reserved
                stream.WriteByte(1); stream.WriteByte(0); // Color planes
                stream.WriteByte(32); stream.WriteByte(0); // Bits per pixel

                // Write placeholder for image size and offset
                long sizePos = stream.Position;
                stream.Write(new byte[8], 0, 8);

                // Write PNG data
                long imageStart = stream.Position;
                using (var ms = new MemoryStream())
                {
                    resizedImage.Save(ms, ImageFormat.Png);
                    var pngData = ms.ToArray();
                    stream.Write(pngData, 0, pngData.Length);
                }
                long imageEnd = stream.Position;

                // Go back and write size and offset
                stream.Seek(sizePos, SeekOrigin.Begin);
                int imageSize = (int)(imageEnd - imageStart);
                stream.Write(BitConverter.GetBytes(imageSize), 0, 4);
                stream.Write(BitConverter.GetBytes((int)imageStart), 0, 4);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to convert icon: {ex.Message}");
            }
        }

        [SupportedOSPlatform("windows")]
        private static void CreateWindowsShortcut(string desktopPath, string launcherPath, GameInfo game, string? iconPath)
        {
            // Escape game name for command line
            string gameName = game.Name!.Replace("\"", "");
            string shortcutPath = Path.Combine(desktopPath, $"{SanitizeFileName(game.Name!)}.lnk");

            string psScript = $@"
                $WshShell = New-Object -ComObject WScript.Shell
                $Shortcut = $WshShell.CreateShortcut('{shortcutPath}')
                $Shortcut.TargetPath = '{launcherPath}'
                $Shortcut.Arguments = '--run {gameName}'
                $Shortcut.WorkingDirectory = '{Path.GetDirectoryName(launcherPath)}'
                $Shortcut.Description = 'Launch {gameName} via N64 Recomp Launcher'
                {(iconPath != null ? $"$Shortcut.IconLocation = '{iconPath},0'" : "")}
                $Shortcut.Save()
                ";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript.Replace("\"", "`\"")}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit();
        }

        private static void CreateLinuxDesktopFile(string desktopPath, string launcherPath, GameInfo game, string? iconPath)
        {
            string safeGameName = game.Name!;
            string desktopFileName = $"{SanitizeFileName(safeGameName)}.desktop";
            string desktopFilePath = Path.Combine(desktopPath, desktopFileName);
            string escapedGameName = safeGameName.Replace("\"", "\\\"");

            string desktopFileContent = $@"[Desktop Entry]
                Type=Application
                Name={safeGameName}
                Exec=""{launcherPath}"" --run ""{escapedGameName}""
                Icon={iconPath ?? ""}
                Terminal=false
                Categories=Game;
                Comment=Launch {safeGameName} via N64 Recomp Launcher
                ";

            File.WriteAllText(desktopFilePath, desktopFileContent);

            // Make executable
            try
            {
                var chmod = Process.Start("chmod", $"+x \"{desktopFilePath}\"");
                chmod?.WaitForExit();
            }
            catch { }
        }

        private static string SanitizeFileName(string name)
        {
            string invalid = new string(Path.GetInvalidFileNameChars());
            foreach (char c in invalid)
            {
                name = name.Replace(c.ToString(), "");
            }
            return name;
        }

        private static string BuildSteamLaunchOptions(string gameName)
        {
            string escapedGameName = gameName.Replace("\"", string.Empty);
            return $"--run \"{escapedGameName}\"";
        }

        private static int CalculateSteamShortcutAppId(string quotedLauncherPath, string gameName)
        {
            string identity = $"{quotedLauncherPath}{gameName}";
            uint crc = ComputeCrc32(Encoding.UTF8.GetBytes(identity));

            crc |= 0x80000000;

            if (crc == 0x80000000)
                crc = 0x80000001;

            return unchecked((int)crc);
        }

        private static uint ComputeCrc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;

            foreach (byte value in data)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                {
                    bool lsbSet = (crc & 1) != 0;
                    crc >>= 1;
                    if (lsbSet)
                    {
                        crc ^= 0xEDB88320;
                    }
                }
            }

            return ~crc;
        }

        private static string QuoteSteamPath(string path)
        {
            return $"\"{path.Replace("\"", string.Empty)}\"";
        }

        public static bool IsSteamRunning()
        {
            try
            {
                return Process.GetProcessesByName("steam").Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsRunningUnderSteam()
        {
            return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SteamGameId")) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SteamAppId")) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SteamOverlayGameId")) ||
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SteamDeck"));
        }

        private static async Task WaitForSteamExitAsync()
        {
            while (IsSteamRunning())
            {
                await Task.Delay(1000).ConfigureAwait(false);
            }

            // Give Steam a moment to finish flushing its own config writes.
            await Task.Delay(1000).ConfigureAwait(false);
        }

        private static string? FindSteamConfigDirectory()
        {
            var candidates = GetSteamUserdataRoots()
                .Where(Directory.Exists)
                .SelectMany(root =>
                {
                    try
                    {
                        return Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly);
                    }
                    catch
                    {
                        return Array.Empty<string>();
                    }
                })
                .Where(path => Path.GetFileName(path).All(char.IsDigit))
                .Select(path => Path.Combine(path, "config"))
                .ToList();

            if (candidates.Count == 0)
                return null;

            return candidates
                .OrderByDescending(GetSteamConfigSortTime)
                .FirstOrDefault();
        }

        private static IEnumerable<string> GetSteamUserdataRoots()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                return new[]
                {
                    Path.Combine(programFilesX86, "Steam", "userdata"),
                    Path.Combine(programFiles, "Steam", "userdata"),
                    Path.Combine(localAppData, "Steam", "userdata")
                }.Distinct(StringComparer.OrdinalIgnoreCase);
            }

            return new[]
            {
                Path.Combine(userProfile, ".steam", "steam", "userdata"),
                Path.Combine(userProfile, ".local", "share", "Steam", "userdata"),
                Path.Combine(userProfile, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "userdata")
            }.Distinct(StringComparer.Ordinal);
        }

        private static DateTime GetSteamConfigSortTime(string configDirectory)
        {
            try
            {
                string shortcutsPath = Path.Combine(configDirectory, "shortcuts.vdf");
                if (File.Exists(shortcutsPath))
                    return File.GetLastWriteTimeUtc(shortcutsPath);

                return Directory.GetLastWriteTimeUtc(configDirectory);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static SteamObject CreateEmptySteamShortcutsRoot()
        {
            var root = new SteamObject();
            root.Properties.Add(new KeyValuePair<string, SteamValue>("shortcuts", new SteamObject()));
            return root;
        }

        private static SteamObject ReadSteamShortcuts(string shortcutsPath)
        {
            using var stream = new FileStream(shortcutsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            return ReadSteamObject(reader);
        }

        private static SteamObject ReadSteamObject(BinaryReader reader)
        {
            var result = new SteamObject();

            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                byte entryType = reader.ReadByte();
                if (entryType == 0x08)
                    break;

                string key = ReadNullTerminatedString(reader);
                SteamValue value = entryType switch
                {
                    0x00 => ReadSteamObject(reader),
                    0x01 => new SteamString(ReadNullTerminatedString(reader)),
                    0x02 => new SteamInt(reader.ReadInt32()),
                    _ => throw new InvalidDataException($"Unsupported Steam shortcut entry type: 0x{entryType:X2}")
                };

                result.Properties.Add(new KeyValuePair<string, SteamValue>(key, value));
            }

            return result;
        }

        private static string ReadNullTerminatedString(BinaryReader reader)
        {
            using var stream = new MemoryStream();

            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                byte next = reader.ReadByte();
                if (next == 0x00)
                    break;

                stream.WriteByte(next);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void WriteSteamShortcuts(string shortcutsPath, SteamObject rootValue)
        {
            using var stream = new FileStream(shortcutsPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
            WriteSteamObject(writer, rootValue);
            writer.Flush();
        }

        private static void WriteSteamObject(BinaryWriter writer, SteamObject value)
        {
            foreach (var property in value.Properties)
            {
                switch (property.Value)
                {
                    case SteamObject nestedObject:
                        writer.Write((byte)0x00);
                        WriteNullTerminatedString(writer, property.Key);
                        WriteSteamObject(writer, nestedObject);
                        break;
                    case SteamString stringValue:
                        writer.Write((byte)0x01);
                        WriteNullTerminatedString(writer, property.Key);
                        WriteNullTerminatedString(writer, stringValue.Value);
                        break;
                    case SteamInt intValue:
                        writer.Write((byte)0x02);
                        WriteNullTerminatedString(writer, property.Key);
                        writer.Write(intValue.Value);
                        break;
                    default:
                        throw new InvalidDataException($"Unsupported Steam shortcut value type: {property.Value.GetType().Name}");
                }
            }

            writer.Write((byte)0x08);
        }

        private static void WriteNullTerminatedString(BinaryWriter writer, string value)
        {
            writer.Write(Encoding.UTF8.GetBytes(value));
            writer.Write((byte)0x00);
        }

        private static SteamObject? FindMatchingSteamEntry(SteamObject shortcutsObject, string gameName, string launchOptions)
        {
            foreach (var property in shortcutsObject.Properties)
            {
                if (property.Value is not SteamObject shortcut)
                    continue;

                string? existingName = GetShortcutString(shortcut, "appname");
                string? existingOptions = GetShortcutString(shortcut, "LaunchOptions");
                var tags = TryGetObject(shortcut, "tags");
                bool hasLauncherTag = tags?.Properties.Any(p => p.Value is SteamString tag && string.Equals(tag.Value, LauncherSteamTag, StringComparison.Ordinal)) == true;

                if (string.Equals(existingName, gameName, StringComparison.Ordinal) &&
                    (string.Equals(existingOptions, launchOptions, StringComparison.Ordinal) || hasLauncherTag))
                {
                    return shortcut;
                }
            }

            return null;
        }

        private static int GetNextShortcutIndex(SteamObject shortcutsObject)
        {
            int maxIndex = -1;
            foreach (var property in shortcutsObject.Properties)
            {
                if (int.TryParse(property.Key, out int index) && index > maxIndex)
                {
                    maxIndex = index;
                }
            }

            return maxIndex + 1;
        }

        private static void NormalizeShortcutIndices(SteamObject shortcutsObject)
        {
            for (int i = 0; i < shortcutsObject.Properties.Count; i++)
            {
                shortcutsObject.Properties[i] =
                    new KeyValuePair<string, SteamValue>(i.ToString(), shortcutsObject.Properties[i].Value);
            }
        }

        private static string? GetShortcutString(SteamObject shortcut, string key)
        {
            return shortcut.Properties
                .FirstOrDefault(property => string.Equals(property.Key, key, StringComparison.Ordinal))
                .Value is SteamString stringValue
                ? stringValue.Value
                : null;
        }

        private static void SetShortcutString(SteamObject shortcut, string key, string value)
        {
            SetShortcutValue(shortcut, key, new SteamString(value));
        }

        private static void SetShortcutInt(SteamObject shortcut, string key, int value)
        {
            SetShortcutValue(shortcut, key, new SteamInt(value));
        }

        private static void SetShortcutValue(SteamObject shortcut, string key, SteamValue value)
        {
            for (int i = 0; i < shortcut.Properties.Count; i++)
            {
                if (string.Equals(shortcut.Properties[i].Key, key, StringComparison.Ordinal))
                {
                    shortcut.Properties[i] = new KeyValuePair<string, SteamValue>(key, value);
                    return;
                }
            }

            shortcut.Properties.Add(new KeyValuePair<string, SteamValue>(key, value));
        }

        private static SteamObject EnsureObject(SteamObject shortcut, string key)
        {
            var existing = TryGetObject(shortcut, key);
            if (existing != null)
                return existing;

            var created = new SteamObject();
            shortcut.Properties.Add(new KeyValuePair<string, SteamValue>(key, created));
            return created;
        }

        private static SteamObject? TryGetObject(SteamObject shortcut, string key)
        {
            return shortcut.Properties
                .FirstOrDefault(property => string.Equals(property.Key, key, StringComparison.Ordinal))
                .Value as SteamObject;
        }

        private abstract record SteamValue;
        private sealed record SteamString(string Value) : SteamValue;
        private sealed record SteamInt(int Value) : SteamValue;
        private sealed record SteamObject : SteamValue
        {
            public List<KeyValuePair<string, SteamValue>> Properties { get; } = [];
        }
    }
}
