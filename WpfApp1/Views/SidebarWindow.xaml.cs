using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Overseer.Services;
using Overseer.ViewModels;
using FormsScreen = System.Windows.Forms.Screen;

namespace Overseer.Views;

public partial class SidebarWindow : Window
{
    private const int ExtendedStyleIndex = -20;
    private const long TransparentStyle = 0x00000020L;
    private const long NoActivateStyle = 0x08000000L;
    private const double VerticalSidebarWidth = 320d;
    private const double VerticalMinimumHeight = 220d;
    private const double HorizontalMinimumHeight = 150d;
    private const double SidebarChromeHeight = 86d;
    private const uint SetPositionNoSize = 0x0001;
    private const uint SetPositionNoZOrder = 0x0004;
    private const uint SetPositionNoActivate = 0x0010;
    private readonly SidebarViewModel _viewModel;
    private readonly DispatcherTimer _clickThroughTimer;

    public SidebarWindow(SidebarViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += SidebarWindow_Loaded;
        DpiChanged += SidebarWindow_DpiChanged;
        _viewModel.PropertyChanged += ViewModelPropertyChanged;
        _clickThroughTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(50d)
        };
        _clickThroughTimer.Tick += ClickThroughTimer_Tick;
    }

    public bool IsClickThrough
    {
        get => _viewModel.IsClickThrough;
        set => _viewModel.IsClickThrough = value;
    }

    public event EventHandler? ClickThroughChanged;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyClickThrough();
    }

    private void SidebarWindow_Loaded(object sender, RoutedEventArgs e)
    {
        QueuePreferredBounds();
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SidebarViewModel.ShowMinMax)
            || e.PropertyName == nameof(SidebarViewModel.VisibleModuleCount)
            || e.PropertyName == nameof(SidebarViewModel.DockEdge))
        {
            QueuePreferredBounds();
        }
        else if (e.PropertyName == nameof(SidebarViewModel.IsClickThrough))
        {
            ApplyClickThrough();
            ClickThroughChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void QueuePreferredBounds()
    {
        Dispatcher.BeginInvoke(ApplyPreferredBounds, DispatcherPriority.Loaded);
    }

    private void ApplyClickThrough()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        long styles = GetWindowLongPtr(handle, ExtendedStyleIndex).ToInt64();
        bool keepHeaderInteractive = _viewModel.IsClickThrough && IsCursorOverHeaderControl();
        long updatedStyles = _viewModel.IsClickThrough
            ? keepHeaderInteractive
                ? (styles | NoActivateStyle) & ~TransparentStyle
                : styles | NoActivateStyle | TransparentStyle
            : styles & ~(NoActivateStyle | TransparentStyle);
        if (updatedStyles != styles)
        {
            SetWindowLongPtr(handle, ExtendedStyleIndex, new IntPtr(updatedStyles));
        }

        if (_viewModel.IsClickThrough)
        {
            _clickThroughTimer.Start();
        }
        else
        {
            _clickThroughTimer.Stop();
        }
    }

    private void ClickThroughTimer_Tick(object? sender, EventArgs e) => ApplyClickThrough();

    private bool IsCursorOverHeaderControl()
    {
        if (!GetCursorPos(out NativePoint cursorPosition))
        {
            return false;
        }

        Point windowPoint = PointFromScreen(new Point(cursorPosition.X, cursorPosition.Y));
        return IsPointInside(AlwaysOnTopButton, windowPoint)
            || IsPointInside(SidebarSettingsButton, windowPoint)
            || IsPointInside(CloseButton, windowPoint)
            || IsPointInside(OpacityButton, windowPoint)
            || IsPointInside(TopDockButton, windowPoint)
            || IsPointInside(LeftDockButton, windowPoint)
            || IsPointInside(RightDockButton, windowPoint)
            || IsPointInside(BottomDockButton, windowPoint);
    }

    private bool IsPointInside(FrameworkElement element, Point windowPoint)
    {
        if (!element.IsVisible || element.ActualWidth <= 0d || element.ActualHeight <= 0d)
        {
            return false;
        }

        Point origin = element.TranslatePoint(new Point(0d, 0d), this);
        return new Rect(origin, element.RenderSize).Contains(windowPoint);
    }

    private void ApplyPreferredBounds()
    {
        FormsScreen screen = ResolveDockScreen();
        System.Drawing.Rectangle workArea = screen.WorkingArea;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        bool horizontal = IsHorizontalDock();
        MinHeight = horizontal ? HorizontalMinimumHeight : VerticalMinimumHeight;
        Width = horizontal
            ? Math.Max(MinWidth, (workArea.Width / dpi.DpiScaleX) - 24d)
            : VerticalSidebarWidth;
        UpdateLayout();
        ApplyModuleLayout(horizontal);
        ModulesPanel.Measure(new Size(Math.Max(1d, ModulesScrollViewer.ViewportWidth), double.PositiveInfinity));
        double preferredHeight = SidebarChromeHeight + ModulesPanel.DesiredSize.Height;
        double maximumHeight = Math.Max(MinHeight, (workArea.Height / dpi.DpiScaleY) - 24d);
        Height = Math.Min(preferredHeight, maximumHeight);
        UpdateLayout();

        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out NativeRect windowRect))
        {
            return;
        }

        int horizontalMargin = Math.Max(8, (int)Math.Round(12d * dpi.DpiScaleX));
        int verticalMargin = Math.Max(8, (int)Math.Round(12d * dpi.DpiScaleY));
        int x;
        int y;
        switch (_viewModel.DockEdge)
        {
            case SidebarDockEdge.Top:
                x = workArea.Left + horizontalMargin;
                y = workArea.Top + verticalMargin;
                break;
            case SidebarDockEdge.Bottom:
                x = workArea.Left + horizontalMargin;
                y = workArea.Bottom - windowRect.Height - verticalMargin;
                break;
            case SidebarDockEdge.Left:
                x = workArea.Left + horizontalMargin;
                y = workArea.Top + verticalMargin;
                break;
            default:
                x = workArea.Right - windowRect.Width - horizontalMargin;
                y = workArea.Top + verticalMargin;
                break;
        }
        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SetPositionNoSize | SetPositionNoZOrder | SetPositionNoActivate);
    }

    private bool IsHorizontalDock() => _viewModel.DockEdge is SidebarDockEdge.Top or SidebarDockEdge.Bottom;

    private void ApplyModuleLayout(bool horizontal)
    {
        ModulesPanel.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        double availableWidth = Math.Max(1d, ModulesScrollViewer.ViewportWidth);
        int visibleModules = Math.Max(1, _viewModel.VisibleModuleCount);
        double horizontalCardWidth = availableWidth / visibleModules;

        foreach (UIElement child in ModulesPanel.Children)
        {
            if (child is not FrameworkElement module)
            {
                continue;
            }

            if (horizontal)
            {
                module.Width = horizontalCardWidth;
                module.Margin = new Thickness(0d, 0d, 0d, 0d);
            }
            else
            {
                module.ClearValue(WidthProperty);
                module.Margin = new Thickness(0d, 0d, 0d, 5d);
            }
        }
    }

    private FormsScreen ResolveDockScreen()
    {
        string? configuredName = SidebarSettingsService.Instance.Settings.MonitorDeviceName;
        FormsScreen? configured = FormsScreen.AllScreens.FirstOrDefault(
            screen => string.Equals(screen.DeviceName, configuredName, StringComparison.OrdinalIgnoreCase));
        if (configured is not null)
        {
            return configured;
        }

        FormsScreen fallback = FormsScreen.PrimaryScreen ?? FormsScreen.FromHandle(new WindowInteropHelper(this).Handle);
        RememberMonitor(fallback);
        return fallback;
    }

    private void RememberCurrentMonitor()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            RememberMonitor(FormsScreen.FromHandle(handle));
        }
    }

    private static void RememberMonitor(FormsScreen screen)
    {
        SidebarSettings settings = SidebarSettingsService.Instance.Settings;
        if (string.Equals(settings.MonitorDeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        settings.MonitorDeviceName = screen.DeviceName;
        SidebarSettingsService.Instance.Save();
    }

    private void SidebarWindow_DpiChanged(object sender, DpiChangedEventArgs e) => QueuePreferredBounds();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
            RememberCurrentMonitor();
            QueuePreferredBounds();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            _viewModel.Telemetry.ResetStatistics();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SidebarSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu is not null)
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
        }
    }

    private void DriveMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        ContextMenu menu = new()
        {
            PlacementTarget = button,
            Style = (Style)FindResource("SidebarContextMenuStyle"),
            ItemContainerStyle = (Style)FindResource("SidebarContextMenuItemStyle")
        };

        foreach (MainViewModel.DriveTemperatureViewModel drive in _viewModel.Telemetry.DriveTemperatures)
        {
            MenuItem item = new()
            {
                Header = drive.Name,
                IsCheckable = true,
                IsChecked = ReferenceEquals(drive, _viewModel.SelectedDrive)
            };
            item.Click += (_, _) => _viewModel.SelectDrive(drive);
            menu.Items.Add(item);
        }

        if (menu.Items.Count > 0)
        {
            menu.IsOpen = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= SidebarWindow_Loaded;
        DpiChanged -= SidebarWindow_DpiChanged;
        _viewModel.PropertyChanged -= ViewModelPropertyChanged;
        _clickThroughTimer.Stop();
        _clickThroughTimer.Tick -= ClickThroughTimer_Tick;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr newValue);
}
