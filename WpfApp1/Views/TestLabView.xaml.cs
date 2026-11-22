using System;
using System.Windows;
using System.Windows.Controls;
using Overseer.Services;
using Overseer.ViewModels;

namespace Overseer.Views;

public partial class TestLabView : UserControl
{
    public TestLabView()
    {
        InitializeComponent();
    }

    private async void StartBenchmark_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is TestLabViewModel { CanStartTest: true } testLab)
        {
            await testLab.StartBenchmarkAsync();
        }
    }

    private async void StartStress_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TestLabViewModel { CanStartTest: true } testLab) return;

        if (testLab.ShouldShowStressWarning)
        {
            LocalizationService strings = LocalizationService.Instance;
            string message = strings["TestLabStressWarning"];
            if (testLab.IsBatteryPower)
            {
                message += Environment.NewLine + Environment.NewLine + strings["TestLabBatteryWarning"];
            }
            if (MessageBox.Show(Window.GetWindow(this), message, strings["TestLabStressWarningTitle"],
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }
        }

        await testLab.StartStressAsync();
    }

    private async void StopTest_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is TestLabViewModel testLab) await testLab.StopAsync();
    }

    private void CopyBenchmark_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is TestLabViewModel { HasBenchmarkResult: true } testLab)
        {
            CopyResult(testLab, "TestLabLastBenchmark", testLab.LastBenchmarkSummary);
        }
    }

    private void CopyStress_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is TestLabViewModel { HasStressResult: true } testLab)
        {
            CopyResult(testLab, "TestLabLastStress", testLab.LastStressSummary);
        }
    }

    private static void CopyResult(TestLabViewModel testLab, string titleKey, string summary)
    {
        Clipboard.SetText(string.Join(Environment.NewLine,
            LocalizationService.Instance[titleKey], testLab.CpuModel, testLab.CpuCoresThreads, string.Empty, summary));
    }
}
