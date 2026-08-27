using System;
using System.Configuration;
using System.Data;
using System.IO.Ports;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Overseer.Models;
using Overseer.Services;

namespace Overseer
{
    public partial class App : System.Windows.Application
    {
        public static HardwareMonitorEngine? HardwareEngine { get; private set; }

        public App()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

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

            try
            {
                var splashWindow = new Overseer.SplashScreen();
                splashWindow.Show();

                await Task.Run(() =>
                {
                    HardwareEngine = new HardwareMonitorEngine();
                });

                var mainWindow = new MainWindow();
                System.Windows.Application.Current.MainWindow = mainWindow;
                mainWindow.Show();
                splashWindow.Close();
                AppLog.Write($"Overseer {typeof(App).Assembly.GetName().Version?.ToString(3) ?? "unknown"} started.");
            }
            catch (Exception ex)
            {
                AppLog.Write("Application startup failed.", ex);
                MessageBox.Show("Overseer could not start. See the application log for details.", "Overseer", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(-1);
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppLog.Write("Unhandled dispatcher exception.", e.Exception);
            e.Handled = true;
            MessageBox.Show("Overseer encountered an unexpected error. See the application log for details.", "Overseer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }

        private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            AppLog.Write("Unhandled application-domain exception.", e.ExceptionObject as Exception);
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            AppLog.Write("Unobserved task exception.", e.Exception);
            e.SetObserved();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppLog.Write("Overseer is shutting down.");
            HardwareEngine?.Dispose();
            base.OnExit(e);
        }
    }
}
