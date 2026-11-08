using System;

namespace RunEverSettings
{
    public static class Probe
    {
        [STAThread]
        public static void Main()
        {
            var app = new System.Windows.Application();
            app.Run(new ProbeWindow());
        }
    }
}
