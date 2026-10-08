using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using PoAutoRobo.App.ViewModels;
using Windows.System;
using WinUIEx;

namespace PoAutoRobo.App;

public sealed partial class MainWindow : WindowEx
{
    private readonly DispatcherTimer _percentTick = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly ITaskbarList3? _taskbar = NewTaskbar();
    private double _shownPercent;
    private bool _inFront = true;
    private bool _toastsReady;

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.Confirm = ConfirmAsync;
        ViewModel.JobFinished = OnJobFinished;
        ViewModel.PropertyChanged += OnViewModelChanged;
        _percentTick.Tick += (_, _) => RollPercent();
        Activated += (_, e) => _inFront = e.WindowActivationState != WindowActivationState.Deactivated;
        Closed += (_, _) =>
        {
            if (_toastsReady) AppNotificationManager.Default.Unregister();
        };
        ApplySounds();
    }

    public MainViewModel ViewModel { get; }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SoundsOn):
                ApplySounds();
                break;
            case nameof(MainViewModel.IsCreating):
                if (ViewModel.IsCreating) SkeletonPulse.Begin(); else SkeletonPulse.Stop();
                break;
            case nameof(MainViewModel.IsWorking) when ViewModel.IsWorking:
                _shownPercent = 0;
                ActivityPercentText.Text = "0%";
                break;
            case nameof(MainViewModel.ActivityPercent):
                _percentTick.Start();
                ShowTaskbarProgress(ViewModel.ActivityPercent);
                break;
        }
    }

    // The controls' own built-in sounds, placed left to right by where the control is on screen.
    private void ApplySounds()
    {
        ElementSoundPlayer.State = ViewModel.SoundsOn ? ElementSoundPlayerState.On : ElementSoundPlayerState.Off;
        ElementSoundPlayer.SpatialAudioMode = ElementSpatialAudioMode.On;
    }

    // The percentage counts up to its new value over a few frames instead of jumping.
    private void RollPercent()
    {
        var target = ViewModel.ActivityPercent;
        _shownPercent = Math.Abs(target - _shownPercent) < 0.5 ? target : _shownPercent + (target - _shownPercent) * 0.25;
        ActivityPercentText.Text = $"{_shownPercent:0}%";
        if (_shownPercent == target) _percentTick.Stop();
    }

    /// <summary>A long job has ended: clear the taskbar bar, chime, and say so with a notification when the window is not in front.</summary>
    private void OnJobFinished(string job, bool finished)
    {
        ShowTaskbarProgress(null);
        ElementSoundPlayer.Play(finished ? ElementSoundKind.Invoke : ElementSoundKind.Hide); // silent when sounds are off
        if (_inFront) return;
        try
        {
            if (!_toastsReady)
            {
                AppNotificationManager.Default.Register();
                _toastsReady = true;
            }
            AppNotificationManager.Default.Show(new AppNotificationBuilder()
                .AddText(finished ? "Finished" : "Stopped")
                .AddText(ViewModel.ErrorMessage ?? ViewModel.StatusMessage ?? job)
                .BuildNotification());
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or NotSupportedException)
        {
            // Notifications can be switched off or unavailable on this machine. The result is in the window either way.
        }
    }

    private void OnStepShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.GoTo(sender.Key - VirtualKey.Number1);
        args.Handled = true;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnVisualMix(object sender, RoutedEventArgs e)
    {
        var dialog = new Views.MixDialog(ViewModel.Mix) { XamlRoot = Content.XamlRoot };
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary: ViewModel.ApplyMix(dialog.Mix); break;
            case ContentDialogResult.Secondary: ViewModel.RerollMix(); break;
        }
    }

    private async void OnHostSetup(object sender, RoutedEventArgs e) =>
        await new Views.HostSetupDialog(ViewModel) { XamlRoot = Content.XamlRoot }.ShowAsync();

    public static bool HasText(string? text) => !string.IsNullOrEmpty(text);

    // ---- Progress on the taskbar button, so a long job can be watched with the window hidden ----

    private void ShowTaskbarProgress(double? percent)
    {
        try
        {
            _taskbar?.SetProgressState(App.WindowHandle, percent is null ? 0 : 2); // 0 clears the bar, 2 shows a normal one
            if (percent is { } value)
                _taskbar?.SetProgressValue(App.WindowHandle, (ulong)value, 100);
        }
        catch (COMException)
        {
            // The taskbar bar is a nicety; the panel in the window is the real progress display.
        }
    }

    private static ITaskbarList3? NewTaskbar()
    {
        try
        {
            var taskbar = (ITaskbarList3)new TaskbarList();
            taskbar.HrInit();
            return taskbar;
        }
        catch (COMException)
        {
            return null;
        }
    }

    // The Windows taskbar's own interface. Methods must stay in this order: it is how the system finds them.
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(nint window);
        void DeleteTab(nint window);
        void ActivateTab(nint window);
        void SetActiveAlt(nint window);
        void MarkFullscreenWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(nint window, ulong completed, ulong total);
        void SetProgressState(nint window, int state);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList;
}
