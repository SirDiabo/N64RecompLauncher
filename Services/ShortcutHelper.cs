using N64RecompLauncher.Models;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace N64RecompLauncher.Services
{
    public static class ShortcutHelper
    {
        public static void CreateGameShortcut(GameInfo game, string launcherPath, string cacheDirectory)
        {
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string iconPath = PrepareIcon(game, cacheDirectory);

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

        private static string PrepareIcon(GameInfo game, string cacheDirectory)
        {
            string iconsDir = Path.Combine(cacheDirectory, "ShortcutIcons");
            Directory.CreateDirectory(iconsDir);

            string sourcePath = null;

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

        private static void CreateWindowsShortcut(string desktopPath, string launcherPath, GameInfo game, string iconPath)
        {
            // Escape game name for command line
            string gameName = game.Name.Replace("\"", "");
            string shortcutPath = Path.Combine(desktopPath, $"{SanitizeFileName(game.Name)}.lnk");

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

        private static void CreateLinuxDesktopFile(string desktopPath, string launcherPath, GameInfo game, string iconPath)
        {
            string desktopFileName = $"{SanitizeFileName(game.Name)}.desktop";
            string desktopFilePath = Path.Combine(desktopPath, desktopFileName);

            string desktopFileContent = $@"[Desktop Entry]
                Type=Application
                Name={game.Name}
                Exec=""{launcherPath}"" --run {game.Name}
                Icon={iconPath ?? ""}
                Terminal=false
                Categories=Game;
                Comment=Launch {game.Name} via N64 Recomp Launcher
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
    }
}