using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;

namespace KeyTrigger
{
    public partial class App : Application
    {
        private static Mutex? _mutex;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        private static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static void BringExistingInstanceToFront()
        {
            try
            {
                using var current = Process.GetCurrentProcess();
                var list = Process.GetProcessesByName(current.ProcessName);
                if (list.Length <= 1 && !current.ProcessName.Equals("KeyTrigger", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var p in list) p.Dispose();
                    list = Process.GetProcessesByName("KeyTrigger");
                }

                foreach (var p in list)
                {
                    using (p)
                    {
                        if (p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero)
                        {
                            ShowWindow(p.MainWindowHandle, SW_RESTORE);
                            SetForegroundWindow(p.MainWindowHandle);
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Ignore permission or process lookup errors
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Single Instance Enforcement
            bool createdNew;
            try
            {
                _mutex = new Mutex(true, @"Global\KeyTrigger_SingleInstance_Mutex_39F18B92", out createdNew);
            }
            catch (UnauthorizedAccessException)
            {
                // Mutex is owned by an existing elevated instance
                createdNew = false;
            }

            if (!createdNew)
            {
                BringExistingInstanceToFront();
                Current.Shutdown();
                return;
            }

            // Always ensure application runs with Administrator privileges
            if (!IsAdministrator())
            {
                try
                {
                    var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = exePath,
                            UseShellExecute = true,
                            Verb = "runas",
                            Arguments = string.Join(" ", e.Args.Select(a => $"\"{a}\""))
                        };
                        Process.Start(psi);
                        Current.Shutdown();
                        return;
                    }
                }
                catch
                {
                    // User declined UAC prompt
                    MessageBox.Show(
                        "Key Trigger requires Administrator privileges to intercept inputs for games running with elevated permissions.\n\nPlease accept the Administrator prompt to launch.",
                        "Admin Privileges Required",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    Current.Shutdown();
                    return;
                }
            }

            DispatcherUnhandledException += (s, args) =>
            {
                WriteCrashLog(args.Exception.ToString());
                MessageBox.Show(args.Exception.ToString(), "Key Trigger Error", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                WriteCrashLog(args.ExceptionObject?.ToString());
            };

            // --diag writes keyboard hook health and focus changes to keytrigger-diag-*.log next to the exe
            if (e.Args.Any(a => a.Equals("--diag", StringComparison.OrdinalIgnoreCase)))
            {
                Core.Utils.DiagLog.Start(Path.Combine(AppContext.BaseDirectory, $"keytrigger-diag-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
            }

            // Avoid blocking full garbage collections, which would pause the keyboard hook thread while Windows waits on it
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

            base.OnStartup(e);
        }

        // Next to the exe: a relative path would land in the working directory, which can be System32 when elevated
        private static void WriteCrashLog(string? text)
        {
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), text ?? string.Empty);
            }
            catch
            {
                // Nothing more we can do while crashing
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch { }
                _mutex.Dispose();
                _mutex = null;
            }
            base.OnExit(e);
        }
    }
}
