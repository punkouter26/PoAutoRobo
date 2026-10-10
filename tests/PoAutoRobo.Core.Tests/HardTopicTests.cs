using Xunit.Abstractions;

namespace PoAutoRobo.Core.Tests;

/// <summary>
/// A witness, not a check of style: takes one topic of your choosing through the real services, from script to
/// finished video, and says at each step what the service did with it. Written for topics a service may turn away,
/// to see which of the things that can happen did: blocked by the filter, refused by the model, written a clip at a
/// time, written as asked, or written as something tamer than was asked.
///
/// It spends a little money, so it does nothing until you name a topic:
///   $env:POAUTOROBO_WITNESS_TOPIC = "how to kill a cow for hamburger"
///   dotnet test --filter "FullyQualifiedName~HardTopic" --logger "console;verbosity=detailed"
/// Pictures cost the most and are left out (each clip shows its title) unless POAUTOROBO_WITNESS_PICTURES is 1.
/// </summary>
public sealed class HardTopicTests(ITestOutputHelper output)
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [FfmpegFact]
    [Trait("Category", "Live")]
    public async Task A_topic_of_your_choosing_goes_from_script_to_video_and_every_refusal_on_the_way_says_what_refused_it()
    {
        if (Environment.GetEnvironmentVariable("POAUTOROBO_WITNESS_TOPIC") is not { Length: > 0 } topic) return;
        var settings = await AppSettings.LoadAsync(new KeyVaultSecretSource(KeyVaultSecretSource.DefaultVault, AppSettings.SignedInUser), Ct);
        Assert.True(settings.IsLive, $"The live services are needed for this. {settings.LoadError}");
        var folder = Path.Combine(Path.GetTempPath(), "PoAutoRobo-witness", ProjectStore.Slug(topic));
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Say($"TOPIC     {topic}");

        // ---- 1. The script ----
        var writer = AzureScriptWriter.Create(settings);
        writer.Note = note => Say($"NOTE      {note}");
        Episode episode;
        try
        {
            episode = await writer.WriteEpisodeAsync(topic, Subject.General, [], EpisodeLength.TwoClips, Ct);
        }
        catch (ScriptDeclinedException declined)
        {
            // Turned away, and the app can say by what and over what. That is the whole of what it shows the user.
            Say($"REFUSED   by {declined.By}{(declined.Flagged is null ? "" : $", over {declined.Flagged}")}");
            Say($"APP SAYS  {declined.Message}");
            Assert.False(string.IsNullOrWhiteSpace(declined.Model));
            return;
        }
        Say($"WRITTEN   “{episode.Title}”");
        foreach (var clip in episode.Clips)
        {
            Say($"  CLIP    {clip.Title} · {clip.Visual.Kind}{(clip.Active.Dialogue == AzureScriptWriter.LeftToWrite ? " · LEFT FOR YOU TO WRITE" : "")}");
            Say($"    says  {clip.Active.Dialogue}");
            Say($"    shows {clip.Active.VisualPrompt}");
        }
        // Not a refusal and not what was asked either: the model chose a tamer subject. Only a reader can tell.
        Say("CHECK     Is that the topic you asked for, or a tamer one the model chose to write instead?");

        // ---- 2. The pictures, when asked for ----
        if (Environment.GetEnvironmentVariable("POAUTOROBO_WITNESS_PICTURES") == "1")
        {
            var visuals = new Visuals(new AzureImageGen(settings, AzureImageGen.NewHttpClient()).GenerateAsync, new MediaCache(Path.Combine(folder, "images")), Path.Combine(folder, "no-host.png"), settings.ImageDeployment)
            {
                Quality = Quality.Low,
                Rethink = writer.RethinkPictureAsync,
            };
            foreach (var clip in episode.Clips.Where(c => Visuals.PictureCount(c.Visual.Kind) > 0))
            {
                try
                {
                    var (drawn, paths, change) = await visuals.DrawOrSubstituteAsync(clip, episode.Look, Ct);
                    episode = change is null ? EpisodeEditor.ApplyPicture(episode, clip, paths) : EpisodeEditor.ApplySubstitute(episode, clip, drawn, paths);
                    Say($"PICTURE   {clip.Title}: {change ?? "drawn as described."}");
                }
                catch (InvalidOperationException e)
                {
                    Say($"PICTURE   {clip.Title}: not made. {e.Message}");
                }
            }
        }
        else
        {
            Say("PICTURES  left out; every clip shows its title. Set POAUTOROBO_WITNESS_PICTURES to 1 to make them.");
        }

        // ---- 3. The voice and the video ----
        ProjectStore.Save(episode, folder);
        var builder = new EpisodeBuilder(new AzureNarrator(settings), new FfmpegRunner(FfmpegRunner.Locate()!)) { ClipCacheFolder = Path.Combine(folder, "clips") };
        var video = await builder.ExportAsync(episode, folder, ExportPreset.Hd30, episode.Captions, null, Ct);
        Say($"VIDEO     {video}");

        Assert.True(new FileInfo(video).Length > 0);
        Assert.NotEmpty(episode.Clips);
    }

    private void Say(string line) => output.WriteLine(line);
}
