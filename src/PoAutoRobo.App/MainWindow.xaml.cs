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

/// <summary>
/// The one window. This file holds what it shows and how it answers the view model; beside it are its moments of
/// light and sound (.Moments), the questions it asks (.Dialogs) and its progress on the taskbar (.Taskbar).
/// </summary>
public sealed partial class MainWindow : WindowEx
{
    private readonly DispatcherTimer _roll = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private double _shownPercent;
    private double _shownRuntime;
    private double _shownSpent;
    private bool _inFront = true;
    private bool _toastsReady;
    private readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool _messageHeld; // the pointer is on the message, so it stays to be read

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        Ui.EffectsWanted = viewModel.Effects; // before anything is drawn
        InitializeComponent();
        ViewModel.Confirm = ConfirmAsync;
        ViewModel.ConfirmSpend = ConfirmSpendAsync;
        ViewModel.Ask = AskAsync;
        ViewModel.JobFinished = OnJobFinished;
        ViewModel.ClipDone = OnClipDone;
        ViewModel.Premiere = OnPremiere;
        ViewModel.PlayCue = cue => Sounds.Play(cue);
        Darkroom.ViewModel = ViewModel;
        ViewModel.PlayVideo = PlayVideo;
        ViewModel.PropertyChanged += OnViewModelChanged;
        _roll.Tick += (_, _) => Roll();
        Activated += (_, e) =>
        {
            _inFront = e.WindowActivationState != WindowActivationState.Deactivated;
            Lesson.SetInFront(_inFront);
            DeckBackdrop.SetInFront(_inFront);
        };
        DeckBackdrop.Wanted = ViewModel.ShowDeck;
        // An outcome goes by itself once there has been time to read it; an error stays until it is closed.
        _messageTimer.Tick += (_, _) =>
        {
            _messageTimer.Stop();
            if (!_messageHeld && ViewModel.ErrorMessage is null) MessageBar.IsOpen = false;
        };
        SlideUp(ActivityPanel);
        Closed += (_, _) =>
        {
            // A job still running is stopped here and now: FFmpeg and the browser it started would otherwise carry on
            // working after the window, with nobody to hand the result to.
            if (ViewModel.IsWorking) ViewModel.CancelActivityCommand.Execute(null);
            ViewModel.StopWatching();
            Intro.Release();
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
            case nameof(MainViewModel.Effects):
                Ui.EffectsWanted = ViewModel.Effects;
                DeckBackdrop.Wanted = ViewModel.ShowDeck; // looks afresh at the switch; the lesson page's backdrop does the same
                break;
            case nameof(MainViewModel.IsCreating):
                Ui.Shimmer(ViewModel.IsCreating); // light crosses the placeholder cards while the script is written
                _skeletons = [];
                if (ViewModel.IsCreating)
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Fx.Drift(Area(Skeleton) is { Width: > 0 } deck ? deck : new Windows.Foundation.Rect(0, 0, Root.ActualWidth, Root.ActualHeight), Accent));
                else
                    Fx.StopDrifting();
                break;
            case nameof(MainViewModel.Skeletons):
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Landed); // once the placeholders have been laid out
                break;
            case nameof(MainViewModel.ShowDeck):
                DeckBackdrop.Wanted = ViewModel.ShowDeck;
                break;
            case nameof(MainViewModel.DrawingClips):
                Darkroom.Run(ViewModel.DrawingClips);
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
            VirtualKey.G when ViewModel.Step == MainViewModel.ClipsStep => ViewModel.GeneratePictureCommand,
            VirtualKey.D when ViewModel.Step == MainViewModel.ClipsStep => ViewModel.DuplicateClipCommand,
            VirtualKey.P when ViewModel.Step == MainViewModel.ClipsStep && sender.Modifiers.HasFlag(VirtualKeyModifiers.Shift) => ViewModel.WatchClipCommand,
            VirtualKey.P when ViewModel.Step == MainViewModel.ClipsStep => ViewModel.AuditionCommand,
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
        _messageTimer.Stop();
        if (error is null && MessageBar.IsOpen) _messageTimer.Start();
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
        if (args.InRecycleQueue || !Ui.Animated) return;
        // Whenever the layout gives this card a new position (a reorder, an undo, a card added before it, a wider
        // window), it travels there and settles with a slight overshoot, the way a card lifts under the pointer. It must
        // end on the place the layout gave it: an animation that names no end of its own leaves the card where it was.
        var visual = ElementCompositionPreview.GetElementVisual(args.ItemContainer);
        var glide = Compositor.CreateVector3KeyFrameAnimation();
        glide.Target = "Offset";
        glide.InsertExpressionKeyFrame(1, "this.FinalValue", Compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.3f, 1.3f), new System.Numerics.Vector2(0.4f, 1f)));
        glide.Duration = TimeSpan.FromMilliseconds(320);
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

    // ---- Watching one clip or one teaser, in a player that opens beside its card ----

    private void PlayVideo(object card, string path)
    {
        // Beside the card when it is on screen; a clip's card scrolled out of sight is not, and the inspector stands in.
        var anchor = card switch
        {
            ClipViewModel clip => ClipDeck.ContainerFromItem(clip) as FrameworkElement ?? Inspector,
            TeaserViewModel teaser => TeaserCardList.ContainerFromItem(teaser) as FrameworkElement,
            _ => null,
        };
        if (anchor is null || anchor.XamlRoot is null) return; // the page was left while it was being made
        var upright = card is TeaserViewModel;
        var player = new Windows.Media.Playback.MediaPlayer { Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path)), AutoPlay = true };
        var screen = new MediaPlayerElement { AreTransportControlsEnabled = true, Width = upright ? 270 : 480, Height = upright ? 480 : 270 };
        screen.TransportControls.IsCompact = true;
        screen.SetMediaPlayer(player);
        // The usual flyout is narrower than a 16:9 player, and its padding would frame the picture in grey.
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 800d));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 800d));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        var flyout = new Flyout { Content = screen, FlyoutPresenterStyle = presenter };
        flyout.Closed += (_, _) => player.Dispose(); // lets go of the file, which a later render may replace
        flyout.ShowAt(anchor);
    }

    // ---- The floating bar ----

    /// <summary>The job panel rises into place as it appears and sinks away as it goes, unless Windows is set to show no animation.</summary>
    private static void SlideUp(UIElement element)
    {
        if (!Ui.Animated) return;
        // Moved by the composition layer alone, never by the element's own Translation, which may not be used alongside.
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var rise = compositor.CreateVector3KeyFrameAnimation();
        rise.Target = "Translation";
        rise.InsertKeyFrame(0, new System.Numerics.Vector3(0, 40, 0));
        rise.InsertKeyFrame(1, System.Numerics.Vector3.Zero);
        rise.Duration = TimeSpan.FromMilliseconds(260);
        var appear = compositor.CreateScalarKeyFrameAnimation();
        appear.Target = "Opacity";
        appear.InsertKeyFrame(0, 0);
        appear.InsertKeyFrame(1, 1);
        appear.Duration = rise.Duration;
        var show = compositor.CreateAnimationGroup();
        show.Add(rise);
        show.Add(appear);
        ElementCompositionPreview.SetImplicitShowAnimation(element, show);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(1, 0);
        fade.Duration = TimeSpan.FromMilliseconds(180);
        ElementCompositionPreview.SetImplicitHideAnimation(element, fade);
    }

    private void OnMessageHeld(object sender, PointerRoutedEventArgs e) => _messageHeld = true;

    private void OnMessageLetGo(object sender, PointerRoutedEventArgs e)
    {
        _messageHeld = false;
        if (MessageBar.IsOpen && ViewModel.ErrorMessage is null) _messageTimer.Start();
    }
}
