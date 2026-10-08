using PoAutoRobo.App.ViewModels;
using WinUIEx;

namespace PoAutoRobo.App;

public sealed partial class MainWindow : WindowEx
{
    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public MainViewModel ViewModel { get; }

    // Instance method on purpose: the generated x:Bind code calls it through the window.
    public bool HasText(string? text) => !string.IsNullOrEmpty(text);
}
