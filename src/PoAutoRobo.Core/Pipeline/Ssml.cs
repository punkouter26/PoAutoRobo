using System.Globalization;
using System.Xml.Linq;

namespace PoAutoRobo.Core.Pipeline;

/// <summary>Builds the speech markup for the host's voice.</summary>
public static class Ssml
{
    /// <summary>Furthest the speaking rate may move from normal before it starts to sound wrong.</summary>
    public const double MaxRateChange = 0.10;

    private static readonly XNamespace Speak = "http://www.w3.org/2001/10/synthesis";
    private static readonly XNamespace Mstts = "http://www.w3.org/2001/mstts";

    /// <param name="rate">Speaking rate multiplier; clamped to within <see cref="MaxRateChange"/> of 1.0.</param>
    public static string Build(string text, string voice, double rate)
    {
        var percent = (int)Math.Round((Math.Clamp(rate, 1 - MaxRateChange, 1 + MaxRateChange) - 1) * 100);
        // Built as XML rather than by string formatting, so dialogue can never be read as markup.
        return new XElement(Speak + "speak",
            new XAttribute("version", "1.0"),
            new XAttribute(XNamespace.Xmlns + "mstts", Mstts),
            new XAttribute(XNamespace.Xml + "lang", "en-US"),
            new XElement(Speak + "voice", new XAttribute("name", voice),
                new XElement(Mstts + "express-as", new XAttribute("style", "excited"),
                    new XElement(Speak + "prosody", new XAttribute("rate", percent.ToString("+0;-0", CultureInfo.InvariantCulture) + "%"), text))))
            .ToString(SaveOptions.DisableFormatting);
    }
}
