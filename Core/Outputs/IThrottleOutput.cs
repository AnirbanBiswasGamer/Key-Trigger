using System;

namespace KeyTrigger.Core.Outputs
{
    public enum OutputMode
    {
        VirtualGamepad
    }

    public interface IThrottleOutput : IDisposable
    {
        OutputMode Mode { get; }
        string Name { get; }
        bool IsAvailable { get; }
        string StatusMessage { get; }

        void Initialize();
        void Update(double throttle, double brake, double steer = 0.0);
        void Reset();
        void SetButton(string buttonId, bool pressed, bool force = false);
        void ResetButtons();
    }
}
