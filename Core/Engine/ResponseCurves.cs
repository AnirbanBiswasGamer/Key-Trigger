using System;

namespace KeyTrigger.Core.Engine
{
    public enum ResponseCurveType
    {
        Linear,
        SmoothStep,
        SmootherStep,
        Exponential,
        Progressive,
        Aggressive,
        Custom
    }

    public static class ResponseCurves
    {
        public static double Evaluate(ResponseCurveType type, double t, double p1x = 0.35, double p1y = 0.15, double p2x = 0.65, double p2y = 0.85)
        {
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;

            return type switch
            {
                ResponseCurveType.Linear => t,
                ResponseCurveType.SmoothStep => t * t * (3.0 - 2.0 * t),
                ResponseCurveType.SmootherStep => t * t * t * (t * (t * 6.0 - 15.0) + 10.0),
                ResponseCurveType.Exponential => t * t,
                ResponseCurveType.Progressive => Math.Pow(t, 1.5),
                ResponseCurveType.Aggressive => Math.Sqrt(t),
                ResponseCurveType.Custom => EvaluateCubicBezier(t, p1x, p1y, p2x, p2y),
                _ => t
            };
        }

        public static double EvaluateCubicBezier(double t, double p1x, double p1y, double p2x, double p2y)
        {
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;

            // Clamp control points to valid 0..1 ranges
            p1x = Math.Clamp(p1x, 0.01, 0.99);
            p2x = Math.Clamp(p2x, 0.01, 0.99);
            p1y = Math.Clamp(p1y, 0.0, 1.0);
            p2y = Math.Clamp(p2y, 0.0, 1.0);

            // Binary search to find parameter s where X(s) = t
            double low = 0.0, high = 1.0;
            double s = t;

            for (int i = 0; i < 14; i++)
            {
                s = (low + high) * 0.5;
                double oneMinusS = 1.0 - s;
                double x = 3.0 * oneMinusS * oneMinusS * s * p1x +
                           3.0 * oneMinusS * s * s * p2x +
                           s * s * s;

                if (x < t)
                    low = s;
                else
                    high = s;
            }

            double om = 1.0 - s;
            double y = 3.0 * om * om * s * p1y +
                       3.0 * om * s * s * p2y +
                       s * s * s;

            return Math.Clamp(y, 0.0, 1.0);
        }

        public static string GetDescription(ResponseCurveType type)
        {
            return type switch
            {
                ResponseCurveType.Linear => "Constant 1:1 acceleration rate",
                ResponseCurveType.SmoothStep => "S-Curve: Smooth launch and gentle max transition",
                ResponseCurveType.SmootherStep => "Ultra S-Curve: Extra gentle launch and soft plateau",
                ResponseCurveType.Exponential => "Soft initial touch for grip, explosive top end",
                ResponseCurveType.Progressive => "Balanced realistic automotive pedal curve",
                ResponseCurveType.Aggressive => "Quick initial bite tapering toward full throttle",
                ResponseCurveType.Custom => "Custom Interactive Bézier: Drag handles P1 & P2 to sculpt response",
                _ => ""
            };
        }
    }
}
