using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using KeyTrigger.Core.Engine;
using KeyTrigger.Core.Outputs;

namespace KeyTrigger.Models
{
    public class AppSettings
    {
        public List<ThrottleProfile> Profiles { get; set; } = new();
        public string ActiveProfileId { get; set; } = string.Empty;

        // Individual Axis Enable / Disable switches
        public bool EnableThrottle { get; set; } = true;
        public bool EnableBrake { get; set; } = true;
        public bool EnableSteering { get; set; } = true;

        public double AttackTimeSeconds { get; set; } = 0.90;
        public double ReleaseTimeSeconds { get; set; } = 0.45;
        public ResponseCurveType CurveType { get; set; } = ResponseCurveType.SmoothStep;

        public double CustomP1X { get; set; } = 0.35;
        public double CustomP1Y { get; set; } = 0.15;
        public double CustomP2X { get; set; } = 0.65;
        public double CustomP2Y { get; set; } = 0.85;

        public int ThrottleVkCode { get; set; } = 0x57; // 'W' (Physical key you press)
        public string ThrottleKeyName { get; set; } = "W";

        public bool EnableBrakeRamp { get; set; } = true;
        public int BrakeVkCode { get; set; } = 0x53; // 'S'
        public string BrakeKeyName { get; set; } = "S";
        public double BrakeAttackTimeSeconds { get; set; } = 0.45;
        public double BrakeReleaseTimeSeconds { get; set; } = 0.20;
        public ResponseCurveType BrakeCurveType { get; set; } = ResponseCurveType.SmoothStep;
        public double MinBrake { get; set; } = 0.0;
        public double MaxBrake { get; set; } = 1.0;
        public double BrakeCustomP1X { get; set; } = 0.35;
        public double BrakeCustomP1Y { get; set; } = 0.15;
        public double BrakeCustomP2X { get; set; } = 0.65;
        public double BrakeCustomP2Y { get; set; } = 0.85;

        // Steering Left Settings
        public bool EnableSteerLeftRamp { get; set; } = true;
        public int SteerLeftVkCode { get; set; } = 0x41; // 'A'
        public string SteerLeftKeyName { get; set; } = "A";
        public double SteerLeftAttackTimeSeconds { get; set; } = 0.25;
        public double SteerLeftReleaseTimeSeconds { get; set; } = 0.15;
        public ResponseCurveType SteerLeftCurveType { get; set; } = ResponseCurveType.Linear;
        public double MinSteerLeft { get; set; } = 0.0;
        public double MaxSteerLeft { get; set; } = 1.0;
        public double SteerLeftCustomP1X { get; set; } = 0.35;
        public double SteerLeftCustomP1Y { get; set; } = 0.15;
        public double SteerLeftCustomP2X { get; set; } = 0.65;
        public double SteerLeftCustomP2Y { get; set; } = 0.85;

