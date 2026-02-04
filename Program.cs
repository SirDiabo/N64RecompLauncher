using Avalonia;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace N64RecompLauncher
{
    class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        private const int ATTACH_PARENT_PROCESS = -1;

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0].StartsWith("-"))
            {
                if (OperatingSystem.IsWindows())
                {
                    if (AttachConsole(ATTACH_PARENT_PROCESS))
                    {
                        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                    }
                }

                int exitCode = RunCLI(args);

                if (OperatingSystem.IsWindows())
                {
                    FreeConsole();
                }

                return exitCode;
            }

            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
            return 0;
        }

        private static int RunCLI(string[] args)
        {
            try
            {
                var cliHandler = new CLIHandler();
                return cliHandler.Execute(args).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}