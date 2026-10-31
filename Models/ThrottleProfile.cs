using System;
using System.Text.Json.Serialization;
using KeyTrigger.Core.Engine;

namespace KeyTrigger.Models
{
    public class ThrottleProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "Sport";

        // Axis Enable / Disable switches
        public bool EnableThrottle { get; set; } = true;
        public bool EnableBrake { get; set; } = true;
        public bool EnableSteering { get; set; } = true;

        // Throttle Settings
        public double AttackTimeSeconds { get; set; } = 0.40;
        public double ReleaseTimeSeconds { get; set; } = 0.20;
        public ResponseCurveType CurveType { get; set; } = ResponseCurveType.Progressive;
        public double MinThrottle { get; set; } = 0.0;
        public double MaxThrottle { get; set; } = 1.0;
        public int HotkeyVkCode { get; set; } = 0x70; // e.g. F1
        public string HotkeyName { get; set; } = "F1";

        // Custom Bézier control points for Throttle
        public double CustomP1X { get; set; } = 0.35;
        public double CustomP1Y { get; set; } = 0.15;
        public double CustomP2X { get; set; } = 0.65;
        public double CustomP2Y { get; set; } = 0.85;

        // Brake Settings
        public bool EnableBrakeRamp { get; set; } = true;
        public double BrakeAttackTimeSeconds { get; set; } = 0.45;
        public double BrakeReleaseTimeSeconds { get; set; } = 0.20;
        public ResponseCurveType BrakeCurveType { get; set; } = ResponseCurveType.SmoothStep;
        public double MinBrake { get; set; } = 0.0;
        public double MaxBrake { get; set; } = 1.0;

        // Custom Bézier control points for Brake
        public double BrakeCustomP1X { get; set; } = 0.35;
        public double BrakeCustomP1Y { get; set; } = 0.15;
        public double BrakeCustomP2X { get; set; } = 0.65;
        public double BrakeCustomP2Y { get; set; } = 0.85;

        // Steering Left Settings (stored for full backwards compatibility)
        public bool EnableSteerLeftRamp { get; set; } = true;
        public double SteerLeftAttackTimeSeconds { get; set; } = 0.25;
        public double SteerLeftReleaseTimeSeconds { get; set; } = 0.15;
        public ResponseCurveType SteerLeftCurveType { get; set; } = ResponseCurveType.Linear;
        public double MinSteerLeft { get; set; } = 0.0;
        public double MaxSteerLeft { get; set; } = 1.0;
        public double SteerLeftCustomP1X { get; set; } = 0.35;
        public double SteerLeftCustomP1Y { get; set; } = 0.15;
        public double SteerLeftCustomP2X { get; set; } = 0.65;
        public double SteerLeftCustomP2Y { get; set; } = 0.85;

        // Steering Right Settings (stored for full backwards compatibility)
        public bool EnableSteerRightRamp { get; set; } = true;
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
        [JsonIgnore]
        public double SteerAttackTimeSeconds
        {
            get => SteerLeftAttackTimeSeconds;
            set { SteerLeftAttackTimeSeconds = value; SteerRightAttackTimeSeconds = value; }
        }

        [JsonIgnore]
        public double SteerReleaseTimeSeconds
        {
            get => SteerLeftReleaseTimeSeconds;
            set { SteerLeftReleaseTimeSeconds = value; SteerRightReleaseTimeSeconds = value; }
        }

        [JsonIgnore]
        public ResponseCurveType SteerCurveType
        {
            get => SteerLeftCurveType;
            set { SteerLeftCurveType = value; SteerRightCurveType = value; }
        }

        [JsonIgnore]
        public double SteerCustomP1X
        {
            get => SteerLeftCustomP1X;
            set { SteerLeftCustomP1X = value; SteerRightCustomP1X = value; }
        }

        [JsonIgnore]
        public double SteerCustomP1Y
        {
            get => SteerLeftCustomP1Y;
            set { SteerLeftCustomP1Y = value; SteerRightCustomP1Y = value; }
        }

        [JsonIgnore]
        public double SteerCustomP2X
        {
            get => SteerLeftCustomP2X;
            set { SteerLeftCustomP2X = value; SteerRightCustomP2X = value; }
        }

        [JsonIgnore]
        public double SteerCustomP2Y
        {
            get => SteerLeftCustomP2Y;
            set { SteerLeftCustomP2Y = value; SteerRightCustomP2Y = value; }
        }

