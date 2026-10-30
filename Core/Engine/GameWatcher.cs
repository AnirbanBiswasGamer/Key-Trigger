using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using KeyTrigger.Core.Win32;
using KeyTrigger.Models;

namespace KeyTrigger.Core.Engine
{
    public class GameWatcher : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly ThrottleEngine _engine;
        private readonly DispatcherTimer _timer;
        private readonly NativeMethods.WinEventDelegate _winEventProc;

        private IntPtr _winEventHook = IntPtr.Zero;
        private bool _wasAnyGameRunning;
        private string _lastActiveGame = string.Empty;

        public event Action<string, bool>? GameStateChanged;

        public GameWatcher(AppSettings settings, ThrottleEngine engine)
        {
            _settings = settings;
            _engine = engine;

            // Keep reference rooted to prevent garbage collection
            _winEventProc = OnWinEvent;

            // Check every 500ms for fast game startup/exit detection with negligible CPU load
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += OnTimerTick;
        }

        public void Start()
        {
            _timer.Start();

            // Hook system-wide foreground window change events for instant 0ms focus switching
            if (_winEventHook == IntPtr.Zero)
            {
                try
                {
                    _winEventHook = NativeMethods.SetWinEventHook(
                        NativeMethods.EVENT_SYSTEM_FOREGROUND,
                        NativeMethods.EVENT_SYSTEM_FOREGROUND,
                        IntPtr.Zero,
                        _winEventProc,
                        0,
                        0,
                        NativeMethods.WINEVENT_OUTOFCONTEXT);
                }
                catch
                {
                    // Fall back to timer-only polling if hook installation fails
                    _winEventHook = IntPtr.Zero;
                }
            }
        }

        public void Stop()
        {
            _timer.Stop();

            if (_winEventHook != IntPtr.Zero)
            {
                try
                {
                    NativeMethods.UnhookWinEvent(_winEventHook);
                }
                catch { }
                _winEventHook = IntPtr.Zero;
            }
        }

