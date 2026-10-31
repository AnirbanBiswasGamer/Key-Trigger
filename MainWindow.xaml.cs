using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KeyTrigger.Core.Engine;
using KeyTrigger.Core.Outputs;
using KeyTrigger.Core.Utils;
using KeyTrigger.Core.Win32;
using KeyTrigger.Models;

namespace KeyTrigger
{
    public partial class MainWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly ThrottleEngine _engine;
        private readonly GameWatcher _gameWatcher;

        // Which binding the next captured key goes to. One value, so two bind buttons can never both be armed.
        private enum CaptureTarget
        {
            None,
            Throttle,
            Brake,
            SteerLeft,
            SteerRight,
            ProfileHotkey,
            RemapButton
        }

        private CaptureTarget _captureTarget = CaptureTarget.None;
        private string? _capturingRemapButtonId;
        private string? _editingProfileId;
        private bool _isUpdatingUiFromSettings;
        private readonly DispatcherTimer _toastTimer;

        // Slider ticks and curve drags fire many times a second; serializing every profile and writing the file
        // on each one stalled the UI thread and churned the GC. They restart this one-shot timer instead.
        private readonly DispatcherTimer _saveTimer;

        private Style? _cardActionBtnStyle;
        private Style CardActionBtnStyle => _cardActionBtnStyle ??= (Style)FindResource("CardActionBtn");

        private Brush? _textSecondaryBrush;
        private Brush TextSecondaryBrush => _textSecondaryBrush ??= (Brush)FindResource("TextSecondary");

        public MainWindow()
        {
            _isUpdatingUiFromSettings = true;
            _settings = AppSettings.Load();
            _engine = new ThrottleEngine(_settings);
            _gameWatcher = new GameWatcher(_settings, _engine);

            InitializeComponent();

            // The live telemetry bars are only visible on Home, so don't tick while minimized
            StateChanged += (s, e) => UpdateTelemetryTimer();

            // Setup Toast Auto-fade Timer
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
            _toastTimer.Tick += (s, e) =>
            {
                ProfileToastBanner.Visibility = Visibility.Collapsed;
                _toastTimer.Stop();
            };

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += (s, e) => SaveNow();

            // Populate Response Curves dropdown
            PopulateCurveDropdown();

            // Load settings into UI
            LoadSettingsIntoControls();

            // Connect Engine events
            _engine.ActiveStateChanged += OnActiveStateChanged;
            OnActiveStateChanged(_engine.IsActive);
            _engine.ProfileActivated += OnProfileActivated;
            _engine.KeyboardHook.KeyCapturedForBinding += OnKeyCaptured;

            // Start the engine
            _engine.Start();

            // Game auto-detection watcher
            _gameWatcher.GameStateChanged += OnGameStateChanged;
            _gameWatcher.Start();

            // Setup telemetry and navigation
            InitTelemetryTimer();

            // Home's page setup also checks ViGEm availability; chips were rendered by LoadSettingsIntoControls
            Loaded += (s, e) => NavigateToPage("Home");
            Closing += MainWindow_Closing;
        }

        // Settings take effect live (the engine reads _settings directly); only the disk write is deferred
        private void RequestSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void FlushSave()
        {
            if (_saveTimer.IsEnabled) SaveNow();
        }

        private void SaveNow()
        {
            _saveTimer.Stop();
            _settings.Save();
        }

        private void ShowToast(string title, string details)
        {
            TxtToastProfileName.Text = title;
            TxtToastDetails.Text = details;
            ProfileToastBanner.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            EnableModernBackdrop();
        }

        private void EnableModernBackdrop()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                if (source?.CompositionTarget != null)
                {
                    source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
                }

                // Enable Windows 11 Immersive Dark Mode for window frame
                int darkMode = 1;
                NativeMethods.DwmSetWindowAttribute(
                    hwnd,
                    NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                    ref darkMode,
                    sizeof(int));

                // Enable Windows 11 Acrylic Backdrop
                int backdropType = NativeMethods.DWMSBT_TRANSIENTWINDOW;
                int hr = NativeMethods.DwmSetWindowAttribute(
                    hwnd,
                    NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE,
                    ref backdropType,
                    sizeof(int));

                if (hr != 0)
                {
                    backdropType = NativeMethods.DWMSBT_MAINWINDOW;
                    NativeMethods.DwmSetWindowAttribute(
                        hwnd,
                        NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE,
                        ref backdropType,
                        sizeof(int));
                }

