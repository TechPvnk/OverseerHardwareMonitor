using System;
using System.Configuration;
using System.Data;
using System.IO.Ports;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Overseer.Models;

namespace Overseer
{
    public partial class App : System.Windows.Application
    {
        public static HardwareMonitorEngine? HardwareEngine { get; private set; }

        public App()
        {
            // Intercept assembly version mismatches from the nightly LHM build
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                var requestedName = new AssemblyName(args.Name);
                if (requestedName.Name == "System.IO.Ports")
                {
                    return typeof(SerialPort).Assembly;
                }
                return null;
            };
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Call it directly using the Overseer namespace
            var splashWindow = new Overseer.SplashScreen();
            splashWindow.Show();

            await Task.Run(() =>
            {
                HardwareEngine = new HardwareMonitorEngine();
            });

            // Check your MainWindow x:Class as well (Overseer)
            var mainWindow = new MainWindow();

            // Make sure the newly created main window becomes the application's MainWindow
            // so any dialogs or owners resolve to it instead of the splash window.
            System.Windows.Application.Current.MainWindow = mainWindow;

            mainWindow.Show();

            splashWindow.Close();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            HardwareEngine?.Dispose();
            base.OnExit(e);
        }
    }
}