using System;
using System.Threading;
using System.Windows;

namespace ClaudeBuddy
{
    public partial class App : Application
    {
        private Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Prevent launching multiple buddies by accident.
            _singleInstanceMutex = new Mutex(true, "ClaudeBuddy_SingleInstance_Mutex", out bool isNew);
            if (!isNew)
            {
                Shutdown();
                return;
            }

            base.OnStartup(e);

            new SessionManager().Start();
        }
    }
}
