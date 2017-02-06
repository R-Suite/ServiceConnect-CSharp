using System;

namespace ProcessManager.Client
{
    public static class State
    {
        public static DateTime Start { get; set; }
        public static int Pms { get; set; }
        public static bool Finished { get; internal set; }
    }
}