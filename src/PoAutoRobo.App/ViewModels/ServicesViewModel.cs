using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PoAutoRobo.App.ViewModels;

/// <summary>
/// The optional services' switches on the Settings page, saved as they change. The services are put together as the
/// app starts, so a change says so and offers to start it again.
/// </summary>
public sealed partial class ServicesViewModel : ObservableObject
{
    private readonly bool _ready;

    /// <summary>The choices the app started with, set as it starts; a restart is wanted while the saved ones differ.</summary>
    public static ServiceChoices StartedWith { get; set; } = new();

    public ServicesViewModel()
    {
        var saved = ServiceChoices.Load(AppPaths.Services);
        (Video, VideoModel, StockPhotos, Local, LocalModel, Meaning, EmbeddingModel) =
            (saved.Video, saved.VideoModel, saved.StockPhotos, saved.Local, saved.LocalModel, saved.Meaning, saved.EmbeddingModel);
        _ready = true;
    }

    [ObservableProperty]
    public partial bool Video { get; set; }

    [ObservableProperty]
    public partial string VideoModel { get; set; } = "";

    [ObservableProperty]
    public partial bool StockPhotos { get; set; }

    [ObservableProperty]
    public partial bool Local { get; set; }

    [ObservableProperty]
    public partial string LocalModel { get; set; } = "";

    [ObservableProperty]
    public partial bool Meaning { get; set; }

    [ObservableProperty]
    public partial string EmbeddingModel { get; set; } = "";

    private ServiceChoices Choices => new(Video, VideoModel.Trim(), StockPhotos, Local, LocalModel.Trim(), Meaning, EmbeddingModel.Trim());

    /// <summary>The switches differ from what the app started with, so it must start again to use them.</summary>
    public bool NeedsRestart => Choices != StartedWith;

    /// <summary>The services' variables set on this computer, which win over the switches; empty when none is.</summary>
    public static string SetOutside => string.Join(", ", AppSettings.ServiceVariables.Where(v => Environment.GetEnvironmentVariable(v) is { Length: > 0 }));

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_ready || e.PropertyName == nameof(NeedsRestart)) return;
        Choices.Save(AppPaths.Services);
        OnPropertyChanged(nameof(NeedsRestart));
    }
}
