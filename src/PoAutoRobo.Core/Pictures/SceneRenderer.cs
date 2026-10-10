using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace PoAutoRobo.Core.Pictures;

/// <summary>A scene drawn in code: one SVG picture, and a script whose <c>render(t, duration)</c> sets it for any instant.</summary>
public sealed record Scene(string Svg, string Script);

/// <summary>The scene's own code is at fault, not the browser or the computer: writing it again can put it right.</summary>
/// <param name="detail">What the browser said went wrong, for whoever rewrites the code.</param>
public sealed class SceneCodeException(string detail)
    : InvalidOperationException("The animation's code failed. Generate it again for a fresh attempt. " + detail)
{
    public string Detail { get; } = detail;
}

/// <summary>
/// Turns a code-drawn scene into a video. A hidden Microsoft Edge shows the scene, is asked to set it for each frame's
/// instant in turn and to take a picture of it, and FFmpeg joins the pictures.
/// </summary>
public sealed class SceneRenderer(string browserPath, FfmpegRunner ffmpeg)
{
    public const int Width = 1280, Height = 720, Fps = 30;

    /// <summary>The longest one frame's code may run, in milliseconds. A scene that loops for ever is stopped here and not left to hang the job.</summary>
    private const int FrameTimeout = 5000;

    /// <summary>Finds Microsoft Edge, which every Windows 11 computer has; null when it is not there.</summary>
    public static string? Locate() =>
        new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
            .Select(root => Path.Combine(Environment.GetFolderPath(root), "Microsoft", "Edge", "Application", "msedge.exe"))
            .FirstOrDefault(File.Exists);

