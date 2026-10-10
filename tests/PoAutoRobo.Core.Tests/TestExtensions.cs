using Azure.Core;
using NSubstitute;

namespace PoAutoRobo.Core.Tests;

/// <summary>Whole-episode conveniences the tests use; the app itself works one clip at a time.</summary>
internal static class TestExtensions
{
    /// <summary>Draws one clip's pictures and attaches them, the same two steps the app performs.</summary>
    public static async Task<Episode> GenerateAsync(this Visuals visuals, Episode episode, Guid clipId, CancellationToken ct)
    {
        var clip = episode.Clips.First(c => c.Id == clipId);
        return EpisodeEditor.ApplyPicture(episode, clip, await visuals.DrawAsync(clip, episode.Look, ct));
    }

    public static async Task<IReadOnlyList<Narration>> NarrateAsync(this EpisodeBuilder builder, Episode episode, string folder, CancellationToken ct)
    {
        var narrations = new List<Narration>(episode.Clips.Count);
        foreach (var clip in episode.Clips)
            narrations.Add(await builder.NarrateClipAsync(clip, folder, ct));
        return narrations;
    }

    /// <summary>A voice that writes one second of silence, for tests that only care what was spoken and when.</summary>
    /// <param name="takes">How long each line takes to speak; null for no wait.</param>
    public static INarrator SilentNarrator(TimeSpan? takes = null)
    {
        var narrator = Substitute.For<INarrator>();
        narrator.SynthesizeAsync(default!, default!, default, default).ReturnsForAnyArgs(async call =>
        {
            var path = call.ArgAt<string>(1);
            if (takes is { } wait)
                await Task.Delay(wait);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            NarratorTests.WriteSilentWav(path, 1.0);
            return new Narration(path, TimeSpan.FromSeconds(1), [new WordTiming("x", TimeSpan.Zero, TimeSpan.FromSeconds(1))]);
        });
        return narrator;
    }
}

/// <summary>Stands in for the signed-in Azure user, so no test ever asks the real sign-in for a token.</summary>
internal sealed class FakeCredential : TokenCredential
{
    public const string Token = "test-token";

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(Token, DateTimeOffset.MaxValue);

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(GetToken(requestContext, cancellationToken));
}
