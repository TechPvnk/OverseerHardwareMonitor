using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Overseer;

public partial class SplashScreen : Window
{
    public SplashScreen()
    {
        InitializeComponent();
    }

    public void UpdateStatus(string message)
    {
        // Dispatcher guarantees thread-safety if called during background tasks
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = message;
        });
    }
}