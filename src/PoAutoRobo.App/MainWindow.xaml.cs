using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using PoAutoRobo.App.ViewModels;
using Windows.Storage.Pickers;
using Windows.System;
using WinUIEx;

namespace PoAutoRobo.App;

public sealed partial class MainWindow : WindowEx
{
    private readonly DispatcherTimer _roll = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly ITaskbarList3? _taskbar = NewTaskbar();
    private double _shownPercent;
    private double _shownRuntime;
    private double _shownSpent;
    private bool _inFront = true;
    private bool _toastsReady;

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.Confirm = ConfirmAsync;
        ViewModel.Ask = AskAsync;
        ViewModel.JobFinished = OnJobFinished;
        ViewModel.ClipDone = place => Sounds.Play(Cue.Tick, place * 2 - 1); // the ticks travel left to right across the batch
        ViewModel.PlayCue = cue => Sounds.Play(cue);
        ViewModel.PropertyChanged += OnViewModelChanged;
        _roll.Tick += (_, _) => Roll();
        Activated += (_, e) =>
        {
            _inFront = e.WindowActivationState != WindowActivationState.Deactivated;
            Topic.SetInFront(_inFront);
        };
        Closed += (_, _) =>
        {
            // A job still running is stopped here and now: FFmpeg and the browser it started would otherwise carry on
            // working after the window, with nobody to hand the result to.
            if (ViewModel.IsWorking) ViewModel.CancelActivityCommand.Execute(null);
            ViewModel.Flush();
            if (_toastsReady) AppNotificationManager.Default.Unregister();
        };
        ApplySounds();
    }

    public MainViewModel ViewModel { get; }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SoundsOn) or nameof(MainViewModel.SoundVolume):
                ApplySounds();
                break;
            case nameof(MainViewModel.IsCreating):
                Ui.Shimmer(ViewModel.IsCreating); // light crosses the placeholder cards while the script is written
                break;
            case nameof(MainViewModel.IsWorking) when ViewModel.IsWorking:
                _shownPercent = 0;
                ActivityPercentText.Text = "0%";
                break;
            case nameof(MainViewModel.ActivityPercent):
                _roll.Start();
                ShowTaskbarProgress(ViewModel.ActivityPercent);
                break;
            case nameof(MainViewModel.Runtime):
                _roll.Start();
                break;
            case nameof(MainViewModel.ErrorMessage) or nameof(MainViewModel.StatusMessage):
                ShowMessage();
                break;
        }
    }

    // The controls' own built-in sounds, placed left to right by where the control is on screen, and the app's own.
    private void ApplySounds()
    {
        ElementSoundPlayer.State = ViewModel.SoundsOn ? ElementSoundPlayerState.On : ElementSoundPlayerState.Off;
        ElementSoundPlayer.SpatialAudioMode = ElementSpatialAudioMode.On;
        ElementSoundPlayer.Volume = ViewModel.SoundVolume;
        (Sounds.On, Sounds.Volume) = (ViewModel.SoundsOn, ViewModel.SoundVolume);
    }

    // The figures the app shows that change in steps (how far a job has got, the running time, what has been
    // spent) count up to their new value over a few frames instead of jumping.
    private void Roll()
    {
        var (percent, runtime, spent) = (ViewModel.ActivityPercent, ViewModel.Runtime.TotalSeconds, (double)ViewModel.Spent);
        _shownPercent = Towards(_shownPercent, percent, 0.5);
        _shownRuntime = Towards(_shownRuntime, runtime, 0.5);
        _shownSpent = Towards(_shownSpent, spent, 0.004);
        ActivityPercentText.Text = $"{_shownPercent:0}%";
        MetaText.Text = ViewModel.MetaText(_shownRuntime, _shownSpent);
        if (_shownPercent == percent && _shownRuntime == runtime && _shownSpent == spent) _roll.Stop();

        static double Towards(double shown, double target, double near) =>
            Math.Abs(target - shown) < near ? target : shown + (target - shown) * 0.25;
    }

    /// <summary>A long job has ended: clear the taskbar bar, chime, and say so with a notification when the window is not in front.</summary>
    private void OnJobFinished(string job, bool finished)
    {
        ShowTaskbarProgress(null);
        Sounds.Play(finished ? Cue.Finished : ViewModel.ErrorMessage is null ? Cue.Stopped : Cue.Error); // silent when sounds are off
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

    // Each shortcut belongs to one page and does nothing on the others: Ctrl+Enter on the clips page must never
    // start (and pay for) a new script from a topic left in the box.
    private void OnCommandShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        System.Windows.Input.ICommand? command = sender.Key switch
        {
            VirtualKey.Enter when ViewModel.ShowTopic => ViewModel.CreateEpisodeCommand,
            VirtualKey.G when ViewModel.Step == 1 => ViewModel.GeneratePictureCommand,
            VirtualKey.D when ViewModel.Step == 1 => ViewModel.DuplicateClipCommand,
            VirtualKey.P when ViewModel.Step == 1 => ViewModel.AuditionCommand,
            _ => null,
        };
        if (command?.CanExecute(null) != true) return;
        command.Execute(null);
        args.Handled = true;
    }

    // Delete, with a card in hand, takes that clip out. Only here: in a text box the key deletes text.
    private void OnDeckKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete || !ViewModel.RemoveClipCommand.CanExecute(null)) return;
        ViewModel.RemoveClipCommand.Execute(null);
        e.Handled = true;
    }

    private void OnSettingsOpening(object? sender, object e) => ViewModel.RefreshSpendDetail();

    private async void OnPickMusic(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        foreach (var extension in MainViewModel.MusicExtensions)
            picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle); // a desktop app must say which window owns the dialog
        if (await picker.PickSingleFileAsync() is { } file)
            await ViewModel.SetMusicAsync(file.Path);
    }

    // The one message bar: an error when there is one, otherwise the outcome of the last action. Set here and not
    // by binding, because a binding is not told when a message is cleared.
    private void ShowMessage()
    {
        var (error, status) = (ViewModel.ErrorMessage, ViewModel.StatusMessage);
        MessageBar.Severity = error is null ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        MessageBar.Title = error is null ? "" : "Something went wrong";
        MessageBar.Message = error ?? status ?? "";
        MessageBar.IsOpen = !string.IsNullOrEmpty(error ?? status);
        // A job that fails says so with its own sound as it ends; anything else that goes wrong is heard here.
        if (error is not null && !ViewModel.IsWorking) Sounds.Play(Cue.Error);
    }

    // Closed with its own button, the bar forgets what it said, so the same message said again shows again.
    private void OnMessageClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton) ViewModel.DismissMessages();
    }

    // ---- The deck: cards glide to a new place, and a selected card's picture grows into the inspector ----

    private void OnCardShown(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        // Whenever the layout gives this card a new position (a reorder, an undo, a card added before it), it travels
        // there on a spring and settles with a slight overshoot, the way a card lifts under the pointer.
        var visual = ElementCompositionPreview.GetElementVisual(args.ItemContainer);
        var glide = Compositor.CreateSpringVector3Animation();
        glide.Target = "Offset";
        glide.DampingRatio = 0.75f;
        glide.Period = TimeSpan.FromMilliseconds(70);
        var moves = Compositor.CreateImplicitAnimationCollection();
        moves["Offset"] = glide;
        visual.ImplicitAnimations = moves;
    }

    private void OnCardSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ClipDeck.SelectedItem is not ClipViewModel { ThumbnailPath.Length: > 0 } card || !Inspector.ShowsPicture || ClipDeck.ContainerFromItem(card) is null) return;
        try
        {
            ClipDeck.PrepareConnectedAnimation("clip", card, "CardRoot");
            ConnectedAnimationService.GetForCurrentView().GetAnimation("clip")?.TryStart(Inspector.Picture);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            // The flourish needs the card on screen and laid out; without it the picture simply appears.
        }
    }

    // ---- Questions the view model asks the user ----

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
        dialog.Opened += (opened, _) => Ui.PopIn(opened.Content as UIElement ?? opened);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<string?> AskAsync(string title, string current)
    {
        var box = new TextBox { Text = current, SelectionStart = 0, SelectionLength = current.Length };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        // Nothing to save while the box is blank, and the button says so by being unavailable.
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        dialog.IsPrimaryButtonEnabled = current.Trim().Length > 0;
        dialog.Opened += (opened, _) => Ui.PopIn(opened.Content as UIElement ?? opened);
        return await dialog.ShowAsync() == ContentDialogResult.Primary && box.Text.Trim() is { Length: > 0 } text ? text : null;
    }

    private async void OnVisualMix(object sender, RoutedEventArgs e)
    {
        var (mix, host) = (ViewModel.Mix, ViewModel.HostMostlyVisible);
        var dialog = new Views.MixDialog(mix, ViewModel.Look, host) { XamlRoot = Content.XamlRoot };
        dialog.Opened += (opened, _) => Ui.PopIn(opened.Content as UIElement ?? opened);
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                // Only what was changed is applied: dealing the mix again would undo the script's choice of picture types.
                ViewModel.ApplyLook(dialog.Look);
                if (dialog.HostVisible != host) ViewModel.ShowHostEverywhere(dialog.HostVisible);
                if (dialog.Mix != mix) ViewModel.ApplyMix(dialog.Mix);
                break;
            case ContentDialogResult.Secondary: ViewModel.RerollMix(); break;
        }
    }

    private async void OnHostSetup(object sender, RoutedEventArgs e)
    {
        var dialog = new Views.HostSetupDialog(ViewModel) { XamlRoot = Content.XamlRoot };
        dialog.Opened += (opened, _) => Ui.PopIn(opened.Content as UIElement ?? opened);
        await dialog.ShowAsync();
    }

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