        [JsonIgnore]
        public double MinSteer
        {
            get => MinSteerLeft;
            set { MinSteerLeft = value; MinSteerRight = value; }
        }

        [JsonIgnore]
        public double MaxSteer
        {
            get => MaxSteerLeft;
            set { MaxSteerLeft = value; MaxSteerRight = value; }
        }

        // Keymaps & Controller Mappings (Per Profile)
        public int ThrottleVkCode { get; set; } = 0x57; // W
        public string ThrottleKeyName { get; set; } = "W";
        public int BrakeVkCode { get; set; } = 0x53; // S
        public string BrakeKeyName { get; set; } = "S";
        public int SteerLeftVkCode { get; set; } = 0x41; // A
        public string SteerLeftKeyName { get; set; } = "A";
        public int SteerRightVkCode { get; set; } = 0x44; // D
        public string SteerRightKeyName { get; set; } = "D";

        public System.Collections.Generic.List<ControllerButtonMapping>? ButtonMappings { get; set; }

        public ThrottleProfile Clone()
        {
            return new ThrottleProfile
            {
                Id = Guid.NewGuid().ToString(),
                Name = Name + " (Copy)",
                EnableThrottle = EnableThrottle,
                EnableBrake = EnableBrake,
                EnableSteering = EnableSteering,

                AttackTimeSeconds = AttackTimeSeconds,
                ReleaseTimeSeconds = ReleaseTimeSeconds,
                CurveType = CurveType,
                MinThrottle = MinThrottle,
                MaxThrottle = MaxThrottle,
                HotkeyVkCode = 0,
                HotkeyName = "None",
                CustomP1X = CustomP1X,
                CustomP1Y = CustomP1Y,
                CustomP2X = CustomP2X,
                CustomP2Y = CustomP2Y,

                EnableBrakeRamp = EnableBrakeRamp,
                BrakeAttackTimeSeconds = BrakeAttackTimeSeconds,
                BrakeReleaseTimeSeconds = BrakeReleaseTimeSeconds,
                BrakeCurveType = BrakeCurveType,
                MinBrake = MinBrake,
                MaxBrake = MaxBrake,
                BrakeCustomP1X = BrakeCustomP1X,
                BrakeCustomP1Y = BrakeCustomP1Y,
                BrakeCustomP2X = BrakeCustomP2X,
                BrakeCustomP2Y = BrakeCustomP2Y,

                EnableSteerLeftRamp = EnableSteerLeftRamp,
                SteerLeftAttackTimeSeconds = SteerLeftAttackTimeSeconds,
                SteerLeftReleaseTimeSeconds = SteerLeftReleaseTimeSeconds,
                SteerLeftCurveType = SteerLeftCurveType,
                MinSteerLeft = MinSteerLeft,
                MaxSteerLeft = MaxSteerLeft,
                SteerLeftCustomP1X = SteerLeftCustomP1X,
                SteerLeftCustomP1Y = SteerLeftCustomP1Y,
                SteerLeftCustomP2X = SteerLeftCustomP2X,
                SteerLeftCustomP2Y = SteerLeftCustomP2Y,

                EnableSteerRightRamp = EnableSteerRightRamp,
                SteerRightAttackTimeSeconds = SteerRightAttackTimeSeconds,
                SteerRightReleaseTimeSeconds = SteerRightReleaseTimeSeconds,
                SteerRightCurveType = SteerRightCurveType,
                MinSteerRight = MinSteerRight,
                MaxSteerRight = MaxSteerRight,
                SteerRightCustomP1X = SteerRightCustomP1X,
                SteerRightCustomP1Y = SteerRightCustomP1Y,
                SteerRightCustomP2X = SteerRightCustomP2X,
                SteerRightCustomP2Y = SteerRightCustomP2Y,

                ThrottleVkCode = ThrottleVkCode,
                ThrottleKeyName = ThrottleKeyName,
                BrakeVkCode = BrakeVkCode,
                BrakeKeyName = BrakeKeyName,
                SteerLeftVkCode = SteerLeftVkCode,
                SteerLeftKeyName = SteerLeftKeyName,
                SteerRightVkCode = SteerRightVkCode,
                SteerRightKeyName = SteerRightKeyName,
                ButtonMappings = ButtonMappings?.ConvertAll(m => m.Clone())
            };
        }
    }
}