        // Steering Right Settings
        public bool EnableSteerRightRamp { get; set; } = true;
        public int SteerRightVkCode { get; set; } = 0x44; // 'D'
        public string SteerRightKeyName { get; set; } = "D";
        public double SteerRightAttackTimeSeconds { get; set; } = 0.25;
        public double SteerRightReleaseTimeSeconds { get; set; } = 0.15;
        public ResponseCurveType SteerRightCurveType { get; set; } = ResponseCurveType.Linear;
        public double MinSteerRight { get; set; } = 0.0;
        public double MaxSteerRight { get; set; } = 1.0;
        public double SteerRightCustomP1X { get; set; } = 0.35;
        public double SteerRightCustomP1Y { get; set; } = 0.15;
        public double SteerRightCustomP2X { get; set; } = 0.65;
        public double SteerRightCustomP2Y { get; set; } = 0.85;
        // Unified Steering Properties
        [System.Text.Json.Serialization.JsonIgnore]
        public double SteerAttackTimeSeconds
        {
            get => SteerLeftAttackTimeSeconds;
            set { SteerLeftAttackTimeSeconds = value; SteerRightAttackTimeSeconds = value; }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public double SteerReleaseTimeSeconds
        {
            get => SteerLeftReleaseTimeSeconds;
            set { SteerLeftReleaseTimeSeconds = value; SteerRightReleaseTimeSeconds = value; }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public ResponseCurveType SteerCurveType
        {
            get => SteerLeftCurveType;
            set { SteerLeftCurveType = value; SteerRightCurveType = value; }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public double SteerCustomP1X
        {
            get => SteerLeftCustomP1X;
            set { SteerLeftCustomP1X = value; SteerRightCustomP1X = value; }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public double SteerCustomP1Y
        {
            get => SteerLeftCustomP1Y;
            set { SteerLeftCustomP1Y = value; SteerRightCustomP1Y = value; }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public double SteerCustomP2X
        {
            get => SteerLeftCustomP2X;
            set { SteerLeftCustomP2X = value; SteerRightCustomP2X = value; }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public double SteerCustomP2Y
        {
            get => SteerLeftCustomP2Y;
            set { SteerLeftCustomP2Y = value; SteerRightCustomP2Y = value; }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public double MinSteer
        {
            get => MinSteerLeft;
            set { MinSteerLeft = value; MinSteerRight = value; }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public double MaxSteer
        {
            get => MaxSteerLeft;
            set { MaxSteerLeft = value; MaxSteerRight = value; }
        }


        public bool BlockNativeKey { get; set; } = true;
        public double MinThrottle { get; set; } = 0.0;
        public double MaxThrottle { get; set; } = 1.0;

        public int ToggleHotKeyVk { get; set; } = 0x77; // F8
        public string ToggleHotKeyName { get; set; } = "F8";

        public bool StartMinimized { get; set; } = false;

        // Controller Button Remapping
        public List<ControllerButtonMapping> ButtonMappings { get; set; } = new();

        // Auto-Activation for Games
        public bool EnableAutoActivation { get; set; } = true;
        public bool PauseWhenUnfocused { get; set; } = true;
        public List<AutoActivateGame> AutoActivateGames { get; set; } = new();

        public void EnsureDefaultProfiles()
        {
            if (Profiles == null || Profiles.Count == 0)
            {
                Profiles = new List<ThrottleProfile>
                {
                    new ThrottleProfile
                    {
                        Name = "Sport / Track",
                        AttackTimeSeconds = 0.35,
                        ReleaseTimeSeconds = 0.15,
                        CurveType = ResponseCurveType.Progressive,
                        MinThrottle = 0.0,
                        MaxThrottle = 1.0,
                        HotkeyVkCode = 0x70, // F1
                        HotkeyName = "F1",
                        EnableBrakeRamp = true,
                        BrakeAttackTimeSeconds = 0.25,
                        BrakeReleaseTimeSeconds = 0.15,
                        BrakeCurveType = ResponseCurveType.Progressive,
                        MinBrake = 0.0,
                        MaxBrake = 1.0
                    },
                    new ThrottleProfile
                    {
                        Name = "Street / Balanced",
                        AttackTimeSeconds = 0.90,
                        ReleaseTimeSeconds = 0.45,
                        CurveType = ResponseCurveType.SmoothStep,
                        MinThrottle = 0.0,
                        MaxThrottle = 1.0,
                        HotkeyVkCode = 0x71, // F2
                        HotkeyName = "F2",
                        EnableBrakeRamp = true,
                        BrakeAttackTimeSeconds = 0.45,
                        BrakeReleaseTimeSeconds = 0.20,
                        BrakeCurveType = ResponseCurveType.SmoothStep,
                        MinBrake = 0.0,
                        MaxBrake = 1.0
                    },
                    new ThrottleProfile
                    {
                        Name = "Rain / Traction Control",
                        AttackTimeSeconds = 1.80,
                        ReleaseTimeSeconds = 0.50,
                        CurveType = ResponseCurveType.Exponential,
                        MinThrottle = 0.0,
                        MaxThrottle = 0.70, // Capped to 70% to prevent wheelspin
                        HotkeyVkCode = 0x72, // F3
                        HotkeyName = "F3",
                        EnableBrakeRamp = true,
                        BrakeAttackTimeSeconds = 0.85,
                        BrakeReleaseTimeSeconds = 0.35,
                        BrakeCurveType = ResponseCurveType.Exponential,
                        MinBrake = 0.0,
                        MaxBrake = 0.75 // Capped to prevent braking wheel lockup
                    },
                    new ThrottleProfile
                    {
                        Name = "Heavy Truck / Hauler",
                        AttackTimeSeconds = 2.80,
                        ReleaseTimeSeconds = 1.20,
                        CurveType = ResponseCurveType.SmootherStep,
                        MinThrottle = 0.05,
                        MaxThrottle = 1.0,
                        HotkeyVkCode = 0x73, // F4
                        HotkeyName = "F4",
                        EnableBrakeRamp = true,
                        BrakeAttackTimeSeconds = 1.30,
                        BrakeReleaseTimeSeconds = 0.55,
                        BrakeCurveType = ResponseCurveType.SmootherStep,
                        MinBrake = 0.0,
                        MaxBrake = 1.0
                    }
                };
            }

            if (string.IsNullOrEmpty(ActiveProfileId) || !Profiles.Any(p => p.Id == ActiveProfileId))
            {
                ActiveProfileId = Profiles[0].Id;
                SyncFromActiveProfile();
            }
        }

        

        
        public static List<ControllerButtonMapping> GetDefaultButtonMappings()
        {
            return new List<ControllerButtonMapping>
            {
                // Face Buttons
                new ControllerButtonMapping { ButtonId = "A", DisplayName = "A Button", Category = "Face Buttons", DefaultRole = "Handbrake / Select", VkCode = 0x20, KeyName = "Spacebar" },
                new ControllerButtonMapping { ButtonId = "B", DisplayName = "B Button", Category = "Face Buttons", DefaultRole = "Look Back / Cancel", VkCode = 0x42, KeyName = "B" },
                new ControllerButtonMapping { ButtonId = "X", DisplayName = "X Button", Category = "Face Buttons", DefaultRole = "Rewind / Flashback", VkCode = 0x52, KeyName = "R" },
                new ControllerButtonMapping { ButtonId = "Y", DisplayName = "Y Button", Category = "Face Buttons", DefaultRole = "Change Camera", VkCode = 0x43, KeyName = "C" },

                // Bumpers
                new ControllerButtonMapping { ButtonId = "LB", DisplayName = "Left Bumper (LB)", Category = "Bumpers", DefaultRole = "Shift Down / Clutch", VkCode = 0x51, KeyName = "Q" },
                new ControllerButtonMapping { ButtonId = "RB", DisplayName = "Right Bumper (RB)", Category = "Bumpers", DefaultRole = "Shift Up", VkCode = 0x45, KeyName = "E" },

                // Menu
                new ControllerButtonMapping { ButtonId = "Start", DisplayName = "Start Button", Category = "Menu", DefaultRole = "Pause / In-Game Menu", VkCode = 0x1B, KeyName = "Escape" },
                new ControllerButtonMapping { ButtonId = "Back", DisplayName = "Back / View Button", Category = "Menu", DefaultRole = "Car Status / Reset Car", VkCode = 0x09, KeyName = "Tab" },

                // Thumbstick Clicks
                new ControllerButtonMapping { ButtonId = "LS", DisplayName = "Left Stick Click (LS)", Category = "Thumbstick Clicks", DefaultRole = "Horn / Flash Lights", VkCode = 0x48, KeyName = "H" },
                new ControllerButtonMapping { ButtonId = "RS", DisplayName = "Right Stick Click (RS)", Category = "Thumbstick Clicks", DefaultRole = "Rear View / Reset Camera", VkCode = 0, KeyName = "None" },

                // Left Stick Up & Down
                new ControllerButtonMapping { ButtonId = "LS_Up", DisplayName = "Left Stick Up", Category = "Left Stick (LS)", DefaultRole = "Pitch Down / Lean Forward", VkCode = 0, KeyName = "None" },
                new ControllerButtonMapping { ButtonId = "LS_Down", DisplayName = "Left Stick Down", Category = "Left Stick (LS)", DefaultRole = "Pitch Up / Lean Back", VkCode = 0, KeyName = "None" },

                // Right Stick Directions
                new ControllerButtonMapping { ButtonId = "RS_Up", DisplayName = "Right Stick Up", Category = "Right Stick (RS)", DefaultRole = "Camera Look Up", VkCode = 0x49, KeyName = "I" },
                new ControllerButtonMapping { ButtonId = "RS_Down", DisplayName = "Right Stick Down", Category = "Right Stick (RS)", DefaultRole = "Camera Look Down", VkCode = 0x4B, KeyName = "K" },
                new ControllerButtonMapping { ButtonId = "RS_Left", DisplayName = "Right Stick Left", Category = "Right Stick (RS)", DefaultRole = "Camera Look Left", VkCode = 0x4A, KeyName = "J" },
                new ControllerButtonMapping { ButtonId = "RS_Right", DisplayName = "Right Stick Right", Category = "Right Stick (RS)", DefaultRole = "Camera Look Right", VkCode = 0x4C, KeyName = "L" },

                // D-Pad
                new ControllerButtonMapping { ButtonId = "Up", DisplayName = "D-Pad Up", Category = "D-Pad", DefaultRole = "Telemetry / MFD Up", VkCode = 0x26, KeyName = "Up Arrow" },
                new ControllerButtonMapping { ButtonId = "Down", DisplayName = "D-Pad Down", Category = "D-Pad", DefaultRole = "Telemetry / MFD Down", VkCode = 0x28, KeyName = "Down Arrow" },
                new ControllerButtonMapping { ButtonId = "Left", DisplayName = "D-Pad Left", Category = "D-Pad", DefaultRole = "Brake Bias Left / Indicator", VkCode = 0x25, KeyName = "Left Arrow" },
                new ControllerButtonMapping { ButtonId = "Right", DisplayName = "D-Pad Right", Category = "D-Pad", DefaultRole = "Brake Bias Right / Indicator", VkCode = 0x27, KeyName = "Right Arrow" }
            };
        }

        public void EnsureDefaultButtonMappings()
        {
            if (ButtonMappings == null || ButtonMappings.Count == 0)
            {
                ButtonMappings = GetDefaultButtonMappings();
                return;
            }

            var defaults = GetDefaultButtonMappings();
            foreach (var d in defaults)
            {
                if (!ButtonMappings.Any(m => m.ButtonId.Equals(d.ButtonId, StringComparison.OrdinalIgnoreCase)))
                {
                    ButtonMappings.Add(d);
                }
            }
        }

        public void SyncFromActiveProfile()
        {
            var active = GetActiveProfile();
            EnableThrottle = active.EnableThrottle;
            EnableBrake = active.EnableBrake;
            EnableSteering = active.EnableSteering;
            AttackTimeSeconds = active.AttackTimeSeconds;
            ReleaseTimeSeconds = active.ReleaseTimeSeconds;
            CurveType = active.CurveType;
            MinThrottle = active.MinThrottle;
            MaxThrottle = active.MaxThrottle;
            CustomP1X = active.CustomP1X;
            CustomP1Y = active.CustomP1Y;
            CustomP2X = active.CustomP2X;
            CustomP2Y = active.CustomP2Y;

            EnableBrakeRamp = active.EnableBrakeRamp;
            BrakeAttackTimeSeconds = active.BrakeAttackTimeSeconds;
            BrakeReleaseTimeSeconds = active.BrakeReleaseTimeSeconds;
            BrakeCurveType = active.BrakeCurveType;
            MinBrake = active.MinBrake;
            MaxBrake = active.MaxBrake;
            BrakeCustomP1X = active.BrakeCustomP1X;
            BrakeCustomP1Y = active.BrakeCustomP1Y;
            BrakeCustomP2X = active.BrakeCustomP2X;
            BrakeCustomP2Y = active.BrakeCustomP2Y;

            EnableSteerLeftRamp = active.EnableSteerLeftRamp;
            SteerLeftAttackTimeSeconds = active.SteerLeftAttackTimeSeconds;
            SteerLeftReleaseTimeSeconds = active.SteerLeftReleaseTimeSeconds;
            SteerLeftCurveType = active.SteerLeftCurveType;
            MinSteerLeft = active.MinSteerLeft;
            MaxSteerLeft = active.MaxSteerLeft;
            SteerLeftCustomP1X = active.SteerLeftCustomP1X;
            SteerLeftCustomP1Y = active.SteerLeftCustomP1Y;
            SteerLeftCustomP2X = active.SteerLeftCustomP2X;
            SteerLeftCustomP2Y = active.SteerLeftCustomP2Y;

            EnableSteerRightRamp = active.EnableSteerRightRamp;
            SteerRightAttackTimeSeconds = active.SteerRightAttackTimeSeconds;
            SteerRightReleaseTimeSeconds = active.SteerRightReleaseTimeSeconds;
            SteerRightCurveType = active.SteerRightCurveType;
            MinSteerRight = active.MinSteerRight;
            MaxSteerRight = active.MaxSteerRight;
            SteerRightCustomP1X = active.SteerRightCustomP1X;
            SteerRightCustomP1Y = active.SteerRightCustomP1Y;
            SteerRightCustomP2X = active.SteerRightCustomP2X;
            SteerRightCustomP2Y = active.SteerRightCustomP2Y;

            // Keymaps & Controller Remaps
            if (active.ThrottleVkCode != 0)
            {
                ThrottleVkCode = active.ThrottleVkCode;
                ThrottleKeyName = string.IsNullOrEmpty(active.ThrottleKeyName) ? "W" : active.ThrottleKeyName;
            }
            if (active.BrakeVkCode != 0)
            {
                BrakeVkCode = active.BrakeVkCode;
                BrakeKeyName = string.IsNullOrEmpty(active.BrakeKeyName) ? "S" : active.BrakeKeyName;
            }
            if (active.SteerLeftVkCode != 0)
            {
                SteerLeftVkCode = active.SteerLeftVkCode;
                SteerLeftKeyName = string.IsNullOrEmpty(active.SteerLeftKeyName) ? "A" : active.SteerLeftKeyName;
            }
            if (active.SteerRightVkCode != 0)
            {
                SteerRightVkCode = active.SteerRightVkCode;
                SteerRightKeyName = string.IsNullOrEmpty(active.SteerRightKeyName) ? "D" : active.SteerRightKeyName;
            }

            if (active.ButtonMappings != null && active.ButtonMappings.Count > 0)
            {
                ButtonMappings = active.ButtonMappings.ConvertAll(m => new ControllerButtonMapping
                {
                    ButtonId = m.ButtonId,
                    DisplayName = m.DisplayName,
                    Category = m.Category,
                    DefaultRole = m.DefaultRole,
                    VkCode = m.VkCode,
                    KeyName = m.KeyName
                });
                EnsureDefaultButtonMappings();
            }
            else
            {
                ButtonMappings = GetDefaultButtonMappings();
            }
        }

        public void SyncToActiveProfile()
        {
            var active = Profiles.FirstOrDefault(p => p.Id == ActiveProfileId);
            if (active != null)
            {
                active.EnableThrottle = EnableThrottle;
                active.EnableBrake = EnableBrake;
                active.EnableSteering = EnableSteering;
                active.AttackTimeSeconds = AttackTimeSeconds;
                active.ReleaseTimeSeconds = ReleaseTimeSeconds;
                active.CurveType = CurveType;
                active.MinThrottle = MinThrottle;
                active.MaxThrottle = MaxThrottle;
                active.CustomP1X = CustomP1X;
                active.CustomP1Y = CustomP1Y;
                active.CustomP2X = CustomP2X;
                active.CustomP2Y = CustomP2Y;

                active.EnableBrakeRamp = EnableBrakeRamp;
                active.BrakeAttackTimeSeconds = BrakeAttackTimeSeconds;
                active.BrakeReleaseTimeSeconds = BrakeReleaseTimeSeconds;
                active.BrakeCurveType = BrakeCurveType;
                active.MinBrake = MinBrake;
                active.MaxBrake = MaxBrake;
                active.BrakeCustomP1X = BrakeCustomP1X;
                active.BrakeCustomP1Y = BrakeCustomP1Y;
                active.BrakeCustomP2X = BrakeCustomP2X;
                active.BrakeCustomP2Y = BrakeCustomP2Y;

                active.EnableSteerLeftRamp = EnableSteerLeftRamp;
                active.SteerLeftAttackTimeSeconds = SteerLeftAttackTimeSeconds;
                active.SteerLeftReleaseTimeSeconds = SteerLeftReleaseTimeSeconds;
                active.SteerLeftCurveType = SteerLeftCurveType;
                active.MinSteerLeft = MinSteerLeft;
                active.MaxSteerLeft = MaxSteerLeft;
                active.SteerLeftCustomP1X = SteerLeftCustomP1X;
                active.SteerLeftCustomP1Y = SteerLeftCustomP1Y;
                active.SteerLeftCustomP2X = SteerLeftCustomP2X;
                active.SteerLeftCustomP2Y = SteerLeftCustomP2Y;

                active.EnableSteerRightRamp = EnableSteerRightRamp;
                active.SteerRightAttackTimeSeconds = SteerRightAttackTimeSeconds;
                active.SteerRightReleaseTimeSeconds = SteerRightReleaseTimeSeconds;
                active.SteerRightCurveType = SteerRightCurveType;
                active.MinSteerRight = MinSteerRight;
                active.MaxSteerRight = MaxSteerRight;
                active.SteerRightCustomP1X = SteerRightCustomP1X;
                active.SteerRightCustomP1Y = SteerRightCustomP1Y;
                active.SteerRightCustomP2X = SteerRightCustomP2X;
                active.SteerRightCustomP2Y = SteerRightCustomP2Y;

                // Keymaps & Controller Remaps
                active.ThrottleVkCode = ThrottleVkCode;
                active.ThrottleKeyName = ThrottleKeyName;
                active.BrakeVkCode = BrakeVkCode;
                active.BrakeKeyName = BrakeKeyName;
                active.SteerLeftVkCode = SteerLeftVkCode;
                active.SteerLeftKeyName = SteerLeftKeyName;
                active.SteerRightVkCode = SteerRightVkCode;
                active.SteerRightKeyName = SteerRightKeyName;

                if (ButtonMappings != null && ButtonMappings.Count > 0)
                {
                    active.ButtonMappings = ButtonMappings.ConvertAll(m => new ControllerButtonMapping
                    {
                        ButtonId = m.ButtonId,
                        DisplayName = m.DisplayName,
                        Category = m.Category,
                        DefaultRole = m.DefaultRole,
                        VkCode = m.VkCode,
                        KeyName = m.KeyName
                    });
                }
            }
        }

        

        public ThrottleProfile GetActiveProfile()
        {
            EnsureDefaultProfiles();
            return Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles[0];
        }

        private static string GetConfigPath()
        {
            // Portable mode: Save settings.json alongside KeyTrigger.exe in the application directory
            string appDir = AppContext.BaseDirectory;
            string localPath = Path.Combine(appDir, "settings.json");

            if (!File.Exists(localPath))
            {
                // Auto-migrate from %APPDATA%\KeyTrigger\settings.json if it exists so user keeps their setup
                try
                {
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string appDataFile = Path.Combine(appData, "KeyTrigger", "settings.json");

                    if (File.Exists(appDataFile))
                    {
                        File.Copy(appDataFile, localPath, true);
                    }
                }
                catch { }
            }

            return localPath;
        }

        public static AppSettings Load()
        {
            try
            {
                string path = GetConfigPath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        settings.EnsureDefaultProfiles();
                        settings.EnsureDefaultButtonMappings();
                        settings.SyncFromActiveProfile();
                        return settings;
                    }
                }
            }
            catch
            {
                // Fall back to default
            }

            var s = new AppSettings();
            s.EnsureDefaultProfiles();
            s.SyncFromActiveProfile();
            return s;
        }

        public void Save()
        {
            try
            {
                string path = GetConfigPath();
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(path, json);
            }
            catch
            {
                // Silently ignore save errors
            }
        }

        
    }

    public class AutoActivateGame
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string ExePath { get; set; } = string.Empty;
        public string ExeFileName { get; set; } = string.Empty;
        public string LinkedProfileId { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
    }

    public class ControllerButtonMapping
    {
        public string ButtonId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Category { get; set; } = "Action / Face";
        public string DefaultRole { get; set; } = string.Empty;
        public int VkCode { get; set; } = 0;
        public string KeyName { get; set; } = "None";
    }
}