        public static bool IsMatchingProcessName(string runningProc, string targetProc)
        {
            if (string.IsNullOrWhiteSpace(runningProc) || string.IsNullOrWhiteSpace(targetProc))
                return false;

            // 1. Exact match (case-insensitive)
            if (runningProc.Equals(targetProc, StringComparison.OrdinalIgnoreCase))
                return true;

            // 2. Codemasters Intel AVX optimization binaries:
            // GRID 2 and GRID Autosport ship with special 'grid2_avx.exe' / 'GRIDAutosport_avx.exe' for Intel CPUs
            if (runningProc.Equals($"{targetProc}_avx", StringComparison.OrdinalIgnoreCase) ||
                targetProc.Equals($"{runningProc}_avx", StringComparison.OrdinalIgnoreCase))
                return true;

            // 3. DirectX 11/12, 64-bit, or Unreal Shipping variants
            // e.g. "Grid_dx12", "Grid_dx11", "Grid_x64", "GRIDLegends_dx12"
            if (runningProc.StartsWith($"{targetProc}_", StringComparison.OrdinalIgnoreCase) ||
                runningProc.StartsWith($"{targetProc}-", StringComparison.OrdinalIgnoreCase) ||
                targetProc.StartsWith($"{runningProc}_", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        public bool IsGameRunning(AutoActivateGame game)
        {
            if (string.IsNullOrWhiteSpace(game.ExeFileName)) return false;
            string procName = Path.GetFileNameWithoutExtension(game.ExeFileName);
            try
            {
                // Standard match
                var procs = Process.GetProcessesByName(procName);
                if (procs.Length > 0) return true;

                // Check Intel AVX executable variant (e.g. grid2_avx)
                var avxProcs = Process.GetProcessesByName($"{procName}_avx");
                if (avxProcs.Length > 0) return true;

                // Check if target was registered with _avx and base is running
                if (procName.EndsWith("_avx", StringComparison.OrdinalIgnoreCase))
                {
                    string baseName = procName.Substring(0, procName.Length - 4);
                    var baseProcs = Process.GetProcessesByName(baseName);
                    if (baseProcs.Length > 0) return true;
                }

                // Check DirectX 12 variant (e.g. Grid_dx12)
                var dx12Procs = Process.GetProcessesByName($"{procName}_dx12");
                if (dx12Procs.Length > 0) return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        public bool IsGameWindowFocused(AutoActivateGame game)
        {
            if (string.IsNullOrWhiteSpace(game.ExeFileName)) return false;
            IntPtr fgWnd = NativeMethods.GetForegroundWindow();
            if (fgWnd == IntPtr.Zero) return false;

            NativeMethods.GetWindowThreadProcessId(fgWnd, out uint fgPid);
            if (fgPid == 0) return false;

            try
            {
                using var fgProc = Process.GetProcessById((int)fgPid);
                string targetProcName = Path.GetFileNameWithoutExtension(game.ExeFileName);
                return IsMatchingProcessName(fgProc.ProcessName, targetProcName);
            }
            catch
            {
                return false;
            }
        }

        public bool IsAnyMonitoredGameFocused(out AutoActivateGame? focusedGame)
        {
            focusedGame = null;
            IntPtr fgWnd = NativeMethods.GetForegroundWindow();
            if (fgWnd == IntPtr.Zero) return false;

            NativeMethods.GetWindowThreadProcessId(fgWnd, out uint fgPid);
            if (fgPid == 0) return false;

            try
            {
                using var fgProc = Process.GetProcessById((int)fgPid);
                string fgProcName = fgProc.ProcessName;

                foreach (var game in _settings.AutoActivateGames)
                {
                    if (!game.IsEnabled || string.IsNullOrWhiteSpace(game.ExeFileName)) continue;
                    string targetName = Path.GetFileNameWithoutExtension(game.ExeFileName);
                    if (IsMatchingProcessName(fgProcName, targetName))
                    {
                        focusedGame = game;
                        return true;
                    }
                }
            }
            catch
            {
                // Process terminated or inaccessible
            }

            return false;
        }

        public void CheckNow()
        {
            OnTimerTick(null, EventArgs.Empty);
        }

        private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND)
            {
                EvaluateFocus();
                _engine.OnForegroundWindowChanged();
            }
        }

        private void EvaluateFocus()
        {
            if (!_settings.EnableAutoActivation || !_settings.PauseWhenUnfocused || !_wasAnyGameRunning)
            {
                return;
            }

            bool isFocused = IsAnyMonitoredGameFocused(out _);
            if (isFocused && !_engine.IsActive)
            {
                _engine.IsActive = true;
            }
            else if (!isFocused && _engine.IsActive)
            {
                _engine.IsActive = false;
            }
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            if (!_settings.EnableAutoActivation || _settings.AutoActivateGames.Count == 0)
            {
                return;
            }

            bool anyRunning = false;
            string activeGameName = string.Empty;
            string linkedProfileId = string.Empty;

            foreach (var game in _settings.AutoActivateGames)
            {
                if (!game.IsEnabled) continue;

                if (IsGameRunning(game))
                {
                    anyRunning = true;
                    activeGameName = !string.IsNullOrWhiteSpace(game.Name) ? game.Name : game.ExeFileName;
                    linkedProfileId = game.LinkedProfileId;
                    break;
                }
            }

            if (anyRunning && !_wasAnyGameRunning)
            {
                // Game launched -> activate Key Trigger!
                _wasAnyGameRunning = true;
                _lastActiveGame = activeGameName;

                if (!string.IsNullOrEmpty(linkedProfileId))
                {
                    var matchedProfile = _settings.Profiles.Find(p => p.Id == linkedProfileId);
                    if (matchedProfile != null)
                    {
                        _engine.SwitchToProfile(matchedProfile);
                    }
                }

                if (_settings.PauseWhenUnfocused)
                {
                    bool isFocused = IsAnyMonitoredGameFocused(out _);
                    _engine.IsActive = isFocused;
                }
                else
                {
                    _engine.IsActive = true;
                }

                GameStateChanged?.Invoke(activeGameName, true);
            }
            else if (!anyRunning && _wasAnyGameRunning)
            {
                // Game closed -> deactivate Key Trigger!
                _wasAnyGameRunning = false;
                string closedGame = _lastActiveGame;
                _lastActiveGame = string.Empty;

                if (_engine.IsActive)
                {
                    _engine.IsActive = false;
                }

                GameStateChanged?.Invoke(closedGame, false);
            }
            else if (anyRunning && _wasAnyGameRunning)
            {
                // While game is running, continuously ensure focus policy is honored
                if (_settings.PauseWhenUnfocused)
                {
                    bool isFocused = IsAnyMonitoredGameFocused(out _);
                    if (isFocused && !_engine.IsActive)
                    {
                        _engine.IsActive = true;
                    }
                    else if (!isFocused && _engine.IsActive)
                    {
                        _engine.IsActive = false;
                    }
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}