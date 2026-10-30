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
        private bool _isCapturingKey;
        private bool _isCapturingBrakeKey;
        private bool _isCapturingSteerLeftKey;
        private bool _isCapturingSteerRightKey;
        private bool _isCapturingProfileHotkey;
        private string? _capturingRemapButtonId;
        private string? _editingProfileId;
        private bool _isUpdatingUiFromSettings;
        private volatile bool _isWindowMinimized;
        private DispatcherTimer? _toastTimer;

        public MainWindow()
        {
            _isUpdatingUiFromSettings = true;
            _settings = AppSettings.Load();
            _engine = new ThrottleEngine(_settings);
            _gameWatcher = new GameWatcher(_settings, _engine);

            InitializeComponent();

            try
            {
                var iconUri = new Uri("pack://application:,,,/Assets/app_icon.ico", UriKind.RelativeOrAbsolute);
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(iconUri);
            }
            catch { }

            StateChanged += (s, e) =>
            {
                _isWindowMinimized = (WindowState == WindowState.Minimized);
            };

            // Setup Toast Auto-fade Timer
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
            _toastTimer.Tick += (s, e) =>
            {
                ProfileToastBanner.Visibility = Visibility.Collapsed;
                _toastTimer.Stop();
            };

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
            NavigateToPage("Home");

            // Check ViGEm availability
            CheckViGemStatus();

            Loaded += (s, e) =>
            {
                NavigateToPage("Home");
                RenderProfileChips();
                RenderProfileCards();
            };
            Closing += MainWindow_Closing;
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
            _gameWatcher.Dispose();
            _engine.Dispose();
            _settings.Save();
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
            TxtCurveDescription.Text = ResponseCurves.GetDescription(_settings.CurveType);

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
            TxtBrakeCurveDescription.Text = ResponseCurves.GetDescription(_settings.BrakeCurveType);

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
            if (TxtSteerCurveDescription != null) TxtSteerCurveDescription.Text = ResponseCurves.GetDescription(_settings.SteerCurveType);

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
                    BadgeViGemStatus.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x1A, 0x38, 0x28));
                    BadgeViGemStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                    TxtViGemBadge.Text = "CONNECTED";
                    TxtViGemBadge.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                    TxtViGemStatusMessage.Text = "ViGEmBus driver is active.";
                    if (TxtBtnViGemAction != null) TxtBtnViGemAction.Text = "Reinstall Driver";
                    if (BtnDownloadViGem != null) BtnDownloadViGem.Visibility = Visibility.Visible;
                }
                else
                {
                    BadgeViGemStatus.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x3F, 0x1A, 0x1A));
                    BadgeViGemStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                    TxtViGemBadge.Text = "NOT INSTALLED";
                    TxtViGemBadge.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                    TxtViGemStatusMessage.Text = "ViGEmBus driver required for virtual Xbox controller.";
                    if (TxtBtnViGemAction != null) TxtBtnViGemAction.Text = "Install Driver";
                    if (BtnDownloadViGem != null) BtnDownloadViGem.Visibility = Visibility.Visible;
                }
            }
        }

        private void RenderProfileCards()
        {
            if (PnlProfileCardsList == null) return;
            PnlProfileCardsList.Children.Clear();

            string activeId = _settings.ActiveProfileId;

            foreach (var profile in _settings.Profiles)
            {
                bool isActive = (profile.Id == activeId);
                bool isEditing = (profile.Id == _editingProfileId);

                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x26)),
                    BorderBrush = isActive
                        ? new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8))
                        : (isEditing ? new SolidColorBrush(Color.FromArgb(180, 0x38, 0xBD, 0xF8)) : new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x3A))),
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
                        Fill = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
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
                        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x12, 0x1B)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
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
                            _settings.Save();
                            RenderProfileChips();
                            TxtToastProfileName.Text = pTarget.Name;
                            TxtToastDetails.Text = $"Profile Renamed to '{pTarget.Name}'";
                            ProfileToastBanner.Visibility = Visibility.Visible;
                            _toastTimer?.Stop();
                            _toastTimer?.Start();
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
                        Style = (Style)FindResource("CardActionBtn"),
                        Padding = new Thickness(16, 7, 16, 7),
                        Margin = new Thickness(0, 0, 8, 0),
                        Cursor = Cursors.Hand
                    };
                    saveBtn.Click += (s, e) => saveAction();
                    btnStack.Children.Add(saveBtn);

                    var cancelBtn = new Button
                    {
                        Content = "Cancel",
                        Style = (Style)FindResource("CardActionBtn"),
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
                        Fill = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
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
                        Style = (Style)FindResource("CardActionBtn"),
                        Padding = new Thickness(12, 7, 12, 7),
                        Margin = new Thickness(0, 0, 8, 0),
                        Cursor = Cursors.Hand
                    };
                    var renameContent = new StackPanel { Orientation = Orientation.Horizontal };
                    renameContent.Children.Add(new System.Windows.Shapes.Path
                    {
                        Data = (Geometry)FindResource("IconEdit"),
                        Fill = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
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
                            Style = (Style)FindResource("CardActionBtn"),
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
                                Style = (Style)FindResource("CardActionBtn"),
                                Padding = new Thickness(10, 7, 10, 7),
                                Margin = new Thickness(8, 0, 0, 0),
                                Cursor = Cursors.Hand,
                                Content = new System.Windows.Shapes.Path
                                {
                                    Data = (Geometry)FindResource("IconClose"),
                                    Fill = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
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
                            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x2A, 0x3B)),
                            BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(6),
                            Padding = new Thickness(16, 7, 16, 7)
                        };
                        activePill.Child = new TextBlock { Text = "Active Profile", FontWeight = FontWeights.Bold, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)) };
                        btnStack.Children.Add(activePill);
                    }
                }

                var curveDesc = new TextBlock
                {
                    Text = $"Curves  —  Throttle: {profile.CurveType}   •   Brake: {profile.BrakeCurveType}   •   Steer: {profile.SteerCurveType}",
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
                    Margin = new Thickness(0, 3, 0, 0)
                };
                infoStack.Children.Add(curveDesc);

                var timingDesc = new TextBlock
                {
                    Text = $"Timings —  Throttle: {profile.AttackTimeSeconds:F2}s / {profile.ReleaseTimeSeconds:F2}s   •   Brake: {profile.BrakeAttackTimeSeconds:F2}s / {profile.BrakeReleaseTimeSeconds:F2}s   •   Steer: {profile.SteerAttackTimeSeconds:F2}s / {profile.SteerReleaseTimeSeconds:F2}s",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)),
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
                        ? new SolidColorBrush(Color.FromArgb(0x40, 0x38, 0xBD, 0xF8))
                        : new SolidColorBrush(Color.FromArgb(0x30, 0x1E, 0x27, 0x38)),
                    BorderBrush = isActive
                        ? new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8))
                        : new SolidColorBrush(Color.FromRgb(0x29, 0x34, 0x48)),
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
                        ? new SolidColorBrush(Color.FromRgb(255, 255, 255))
                        : new SolidColorBrush(Color.FromRgb(203, 213, 225)),
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
                        ? new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8))
                        : new SolidColorBrush(Color.FromRgb(0x29, 0x34, 0x48)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, (_settings.Profiles.Count > 1 ? 6 : 0), 0)
                };

                var hkTextBlock = new TextBlock
                {
                    Text = hkText,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = isActive
                        ? new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A))
                        : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))
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
                            Fill = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
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
            TxtActiveProfileName.Text = active.Name;

            string hkLabel = (active.HotkeyVkCode != 0) ? active.HotkeyName : "None";
            TxtProfileHotkeyDisplay.Text = $"[ {hkLabel} ] (Change)";
            BtnClearProfileHotkey.IsEnabled = (active.HotkeyVkCode != 0);
        }

        private void OnProfileActivated(ThrottleProfile profile)
        {
            Dispatcher.InvokeAsync(() =>
            {
                // Reload sliders and readouts
                LoadSettingsIntoControls();

                // Re-render curves & remaps
                DrawCurveEditor();
                DrawBrakeCurveEditor();
                DrawSteerCurveEditor();
                if (_currentPage == "Remap")
                {
                    RenderRemapList();
                }

                // Show Toast Notification
                TxtToastProfileName.Text = profile.Name;
                string hkTag = (profile.HotkeyVkCode != 0) ? $" • Hotkey: [{profile.HotkeyName}]" : "";
                TxtToastDetails.Text = $"Throttle: {profile.CurveType} ({profile.AttackTimeSeconds:F2}s) • Brake: {profile.BrakeCurveType} • Steer: {profile.SteerCurveType}{hkTag}";

                ProfileToastBanner.Visibility = Visibility.Visible;
                _toastTimer?.Stop();
                _toastTimer?.Start();

                RenderProfileChips();
                RenderProfileCards();
            });
        }

        private void BtnCloseToast_Click(object sender, RoutedEventArgs e)
        {
            ProfileToastBanner.Visibility = Visibility.Collapsed;
            _toastTimer?.Stop();
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
                _settings.Save();
                RenderProfileChips();
                RenderProfileCards();
            }
        }

        private void TxtActiveProfileName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUiFromSettings) return;

            var active = _settings.GetActiveProfile();
            string newName = TxtActiveProfileName.Text.Trim();
            if (!string.IsNullOrEmpty(newName) && active.Name != newName)
            {
                active.Name = newName;
                _settings.Save();
                RenderProfileChips();
                RenderProfileCards();
            }
        }

        private void BtnBindProfileHotkey_Click(object sender, RoutedEventArgs e)
        {
            if (_isCapturingProfileHotkey) return;

            _isCapturingProfileHotkey = true;
            _isCapturingKey = false;
            _isCapturingBrakeKey = false;
            _isCapturingSteerLeftKey = false;
            _isCapturingSteerRightKey = false;
            TxtProfileHotkeyDisplay.Text = "[ PRESS ANY KEY... ]";
            _engine.KeyboardHook.IsKeyCaptureActive = true;
        }

        private void BtnClearProfileHotkey_Click(object sender, RoutedEventArgs e)
        {
            var active = _settings.GetActiveProfile();
            active.HotkeyVkCode = 0;
            active.HotkeyName = "None";
            _settings.Save();
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

        private static bool TryParseTiming(string text, out double val)
        {
            val = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string clean = text.Trim().ToLower().Replace("s", "").Replace("sec", "").Trim();
            return double.TryParse(clean, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out val) ||
                   double.TryParse(clean, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out val);
        }

        private void SyncActiveProfileFromSliders()
        {
            if (_isUpdatingUiFromSettings) return;

            CommitAllInputsToSettings();
            _settings.SyncToActiveProfile();
            _settings.Save();
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
                BtnToggleThrottleAxis.Background = th ? new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
                BtnToggleThrottleAxis.BorderBrush = th ? new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            }
            if (DotThrottleAxis != null) DotThrottleAxis.Fill = th ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
            if (TxtThrottleAxisStatus != null) TxtThrottleAxisStatus.Text = th ? "RAMP: SMOOTH (ANALOG)" : "RAMP: DIRECT (DIGITAL 100%)";
            if (DotQuickThrottle != null) DotQuickThrottle.Fill = th ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
            if (TxtQuickThrottle != null) TxtQuickThrottle.Text = th ? "Throttle: Smooth" : "Throttle: Digital";

            // Brake
            bool br = _settings.EnableBrake;
            if (BtnToggleBrakeAxis != null)
            {
                BtnToggleBrakeAxis.Background = br ? new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
                BtnToggleBrakeAxis.BorderBrush = br ? new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            }
            if (DotBrakeAxis != null) DotBrakeAxis.Fill = br ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
            if (TxtBrakeAxisStatus != null) TxtBrakeAxisStatus.Text = br ? "RAMP: SMOOTH (ANALOG)" : "RAMP: DIRECT (DIGITAL 100%)";
            if (DotQuickBrake != null) DotQuickBrake.Fill = br ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
            if (TxtQuickBrake != null) TxtQuickBrake.Text = br ? "Braking: Smooth" : "Braking: Digital";

            // Steering
            bool st = _settings.EnableSteering;
            if (BtnToggleSteeringAxis != null)
            {
                BtnToggleSteeringAxis.Background = st ? new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
                BtnToggleSteeringAxis.BorderBrush = st ? new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            }
            if (DotSteeringAxis != null) DotSteeringAxis.Fill = st ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
            if (TxtSteeringAxisStatus != null) TxtSteeringAxisStatus.Text = st ? "RAMP: SMOOTH (ANALOG)" : "RAMP: DIRECT (DIGITAL 100%)";
            if (DotQuickSteering != null) DotQuickSteering.Fill = st ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
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
                    PillMasterToggle.Background = new SolidColorBrush(Color.FromArgb(0x25, 0x10, 0xB9, 0x81));
                    PillMasterToggle.BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x10, 0xB9, 0x81));
                    DotMasterStatus.Fill = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                    TxtMasterStatus.Text = "ACTIVE";
                    TxtMasterStatus.Foreground = Brushes.White;
                }
                else
                {
                    PillMasterToggle.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
                    PillMasterToggle.BorderBrush = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF));
                    DotMasterStatus.Fill = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
                    TxtMasterStatus.Text = "DISABLED";
                    TxtMasterStatus.Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
                }
            });
        }

        #endregion

        #region Page Navigation & Sub-Tabs

        private string _currentPage = "Home";
        private DispatcherTimer? _telemetryTimer;

        private void InitTelemetryTimer()
        {
            _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _telemetryTimer.Tick += (s, e) =>
            {
                if (_isWindowMinimized || _currentPage != "Home") return;

                double th = _engine.LastEvaluatedThrottle;
                double br = _engine.LastEvaluatedBrake;
                double st = _engine.LastEvaluatedSteer;

                if (ProgLiveThrottle != null) ProgLiveThrottle.Value = th * 100.0;
                if (TxtLiveThrottleVal != null) TxtLiveThrottleVal.Text = $"{(int)(th * 100.0)}%";

                if (ProgLiveBrake != null) ProgLiveBrake.Value = br * 100.0;
                if (TxtLiveBrakeVal != null) TxtLiveBrakeVal.Text = $"{(int)(br * 100.0)}%";

                double stLeft = st < 0 ? (-st * 100.0) : 0;
                if (ProgLiveSteerLeft != null) ProgLiveSteerLeft.Value = stLeft;
                if (TxtLiveSteerLeftVal != null) TxtLiveSteerLeftVal.Text = $"{(int)stLeft}%";

                double stRight = st > 0 ? (st * 100.0) : 0;
                if (ProgLiveSteerRight != null) ProgLiveSteerRight.Value = stRight;
                if (TxtLiveSteerRightVal != null) TxtLiveSteerRightVal.Text = $"{(int)stRight}%";
            };
            _telemetryTimer.Start();
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
            _currentPage = pageName;

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
                btn.Background = new SolidColorBrush(Color.FromRgb(0x19, 0x20, 0x2E));
                btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x3A, 0x52));
                btn.Foreground = Brushes.White;
            }
            else
            {
                btn.Background = Brushes.Transparent;
                btn.BorderBrush = Brushes.Transparent;
                btn.Foreground = (Brush)FindResource("TextSecondary");
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
                    TxtToastProfileName.Text = "Game Auto-Detected";
                    TxtToastDetails.Text = $"{gameName} launched → Key Trigger ACTIVATED!";
                }
                else
                {
                    TxtToastProfileName.Text = "Game Closed";
                    TxtToastDetails.Text = $"Game exited → Key Trigger DEACTIVATED";
                }

                ProfileToastBanner.Visibility = Visibility.Visible;
                _toastTimer?.Stop();
                _toastTimer?.Start();

                if (_currentPage == "Games")
                {
                    RenderGamesList();
                }
            });
        }

        private void BtnToggleAutoActivation_Click(object sender, RoutedEventArgs e)
        {
            _settings.EnableAutoActivation = !_settings.EnableAutoActivation;
            _settings.Save();
            UpdateAutoActivationUi();
            RenderGamesList();
            _gameWatcher.CheckNow();
        }

        private void BtnTogglePauseWhenUnfocused_Click(object sender, RoutedEventArgs e)
        {
            _settings.PauseWhenUnfocused = !_settings.PauseWhenUnfocused;
            _settings.Save();
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
                ? new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76))
                : new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));

            BadgeAutoActivationStatus.Background = enabled
                ? new SolidColorBrush(Color.FromRgb(0x12, 0x2C, 0x20))
                : new SolidColorBrush(Color.FromRgb(0x23, 0x29, 0x36));
            BadgeAutoActivationStatus.BorderBrush = enabled
                ? new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76))
                : new SolidColorBrush(Color.FromRgb(0x4F, 0x58, 0x6E));

            TxtAutoActivationBadge.Text = enabled ? "ACTIVE SCANNING" : "PAUSED";
            TxtAutoActivationBadge.Foreground = enabled
                ? new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76))
                : new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));

            if (BtnTogglePauseWhenUnfocused != null && BadgeFocusTrackingStatus != null && TxtFocusTrackingBadge != null)
            {
                bool focusEnabled = _settings.PauseWhenUnfocused;
                BtnTogglePauseWhenUnfocused.Content = focusEnabled ? "ENABLED" : "DISABLED";
                BtnTogglePauseWhenUnfocused.Foreground = focusEnabled
                    ? new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76))
                    : new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));

                BadgeFocusTrackingStatus.Background = focusEnabled
                    ? new SolidColorBrush(Color.FromRgb(0x12, 0x2C, 0x20))
                    : new SolidColorBrush(Color.FromRgb(0x23, 0x29, 0x36));
                BadgeFocusTrackingStatus.BorderBrush = focusEnabled
                    ? new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76))
                    : new SolidColorBrush(Color.FromRgb(0x4F, 0x58, 0x6E));

                TxtFocusTrackingBadge.Text = focusEnabled ? "ACTIVE ONLY WHEN FOCUSED" : "DISABLED (ALWAYS ACTIVE)";
                TxtFocusTrackingBadge.Foreground = focusEnabled
                    ? new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76))
                    : new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
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
                        _settings.Save();
                        RenderGamesList();

                        TxtToastProfileName.Text = "Game Added";
                        TxtToastDetails.Text = $"Key Trigger will auto-activate whenever {fileName} is running.";
                        ProfileToastBanner.Visibility = Visibility.Visible;
                        _toastTimer?.Stop();
                        _toastTimer?.Start();
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
                    Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x26)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x29, 0x38)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(24, 28, 24, 28),
                    Margin = new Thickness(0, 0, 0, 14)
                };

                var emptyStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                emptyStack.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = (Geometry)FindResource("IconGamepad"),
                    Fill = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
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
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 14)
                });

                var addBtn = new Button
                {
                    Style = (Style)FindResource("CardActionBtn"),
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

            foreach (var game in _settings.AutoActivateGames)
            {
                bool isRunning = _gameWatcher.IsGameRunning(game);

                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x26)),
                    BorderBrush = isRunning
                        ? new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76))
                        : new SolidColorBrush(Color.FromRgb(0x22, 0x29, 0x38)),
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
                    Fill = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
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
                            ? new SolidColorBrush(Color.FromRgb(0x12, 0x2C, 0x20))
                            : new SolidColorBrush(Color.FromRgb(0x2A, 0x24, 0x14)),
                        BorderBrush = isFocused
                            ? new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76))
                            : new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2, 6, 2),
                        Margin = new Thickness(10, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    string statusText = isFocused
                        ? "RUNNING (FOCUSED - ACTIVE)"
                        : (_settings.PauseWhenUnfocused ? "RUNNING (UNFOCUSED - PAUSED)" : "RUNNING (ACTIVE)");
                    Color textColor = isFocused
                        ? Color.FromRgb(0x10, 0xB9, 0x81)
                        : (_settings.PauseWhenUnfocused ? Color.FromRgb(0xF5, 0x9E, 0x0B) : Color.FromRgb(0x10, 0xB9, 0x81));

                    liveBadge.Child = new TextBlock
                    {
                        Text = statusText,
                        FontSize = 9,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(textColor)
                    };
                    titleRow.Children.Add(liveBadge);
                }
                else
                {
                    var idleBadge = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x22, 0x30)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x40, 0x57)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2, 6, 2),
                        Margin = new Thickness(10, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    idleBadge.Child = new TextBlock { Text = "STANDBY", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)) };
                    titleRow.Children.Add(idleBadge);
                }

                var fileBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x30, 0x81, 0x8C, 0xF8)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x81, 0x8C, 0xF8)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                fileBadge.Child = new TextBlock { Text = game.ExeFileName, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x81, 0x8C, 0xF8)) };
                titleRow.Children.Add(fileBadge);

                infoStack.Children.Add(titleRow);

                var pathBlock = new TextBlock
                {
                    Text = string.IsNullOrEmpty(game.ExePath) ? game.ExeFileName : game.ExePath,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
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
                    Style = (Style)FindResource("CardActionBtn"),
                    Padding = new Thickness(12, 6, 12, 6),
                    Foreground = game.IsEnabled
                        ? new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8))
                        : new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
                    Cursor = Cursors.Hand
                };
                var g = game;
                toggleBtn.Click += (s, e) =>
                {
                    g.IsEnabled = !g.IsEnabled;
                    _settings.Save();
                    RenderGamesList();
                };
                btnStack.Children.Add(toggleBtn);

                var delBtn = new Button
                {
                    ToolTip = "Remove Game",
                    Style = (Style)FindResource("CardActionBtn"),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(8, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    Content = new System.Windows.Shapes.Path
                    {
                        Data = (Geometry)FindResource("IconClose"),
                        Fill = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
                        Width = 9,
                        Height = 9,
                        Stretch = Stretch.Uniform
                    }
                };
                delBtn.Click += (s, e) =>
                {
                    _settings.AutoActivateGames.Remove(g);
                    _settings.Save();
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
            if (_isCapturingKey || _isCapturingBrakeKey || _isCapturingSteerLeftKey || _isCapturingSteerRightKey) return;

            _isCapturingKey = true;
            _isCapturingBrakeKey = false;
            _isCapturingSteerLeftKey = false;
            _isCapturingSteerRightKey = false;
            _isCapturingProfileHotkey = false;
            TxtBoundKey.Text = "[ PRESS ANY KEY... ]";
            TxtBoundKey.Foreground = new SolidColorBrush(Color.FromRgb(255, 179, 0));
            _engine.KeyboardHook.IsKeyCaptureActive = true;
        }

        private void BtnBindBrakeKey_Click(object sender, RoutedEventArgs e)
        {
            if (_isCapturingKey || _isCapturingBrakeKey || _isCapturingSteerLeftKey || _isCapturingSteerRightKey) return;

            _isCapturingBrakeKey = true;
            _isCapturingKey = false;
            _isCapturingSteerLeftKey = false;
            _isCapturingSteerRightKey = false;
            _isCapturingProfileHotkey = false;
            TxtBoundBrakeKey.Text = "[ PRESS ANY KEY... ]";
            TxtBoundBrakeKey.Foreground = new SolidColorBrush(Color.FromRgb(255, 179, 0));
            _engine.KeyboardHook.IsKeyCaptureActive = true;
        }

                private void BtnRebindSteerLeft_Click(object sender, RoutedEventArgs e)
        {
            BtnBindSteerLeftKey_Click(sender, e);
        }

        private void BtnRebindSteerRight_Click(object sender, RoutedEventArgs e)
        {
            BtnBindSteerRightKey_Click(sender, e);
        }

        private void BtnBindSteerLeftKey_Click(object sender, RoutedEventArgs e)
        {
            if (_isCapturingKey || _isCapturingBrakeKey || _isCapturingSteerLeftKey || _isCapturingSteerRightKey) return;

            _isCapturingSteerLeftKey = true;
            _isCapturingKey = false;
            _isCapturingBrakeKey = false;
            _isCapturingSteerRightKey = false;
            _isCapturingProfileHotkey = false;
            TxtBoundSteerLeftKey.Text = "[ PRESS ANY KEY... ]";
            TxtBoundSteerLeftKey.Foreground = new SolidColorBrush(Color.FromRgb(255, 179, 0));
            _engine.KeyboardHook.IsKeyCaptureActive = true;
        }

        private void BtnBindSteerRightKey_Click(object sender, RoutedEventArgs e)
        {
            if (_isCapturingKey || _isCapturingBrakeKey || _isCapturingSteerLeftKey || _isCapturingSteerRightKey) return;

            _isCapturingSteerRightKey = true;
            _isCapturingKey = false;
            _isCapturingBrakeKey = false;
            _isCapturingSteerLeftKey = false;
            _isCapturingProfileHotkey = false;
            TxtBoundSteerRightKey.Text = "[ PRESS ANY KEY... ]";
            TxtBoundSteerRightKey.Foreground = new SolidColorBrush(Color.FromRgb(255, 179, 0));
            _engine.KeyboardHook.IsKeyCaptureActive = true;
        }

        private void OnKeyCaptured(int vkCode)
        {
            Dispatcher.InvokeAsync(() =>
            {
                string keyName = KeyNameHelper.GetKeyName(vkCode);

                if (_isCapturingProfileHotkey)
                {
                    _isCapturingProfileHotkey = false;
                    var active = _settings.GetActiveProfile();
                    active.HotkeyVkCode = vkCode;
                    active.HotkeyName = keyName;
                    _settings.Save();

                    UpdateActiveProfileControls();
                    RenderProfileChips();
                    RenderProfileCards();
                }
                else if (!string.IsNullOrEmpty(_capturingRemapButtonId))
                {
                    string btnId = _capturingRemapButtonId;
                    _capturingRemapButtonId = null;

                    var mapping = _settings.ButtonMappings.FirstOrDefault(m => m.ButtonId.Equals(btnId, StringComparison.OrdinalIgnoreCase));
                    if (mapping != null)
                    {
                        mapping.VkCode = vkCode;
                        mapping.KeyName = keyName;
                        _settings.SyncToActiveProfile();
                        _settings.Save();
                        _engine.UpdateKeyBindings();
                    }

                    RenderRemapList();
                }
                else if (_isCapturingKey)
                {
                    _isCapturingKey = false;
                    _settings.ThrottleVkCode = vkCode;
                    _settings.ThrottleKeyName = keyName;
                    _settings.SyncToActiveProfile();
                    _settings.Save();

                    _engine.UpdateKeyBindings();

                    TxtBoundKey.Text = $"[ {keyName} ]";
                    TxtBoundKey.Foreground = new SolidColorBrush(Color.FromRgb(0, 229, 255));
                }
                else if (_isCapturingBrakeKey)
                {
                    _isCapturingBrakeKey = false;
                    _settings.BrakeVkCode = vkCode;
                    _settings.BrakeKeyName = keyName;
                    _settings.SyncToActiveProfile();
                    _settings.Save();

                    _engine.UpdateKeyBindings();

                    TxtBoundBrakeKey.Text = $"[ {keyName} ]";
                    TxtBoundBrakeKey.Foreground = new SolidColorBrush(Color.FromRgb(255, 82, 82));
                }
                else if (_isCapturingSteerLeftKey)
                {
                    _isCapturingSteerLeftKey = false;
                    _settings.SteerLeftVkCode = vkCode;
                    _settings.SteerLeftKeyName = keyName;
                    _settings.SyncToActiveProfile();
                    _settings.Save();

                    _engine.UpdateKeyBindings();

                    TxtBoundSteerLeftKey.Text = $"[ {keyName} ]";
                    TxtBoundSteerLeftKey.Foreground = new SolidColorBrush(Color.FromRgb(68, 138, 255));
                }
                else if (_isCapturingSteerRightKey)
                {
                    _isCapturingSteerRightKey = false;
                    _settings.SteerRightVkCode = vkCode;
                    _settings.SteerRightKeyName = keyName;
                    _settings.SyncToActiveProfile();
                    _settings.Save();

                    _engine.UpdateKeyBindings();

                    TxtBoundSteerRightKey.Text = $"[ {keyName} ]";
                    TxtBoundSteerRightKey.Foreground = new SolidColorBrush(Color.FromRgb(179, 136, 255));
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
                        Background = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF)),
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
                        Style = (Style)FindResource("CardActionBtn"),
                        MinWidth = 120,
                        Height = 32,
                        Cursor = Cursors.Hand,
                        Content = isCapturingThis ? "[ PRESS KEY... ]" : $"[ {mapping.KeyName} ]",
                        Foreground = isCapturingThis
                            ? new SolidColorBrush(Color.FromRgb(255, 179, 0))
                            : (mapping.VkCode != 0 ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)))
                    };

                    var currentMapping = mapping;
                    bindBtn.Click += (s, e) =>
                    {
                        if (_isCapturingKey || _isCapturingBrakeKey || _isCapturingSteerLeftKey || _isCapturingSteerRightKey || _isCapturingProfileHotkey) return;

                        _capturingRemapButtonId = currentMapping.ButtonId;
                        _engine.KeyboardHook.IsKeyCaptureActive = true;
                        RenderRemapList();
                    };
                    actionsStack.Children.Add(bindBtn);

                    // Clear button (only if key is bound)
                    if (mapping.VkCode != 0)
                    {
                        var clearBtn = new Button
                        {
                            Style = (Style)FindResource("CardActionBtn"),
                            Padding = new Thickness(10, 6, 10, 6),
                            Margin = new Thickness(6, 0, 0, 0),
                            Height = 32,
                            Cursor = Cursors.Hand,
                            ToolTip = "Clear Mapping (Set to None)",
                            Content = new System.Windows.Shapes.Path
                            {
                                Data = (Geometry)FindResource("IconClose"),
                                Fill = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
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
                            _settings.Save();
                            _engine.UpdateKeyBindings();
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
            _settings.Save();
            _engine.UpdateKeyBindings();
            RenderRemapList();

            TxtToastProfileName.Text = "Defaults Restored";
            TxtToastDetails.Text = "Controller button mappings reset to standard racing configuration.";
            ProfileToastBanner.Visibility = Visibility.Visible;
            _toastTimer?.Stop();
            _toastTimer?.Start();
        }

        private void BtnClearAllRemap_Click(object sender, RoutedEventArgs e)
        {
            foreach (var m in _settings.ButtonMappings)
            {
                m.VkCode = 0;
                m.KeyName = "None";
            }
            _settings.SyncToActiveProfile();
            _settings.Save();
            _engine.UpdateKeyBindings();
            RenderRemapList();

            TxtToastProfileName.Text = "Mappings Cleared";
            TxtToastDetails.Text = "All controller button keybindings unmapped.";
            ProfileToastBanner.Visibility = Visibility.Visible;
            _toastTimer?.Stop();
            _toastTimer?.Start();
        }

        #endregion

        #region ViGEm Driver Controls

        private void BtnDownloadViGem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string localInstaller = @"C:\Users\spider\Desktop\ViGEmBusSetup.exe";
                if (System.IO.File.Exists(localInstaller))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = localInstaller,
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    Process.Start(psi);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/nefarius/ViGEmBus/releases/latest",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://github.com/nefarius/ViGEmBus/releases/latest",
                        UseShellExecute = true
                    });
                }
                catch
                {
                    MessageBox.Show($"Could not run installer: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
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
            string text = TxtAttackInput.Text.Trim().ToLower().Replace("s", "").Replace("sec", "").Trim();
            if (double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double sec) ||
                double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out sec))
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
            string text = TxtReleaseInput.Text.Trim().ToLower().Replace("s", "").Replace("sec", "").Trim();
            if (double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double sec) ||
                double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out sec))
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
            string text = TxtBrakeAttackInput.Text.Trim().ToLower().Replace("s", "").Replace("sec", "").Trim();
            if (double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double sec) ||
                double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out sec))
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
            string text = TxtBrakeReleaseInput.Text.Trim().ToLower().Replace("s", "").Replace("sec", "").Trim();
            if (double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double sec) ||
                double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out sec))
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
            string text = TxtSteerAttackInput.Text.Trim().ToLower().Replace("s", "").Replace("sec", "").Trim();
            if (double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double sec) ||
                double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out sec))
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
            string text = TxtSteerReleaseInput.Text.Trim().ToLower().Replace("s", "").Replace("sec", "").Trim();
            if (double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double sec) ||
                double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out sec))
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
                TxtCurveDescription.Text = ResponseCurves.GetDescription(curveType);
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
                TxtBrakeCurveDescription.Text = ResponseCurves.GetDescription(curveType);
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
                if (TxtSteerCurveDescription != null) TxtSteerCurveDescription.Text = ResponseCurves.GetDescription(curveType);
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
                    Stroke = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                    StrokeDashArray = new DoubleCollection { 3, 3 }
                };
                CurveEditorCanvas.Children.Add(hLine);

                var vLine = new Line
                {
                    X1 = width * ratio, Y1 = 0,
                    X2 = width * ratio, Y2 = height,
                    Stroke = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                    StrokeDashArray = new DoubleCollection { 3, 3 }
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
                Stroke = new SolidColorBrush(Color.FromArgb(120, 0x38, 0xBD, 0xF8)),
                StrokeDashArray = new DoubleCollection { 3, 3 },
                StrokeThickness = 1.2
            };
            CurveEditorCanvas.Children.Add(tang1);

            var tang2 = new Line
            {
                X1 = width, Y1 = 0,
                X2 = p2X, Y2 = p2Y,
                Stroke = new SolidColorBrush(Color.FromArgb(120, 0x81, 0x8C, 0xF8)),
                StrokeDashArray = new DoubleCollection { 3, 3 },
                StrokeThickness = 1.2
            };
            CurveEditorCanvas.Children.Add(tang2);

            // Handle 1 Dot (P1 - Cyan Launch Bite)
            var dot1 = new Ellipse
            {
                Width = 14, Height = 14,
                Fill = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
                Stroke = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
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
                Fill = new SolidColorBrush(Color.FromRgb(0x81, 0x8C, 0xF8)),
                Stroke = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                StrokeThickness = 2,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(dot2, p2X - 7);
            Canvas.SetTop(dot2, p2Y - 7);
            CurveEditorCanvas.Children.Add(dot2);

            TxtCurveHandlesReadout.Text = $"P1 Launch: ({_settings.CustomP1X:F2}, {_settings.CustomP1Y:F2}) • P2 Top-End: ({_settings.CustomP2X:F2}, {_settings.CustomP2Y:F2})";
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
                TxtCurveDescription.Text = ResponseCurves.GetDescription(ResponseCurveType.Custom);
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
            _settings.Save();
            DrawCurveEditor();
        }

        private void CurveEditorCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingHandle = 0;
            CurveEditorCanvas.ReleaseMouseCapture();
        }

        private void BtnCurvePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                var parts = tag.Split(',');
                if (parts.Length == 4 &&
                    double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out double p1x) &&
                    double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out double p1y) &&
                    double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out double p2x) &&
                    double.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out double p2y))
                {
                    _settings.CustomP1X = p1x;
                    _settings.CustomP1Y = p1y;
                    _settings.CustomP2X = p2x;
                    _settings.CustomP2Y = p2y;
                    _settings.CurveType = ResponseCurveType.Custom;
                    CmbCurveType.SelectedItem = ResponseCurveType.Custom;
                    TxtCurveDescription.Text = ResponseCurves.GetDescription(ResponseCurveType.Custom);

                    _settings.SyncToActiveProfile();
                    _settings.Save();
                    DrawCurveEditor();
                }
            }
        }

        private void BtnUniversalSave_Click(object sender, RoutedEventArgs e)
        {
            // Sync all UI inputs and sliders
            SyncActiveProfileFromSliders();

            // Sync all settings (curves, timings, limits, and all keymaps) into the active profile object
            _settings.SyncToActiveProfile();

            // Persist to settings.json
            _settings.Save();

            // Push updated bindings to the engine keyboard hook immediately
            _engine.UpdateKeyBindings();

            // Refresh UI chips & cards
            RenderProfileChips();
            RenderProfileCards();

            // Toast feedback
            var active = _settings.GetActiveProfile();
            TxtToastProfileName.Text = active.Name;
            TxtToastDetails.Text = $"Saved — Throttle: {active.CurveType} • Brake: {active.BrakeCurveType} • Steer: {active.SteerCurveType}";
            ProfileToastBanner.Visibility = Visibility.Visible;
            _toastTimer?.Stop();
            _toastTimer?.Start();
        }

        private void BtnSaveCurvePreset_Click(object sender, RoutedEventArgs e) => BtnUniversalSave_Click(sender, e);

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
                    Stroke = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                    StrokeDashArray = new DoubleCollection { 3, 3 }
                };
                BrakeCurveEditorCanvas.Children.Add(hLine);

                var vLine = new Line
                {
                    X1 = width * ratio, Y1 = 0,
                    X2 = width * ratio, Y2 = height,
                    Stroke = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                    StrokeDashArray = new DoubleCollection { 3, 3 }
                };
                BrakeCurveEditorCanvas.Children.Add(vLine);
            }

            // Curve polyline (Red/Coral for Brake)
            var polyline = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(255, 82, 82)),
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
                Stroke = new SolidColorBrush(Color.FromArgb(120, 255, 82, 82)),
                StrokeDashArray = new DoubleCollection { 3, 3 },
                StrokeThickness = 1.2
            };
            BrakeCurveEditorCanvas.Children.Add(tang1);

            var tang2 = new Line
            {
                X1 = width, Y1 = 0,
                X2 = p2X, Y2 = p2Y,
                Stroke = new SolidColorBrush(Color.FromArgb(120, 255, 179, 0)),
                StrokeDashArray = new DoubleCollection { 3, 3 },
                StrokeThickness = 1.2
            };
            BrakeCurveEditorCanvas.Children.Add(tang2);

            // Handle 1 Dot (P1 - Coral Initial Bite)
            var dot1 = new Ellipse
            {
                Width = 14, Height = 14,
                Fill = new SolidColorBrush(Color.FromRgb(255, 82, 82)),
                Stroke = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
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
                Fill = new SolidColorBrush(Color.FromRgb(255, 179, 0)),
                Stroke = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                StrokeThickness = 2,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(dot2, p2X - 7);
            Canvas.SetTop(dot2, p2Y - 7);
            BrakeCurveEditorCanvas.Children.Add(dot2);

            TxtBrakeCurveHandlesReadout.Text = $"P1 Initial: ({_settings.BrakeCustomP1X:F2}, {_settings.BrakeCustomP1Y:F2}) • P2 Heavy: ({_settings.BrakeCustomP2X:F2}, {_settings.BrakeCustomP2Y:F2})";
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
                TxtBrakeCurveDescription.Text = ResponseCurves.GetDescription(ResponseCurveType.Custom);
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
            _settings.Save();
            DrawBrakeCurveEditor();
        }

        private void BrakeCurveEditorCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingBrakeHandle = 0;
            BrakeCurveEditorCanvas.ReleaseMouseCapture();
        }

        private void BtnBrakeCurvePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                var parts = tag.Split(',');
                if (parts.Length == 4 &&
                    double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out double p1x) &&
                    double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out double p1y) &&
                    double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out double p2x) &&
                    double.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out double p2y))
                {
                    _settings.BrakeCustomP1X = p1x;
                    _settings.BrakeCustomP1Y = p1y;
                    _settings.BrakeCustomP2X = p2x;
                    _settings.BrakeCustomP2Y = p2y;
                    _settings.BrakeCurveType = ResponseCurveType.Custom;
                    CmbBrakeCurveType.SelectedItem = ResponseCurveType.Custom;
                    TxtBrakeCurveDescription.Text = ResponseCurves.GetDescription(ResponseCurveType.Custom);

                    _settings.SyncToActiveProfile();
                    _settings.Save();
                    DrawBrakeCurveEditor();
                }
            }
        }

        private void BtnSaveBrakeCurvePreset_Click(object sender, RoutedEventArgs e) => BtnUniversalSave_Click(sender, e);

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
                    Stroke = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                    StrokeDashArray = new DoubleCollection { 3, 3 }
                };
                SteerCurveEditorCanvas.Children.Add(hLine);

                var vLine = new Line
                {
                    X1 = width * ratio, Y1 = 0,
                    X2 = width * ratio, Y2 = height,
                    Stroke = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                    StrokeDashArray = new DoubleCollection { 3, 3 }
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
                Stroke = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                StrokeThickness = 1.2
            };
            SteerCurveEditorCanvas.Children.Add(tang1);

            var tang2 = new Line
            {
                X1 = width, Y1 = 0,
                X2 = p2X, Y2 = p2Y,
                Stroke = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                StrokeThickness = 1.2
            };
            SteerCurveEditorCanvas.Children.Add(tang2);

            // Handle 1 Dot (P1 - Pure White)
            var dot1 = new Ellipse
            {
                Width = 14, Height = 14,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
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
                Fill = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
                Stroke = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                StrokeThickness = 2,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(dot2, p2X - 7);
            Canvas.SetTop(dot2, p2Y - 7);
            SteerCurveEditorCanvas.Children.Add(dot2);

            if (TxtSteerCurveHandlesReadout != null)
                TxtSteerCurveHandlesReadout.Text = $"P1 Center: ({_settings.SteerCustomP1X:F2}, {_settings.SteerCustomP1Y:F2}) • P2 Lock: ({_settings.SteerCustomP2X:F2}, {_settings.SteerCustomP2Y:F2})";
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
                if (TxtSteerCurveDescription != null) TxtSteerCurveDescription.Text = ResponseCurves.GetDescription(ResponseCurveType.Custom);
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

        private void BtnSaveSteerCurvePreset_Click(object sender, RoutedEventArgs e) => BtnUniversalSave_Click(sender, e);

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