                var margins = new NativeMethods.MARGINS
                {
                    cxLeftWidth = -1,
                    cxRightWidth = -1,
                    cyTopHeight = -1,
                    cyBottomHeight = -1
                };
                NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            }
            catch
            {
                // Graceful fallback on older Windows
            }
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _telemetryTimer?.Stop();
            _gameWatcher.Dispose();
            _engine.Dispose();
            SaveNow();
        }

        private void PopulateCurveDropdown()
        {
            CmbCurveType.Items.Clear();
            CmbBrakeCurveType.Items.Clear();
            if (CmbSteerCurveType != null) CmbSteerCurveType.Items.Clear();
            foreach (ResponseCurveType type in Enum.GetValues(typeof(ResponseCurveType)))
            {
                CmbCurveType.Items.Add(type);
                CmbBrakeCurveType.Items.Add(type);
                if (CmbSteerCurveType != null) CmbSteerCurveType.Items.Add(type);
            }
        }

        private void LoadSettingsIntoControls()
        {
            _isUpdatingUiFromSettings = true;

            var activeProfile = _settings.GetActiveProfile();

            // Key display
            TxtBoundKey.Text = $"[ {_settings.ThrottleKeyName} ]";
            TxtBoundBrakeKey.Text = $"[ {_settings.BrakeKeyName} ]";
            TxtBoundSteerLeftKey.Text = $"[ {_settings.SteerLeftKeyName} ]";
            TxtBoundSteerRightKey.Text = $"[ {_settings.SteerRightKeyName} ]";

            // Throttle Manual Timing Inputs
            TxtAttackInput.Text = $"{_settings.AttackTimeSeconds:0.00}";
            TxtAttackDisplay.Text = $"{_settings.AttackTimeSeconds:0.00} s";

            TxtReleaseInput.Text = $"{_settings.ReleaseTimeSeconds:0.00}";
            TxtReleaseDisplay.Text = $"{_settings.ReleaseTimeSeconds:0.00} s";

            // Throttle Curve
            CmbCurveType.SelectedItem = _settings.CurveType;

            // Throttle Limits
            SliderMinThrottle.Value = _settings.MinThrottle * 100.0;
            TxtMinThrottleDisplay.Text = $"{_settings.MinThrottle * 100.0:F0}%";

            SliderMaxThrottle.Value = _settings.MaxThrottle * 100.0;
            TxtMaxThrottleDisplay.Text = $"{_settings.MaxThrottle * 100.0:F0}%";

            // Brake Manual Timing Inputs
            TxtBrakeAttackInput.Text = $"{_settings.BrakeAttackTimeSeconds:0.00}";
            TxtBrakeAttackDisplay.Text = $"{_settings.BrakeAttackTimeSeconds:0.00} s";

            TxtBrakeReleaseInput.Text = $"{_settings.BrakeReleaseTimeSeconds:0.00}";
            TxtBrakeReleaseDisplay.Text = $"{_settings.BrakeReleaseTimeSeconds:0.00} s";

            CmbBrakeCurveType.SelectedItem = _settings.BrakeCurveType;

            SliderMinBrake.Value = _settings.MinBrake * 100.0;
            TxtMinBrakeDisplay.Text = $"{_settings.MinBrake * 100.0:F0}%";

            SliderMaxBrake.Value = _settings.MaxBrake * 100.0;
            TxtMaxBrakeDisplay.Text = $"{_settings.MaxBrake * 100.0:F0}%";

            // Steering Manual Timing Inputs (Unified)
            if (TxtSteerAttackInput != null) TxtSteerAttackInput.Text = $"{_settings.SteerAttackTimeSeconds:0.00}";
            if (TxtSteerAttackDisplay != null) TxtSteerAttackDisplay.Text = $"{_settings.SteerAttackTimeSeconds:0.00} s";

            if (TxtSteerReleaseInput != null) TxtSteerReleaseInput.Text = $"{_settings.SteerReleaseTimeSeconds:0.00}";
            if (TxtSteerReleaseDisplay != null) TxtSteerReleaseDisplay.Text = $"{_settings.SteerReleaseTimeSeconds:0.00} s";

            if (CmbSteerCurveType != null) CmbSteerCurveType.SelectedItem = _settings.SteerCurveType;

            if (SliderMinSteer != null) SliderMinSteer.Value = _settings.MinSteer * 100.0;
            if (TxtMinSteerDisplay != null) TxtMinSteerDisplay.Text = $"{_settings.MinSteer * 100.0:F0}%";

            if (SliderMaxSteer != null) SliderMaxSteer.Value = _settings.MaxSteer * 100.0;
            if (TxtMaxSteerDisplay != null) TxtMaxSteerDisplay.Text = $"{_settings.MaxSteer * 100.0:F0}%";

            UpdateAxisToggleButtons();

            // Profiles UI
            RenderProfileChips();
            UpdateActiveProfileControls();

            // Redraw active page curves or remaps immediately
            if (_currentPage == "Throttle") DrawCurveEditor();
            else if (_currentPage == "Brake") DrawBrakeCurveEditor();
            else if (_currentPage == "Steering") DrawSteerCurveEditor();
            else if (_currentPage == "Remap") RenderRemapList();

            _isUpdatingUiFromSettings = false;
        }

        private void CheckViGemStatus()
        {
            if (!_engine.GamepadOutput.IsAvailable)
            {
                _engine.GamepadOutput.Initialize();
            }

            bool isViGemAvailable = _engine.GamepadOutput.IsAvailable;

            if (BadgeViGemStatus != null && TxtViGemBadge != null && TxtViGemStatusMessage != null)
            {
                if (isViGemAvailable)
                {
                    int slot = _engine.GamepadOutput.UserIndex;
                    if (slot > 0)
                    {
                        BadgeViGemStatus.Background = Palette.WarnBadgeBg;
                        BadgeViGemStatus.BorderBrush = Palette.Amber500;
                        TxtViGemBadge.Text = $"PLAYER {slot + 1} (SLOT {slot})";
                        TxtViGemBadge.Foreground = Palette.Amber500;
                        TxtViGemStatusMessage.Text = $"Warning: Connected as Player {slot + 1}. GRID only listens to Player 1! Unplug other controllers.";
                    }
                    else
                    {
                        BadgeViGemStatus.Background = Palette.OkBadgeBg;
                        BadgeViGemStatus.BorderBrush = Palette.Emerald;
                        TxtViGemBadge.Text = "CONNECTED (PLAYER 1)";
                        TxtViGemBadge.Foreground = Palette.Emerald;
                        // Codemasters (EGO engine) games drive with one device at a time and ignore the pad while a keyboard preset is selected
                        TxtViGemStatusMessage.Text = "Connected as Player 1 (Virtual Xbox 360 Controller). In GRID / Codemasters games, set Options > Controls to the Xbox 360 Controller preset.";
                    }
                    if (TxtBtnViGemAction != null) TxtBtnViGemAction.Text = "Reinstall Driver";
                    if (BtnDownloadViGem != null) BtnDownloadViGem.Visibility = Visibility.Visible;
                }
                else
                {
                    BadgeViGemStatus.Background = Palette.ErrorBadgeBg;
                    BadgeViGemStatus.BorderBrush = Palette.Red;
                    TxtViGemBadge.Text = "NOT INSTALLED";
                    TxtViGemBadge.Foreground = Palette.Red;
                    TxtViGemStatusMessage.Text = "ViGEmBus driver required for virtual Xbox controller.";
                    if (TxtBtnViGemAction != null) TxtBtnViGemAction.Text = "Install Driver";
                    if (BtnDownloadViGem != null) BtnDownloadViGem.Visibility = Visibility.Visible;
                }
            }
        }

        private void RenderProfileCards()
        {
            // The cards are rebuilt on every edit; while the page is hidden that is wasted work, and
            // NavigateToPage("Profiles") rebuilds them on entry anyway
            if (PnlProfileCardsList == null || _currentPage != "Profiles") return;
            PnlProfileCardsList.Children.Clear();

            string activeId = _settings.ActiveProfileId;

            foreach (var profile in _settings.Profiles)
            {
                bool isActive = (profile.Id == activeId);
                bool isEditing = (profile.Id == _editingProfileId);

                var card = new Border
                {
                    Background = Palette.CardBg,
                    BorderBrush = isActive
                        ? Palette.Sky400
                        : (isEditing ? Palette.Sky400AB4 : Palette.ProfileCardBorder),
                    BorderThickness = new Thickness(isActive || isEditing ? 1.5 : 1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(18, 16, 18, 16),
                    Margin = new Thickness(0, 0, 0, 14)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var btnStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var pTarget = profile;

                if (isEditing)
                {
                    var editRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                    editRow.Children.Add(new System.Windows.Shapes.Path
                    {
                        Data = (Geometry)FindResource("IconEdit"),
                        Fill = Palette.Sky400,
                        Width = 14,
                        Height = 14,
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(0, 0, 8, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });

                    var txtRename = new TextBox
                    {
                        Text = profile.Name,
                        FontWeight = FontWeights.Bold,
                        FontSize = 14,
                        Foreground = Brushes.White,
                        Background = Palette.RenameBoxBg,
                        BorderBrush = Palette.Sky400,
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(8, 4, 8, 4),
                        MinWidth = 180,
                        MaxWidth = 280,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    Action saveAction = () =>
                    {
                        string newName = txtRename.Text.Trim();
                        if (!string.IsNullOrWhiteSpace(newName))
                        {
                            pTarget.Name = newName;
                            if (isActive)
                            {
                                _settings.SyncToActiveProfile();
                            }
                            SaveNow();
                            RenderProfileChips();
                            ShowToast(pTarget.Name, $"Profile Renamed to '{pTarget.Name}'");
                        }
                        _editingProfileId = null;
                        RenderProfileCards();
                    };

                    txtRename.KeyDown += (s, e) =>
                    {
                        if (e.Key == Key.Enter)
                        {
                            saveAction();
                            e.Handled = true;
                        }
                        else if (e.Key == Key.Escape)
                        {
                            _editingProfileId = null;
                            RenderProfileCards();
                            e.Handled = true;
                        }
                    };

                    txtRename.Loaded += (s, e) =>
                    {
                        txtRename.Focus();
                        txtRename.SelectAll();
                    };

                    editRow.Children.Add(txtRename);
                    infoStack.Children.Add(editRow);

                    var saveBtn = new Button
                    {
                        Content = "Save",
                        Style = CardActionBtnStyle,
                        Padding = new Thickness(16, 7, 16, 7),
                        Margin = new Thickness(0, 0, 8, 0),
                        Cursor = Cursors.Hand
                    };
                    saveBtn.Click += (s, e) => saveAction();
                    btnStack.Children.Add(saveBtn);

                    var cancelBtn = new Button
                    {
                        Content = "Cancel",
                        Style = CardActionBtnStyle,
                        Padding = new Thickness(14, 7, 14, 7),
                        Cursor = Cursors.Hand
                    };
                    cancelBtn.Click += (s, e) =>
                    {
                        _editingProfileId = null;
                        RenderProfileCards();
                    };
                    btnStack.Children.Add(cancelBtn);
                }
                else
                {
                    var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                    titleRow.Children.Add(new System.Windows.Shapes.Path
                    {
                        Data = (Geometry)FindResource("IconProfiles"),
                        Fill = Palette.Slate400,
                        Width = 14,
                        Height = 14,
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(0, 0, 8, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });

                    var txtTitle = new TextBlock
                    {
                        Text = profile.Name,
                        FontWeight = FontWeights.Bold,
                        FontSize = 14,
                        Foreground = Brushes.White,
                        VerticalAlignment = VerticalAlignment.Center,
                        Cursor = Cursors.Hand,
                        ToolTip = "Click to Rename"
                    };
                    txtTitle.MouseLeftButtonDown += (s, e) =>
                    {
                        _editingProfileId = pTarget.Id;
                        RenderProfileCards();
                    };
                    titleRow.Children.Add(txtTitle);

                    infoStack.Children.Add(titleRow);

                    var renameBtn = new Button
                    {
                        ToolTip = "Rename Profile",
                        Style = CardActionBtnStyle,
                        Padding = new Thickness(12, 7, 12, 7),
                        Margin = new Thickness(0, 0, 8, 0),
                        Cursor = Cursors.Hand
                    };
                    var renameContent = new StackPanel { Orientation = Orientation.Horizontal };
                    renameContent.Children.Add(new System.Windows.Shapes.Path
                    {
                        Data = (Geometry)FindResource("IconEdit"),
                        Fill = Palette.Slate400,
                        Width = 11,
                        Height = 11,
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(0, 0, 5, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    renameContent.Children.Add(new TextBlock { Text = "Rename", VerticalAlignment = VerticalAlignment.Center });
                    renameBtn.Content = renameContent;
                    renameBtn.Click += (s, e) =>
                    {
                        _editingProfileId = pTarget.Id;
                        RenderProfileCards();
                    };
                    btnStack.Children.Add(renameBtn);

                    if (!isActive)
                    {
                        var switchBtn = new Button
                        {
                            Content = "Switch to this",
                            Style = CardActionBtnStyle,
                            Padding = new Thickness(16, 7, 16, 7),
                            Cursor = Cursors.Hand
                        };
                        switchBtn.Click += (s, e) =>
                        {
                            _engine.SwitchToProfile(pTarget);
                        };
                        btnStack.Children.Add(switchBtn);

                        if (_settings.Profiles.Count > 1)
                        {
                            var delBtn = new Button
                            {
                                ToolTip = "Delete Profile",
                                Style = CardActionBtnStyle,
                                Padding = new Thickness(10, 7, 10, 7),
                                Margin = new Thickness(8, 0, 0, 0),
                                Cursor = Cursors.Hand,
                                Content = new System.Windows.Shapes.Path
                                {
                                    Data = (Geometry)FindResource("IconClose"),
                                    Fill = Palette.Slate400,
                                    Width = 9,
                                    Height = 9,
                                    Stretch = Stretch.Uniform
                                }
                            };
                            delBtn.Click += (s, e) => DeleteProfile(pTarget);
                            btnStack.Children.Add(delBtn);
                        }
                    }
                    else
                    {
                        var activePill = new Border
                        {
                            Background = Palette.ActivePillBg,
                            BorderBrush = Palette.Cyan,
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(6),
                            Padding = new Thickness(16, 7, 16, 7)
                        };
                        activePill.Child = new TextBlock { Text = "Active Profile", FontWeight = FontWeights.Bold, FontSize = 12, Foreground = Palette.Cyan };
                        btnStack.Children.Add(activePill);
                    }
                }

                var curveDesc = new TextBlock
                {
                    Text = $"Curves  —  Throttle: {profile.CurveType}   •   Brake: {profile.BrakeCurveType}   •   Steer: {profile.SteerCurveType}",
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Palette.Sky400,
                    Margin = new Thickness(0, 3, 0, 0)
                };
                infoStack.Children.Add(curveDesc);

                var timingDesc = new TextBlock
                {
                    Text = $"Timings —  Throttle: {profile.AttackTimeSeconds:F2}s / {profile.ReleaseTimeSeconds:F2}s   •   Brake: {profile.BrakeAttackTimeSeconds:F2}s / {profile.BrakeReleaseTimeSeconds:F2}s   •   Steer: {profile.SteerAttackTimeSeconds:F2}s / {profile.SteerReleaseTimeSeconds:F2}s",
                    FontSize = 11,
                    Foreground = Palette.Gray,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                infoStack.Children.Add(timingDesc);

                Grid.SetColumn(infoStack, 0);
                grid.Children.Add(infoStack);

                Grid.SetColumn(btnStack, 1);
                grid.Children.Add(btnStack);

                card.Child = grid;
                PnlProfileCardsList.Children.Add(card);
            }
        }

        #region Profile Management

        private void RenderProfileChips()
        {
            PnlProfileChips.Children.Clear();

            string activeId = _settings.ActiveProfileId;

            foreach (var profile in _settings.Profiles)
            {
                bool isActive = (profile.Id == activeId);

                var chipBorder = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 8, 8),
                    Cursor = Cursors.Hand,
                    Background = isActive
                        ? Palette.Sky400A40
                        : Palette.ChipBg,
                    BorderBrush = isActive
                        ? Palette.Sky400
                        : Palette.ChipBorder,
                    BorderThickness = new Thickness(isActive ? 1.5 : 1)
                };

                // Active profile highlighted with clean border and accent background (no GPU blur effects)

                var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                // Profile Name
                var nameBlock = new TextBlock
                {
                    Text = profile.Name,
                    FontWeight = isActive ? FontWeights.Bold : FontWeights.SemiBold,
                    FontSize = 11,
                    Foreground = isActive
                        ? Brushes.White
                        : Palette.Slate300,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                stack.Children.Add(nameBlock);

                // Hotkey Badge
                string hkText = string.IsNullOrEmpty(profile.HotkeyName) || profile.HotkeyName == "None"
                    ? "-"
                    : profile.HotkeyName;

                var hkBadge = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1),
                    Background = isActive
                        ? Palette.Sky400
                        : Palette.ChipBorder,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, (_settings.Profiles.Count > 1 ? 6 : 0), 0)
                };

                var hkTextBlock = new TextBlock
                {
                    Text = hkText,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = isActive
                        ? Palette.Slate900
                        : Palette.Slate400
                };
                hkBadge.Child = hkTextBlock;
                stack.Children.Add(hkBadge);

                // Delete 'X' button (if more than 1 profile)
                if (_settings.Profiles.Count > 1)
                {
                    var delBtn = new Button
                    {
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Padding = new Thickness(4, 0, 0, 0),
                        Cursor = Cursors.Hand,
                        ToolTip = "Delete Profile",
                        Content = new System.Windows.Shapes.Path
                        {
                            Data = (Geometry)FindResource("IconClose"),
                            Fill = Palette.Slate400,
                            Width = 8,
                            Height = 8,
                            Stretch = Stretch.Uniform
                        }
                    };

                    var currentProf = profile;
                    delBtn.Click += (s, e) =>
                    {
                        e.Handled = true;
                        DeleteProfile(currentProf);
                    };
                    stack.Children.Add(delBtn);
                }

                chipBorder.Child = stack;

                var targetProfile = profile;
                chipBorder.MouseLeftButtonDown += (s, e) =>
                {
                    if (_settings.ActiveProfileId != targetProfile.Id)
                    {
                        _engine.SwitchToProfile(targetProfile);
                    }
                };

                PnlProfileChips.Children.Add(chipBorder);
            }
        }

        private void UpdateActiveProfileControls()
        {
            var active = _settings.GetActiveProfile();

            string hkLabel = (active.HotkeyVkCode != 0) ? active.HotkeyName : "None";
            TxtProfileHotkeyDisplay.Text = $"[ {hkLabel} ] (Change)";
            BtnClearProfileHotkey.IsEnabled = (active.HotkeyVkCode != 0);
        }

        private void OnProfileActivated(ThrottleProfile profile)
        {
            Dispatcher.InvokeAsync(() =>
            {
                // Reloads sliders and readouts, the profile chips, and the visible page's curve or remap list
                LoadSettingsIntoControls();

                // Show Toast Notification
                string hkTag = (profile.HotkeyVkCode != 0) ? $" • Hotkey: [{profile.HotkeyName}]" : "";
                ShowToast(profile.Name, $"Throttle: {profile.CurveType} ({profile.AttackTimeSeconds:F2}s) • Brake: {profile.BrakeCurveType} • Steer: {profile.SteerCurveType}{hkTag}");

                RenderProfileCards();
            });
        }

        private void BtnCloseToast_Click(object sender, RoutedEventArgs e)
        {
            ProfileToastBanner.Visibility = Visibility.Collapsed;
            _toastTimer.Stop();
        }

        private void BtnAddProfile_Click(object sender, RoutedEventArgs e)
        {
            CommitAllInputsToSettings();
            _settings.SyncToActiveProfile();
            var active = _settings.GetActiveProfile();
            var newProfile = active.Clone();
            newProfile.Name = $"Profile {_settings.Profiles.Count + 1}";
            newProfile.HotkeyVkCode = 0;
            newProfile.HotkeyName = "None";

            _settings.Profiles.Add(newProfile);
            _editingProfileId = newProfile.Id;
            _engine.SwitchToProfile(newProfile);
        }

        private void DeleteProfile(ThrottleProfile profileToDelete)
        {
            if (_settings.Profiles.Count <= 1) return;
            if (_editingProfileId == profileToDelete.Id) _editingProfileId = null;

            int index = _settings.Profiles.IndexOf(profileToDelete);
            _settings.Profiles.Remove(profileToDelete);

            if (_settings.ActiveProfileId == profileToDelete.Id)
            {
                int nextIndex = Math.Clamp(index, 0, _settings.Profiles.Count - 1);
                _engine.SwitchToProfile(_settings.Profiles[nextIndex]);
            }
            else
            {
                SaveNow();
                RenderProfileChips();
                RenderProfileCards();
            }
        }

        private void BtnBindProfileHotkey_Click(object sender, RoutedEventArgs e)
        {
            if (!BeginCapture(CaptureTarget.ProfileHotkey)) return;

            TxtProfileHotkeyDisplay.Text = "[ PRESS ANY KEY... ]";
        }

        private void BtnClearProfileHotkey_Click(object sender, RoutedEventArgs e)
        {
            var active = _settings.GetActiveProfile();
            active.HotkeyVkCode = 0;
            active.HotkeyName = "None";
            SaveNow();
            UpdateActiveProfileControls();
            RenderProfileChips();
            RenderProfileCards();
        }

        private void CommitAllInputsToSettings()
        {
            if (_isUpdatingUiFromSettings) return;

            // Response Curves
            if (CmbCurveType?.SelectedItem is ResponseCurveType tc) _settings.CurveType = tc;
            if (CmbBrakeCurveType?.SelectedItem is ResponseCurveType bc) _settings.BrakeCurveType = bc;
            if (CmbSteerCurveType?.SelectedItem is ResponseCurveType sc) _settings.SteerCurveType = sc;

            // Throttle Timings
            if (TxtAttackInput != null && TryParseTiming(TxtAttackInput.Text, out double thAtk))
            {
                _settings.AttackTimeSeconds = Math.Clamp(Math.Round(thAtk, 2), 0.01, 10.0);
                if (TxtAttackDisplay != null) TxtAttackDisplay.Text = $"{_settings.AttackTimeSeconds:0.00} s";
            }
            if (TxtReleaseInput != null && TryParseTiming(TxtReleaseInput.Text, out double thRel))
            {
                _settings.ReleaseTimeSeconds = Math.Clamp(Math.Round(thRel, 2), 0.01, 10.0);
                if (TxtReleaseDisplay != null) TxtReleaseDisplay.Text = $"{_settings.ReleaseTimeSeconds:0.00} s";
            }

            // Brake Timings
            if (TxtBrakeAttackInput != null && TryParseTiming(TxtBrakeAttackInput.Text, out double brkAtk))
            {
                _settings.BrakeAttackTimeSeconds = Math.Clamp(Math.Round(brkAtk, 2), 0.01, 10.0);
                if (TxtBrakeAttackDisplay != null) TxtBrakeAttackDisplay.Text = $"{_settings.BrakeAttackTimeSeconds:0.00} s";
            }
            if (TxtBrakeReleaseInput != null && TryParseTiming(TxtBrakeReleaseInput.Text, out double brkRel))
            {
                _settings.BrakeReleaseTimeSeconds = Math.Clamp(Math.Round(brkRel, 2), 0.01, 10.0);
                if (TxtBrakeReleaseDisplay != null) TxtBrakeReleaseDisplay.Text = $"{_settings.BrakeReleaseTimeSeconds:0.00} s";
            }

            // Steering Timings
            if (TxtSteerAttackInput != null && TryParseTiming(TxtSteerAttackInput.Text, out double strAtk))
            {
                _settings.SteerAttackTimeSeconds = Math.Clamp(Math.Round(strAtk, 2), 0.01, 10.0);
                if (TxtSteerAttackDisplay != null) TxtSteerAttackDisplay.Text = $"{_settings.SteerAttackTimeSeconds:0.00} s";
            }
            if (TxtSteerReleaseInput != null && TryParseTiming(TxtSteerReleaseInput.Text, out double strRel))
            {
                _settings.SteerReleaseTimeSeconds = Math.Clamp(Math.Round(strRel, 2), 0.01, 10.0);
                if (TxtSteerReleaseDisplay != null) TxtSteerReleaseDisplay.Text = $"{_settings.SteerReleaseTimeSeconds:0.00} s";
            }

            // Limits
            if (SliderMinThrottle != null) _settings.MinThrottle = Math.Round(SliderMinThrottle.Value) / 100.0;
            if (SliderMaxThrottle != null) _settings.MaxThrottle = Math.Round(SliderMaxThrottle.Value) / 100.0;
            if (SliderMinBrake != null) _settings.MinBrake = Math.Round(SliderMinBrake.Value) / 100.0;
            if (SliderMaxBrake != null) _settings.MaxBrake = Math.Round(SliderMaxBrake.Value) / 100.0;
            if (SliderMinSteer != null) _settings.MinSteer = Math.Round(SliderMinSteer.Value) / 100.0;
            if (SliderMaxSteer != null) _settings.MaxSteer = Math.Round(SliderMaxSteer.Value) / 100.0;
        }

        // Accepts "0.25", "0.25s", "0.25 sec". The unit is stripped longest-first: removing every "s" first
        // turned "1.5sec" into "1.5ec", which then failed to parse.
        private static bool TryParseTiming(string text, out double val)
        {
            val = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string clean = text.Trim().ToLowerInvariant();
            if (clean.EndsWith("secs")) clean = clean.Substring(0, clean.Length - 4);
            else if (clean.EndsWith("sec")) clean = clean.Substring(0, clean.Length - 3);
            else if (clean.EndsWith("s")) clean = clean.Substring(0, clean.Length - 1);
            clean = clean.Trim();
            return double.TryParse(clean, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out val) ||
                   double.TryParse(clean, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out val);
        }

        private void SyncActiveProfileFromSliders()
        {
            if (_isUpdatingUiFromSettings) return;

            CommitAllInputsToSettings();
            _settings.SyncToActiveProfile();
            RequestSave();
            RenderProfileCards();
        }

        #endregion

        
        #region Individual Axis Enable/Disable Toggles

        private void BtnToggleThrottleAxis_Click(object sender, RoutedEventArgs e)
        {
            _settings.EnableThrottle = !_settings.EnableThrottle;
            UpdateAxisToggleButtons();
            SyncActiveProfileFromSliders();
        }

        private void BtnToggleBrakeAxis_Click(object sender, RoutedEventArgs e)
        {
            _settings.EnableBrake = !_settings.EnableBrake;
            UpdateAxisToggleButtons();
            SyncActiveProfileFromSliders();
        }

        private void BtnToggleSteeringAxis_Click(object sender, RoutedEventArgs e)
        {
            _settings.EnableSteering = !_settings.EnableSteering;
            UpdateAxisToggleButtons();
            SyncActiveProfileFromSliders();
        }

        private void UpdateAxisToggleButtons()
        {
            // Throttle
            bool th = _settings.EnableThrottle;
            if (BtnToggleThrottleAxis != null)
            {
                BtnToggleThrottleAxis.Background = th ? Palette.WhiteA35 : Palette.WhiteA10;
                BtnToggleThrottleAxis.BorderBrush = th ? Palette.WhiteA70 : Palette.WhiteA20;
            }
            if (DotThrottleAxis != null) DotThrottleAxis.Fill = th ? Brushes.White : Palette.WhiteA50;
            if (TxtThrottleAxisStatus != null) TxtThrottleAxisStatus.Text = th ? "RAMP: SMOOTH (ANALOG)" : "RAMP: DIRECT (DIGITAL 100%)";
            if (DotQuickThrottle != null) DotQuickThrottle.Fill = th ? Brushes.White : Palette.WhiteA50;
            if (TxtQuickThrottle != null) TxtQuickThrottle.Text = th ? "Throttle: Smooth" : "Throttle: Digital";

            // Brake
            bool br = _settings.EnableBrake;
            if (BtnToggleBrakeAxis != null)
            {
                BtnToggleBrakeAxis.Background = br ? Palette.WhiteA35 : Palette.WhiteA10;
                BtnToggleBrakeAxis.BorderBrush = br ? Palette.WhiteA70 : Palette.WhiteA20;
            }
            if (DotBrakeAxis != null) DotBrakeAxis.Fill = br ? Brushes.White : Palette.WhiteA50;
            if (TxtBrakeAxisStatus != null) TxtBrakeAxisStatus.Text = br ? "RAMP: SMOOTH (ANALOG)" : "RAMP: DIRECT (DIGITAL 100%)";
            if (DotQuickBrake != null) DotQuickBrake.Fill = br ? Brushes.White : Palette.WhiteA50;
            if (TxtQuickBrake != null) TxtQuickBrake.Text = br ? "Braking: Smooth" : "Braking: Digital";

            // Steering
            bool st = _settings.EnableSteering;
            if (BtnToggleSteeringAxis != null)
            {
                BtnToggleSteeringAxis.Background = st ? Palette.WhiteA35 : Palette.WhiteA10;
                BtnToggleSteeringAxis.BorderBrush = st ? Palette.WhiteA70 : Palette.WhiteA20;
            }
            if (DotSteeringAxis != null) DotSteeringAxis.Fill = st ? Brushes.White : Palette.WhiteA50;
            if (TxtSteeringAxisStatus != null) TxtSteeringAxisStatus.Text = st ? "RAMP: SMOOTH (ANALOG)" : "RAMP: DIRECT (DIGITAL 100%)";
            if (DotQuickSteering != null) DotQuickSteering.Fill = st ? Brushes.White : Palette.WhiteA50;
            if (TxtQuickSteering != null) TxtQuickSteering.Text = st ? "Steering: Smooth" : "Steering: Digital";
        }

        #endregion

        #region Engine State & Events

        private void OnActiveStateChanged(bool isActive)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (isActive)
                {
                    PillMasterToggle.Background = Palette.EmeraldA25;
                    PillMasterToggle.BorderBrush = Palette.EmeraldA60;
                    DotMasterStatus.Fill = Palette.Emerald;
                    TxtMasterStatus.Text = "ACTIVE";
                    TxtMasterStatus.Foreground = Brushes.White;
                }
                else
                {
                    PillMasterToggle.Background = Palette.WhiteA10;
                    PillMasterToggle.BorderBrush = Palette.WhiteA25;
                    DotMasterStatus.Fill = Palette.WhiteA50;
                    TxtMasterStatus.Text = "DISABLED";
                    TxtMasterStatus.Foreground = Palette.WhiteA80;
                }
            });
        }

        #endregion

        #region Page Navigation & Sub-Tabs

        private string _currentPage = "Home";
        private DispatcherTimer? _telemetryTimer;

        // "0%".."100%", so the 30 Hz readout never formats a string
        private static readonly string[] s_percentText = Enumerable.Range(0, 101).Select(i => $"{i}%").ToArray();

        // Last value shown per bar (exact) and per readout (whole percent); -1 forces the first update
        private double _shownThrottleBar = -1, _shownBrakeBar = -1, _shownSteerLeftBar = -1, _shownSteerRightBar = -1;
        private int _shownThrottle = -1;
        private int _shownBrake = -1;
        private int _shownSteerLeft = -1;
        private int _shownSteerRight = -1;

        private void InitTelemetryTimer()
        {
            // Started and stopped by UpdateTelemetryTimer: it only runs while Home is shown and not minimized
            _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _telemetryTimer.Tick += (s, e) => UpdateTelemetry();
        }

        private void UpdateTelemetryTimer()
        {
            if (_telemetryTimer == null) return;

            bool shouldRun = _currentPage == "Home" && WindowState != WindowState.Minimized;
            if (shouldRun == _telemetryTimer.IsEnabled) return;

            if (shouldRun)
            {
                UpdateTelemetry();
                _telemetryTimer.Start();
            }
            else
            {
                _telemetryTimer.Stop();
            }
        }

        private void UpdateTelemetry()
        {
            double th = _engine.LastEvaluatedThrottle;
            double br = _engine.LastEvaluatedBrake;
            double st = _engine.LastEvaluatedSteer;

            SetTelemetryBar(ProgLiveThrottle, TxtLiveThrottleVal, th * 100.0, ref _shownThrottleBar, ref _shownThrottle);
            SetTelemetryBar(ProgLiveBrake, TxtLiveBrakeVal, br * 100.0, ref _shownBrakeBar, ref _shownBrake);
            SetTelemetryBar(ProgLiveSteerLeft, TxtLiveSteerLeftVal, st < 0 ? -st * 100.0 : 0, ref _shownSteerLeftBar, ref _shownSteerLeft);
            SetTelemetryBar(ProgLiveSteerRight, TxtLiveSteerRightVal, st > 0 ? st * 100.0 : 0, ref _shownSteerRightBar, ref _shownSteerRight);
        }

        // The bar shows the exact value, so even a sub-1% input is visible (the readout next to it is whole percent).
        // Each control is only touched when its value changes, so an idle pad costs no layout or allocation.
        private static void SetTelemetryBar(ProgressBar? bar, TextBlock? text, double value, ref double shownBar, ref int shownPercent)
        {
            value = Math.Clamp(value, 0.0, 100.0);
            if (value != shownBar)
            {
                shownBar = value;
                if (bar != null) bar.Value = value;
            }

            int percent = (int)value;
            if (percent != shownPercent)
            {
                shownPercent = percent;
                if (text != null) text.Text = s_percentText[percent];
            }
        }

        private void NavBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string pageName)
            {
                NavigateToPage(pageName);
            }
        }

        private void GoToProfilesPage_Click(object sender, RoutedEventArgs e) => NavigateToPage("Profiles");

        private void NavigateToPage(string pageName)
        {
            if (_captureTarget != CaptureTarget.None)
            {
                var target = _captureTarget;
                _captureTarget = CaptureTarget.None;
                _engine.KeyboardHook.IsKeyCaptureActive = false;
                CancelCapture(target);
            }

            _currentPage = pageName;
            UpdateTelemetryTimer();

            if (PageHome != null) PageHome.Visibility = (pageName == "Home") ? Visibility.Visible : Visibility.Collapsed;
            if (PageThrottle != null) PageThrottle.Visibility = (pageName == "Throttle") ? Visibility.Visible : Visibility.Collapsed;
            if (PageBrake != null) PageBrake.Visibility = (pageName == "Brake") ? Visibility.Visible : Visibility.Collapsed;
            if (PageSteering != null) PageSteering.Visibility = (pageName == "Steering") ? Visibility.Visible : Visibility.Collapsed;
            if (PageRemap != null) PageRemap.Visibility = (pageName == "Remap") ? Visibility.Visible : Visibility.Collapsed;
            if (PageGames != null) PageGames.Visibility = (pageName == "Games") ? Visibility.Visible : Visibility.Collapsed;
            if (PageProfiles != null) PageProfiles.Visibility = (pageName == "Profiles") ? Visibility.Visible : Visibility.Collapsed;

            // Update nav buttons
            UpdateNavButtonState(NavBtnHome, pageName == "Home");
            UpdateNavButtonState(NavBtnThrottle, pageName == "Throttle");
            UpdateNavButtonState(NavBtnBrake, pageName == "Brake");
            UpdateNavButtonState(NavBtnSteering, pageName == "Steering");
            UpdateNavButtonState(NavBtnRemap, pageName == "Remap");
            UpdateNavButtonState(NavBtnGames, pageName == "Games");
            UpdateNavButtonState(NavBtnProfiles, pageName == "Profiles");

            switch (pageName)
            {
                case "Home":
                    CheckViGemStatus();
                    break;
                case "Throttle":
                    DrawCurveEditor();
                    Dispatcher.InvokeAsync(DrawCurveEditor, System.Windows.Threading.DispatcherPriority.Loaded);
                    break;
                case "Brake":
                    DrawBrakeCurveEditor();
                    Dispatcher.InvokeAsync(DrawBrakeCurveEditor, System.Windows.Threading.DispatcherPriority.Loaded);
                    break;
                case "Steering":
                    DrawSteerCurveEditor();
                    Dispatcher.InvokeAsync(DrawSteerCurveEditor, System.Windows.Threading.DispatcherPriority.Loaded);
                    break;
                case "Remap":
                    RenderRemapList();
                    break;
                case "Games":
                    RenderGamesList();
                    UpdateAutoActivationUi();
                    break;
                case "Profiles":
                    RenderProfileCards();
                    break;
            }
        }

        private void UpdateNavButtonState(Button? btn, bool isSelected)
        {
            if (btn == null) return;
            if (isSelected)
            {
                btn.Background = Palette.NavSelectedBg;
                btn.BorderBrush = Palette.NavSelectedBorder;
                btn.Foreground = Brushes.White;
            }
            else
            {
                btn.Background = Brushes.Transparent;
                btn.BorderBrush = Brushes.Transparent;
                btn.Foreground = TextSecondaryBrush;
            }
        }

        

        #endregion

        #region Auto-Detect Games

        private void OnGameStateChanged(string gameName, bool isLaunched)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (isLaunched)
                {
                    ShowToast("Game Auto-Detected", $"{gameName} launched → Key Trigger ACTIVATED!");
                }
                else
                {
                    ShowToast("Game Closed", "Game exited → Key Trigger DEACTIVATED");
                }

                if (_currentPage == "Games")
                {
                    RenderGamesList();
                }
            });
        }

        private void BtnToggleAutoActivation_Click(object sender, RoutedEventArgs e)
        {
            _settings.EnableAutoActivation = !_settings.EnableAutoActivation;
            SaveNow();
            UpdateAutoActivationUi();
            RenderGamesList();
            _gameWatcher.CheckNow();
        }

        private void BtnTogglePauseWhenUnfocused_Click(object sender, RoutedEventArgs e)
        {
            _settings.PauseWhenUnfocused = !_settings.PauseWhenUnfocused;
            SaveNow();
            UpdateAutoActivationUi();
            RenderGamesList();
            _gameWatcher.CheckNow();
        }

        private void UpdateAutoActivationUi()
        {
            if (BtnToggleAutoActivation == null || BadgeAutoActivationStatus == null || TxtAutoActivationBadge == null) return;

            bool enabled = _settings.EnableAutoActivation;
            BtnToggleAutoActivation.Content = enabled ? "ENABLED" : "DISABLED";
            BtnToggleAutoActivation.Foreground = enabled
                ? Palette.Green
                : Palette.Gray;

            BadgeAutoActivationStatus.Background = enabled
                ? Palette.GreenBadgeBg
                : Palette.OffBadgeBg;
            BadgeAutoActivationStatus.BorderBrush = enabled
                ? Palette.Green
                : Palette.OffBadgeBorder;

            TxtAutoActivationBadge.Text = enabled ? "ACTIVE SCANNING" : "PAUSED";
            TxtAutoActivationBadge.Foreground = enabled
                ? Palette.Green
                : Palette.Gray;

            if (BtnTogglePauseWhenUnfocused != null && BadgeFocusTrackingStatus != null && TxtFocusTrackingBadge != null)
            {
                bool focusEnabled = _settings.PauseWhenUnfocused;
                BtnTogglePauseWhenUnfocused.Content = focusEnabled ? "ENABLED" : "DISABLED";
                BtnTogglePauseWhenUnfocused.Foreground = focusEnabled
                    ? Palette.Green
                    : Palette.Gray;

                BadgeFocusTrackingStatus.Background = focusEnabled
                    ? Palette.GreenBadgeBg
                    : Palette.OffBadgeBg;
                BadgeFocusTrackingStatus.BorderBrush = focusEnabled
                    ? Palette.Green
                    : Palette.OffBadgeBorder;

                TxtFocusTrackingBadge.Text = focusEnabled ? "ACTIVE ONLY WHEN FOCUSED" : "DISABLED (ALWAYS ACTIVE)";
                TxtFocusTrackingBadge.Foreground = focusEnabled
                    ? Palette.Green
                    : Palette.Gray;
            }
        }

        private void BtnAddGame_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var ofd = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select Game Executable to Auto-Detect",
                    Filter = "Game Executable (*.exe)|*.exe|All Executables (*.exe)|*.exe",
                    CheckFileExists = true
                };

                if (ofd.ShowDialog() == true)
                {
                    string fullPath = ofd.FileName;
                    string fileName = System.IO.Path.GetFileName(fullPath);
                    string displayName = System.IO.Path.GetFileNameWithoutExtension(fullPath);

                    if (!_settings.AutoActivateGames.Any(g => g.ExeFileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                    {
                        _settings.AutoActivateGames.Add(new AutoActivateGame
                        {
                            Name = displayName,
                            ExePath = fullPath,
                            ExeFileName = fileName,
                            IsEnabled = true
                        });
                        SaveNow();
                        RenderGamesList();

                        ShowToast("Game Added", $"Key Trigger will auto-activate whenever {fileName} is running.");
                    }
                    else
                    {
                        MessageBox.Show($"'{fileName}' is already in your auto-detect list.", "Game Already Added", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error selecting file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RenderGamesList()
        {
            if (PnlGamesList == null) return;
            PnlGamesList.Children.Clear();

            if (_settings.AutoActivateGames.Count == 0)
            {
                var emptyCard = new Border
                {
                    Background = Palette.CardBg,
                    BorderBrush = Palette.CardBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(24, 28, 24, 28),
                    Margin = new Thickness(0, 0, 0, 14)
                };

                var emptyStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                emptyStack.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = (Geometry)FindResource("IconGamepad"),
                    Fill = Palette.Slate500,
                    Width = 32,
                    Height = 32,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 12)
                });
                emptyStack.Children.Add(new TextBlock
                {
                    Text = "No Games Configured Yet",
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 4)
                });
                emptyStack.Children.Add(new TextBlock
                {
                    Text = "Select any game executable (.exe). When that game launches, Key Trigger activates automatically!",
                    FontSize = 12,
                    Foreground = Palette.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 14)
                });

                var addBtn = new Button
                {
                    Style = CardActionBtnStyle,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Padding = new Thickness(18, 8, 18, 8)
                };
                var addBtnStack = new StackPanel { Orientation = Orientation.Horizontal };
                addBtnStack.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = (Geometry)FindResource("IconPlus"),
                    Fill = Brushes.White,
                    Width = 11,
                    Height = 11,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                addBtnStack.Children.Add(new TextBlock { Text = "Select Game Executable (.exe)", VerticalAlignment = VerticalAlignment.Center });
                addBtn.Content = addBtnStack;
                addBtn.Click += BtnAddGame_Click;
                emptyStack.Children.Add(addBtn);

                emptyCard.Child = emptyStack;
                PnlGamesList.Children.Add(emptyCard);
                return;
            }

            // One process snapshot for the whole list
            _gameWatcher.RefreshRunningProcesses();

            foreach (var game in _settings.AutoActivateGames)
            {
                bool isRunning = _gameWatcher.IsGameRunning(game);

                var card = new Border
                {
                    Background = Palette.CardBg,
                    BorderBrush = isRunning
                        ? Palette.Green
                        : Palette.CardBorder,
                    BorderThickness = new Thickness(isRunning ? 1.5 : 1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(18, 16, 18, 16),
                    Margin = new Thickness(0, 0, 0, 14)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

                var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                titleRow.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = (Geometry)FindResource("IconGamepad"),
                    Fill = Palette.Slate400,
                    Width = 14,
                    Height = 14,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                titleRow.Children.Add(new TextBlock
                {
                    Text = game.Name,
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center
                });

                if (isRunning)
                {
                    bool isFocused = _gameWatcher.IsGameWindowFocused(game);
                    var liveBadge = new Border
                    {
                        Background = isFocused
                            ? Palette.GreenBadgeBg
                            : Palette.AmberBadgeBg,
                        BorderBrush = isFocused
                            ? Palette.Green
                            : Palette.Amber500,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2, 6, 2),
                        Margin = new Thickness(10, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    string statusText = isFocused
                        ? "RUNNING (FOCUSED - ACTIVE)"
                        : (_settings.PauseWhenUnfocused ? "RUNNING (UNFOCUSED - PAUSED)" : "RUNNING (ACTIVE)");
                    Brush textBrush = isFocused
                        ? Palette.Emerald
                        : (_settings.PauseWhenUnfocused ? Palette.Amber500 : Palette.Emerald);

                    liveBadge.Child = new TextBlock
                    {
                        Text = statusText,
                        FontSize = 9,
                        FontWeight = FontWeights.Bold,
                        Foreground = textBrush
                    };
                    titleRow.Children.Add(liveBadge);
                }
                else
                {
                    var idleBadge = new Border
                    {
                        Background = Palette.IdleBadgeBg,
                        BorderBrush = Palette.IdleBadgeBorder,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2, 6, 2),
                        Margin = new Thickness(10, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    idleBadge.Child = new TextBlock { Text = "STANDBY", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Palette.Slate400 };
                    titleRow.Children.Add(idleBadge);
                }

                var fileBadge = new Border
                {
                    Background = Palette.Indigo400A30,
                    BorderBrush = Palette.Indigo400,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                fileBadge.Child = new TextBlock { Text = game.ExeFileName, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Palette.Indigo400 };
                titleRow.Children.Add(fileBadge);

                infoStack.Children.Add(titleRow);

                var pathBlock = new TextBlock
                {
                    Text = string.IsNullOrEmpty(game.ExePath) ? game.ExeFileName : game.ExePath,
                    FontSize = 11,
                    Foreground = Palette.Gray500,
                    Margin = new Thickness(0, 0, 0, 2),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = game.ExePath
                };
                infoStack.Children.Add(pathBlock);

                Grid.SetColumn(infoStack, 0);
                grid.Children.Add(infoStack);

                var btnStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                var toggleBtn = new Button
                {
                    Content = game.IsEnabled ? "Monitoring" : "Disabled",
                    Style = CardActionBtnStyle,
                    Padding = new Thickness(12, 6, 12, 6),
                    Foreground = game.IsEnabled
                        ? Palette.Sky400
                        : Palette.Slate500,
                    Cursor = Cursors.Hand
                };
                var g = game;
                toggleBtn.Click += (s, e) =>
                {
                    g.IsEnabled = !g.IsEnabled;
                    SaveNow();
                    RenderGamesList();
                };
                btnStack.Children.Add(toggleBtn);

                var delBtn = new Button
                {
                    ToolTip = "Remove Game",
                    Style = CardActionBtnStyle,
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(8, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    Content = new System.Windows.Shapes.Path
                    {
                        Data = (Geometry)FindResource("IconClose"),
                        Fill = Palette.Slate400,
                        Width = 9,
                        Height = 9,
                        Stretch = Stretch.Uniform
                    }
                };
                delBtn.Click += (s, e) =>
                {
                    _settings.AutoActivateGames.Remove(g);
                    SaveNow();
                    RenderGamesList();
                };
                btnStack.Children.Add(delBtn);

                Grid.SetColumn(btnStack, 1);
                grid.Children.Add(btnStack);

                card.Child = grid;
                PnlGamesList.Children.Add(card);
            }
        }

        #endregion

        #region Key Binding

        private void BtnBindThrottleKey_Click(object sender, RoutedEventArgs e)
        {
            if (!BeginCapture(CaptureTarget.Throttle)) return;

            TxtBoundKey.Text = "[ PRESS ANY KEY... ]";
            TxtBoundKey.Foreground = Palette.Amber;
        }

        private void BtnBindBrakeKey_Click(object sender, RoutedEventArgs e)
        {
            if (!BeginCapture(CaptureTarget.Brake)) return;

            TxtBoundBrakeKey.Text = "[ PRESS ANY KEY... ]";
            TxtBoundBrakeKey.Foreground = Palette.Amber;
        }

        private void BtnBindSteerLeftKey_Click(object sender, RoutedEventArgs e)
        {
            if (!BeginCapture(CaptureTarget.SteerLeft)) return;

            TxtBoundSteerLeftKey.Text = "[ PRESS ANY KEY... ]";
            TxtBoundSteerLeftKey.Foreground = Palette.Amber;
        }

        private void BtnBindSteerRightKey_Click(object sender, RoutedEventArgs e)
        {
            if (!BeginCapture(CaptureTarget.SteerRight)) return;

            TxtBoundSteerRightKey.Text = "[ PRESS ANY KEY... ]";
            TxtBoundSteerRightKey.Foreground = Palette.Amber;
        }

        // Arms the keyboard hook to hand the next key press to OnKeyCaptured. Ignored while another capture
        // is waiting for its key.
        private bool BeginCapture(CaptureTarget target)
        {
            // Clicking the same bind button again while it waits does nothing. Clicking a different one takes over
            // (the last click wins, as before), so a misclick never leaves a capture stuck: Escape is captured as a key.
            if (_captureTarget == target) return false;
            if (_captureTarget != CaptureTarget.None) CancelCapture(_captureTarget);

            // Remember what the label showed, so a later take-over can put it back exactly
            var label = CaptureLabel(target);
            _labelBeforeCapture = label?.Text;
            _labelBrushBeforeCapture = label?.Foreground;

            _captureTarget = target;
            _engine.KeyboardHook.IsKeyCaptureActive = true;
            return true;
        }

        private string? _labelBeforeCapture;
        private Brush? _labelBrushBeforeCapture;

        private TextBlock? CaptureLabel(CaptureTarget target) => target switch
        {
            CaptureTarget.ProfileHotkey => TxtProfileHotkeyDisplay,
            CaptureTarget.Throttle => TxtBoundKey,
            CaptureTarget.Brake => TxtBoundBrakeKey,
            CaptureTarget.SteerLeft => TxtBoundSteerLeftKey,
            CaptureTarget.SteerRight => TxtBoundSteerRightKey,
            _ => null
        };

        // Puts back the display of a capture that another bind button just took over
        private void CancelCapture(CaptureTarget target)
        {
            if (target == CaptureTarget.RemapButton)
            {
                _capturingRemapButtonId = null;
                RenderRemapList();
                return;
            }

            var label = CaptureLabel(target);
            if (label == null) return;
            if (_labelBeforeCapture != null) label.Text = _labelBeforeCapture;
            if (_labelBrushBeforeCapture != null) label.Foreground = _labelBrushBeforeCapture;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            if (_captureTarget != CaptureTarget.None)
            {
                Key key = (e.Key == Key.System) ? e.SystemKey : e.Key;
                int vkCode = KeyInterop.VirtualKeyFromKey(key);
                if (vkCode != 0)
                {
                    e.Handled = true;
                    OnKeyCaptured(vkCode);
                }
            }
        }

        private void OnKeyCaptured(int vkCode)
        {
            Dispatcher.InvokeAsync(() =>
            {
                var target = _captureTarget;
                if (target == CaptureTarget.None) return;

                _captureTarget = CaptureTarget.None;
                _engine.KeyboardHook.IsKeyCaptureActive = false;
                string keyName = KeyNameHelper.GetKeyName(vkCode);

                if (target == CaptureTarget.ProfileHotkey)
                {
                    var active = _settings.GetActiveProfile();
                    active.HotkeyVkCode = vkCode;
                    active.HotkeyName = keyName;
                    SaveNow();

                    UpdateActiveProfileControls();
                    RenderProfileChips();
                    RenderProfileCards();
                }
                else if (target == CaptureTarget.RemapButton)
                {
                    if (!string.IsNullOrEmpty(_capturingRemapButtonId))
                    {
                        string btnId = _capturingRemapButtonId;
                        _capturingRemapButtonId = null;

                        var mapping = _settings.ButtonMappings.FirstOrDefault(m => m.ButtonId.Equals(btnId, StringComparison.OrdinalIgnoreCase));
                        if (mapping != null)
                        {
                            mapping.VkCode = vkCode;
                            mapping.KeyName = keyName;
                            _settings.SyncToActiveProfile();
                            SaveNow();
                        }
                    }
                    else
                    {
                        _capturingRemapButtonId = null;
                    }

                    RenderRemapList();
                }
                else if (target == CaptureTarget.Throttle)
                {
                    _settings.ThrottleVkCode = vkCode;
                    _settings.ThrottleKeyName = keyName;
                    _settings.SyncToActiveProfile();
                    SaveNow();


                    TxtBoundKey.Text = $"[ {keyName} ]";
                    TxtBoundKey.Foreground = Palette.Cyan;
                }
                else if (target == CaptureTarget.Brake)
                {
                    _settings.BrakeVkCode = vkCode;
                    _settings.BrakeKeyName = keyName;
                    _settings.SyncToActiveProfile();
                    SaveNow();


                    TxtBoundBrakeKey.Text = $"[ {keyName} ]";
                    TxtBoundBrakeKey.Foreground = Palette.Coral;
                }
                else if (target == CaptureTarget.SteerLeft)
                {
                    _settings.SteerLeftVkCode = vkCode;
                    _settings.SteerLeftKeyName = keyName;
                    _settings.SyncToActiveProfile();
                    SaveNow();


                    TxtBoundSteerLeftKey.Text = $"[ {keyName} ]";
                    TxtBoundSteerLeftKey.Foreground = Palette.Blue;
                }
                else if (target == CaptureTarget.SteerRight)
                {
                    _settings.SteerRightVkCode = vkCode;
                    _settings.SteerRightKeyName = keyName;
                    _settings.SyncToActiveProfile();
                    SaveNow();


                    TxtBoundSteerRightKey.Text = $"[ {keyName} ]";
                    TxtBoundSteerRightKey.Foreground = Palette.Purple;
                }
            });
        }

        #endregion

        #region Controller Remap List & Actions

        private void RenderRemapList()
        {
            if (PnlRemapCategories == null) return;
            PnlRemapCategories.Children.Clear();

            _settings.EnsureDefaultButtonMappings();

            var categories = _settings.ButtonMappings
                .GroupBy(m => m.Category)
                .ToList();

            foreach (var group in categories)
            {
                var card = new Border
                {
                    Background = (Brush)FindResource("BgCard"),
                    BorderBrush = (Brush)FindResource("BorderBrushColor"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(18, 14, 18, 14),
                    Margin = new Thickness(0, 0, 0, 14)
                };

                var container = new StackPanel();

                // Category Title
                var catHeader = new TextBlock
                {
                    Text = group.Key,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    Margin = new Thickness(0, 0, 0, 12)
                };
                container.Children.Add(catHeader);

                // Buttons in this category
                foreach (var mapping in group)
                {
                    var rowGrid = new Grid
                    {
                        Margin = new Thickness(0, 4, 0, 4)
                    };
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Button badge (auto-fits)
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Description
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Bind & Clear buttons

                    string badgeLabel = mapping.ButtonId switch
                    {
                        "LS_Up" => "LS ↑",
                        "LS_Down" => "LS ↓",
                        "RS_Up" => "RS ↑",
                        "RS_Down" => "RS ↓",
                        "RS_Left" => "RS ←",
                        "RS_Right" => "RS →",
                        "Up" => "↑",
                        "Down" => "↓",
                        "Left" => "←",
                        "Right" => "→",
                        _ => mapping.ButtonId
                    };

                    // Button Badge
                    var badgeBorder = new Border
                    {
                        Background = Palette.WhiteA25,
                        BorderBrush = Palette.WhiteA45,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Height = 28,
                        MinWidth = 38,
                        Padding = new Thickness(8, 0, 8, 0),
                        Margin = new Thickness(0, 0, 10, 0),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var badgeText = new TextBlock
                    {
                        Text = badgeLabel,
                        FontSize = 11,
                        FontWeight = FontWeights.Bold,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    badgeBorder.Child = badgeText;
                    Grid.SetColumn(badgeBorder, 0);
                    rowGrid.Children.Add(badgeBorder);

                    // Details
                    var nameText = new TextBlock
                    {
                        Text = mapping.DisplayName,
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.White,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(nameText, 1);
                    rowGrid.Children.Add(nameText);

                    // Actions Stack (Rebind Button + Clear Button)
                    var actionsStack = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    bool isCapturingThis = (_capturingRemapButtonId == mapping.ButtonId);

                    var bindBtn = new Button
                    {
                        Style = CardActionBtnStyle,
                        MinWidth = 120,
                        Height = 32,
                        Cursor = Cursors.Hand,
                        Content = isCapturingThis ? "[ PRESS KEY... ]" : $"[ {mapping.KeyName} ]",
                        Foreground = isCapturingThis
                            ? Palette.Amber
                            : (mapping.VkCode != 0 ? Brushes.White : Palette.WhiteA80)
                    };

                    var currentMapping = mapping;
                    bindBtn.Click += (s, e) =>
                    {
                        // Clicking the currently waiting row again toggles it off / cancels capture.
                        // Clicking another row takes over the pending capture while keeping the hook armed.
                        if (_captureTarget == CaptureTarget.RemapButton)
                        {
                            if (_capturingRemapButtonId == currentMapping.ButtonId)
                            {
                                _captureTarget = CaptureTarget.None;
                                _capturingRemapButtonId = null;
                                _engine.KeyboardHook.IsKeyCaptureActive = false;
                                RenderRemapList();
                                return;
                            }

                            _capturingRemapButtonId = currentMapping.ButtonId;
                            RenderRemapList();
                            return;
                        }

                        if (!BeginCapture(CaptureTarget.RemapButton)) return;

                        _capturingRemapButtonId = currentMapping.ButtonId;
                        RenderRemapList();
                    };
                    actionsStack.Children.Add(bindBtn);

                    // Clear button (only if key is bound)
                    if (mapping.VkCode != 0)
                    {
                        var clearBtn = new Button
                        {
                            Style = CardActionBtnStyle,
                            Padding = new Thickness(10, 6, 10, 6),
                            Margin = new Thickness(6, 0, 0, 0),
                            Height = 32,
                            Cursor = Cursors.Hand,
                            ToolTip = "Clear Mapping (Set to None)",
                            Content = new System.Windows.Shapes.Path
                            {
                                Data = (Geometry)FindResource("IconClose"),
                                Fill = Palette.Slate400,
                                Width = 9,
                                Height = 9,
                                Stretch = Stretch.Uniform
                            }
                        };
                        clearBtn.Click += (s, e) =>
                        {
                            currentMapping.VkCode = 0;
                            currentMapping.KeyName = "None";
                            _settings.SyncToActiveProfile();
                            SaveNow();
                            RenderRemapList();
                        };
                        actionsStack.Children.Add(clearBtn);
                    }

                    Grid.SetColumn(actionsStack, 2);
                    rowGrid.Children.Add(actionsStack);

                    container.Children.Add(rowGrid);
                }

                card.Child = container;
                PnlRemapCategories.Children.Add(card);
            }
        }

        private void BtnResetRemapDefaults_Click(object sender, RoutedEventArgs e)
        {
            _settings.ButtonMappings = AppSettings.GetDefaultButtonMappings();
            _settings.SyncToActiveProfile();
            SaveNow();
            RenderRemapList();

            ShowToast("Defaults Restored", "Controller button mappings reset to standard racing configuration.");
        }

        private void BtnClearAllRemap_Click(object sender, RoutedEventArgs e)
        {
            foreach (var m in _settings.ButtonMappings)
            {
                m.VkCode = 0;
                m.KeyName = "None";
            }
            _settings.SyncToActiveProfile();
            SaveNow();
            RenderRemapList();

            ShowToast("Mappings Cleared", "All controller button keybindings unmapped.");
        }

        #endregion

        #region ViGEm Driver Controls

        private void BtnDownloadViGem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/nefarius/ViGEmBus/releases/latest",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not run installer: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #region Manual Time Selection Handlers (Seconds-Based Precision)

        // Throttle Attack
        private void SetAttackTime(double sec)
        {
            sec = Math.Clamp(Math.Round(sec, 2), 0.01, 10.0);
            _settings.AttackTimeSeconds = sec;
            TxtAttackInput.Text = $"{sec:0.00}";
            TxtAttackDisplay.Text = $"{sec:0.00} s";
            SyncActiveProfileFromSliders();
        }

        private void StepAttack_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag && double.TryParse(tag, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double delta))
            {
                SetAttackTime(_settings.AttackTimeSeconds + delta);
            }
        }

        private void PresetAttack_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && double.TryParse(tagStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double targetSec))
            {
                SetAttackTime(targetSec);
            }
        }

        private void TxtAttackInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (TryParseTiming(TxtAttackInput.Text, out double sec))
            {
                SetAttackTime(sec);
            }
            else
            {
                TxtAttackInput.Text = $"{_settings.AttackTimeSeconds:0.00}";
            }
        }

        private void TxtAttackInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                TxtAttackInput_LostFocus(sender, e);
                Keyboard.ClearFocus();
            }
        }

        // Throttle Release
        private void SetReleaseTime(double sec)
        {
            sec = Math.Clamp(Math.Round(sec, 2), 0.01, 10.0);
            _settings.ReleaseTimeSeconds = sec;
            TxtReleaseInput.Text = $"{sec:0.00}";
            TxtReleaseDisplay.Text = $"{sec:0.00} s";
            SyncActiveProfileFromSliders();
        }

        private void StepRelease_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag && double.TryParse(tag, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double delta))
            {
                SetReleaseTime(_settings.ReleaseTimeSeconds + delta);
            }
        }

        private void PresetRelease_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && double.TryParse(tagStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double targetSec))
            {
                SetReleaseTime(targetSec);
            }
        }

        private void TxtReleaseInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (TryParseTiming(TxtReleaseInput.Text, out double sec))
            {
                SetReleaseTime(sec);
            }
            else
            {
                TxtReleaseInput.Text = $"{_settings.ReleaseTimeSeconds:0.00}";
            }
        }

        private void TxtReleaseInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                TxtReleaseInput_LostFocus(sender, e);
                Keyboard.ClearFocus();
            }
        }

        // Brake Attack
        private void SetBrakeAttackTime(double sec)
        {
            sec = Math.Clamp(Math.Round(sec, 2), 0.01, 10.0);
            _settings.BrakeAttackTimeSeconds = sec;
            TxtBrakeAttackInput.Text = $"{sec:0.00}";
            TxtBrakeAttackDisplay.Text = $"{sec:0.00} s";
            SyncActiveProfileFromSliders();
        }

        private void StepBrakeAttack_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag && double.TryParse(tag, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double delta))
            {
                SetBrakeAttackTime(_settings.BrakeAttackTimeSeconds + delta);
            }
        }

        private void PresetBrakeAttack_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && double.TryParse(tagStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double targetSec))
            {
                SetBrakeAttackTime(targetSec);
            }
        }

        private void TxtBrakeAttackInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (TryParseTiming(TxtBrakeAttackInput.Text, out double sec))
            {
                SetBrakeAttackTime(sec);
            }
            else
            {
                TxtBrakeAttackInput.Text = $"{_settings.BrakeAttackTimeSeconds:0.00}";
            }
        }

        private void TxtBrakeAttackInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                TxtBrakeAttackInput_LostFocus(sender, e);
                Keyboard.ClearFocus();
            }
        }

        // Brake Release
        private void SetBrakeReleaseTime(double sec)
        {
            sec = Math.Clamp(Math.Round(sec, 2), 0.01, 10.0);
            _settings.BrakeReleaseTimeSeconds = sec;
            TxtBrakeReleaseInput.Text = $"{sec:0.00}";
            TxtBrakeReleaseDisplay.Text = $"{sec:0.00} s";
            SyncActiveProfileFromSliders();
        }

        private void StepBrakeRelease_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag && double.TryParse(tag, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double delta))
            {
                SetBrakeReleaseTime(_settings.BrakeReleaseTimeSeconds + delta);
            }
        }

        private void PresetBrakeRelease_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && double.TryParse(tagStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double targetSec))
            {
                SetBrakeReleaseTime(targetSec);
            }
        }

        private void TxtBrakeReleaseInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (TryParseTiming(TxtBrakeReleaseInput.Text, out double sec))
            {
                SetBrakeReleaseTime(sec);
            }
            else
            {
                TxtBrakeReleaseInput.Text = $"{_settings.BrakeReleaseTimeSeconds:0.00}";
            }
        }

        private void TxtBrakeReleaseInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                TxtBrakeReleaseInput_LostFocus(sender, e);
                Keyboard.ClearFocus();
            }
        }

        // Unified Steering Attack
        private void SetSteerAttackTime(double sec)
        {
            sec = Math.Clamp(Math.Round(sec, 2), 0.01, 10.0);
            _settings.SteerAttackTimeSeconds = sec;
            if (TxtSteerAttackInput != null) TxtSteerAttackInput.Text = $"{sec:0.00}";
            if (TxtSteerAttackDisplay != null) TxtSteerAttackDisplay.Text = $"{sec:0.00} s";
            SyncActiveProfileFromSliders();
        }

        private void StepSteerAttack_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag && double.TryParse(tag, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double delta))
            {
                SetSteerAttackTime(_settings.SteerAttackTimeSeconds + delta);
            }
        }

        private void PresetSteerAttack_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && double.TryParse(tagStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double targetSec))
            {
                SetSteerAttackTime(targetSec);
            }
        }

        private void TxtSteerAttackInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (TxtSteerAttackInput == null) return;
            if (TryParseTiming(TxtSteerAttackInput.Text, out double sec))
            {
                SetSteerAttackTime(sec);
            }
            else
            {
                TxtSteerAttackInput.Text = $"{_settings.SteerAttackTimeSeconds:0.00}";
            }
        }

        private void TxtSteerAttackInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                TxtSteerAttackInput_LostFocus(sender, e);
                Keyboard.ClearFocus();
            }
        }

        // Unified Steering Release
        private void SetSteerReleaseTime(double sec)
        {
            sec = Math.Clamp(Math.Round(sec, 2), 0.01, 10.0);
            _settings.SteerReleaseTimeSeconds = sec;
            if (TxtSteerReleaseInput != null) TxtSteerReleaseInput.Text = $"{sec:0.00}";
            if (TxtSteerReleaseDisplay != null) TxtSteerReleaseDisplay.Text = $"{sec:0.00} s";
            SyncActiveProfileFromSliders();
        }

        private void StepSteerRelease_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag && double.TryParse(tag, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double delta))
            {
                SetSteerReleaseTime(_settings.SteerReleaseTimeSeconds + delta);
            }
        }

        private void PresetSteerRelease_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && double.TryParse(tagStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double targetSec))
            {
                SetSteerReleaseTime(targetSec);
            }
        }

        private void TxtSteerReleaseInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (TxtSteerReleaseInput == null) return;
            if (TryParseTiming(TxtSteerReleaseInput.Text, out double sec))
            {
                SetSteerReleaseTime(sec);
            }
            else
            {
                TxtSteerReleaseInput.Text = $"{_settings.SteerReleaseTimeSeconds:0.00}";
            }
        }

        private void TxtSteerReleaseInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                TxtSteerReleaseInput_LostFocus(sender, e);
                Keyboard.ClearFocus();
            }
        }

        #endregion

        private void CmbCurveType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUiFromSettings) return;

            if (CmbCurveType.SelectedItem is ResponseCurveType curveType)
            {
                _settings.CurveType = curveType;
                SyncActiveProfileFromSliders();
                DrawCurveEditor();
            }
        }

        private void SliderMinThrottle_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUiFromSettings) return;

            double val = Math.Round(SliderMinThrottle.Value);
            _settings.MinThrottle = val / 100.0;
            TxtMinThrottleDisplay.Text = $"{val:F0}%";
            SyncActiveProfileFromSliders();
        }

        private void SliderMaxThrottle_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUiFromSettings) return;

            double val = Math.Round(SliderMaxThrottle.Value);
            _settings.MaxThrottle = val / 100.0;
            TxtMaxThrottleDisplay.Text = $"{val:F0}%";
            SyncActiveProfileFromSliders();
        }

        private void CmbBrakeCurveType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUiFromSettings) return;

            if (CmbBrakeCurveType.SelectedItem is ResponseCurveType curveType)
            {
                _settings.BrakeCurveType = curveType;
                SyncActiveProfileFromSliders();
                DrawBrakeCurveEditor();
            }
        }

        private void SliderMinBrake_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUiFromSettings) return;

            double val = Math.Round(SliderMinBrake.Value);
            _settings.MinBrake = val / 100.0;
            TxtMinBrakeDisplay.Text = $"{val:F0}%";
            SyncActiveProfileFromSliders();
        }

        private void SliderMaxBrake_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUiFromSettings) return;

            double val = Math.Round(SliderMaxBrake.Value);
            _settings.MaxBrake = val / 100.0;
            TxtMaxBrakeDisplay.Text = $"{val:F0}%";
            SyncActiveProfileFromSliders();
        }

        

        private void CmbSteerCurveType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUiFromSettings) return;

            if (CmbSteerCurveType != null && CmbSteerCurveType.SelectedItem is ResponseCurveType curveType)
            {
                _settings.SteerCurveType = curveType;
                SyncActiveProfileFromSliders();
                DrawSteerCurveEditor();
            }
        }

        private void SliderMinSteer_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUiFromSettings) return;

            double val = Math.Round(SliderMinSteer.Value);
            _settings.MinSteer = val / 100.0;
            if (TxtMinSteerDisplay != null) TxtMinSteerDisplay.Text = $"{val:F0}%";
            SyncActiveProfileFromSliders();
        }

        private void SliderMaxSteer_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUiFromSettings) return;

            double val = Math.Round(SliderMaxSteer.Value);
            _settings.MaxSteer = val / 100.0;
            if (TxtMaxSteerDisplay != null) TxtMaxSteerDisplay.Text = $"{val:F0}%";
            SyncActiveProfileFromSliders();
        }

        private void BtnMasterToggle_Click(object sender, RoutedEventArgs e)
        {
            _engine.IsActive = !_engine.IsActive;
        }

        #endregion

        #region Interactive Custom Curve Editor

        private int _draggingHandle = 0; // 0 = none, 1 = P1, 2 = P2

        private void CurveEditorCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawCurveEditor();
        }

        private void DrawCurveEditor()
        {
            if (CurveEditorCanvas == null) return;
            if (PageThrottle == null || PageThrottle.Visibility != Visibility.Visible) return;

            double width = CurveEditorCanvas.ActualWidth;
            double height = CurveEditorCanvas.ActualHeight;
            if (width < 30 || height < 30) return;

            CurveEditorCanvas.Children.Clear();

            // Subtle background grid lines (25%, 50%, 75%)
            for (int i = 1; i < 4; i++)
            {
                double ratio = i / 4.0;
                var hLine = new Line
                {
                    X1 = 0, Y1 = height * (1.0 - ratio),
                    X2 = width, Y2 = height * (1.0 - ratio),
                    Stroke = Palette.WhiteA1C,
                    StrokeDashArray = Palette.Dash3
                };
                CurveEditorCanvas.Children.Add(hLine);

                var vLine = new Line
                {
                    X1 = width * ratio, Y1 = 0,
                    X2 = width * ratio, Y2 = height,
                    Stroke = Palette.WhiteA1C,
                    StrokeDashArray = Palette.Dash3
                };
                CurveEditorCanvas.Children.Add(vLine);
            }

            // Curve polyline
            var polyline = new Polyline
            {
                Stroke = Brushes.White,
                StrokeThickness = 2.5
            };

            const int samples = 50;
            for (int i = 0; i <= samples; i++)
            {
                double t = (double)i / samples;
                double val = ResponseCurves.Evaluate(_settings.CurveType, t, _settings.CustomP1X, _settings.CustomP1Y, _settings.CustomP2X, _settings.CustomP2Y);
                double x = t * width;
                double y = (1.0 - val) * height;
                polyline.Points.Add(new Point(x, y));
            }
            CurveEditorCanvas.Children.Add(polyline);

            // Control Handle Positions
            double p1X = _settings.CustomP1X * width;
            double p1Y = (1.0 - _settings.CustomP1Y) * height;

            double p2X = _settings.CustomP2X * width;
            double p2Y = (1.0 - _settings.CustomP2Y) * height;

            // Dashed Tangent Lines
            var tang1 = new Line
            {
                X1 = 0, Y1 = height,
                X2 = p1X, Y2 = p1Y,
                Stroke = Palette.Sky400A78,
                StrokeDashArray = Palette.Dash3,
                StrokeThickness = 1.2
            };
            CurveEditorCanvas.Children.Add(tang1);

            var tang2 = new Line
            {
                X1 = width, Y1 = 0,
                X2 = p2X, Y2 = p2Y,
                Stroke = Palette.Indigo400A78,
                StrokeDashArray = Palette.Dash3,
                StrokeThickness = 1.2
            };
            CurveEditorCanvas.Children.Add(tang2);

            // Handle 1 Dot (P1 - Cyan Launch Bite)
            var dot1 = new Ellipse
            {
                Width = 14, Height = 14,
                Fill = Palette.Sky400,
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(dot1, p1X - 7);
            Canvas.SetTop(dot1, p1Y - 7);
            CurveEditorCanvas.Children.Add(dot1);

            // Handle 2 Dot (P2 - Emerald Top End)
            var dot2 = new Ellipse
            {
                Width = 14, Height = 14,
                Fill = Palette.Indigo400,
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(dot2, p2X - 7);
            Canvas.SetTop(dot2, p2Y - 7);
            CurveEditorCanvas.Children.Add(dot2);
        }

        private void CurveEditorCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            Point pos = e.GetPosition(CurveEditorCanvas);
            double width = CurveEditorCanvas.ActualWidth;
            double height = CurveEditorCanvas.ActualHeight;

            double p1X = _settings.CustomP1X * width;
            double p1Y = (1.0 - _settings.CustomP1Y) * height;

            double p2X = _settings.CustomP2X * width;
            double p2Y = (1.0 - _settings.CustomP2Y) * height;

            double d1 = Math.Sqrt(Math.Pow(pos.X - p1X, 2) + Math.Pow(pos.Y - p1Y, 2));
            double d2 = Math.Sqrt(Math.Pow(pos.X - p2X, 2) + Math.Pow(pos.Y - p2Y, 2));

            _draggingHandle = (d1 <= d2) ? 1 : 2;
            CurveEditorCanvas.CaptureMouse();

            if (_settings.CurveType != ResponseCurveType.Custom)
            {
                _settings.CurveType = ResponseCurveType.Custom;
                CmbCurveType.SelectedItem = ResponseCurveType.Custom;
            }

            UpdateDraggedHandle(pos, width, height);
        }

        private void CurveEditorCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingHandle == 0 || e.LeftButton != MouseButtonState.Pressed) return;

            Point pos = e.GetPosition(CurveEditorCanvas);
            double width = CurveEditorCanvas.ActualWidth;
            double height = CurveEditorCanvas.ActualHeight;

            UpdateDraggedHandle(pos, width, height);
        }

        private void UpdateDraggedHandle(Point pos, double width, double height)
        {
            double normX = Math.Clamp(pos.X / width, 0.02, 0.98);
            double normY = Math.Clamp(1.0 - (pos.Y / height), 0.0, 1.0);

            if (_draggingHandle == 1)
            {
                _settings.CustomP1X = Math.Round(normX, 2);
                _settings.CustomP1Y = Math.Round(normY, 2);
            }
            else if (_draggingHandle == 2)
            {
                _settings.CustomP2X = Math.Round(normX, 2);
                _settings.CustomP2Y = Math.Round(normY, 2);
            }

            _settings.SyncToActiveProfile();
            RequestSave();
            DrawCurveEditor();
        }

        private void CurveEditorCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingHandle = 0;
            CurveEditorCanvas.ReleaseMouseCapture();
            FlushSave();
        }

        // Also covers a drag that ends without a MouseUp on the canvas (focus loss, Alt+Tab)
        private void CurveEditorCanvas_LostMouseCapture(object sender, MouseEventArgs e)
        {
            FlushSave();
        }

        private void BtnUniversalSave_Click(object sender, RoutedEventArgs e)
        {
            // Sync all UI inputs, sliders, curves, timings, limits and keymaps into the active profile
            // (this also refreshes the profile cards)
            SyncActiveProfileFromSliders();

            // Persist to settings.json now rather than after the save delay
            SaveNow();

            RenderProfileChips();

            // Toast feedback
            var active = _settings.GetActiveProfile();
            ShowToast(active.Name, $"Saved — Throttle: {active.CurveType} • Brake: {active.BrakeCurveType} • Steer: {active.SteerCurveType}");
        }

        #endregion

        #region Interactive Custom Curve Editor (Brake)

        private int _draggingBrakeHandle = 0; // 0 = none, 1 = P1, 2 = P2

        private void BrakeCurveEditorCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawBrakeCurveEditor();
        }

        private void DrawBrakeCurveEditor()
        {
            if (BrakeCurveEditorCanvas == null) return;
            if (PageBrake == null || PageBrake.Visibility != Visibility.Visible) return;

            double width = BrakeCurveEditorCanvas.ActualWidth;
            double height = BrakeCurveEditorCanvas.ActualHeight;
            if (width < 30 || height < 30) return;

            BrakeCurveEditorCanvas.Children.Clear();

            // Subtle background grid lines (25%, 50%, 75%)
            for (int i = 1; i < 4; i++)
            {
                double ratio = i / 4.0;
                var hLine = new Line
                {
                    X1 = 0, Y1 = height * (1.0 - ratio),
                    X2 = width, Y2 = height * (1.0 - ratio),
                    Stroke = Palette.WhiteA1C,
                    StrokeDashArray = Palette.Dash3
                };
                BrakeCurveEditorCanvas.Children.Add(hLine);

                var vLine = new Line
                {
                    X1 = width * ratio, Y1 = 0,
                    X2 = width * ratio, Y2 = height,
                    Stroke = Palette.WhiteA1C,
                    StrokeDashArray = Palette.Dash3
                };
                BrakeCurveEditorCanvas.Children.Add(vLine);
            }

            // Curve polyline (Red/Coral for Brake)
            var polyline = new Polyline
            {
                Stroke = Palette.Coral,
                StrokeThickness = 2.5
            };

            const int samples = 50;
            for (int i = 0; i <= samples; i++)
            {
                double t = (double)i / samples;
                double val = ResponseCurves.Evaluate(_settings.BrakeCurveType, t, _settings.BrakeCustomP1X, _settings.BrakeCustomP1Y, _settings.BrakeCustomP2X, _settings.BrakeCustomP2Y);
                double x = t * width;
                double y = (1.0 - val) * height;
                polyline.Points.Add(new Point(x, y));
            }
            BrakeCurveEditorCanvas.Children.Add(polyline);

            // Control Handle Positions
            double p1X = _settings.BrakeCustomP1X * width;
            double p1Y = (1.0 - _settings.BrakeCustomP1Y) * height;

            double p2X = _settings.BrakeCustomP2X * width;
            double p2Y = (1.0 - _settings.BrakeCustomP2Y) * height;

            // Dashed Tangent Lines
            var tang1 = new Line
            {
                X1 = 0, Y1 = height,
                X2 = p1X, Y2 = p1Y,
                Stroke = Palette.CoralA78,
                StrokeDashArray = Palette.Dash3,
                StrokeThickness = 1.2
            };
            BrakeCurveEditorCanvas.Children.Add(tang1);

            var tang2 = new Line
            {
                X1 = width, Y1 = 0,
                X2 = p2X, Y2 = p2Y,
                Stroke = Palette.AmberA78,
                StrokeDashArray = Palette.Dash3,
                StrokeThickness = 1.2
            };
            BrakeCurveEditorCanvas.Children.Add(tang2);

            // Handle 1 Dot (P1 - Coral Initial Bite)
            var dot1 = new Ellipse
            {
                Width = 14, Height = 14,
                Fill = Palette.Coral,
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(dot1, p1X - 7);
            Canvas.SetTop(dot1, p1Y - 7);
            BrakeCurveEditorCanvas.Children.Add(dot1);

            // Handle 2 Dot (P2 - Gold Heavy Brake)
            var dot2 = new Ellipse
            {
                Width = 14, Height = 14,
                Fill = Palette.Amber,
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(dot2, p2X - 7);
            Canvas.SetTop(dot2, p2Y - 7);
            BrakeCurveEditorCanvas.Children.Add(dot2);
        }

        private void BrakeCurveEditorCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            Point pos = e.GetPosition(BrakeCurveEditorCanvas);
            double width = BrakeCurveEditorCanvas.ActualWidth;
            double height = BrakeCurveEditorCanvas.ActualHeight;

            double p1X = _settings.BrakeCustomP1X * width;
            double p1Y = (1.0 - _settings.BrakeCustomP1Y) * height;

            double p2X = _settings.BrakeCustomP2X * width;
            double p2Y = (1.0 - _settings.BrakeCustomP2Y) * height;

            double d1 = Math.Sqrt(Math.Pow(pos.X - p1X, 2) + Math.Pow(pos.Y - p1Y, 2));
            double d2 = Math.Sqrt(Math.Pow(pos.X - p2X, 2) + Math.Pow(pos.Y - p2Y, 2));

            _draggingBrakeHandle = (d1 <= d2) ? 1 : 2;
            BrakeCurveEditorCanvas.CaptureMouse();

            if (_settings.BrakeCurveType != ResponseCurveType.Custom)
            {
                _settings.BrakeCurveType = ResponseCurveType.Custom;
                CmbBrakeCurveType.SelectedItem = ResponseCurveType.Custom;
            }

            UpdateDraggedBrakeHandle(pos, width, height);
        }

        private void BrakeCurveEditorCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingBrakeHandle == 0 || e.LeftButton != MouseButtonState.Pressed) return;

            Point pos = e.GetPosition(BrakeCurveEditorCanvas);
            double width = BrakeCurveEditorCanvas.ActualWidth;
            double height = BrakeCurveEditorCanvas.ActualHeight;

            UpdateDraggedBrakeHandle(pos, width, height);
        }

        private void UpdateDraggedBrakeHandle(Point pos, double width, double height)
        {
            double normX = Math.Clamp(pos.X / width, 0.02, 0.98);
            double normY = Math.Clamp(1.0 - (pos.Y / height), 0.0, 1.0);

            if (_draggingBrakeHandle == 1)
            {
                _settings.BrakeCustomP1X = Math.Round(normX, 2);
                _settings.BrakeCustomP1Y = Math.Round(normY, 2);
            }
            else if (_draggingBrakeHandle == 2)
            {
                _settings.BrakeCustomP2X = Math.Round(normX, 2);
                _settings.BrakeCustomP2Y = Math.Round(normY, 2);
            }

            _settings.SyncToActiveProfile();
            RequestSave();
            DrawBrakeCurveEditor();
        }

        private void BrakeCurveEditorCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingBrakeHandle = 0;
            BrakeCurveEditorCanvas.ReleaseMouseCapture();
            FlushSave();
        }

        private void BrakeCurveEditorCanvas_LostMouseCapture(object sender, MouseEventArgs e)
        {
            FlushSave();
        }

        #endregion

        #region Interactive Custom Curve Editor (Steer Left)

        private int _draggingSteerHandle = 0; // 0 = none, 1 = P1, 2 = P2

        private void SteerCurveEditorCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawSteerCurveEditor();
        }

        private void DrawSteerCurveEditor()
        {
            if (SteerCurveEditorCanvas == null) return;
            if (PageSteering == null || PageSteering.Visibility != Visibility.Visible) return;

            double width = SteerCurveEditorCanvas.ActualWidth;
            double height = SteerCurveEditorCanvas.ActualHeight;
            if (width < 30 || height < 30) return;

            SteerCurveEditorCanvas.Children.Clear();

            // Background subtle grid lines
            for (int i = 1; i < 4; i++)
            {
                double ratio = i / 4.0;
                var hLine = new Line
                {
                    X1 = 0, Y1 = height * (1.0 - ratio),
                    X2 = width, Y2 = height * (1.0 - ratio),
                    Stroke = Palette.WhiteA14,
                    StrokeDashArray = Palette.Dash3
                };
                SteerCurveEditorCanvas.Children.Add(hLine);

                var vLine = new Line
                {
                    X1 = width * ratio, Y1 = 0,
                    X2 = width * ratio, Y2 = height,
                    Stroke = Palette.WhiteA14,
                    StrokeDashArray = Palette.Dash3
                };
                SteerCurveEditorCanvas.Children.Add(vLine);
            }

            // Curve polyline (Pure crisp white)
            var polyline = new Polyline
            {
                Stroke = Brushes.White,
                StrokeThickness = 2.0
            };

            int segments = 60;
            for (int i = 0; i <= segments; i++)
            {
                double t = (double)i / segments;
                double val = ResponseCurves.Evaluate(_settings.SteerCurveType, t, _settings.SteerCustomP1X, _settings.SteerCustomP1Y, _settings.SteerCustomP2X, _settings.SteerCustomP2Y);
                double x = t * width;
                double y = (1.0 - val) * height;
                polyline.Points.Add(new Point(x, y));
            }
            SteerCurveEditorCanvas.Children.Add(polyline);

            // Control Handle Positions
            double p1X = _settings.SteerCustomP1X * width;
            double p1Y = (1.0 - _settings.SteerCustomP1Y) * height;
            double p2X = _settings.SteerCustomP2X * width;
            double p2Y = (1.0 - _settings.SteerCustomP2Y) * height;

            // Tangent lines
            var tang1 = new Line
            {
                X1 = 0, Y1 = height,
                X2 = p1X, Y2 = p1Y,
                Stroke = Palette.WhiteA32,
                StrokeThickness = 1.2
            };
            SteerCurveEditorCanvas.Children.Add(tang1);

            var tang2 = new Line
            {
                X1 = width, Y1 = 0,
                X2 = p2X, Y2 = p2Y,
                Stroke = Palette.WhiteA32,
                StrokeThickness = 1.2
            };
            SteerCurveEditorCanvas.Children.Add(tang2);

            // Handle 1 Dot (P1 - Pure White)
            var dot1 = new Ellipse
            {
                Width = 14, Height = 14,
                Fill = Brushes.White,
                Stroke = Palette.WhiteA78,
                StrokeThickness = 2,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(dot1, p1X - 7);
            Canvas.SetTop(dot1, p1Y - 7);
            SteerCurveEditorCanvas.Children.Add(dot1);

            // Handle 2 Dot (P2 - Silver)
            var dot2 = new Ellipse
            {
                Width = 14, Height = 14,
                Fill = Palette.Silver,
                Stroke = Palette.WhiteA78,
                StrokeThickness = 2,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(dot2, p2X - 7);
            Canvas.SetTop(dot2, p2Y - 7);
            SteerCurveEditorCanvas.Children.Add(dot2);
        }

        private void SteerCurveEditorCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            Point pos = e.GetPosition(SteerCurveEditorCanvas);
            double width = SteerCurveEditorCanvas.ActualWidth;
            double height = SteerCurveEditorCanvas.ActualHeight;

            double p1X = _settings.SteerCustomP1X * width;
            double p1Y = (1.0 - _settings.SteerCustomP1Y) * height;
            double p2X = _settings.SteerCustomP2X * width;
            double p2Y = (1.0 - _settings.SteerCustomP2Y) * height;

            double d1 = (pos.X - p1X) * (pos.X - p1X) + (pos.Y - p1Y) * (pos.Y - p1Y);
            double d2 = (pos.X - p2X) * (pos.X - p2X) + (pos.Y - p2Y) * (pos.Y - p2Y);

            _draggingSteerHandle = (d1 <= d2) ? 1 : 2;
            SteerCurveEditorCanvas.CaptureMouse();

            if (_settings.SteerCurveType != ResponseCurveType.Custom)
            {
                _settings.SteerCurveType = ResponseCurveType.Custom;
                if (CmbSteerCurveType != null) CmbSteerCurveType.SelectedItem = ResponseCurveType.Custom;
            }

            UpdateDraggedSteerHandle(pos, width, height);
        }

        private void SteerCurveEditorCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingSteerHandle == 0 || e.LeftButton != MouseButtonState.Pressed) return;

            Point pos = e.GetPosition(SteerCurveEditorCanvas);
            double width = SteerCurveEditorCanvas.ActualWidth;
            double height = SteerCurveEditorCanvas.ActualHeight;

            UpdateDraggedSteerHandle(pos, width, height);
        }

        private void SteerCurveEditorCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingSteerHandle = 0;
            SteerCurveEditorCanvas.ReleaseMouseCapture();
            FlushSave();
        }

        private void SteerCurveEditorCanvas_LostMouseCapture(object sender, MouseEventArgs e)
        {
            FlushSave();
        }

        private void UpdateDraggedSteerHandle(Point pos, double width, double height)
        {
            double normX = Math.Clamp(pos.X / width, 0.0, 1.0);
            double normY = Math.Clamp(1.0 - (pos.Y / height), 0.0, 1.0);

            if (_draggingSteerHandle == 1)
            {
                _settings.SteerCustomP1X = normX;
                _settings.SteerCustomP1Y = normY;
            }
            else if (_draggingSteerHandle == 2)
            {
                _settings.SteerCustomP2X = normX;
                _settings.SteerCustomP2Y = normY;
            }

            DrawSteerCurveEditor();
            SyncActiveProfileFromSliders();
        }

        #endregion

        #region Window Controls

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.ButtonState == MouseButtonState.Pressed && e.GetPosition(this).Y <= 52)
            {
                DragMove();
            }
        }

        #endregion
    }
}
