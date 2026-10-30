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
                var current = Process.GetCurrentProcess();
                var list = Process.GetProcessesByName(current.ProcessName);
                if (list.Length <= 1)
                {
                    list = Process.GetProcessesByName("KeyTrigger");
                }

                foreach (var p in list)
                {
                    if (p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(p.MainWindowHandle);
                        break;
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
                            Verb = "runas"
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
                File.WriteAllText("crash.log", args.Exception.ToString());
                MessageBox.Show(args.Exception.ToString(), "Key Trigger Error", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                File.WriteAllText("crash.log", args.ExceptionObject?.ToString());
            };

            base.OnStartup(e);
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
