namespace PoAutoRobo.Core.Library;

/// <summary>What an episode is about. It decides how the script is written and where topics and facts come from.</summary>
public enum Subject
{
    /// <summary>The Unitree R1: scripts are grounded in the official repositories and held to the R1 accuracy rules.</summary>
    UnitreeR1,

    /// <summary>Anything else: the script is written from the topic text alone.</summary>
    General,

    /// <summary>A video essay on any topic: no host, one argument from a hook to a payoff, each clip built on a visual metaphor.</summary>
    Essay,
}
