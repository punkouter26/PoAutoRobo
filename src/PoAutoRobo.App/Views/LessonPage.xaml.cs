using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoAutoRobo.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace PoAutoRobo.App.Views;

/// <summary>Step 2: the lesson form, with the owner's films, and the saved episodes.</summary>
public sealed partial class LessonPage : UserControl
{
    private MainViewModel? _viewModel;

    public LessonPage() => InitializeComponent();

    public MainViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            Bindings.Update();
        }
    }

    // ---- Saved episodes ----

    private async void OnOpenEpisode(object sender, ItemClickEventArgs e)
    {
        if (ViewModel is not null && e.ClickedItem is EpisodeSummary summary)
            await ViewModel.OpenEpisodeAsync(summary.Folder);
    }

    // The menu items carry their episode in Tag.
    private static EpisodeSummary? Episode(object sender) => (sender as FrameworkElement)?.Tag as EpisodeSummary;

    private async void OnOpenFromMenu(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && Episode(sender) is { } summary)
            await ViewModel.OpenEpisodeAsync(summary.Folder);
    }

    private async void OnDuplicateEpisode(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && Episode(sender) is { } summary)
            await ViewModel.DuplicateEpisodeAsync(summary);
    }

    private void OnShowEpisode(object sender, RoutedEventArgs e)
    {
        if (Episode(sender) is { } summary)
            MainViewModel.ShowInExplorer(summary.Folder);
    }

    private async void OnDeleteEpisode(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && Episode(sender) is { } summary)
            await ViewModel.DeleteEpisodeAsync(summary);
    }

    // ---- The owner's films ----

    private async void OnAddFootage(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        foreach (var extension in EpisodeBuilder.VideoExtensions)
            picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle); // a desktop app must say which window owns the dialog
        if (ViewModel is not null && await picker.PickMultipleFilesAsync() is { Count: > 0 } files)
            await ViewModel.AddFootageAsync([.. files.Select(f => f.Path)]);
    }

    private void OnFootageDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void OnFootageDrop(object sender, DragEventArgs e)
    {
        if (ViewModel is null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var videos = items.Select(i => i.Path).Where(p => EpisodeBuilder.VideoExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).ToList();
        if (videos.Count > 0) await ViewModel.AddFootageAsync(videos);
    }

    private void OnSuggestNote(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FootageRow row } && ViewModel?.SuggestNoteCommand.CanExecute(row) == true)
            ViewModel.SuggestNoteCommand.Execute(row);
    }

    private void OnRemoveFootage(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FootageRow row })
            ViewModel?.RemoveFootageCommand.Execute(row);
    }
}
