using System.Diagnostics;

namespace R.MessageBus.Core
{
    public class HeartbeatTimerState
    {
        public PerformanceCounter CpuCounter { get; set; }
        public PerformanceCounter RamCounter { get; set; }
    }
}