    // The scene is written by a model from a topic that may have come from a news feed, so it is not trusted. The
    // page's policy lets it run its own script and nothing else: no network, no files, no other pages.
    public static string Page(Scene scene) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:">
        <style>html,body{margin:0;height:100%;overflow:hidden;background:#101828}body>svg{display:block;width:100vw;height:100vh}</style>
        </head><body>
        {{scene.Svg}}
        <script>{{scene.Script}}</script>
        </body></html>
        """;

    /// <exception cref="InvalidOperationException">The scene's code does not run; the message is fit to show the user.</exception>
    public async Task RenderAsync(Scene scene, TimeSpan length, string outputPath, CancellationToken ct)
    {
        var work = Files.NewScratchFolder("scene");
        Process? browser = null;
        try
        {
            var page = Path.Combine(work, "scene.html");
            await File.WriteAllTextAsync(page, Page(scene), ct);

            // A throwaway profile, every host name made unfindable, and every request sent to a proxy that is not
            // there (which also covers bare number addresses, and this computer's own): belt and braces with the
            // page's own policy, which cannot stop the page sending itself somewhere else.
            var profile = Path.Combine(work, "profile");
            var start = new ProcessStartInfo(browserPath) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[]
            {
                "--headless=new", "--disable-gpu", "--hide-scrollbars", "--mute-audio", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
                $"--user-data-dir={profile}", "--remote-debugging-port=0", "--host-resolver-rules=MAP * ~NOTFOUND", "--proxy-server=127.0.0.1:9", "--proxy-bypass-list=<-loopback>",
                $"--window-size={Width},{Height}", "about:blank",
            })
                start.ArgumentList.Add(arg);
            browser = Process.Start(start)!;
            using var stop = ct.Register(() => FfmpegRunner.Kill(browser)); // gone the moment the job is cancelled, even when the app is closing

            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(await PageAddressAsync(profile, ct), ct);
            var buffer = new byte[1 << 16];
            var lastId = 0;

            // One request, then replies are read until the one that answers it; anything else is the browser's news.
            async Task<JsonElement> Call(string method, object parameters)
            {
                var id = ++lastId;
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters }), WebSocketMessageType.Text, true, ct);
                while (true)
                {
                    using var message = new MemoryStream();
                    ValueWebSocketReceiveResult part;
                    do
                    {
                        part = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                        message.Write(buffer, 0, part.Count);
                    }
                    while (!part.EndOfMessage);
                    using var json = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                    if (!json.RootElement.TryGetProperty("id", out var answered) || answered.GetInt32() != id)
                        continue;
                    return json.RootElement.TryGetProperty("result", out var result)
                        ? result.Clone()
                        : throw new InvalidOperationException($"The browser could not draw the animation ({method}).");
                }
            }

            async Task<JsonElement> Run(string expression)
            {
                JsonElement reply;
                try
                {
                    reply = await Call("Runtime.evaluate", new { expression, returnByValue = true, timeout = FrameTimeout });
                }
                catch (InvalidOperationException)
                {
                    // The browser gives no result at all for code it had to stop.
                    throw new SceneCodeException($"The code did not finish within {FrameTimeout / 1000} seconds; a loop in it may never end.");
                }
                if (reply.TryGetProperty("exceptionDetails", out var failure))
                    throw new SceneCodeException((failure.TryGetProperty("exception", out var thrown) && thrown.TryGetProperty("description", out var why) ? why.GetString() : failure.GetProperty("text").GetString()) ?? "");
                return reply.GetProperty("result");
            }

            await Call("Emulation.setDeviceMetricsOverride", new { width = Width, height = Height, deviceScaleFactor = 1, mobile = false });
            await Call("Page.navigate", new { url = new Uri(page).AbsoluteUri });
            for (var tries = 0; ; tries++)
            {
                var ready = await Run("document.readyState === 'complete' ? typeof render === 'function' : null");
                if (ready.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.True)
                    break;
                // Loaded with no render function means the script did not parse; never loading is the browser's fault.
                if (value.ValueKind == JsonValueKind.False)
                    throw new SceneCodeException("The script does not parse, or defines no function called render.");
                if (tries == 100)
                    throw new InvalidOperationException("The browser did not load the animation.");
                await Task.Delay(100, ct);
            }

            // Tried at its start, middle and end first: code that fails is found in a moment, not after hundreds of frames.
            foreach (var part in new[] { 0, 0.5, 1 })
                await Run(FormattableString.Invariant($"render({length.TotalSeconds * part:0.###}, {length.TotalSeconds:0.###})"));

            var frames = (int)Math.Ceiling(length.TotalSeconds * Fps);
            for (var frame = 0; frame < frames; frame++)
            {
                await Run(FormattableString.Invariant($"render({(double)frame / Fps:0.####}, {length.TotalSeconds:0.###})"));
                var shot = await Call("Page.captureScreenshot", new { format = "jpeg", quality = 92 });
                await File.WriteAllBytesAsync(Path.Combine(work, $"f{frame:00000}.jpg"), shot.GetProperty("data").GetBytesFromBase64(), ct);
            }

            Files.EnsureFolderFor(outputPath);
            await ffmpeg.RunAsync(["-framerate", $"{Fps}", "-i", "f%05d.jpg", "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p", "-y", Path.GetFullPath(outputPath)], work, null, null, ct);
        }
        catch (WebSocketException e) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("The browser drawing the animation stopped unexpectedly.", e);
        }
        catch (WebSocketException)
        {
            throw new OperationCanceledException(ct);
        }
        finally
        {
            try
            {
                if (browser is not null) FfmpegRunner.Kill(browser);
                browser?.WaitForExit(5000); // its files stay locked until it is really gone
                browser?.Dispose();
                Directory.Delete(work, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Leftover scratch in the temp folder is harmless; it must not turn a finished animation into an error.
            }
        }
    }

    /// <summary>Waits for the browser to say which port it is listening on, then asks it for its one page.</summary>
    private static async Task<Uri> PageAddressAsync(string profile, CancellationToken ct)
    {
        var portFile = Path.Combine(profile, "DevToolsActivePort");
        using var http = new HttpClient();
        for (var tries = 0; tries < 150; tries++)
        {
            try
            {
                if (File.Exists(portFile) && int.TryParse((await File.ReadAllLinesAsync(portFile, ct)).FirstOrDefault(), out var port))
                {
                    using var pages = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct));
                    if (pages.RootElement.EnumerateArray().FirstOrDefault(p => p.GetProperty("type").GetString() == "page") is { ValueKind: JsonValueKind.Object } found)
                        return new Uri(found.GetProperty("webSocketDebuggerUrl").GetString()!);
                }
            }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                // Still starting: the file is half written or the port is not open yet.
            }
            await Task.Delay(100, ct);
        }
        throw new InvalidOperationException("Microsoft Edge did not start, so the animation could not be drawn.");
    }
}
