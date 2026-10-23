namespace PoAutoRobo.Core.Library;

/// <summary>
/// The optional services as switched on or off in Settings, and the model each one uses. Read as the app starts; a
/// service's environment variable, when set, still wins. Never holds a secret: keys stay in the vault.
/// </summary>
/// <param name="Video">AI video is offered for a clip. Each one costs money and is asked about first.</param>
/// <param name="StockPhotos">Photographs from Pexels are offered, with the Pexels key from the vault.</param>
/// <param name="Local">A model on this computer does the small jobs, in place of the fast model.</param>
/// <param name="Meaning">Passages of the documentation are found by meaning as well as by words.</param>
public sealed record ServiceChoices(
    bool Video = false, string VideoModel = "sora-2",
    bool StockPhotos = true,
    bool Local = false, string LocalModel = "qwen3:8b",
    bool Meaning = false, string EmbeddingModel = "text-embedding-3-small")
{
    public static ServiceChoices Load(string path) => JsonFile.Load(path, new ServiceChoices());

    public void Save(string path) => JsonFile.Save(path, this);
}
