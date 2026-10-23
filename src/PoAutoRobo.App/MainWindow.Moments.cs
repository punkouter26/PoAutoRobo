using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;

namespace PoAutoRobo.App;

// Light and sound for the moments that matter. The sounds follow the Sounds switch and the light the Effects switch,
// each in Settings; the light layer itself does nothing while Effects is off.
public sealed partial class MainWindow
{
    private IReadOnlyList<string> _skeletons = []; // the placeholders' titles as last seen, to tell which have just arrived

    private static Color Accent => (Color)Application.Current.Resources["SystemAccentColor"];

    /// <summary>
    /// Where an element is on the light layer; empty when it is not laid out. Measured against the window's grid, which
    /// the layer exactly covers, as the layer itself has no size while it sleeps.
    /// </summary>
    private Rect Area(FrameworkElement element) =>
        element.ActualWidth > 0 && element.XamlRoot is not null ? element.TransformToVisual(Root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight)) : default;

    // One clip of a batch is done: a note a step up the scale for each, travelling left to right across the batch, and
    // sparks off its card, or for one that did not come out a sour note and a puff of smoke.
    private void OnClipDone(Guid id, int place, int count, bool made)
    {
        Sounds.Play(made ? Cue.Tick : Cue.Sour, count <= 1 ? 0 : place * 2.0 / (count - 1) - 1, place);
        if (!ViewModel.ShowDeck || ViewModel.Clips.FirstOrDefault(c => c.Id == id) is not { } card || ClipDeck.ContainerFromItem(card) is not FrameworkElement box) return;
        var area = Area(box);
        if (area.Width <= 0) return;
        if (made) Fx.Sparks(area, Accent); else Fx.Smoke(area);
    }

    // A placeholder has just been given its clip's title: light gathers into it, a note sounds, and it pops.
    private void Landed()
    {
        var now = ViewModel.Skeletons;
        for (var i = 0; i < now.Count; i++)
        {
            if (now[i].Length == 0 || (i < _skeletons.Count && _skeletons[i].Length > 0) || Skeleton.ContainerFromIndex(i) is not FrameworkElement box) continue;
            var area = Area(box);
            if (area.Width <= 0) continue;
            Fx.Gather(area, Accent);
            Sounds.Play(Cue.Land, Root.ActualWidth > 0 ? (area.X + area.Width / 2) / Root.ActualWidth * 2 - 1 : 0, i);
            After(TimeSpan.FromMilliseconds(450), () => Ui.PopIn(box)); // as the light arrives
        }
        _skeletons = now;
    }

    // The master video is finished: its thumbnail gathers itself out of a swarm of light in the middle of the window,
    // and as it comes together, confetti in the series colour and the fanfare. Without a thumbnail, the confetti at once.
    private void OnPremiere()
    {
        void Celebrate()
        {
            Fx.Confetti(ViewModel.CaptionAccent, (float)Root.ActualWidth);
            After(TimeSpan.FromMilliseconds(400), () => Sounds.Play(Cue.Fanfare));
        }
        var thumbnail = ViewModel.LastExport is { } video ? Path.ChangeExtension(video, null) + ".thumbnail.png" : null;
        if (thumbnail is null || !File.Exists(thumbnail))
        {
            Celebrate();
            return;
        }
        var width = Math.Min(Root.ActualWidth * 0.55, 760);
        var area = new Rect((Root.ActualWidth - width) / 2, (Root.ActualHeight - width * 9 / 16) / 2, width, width * 9 / 16);
        Fx.Assemble(thumbnail, area, Celebrate);
    }

    private void After(TimeSpan wait, Action then)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = wait;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => then();
        timer.Start();
    }
}
