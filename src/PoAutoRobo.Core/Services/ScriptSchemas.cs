namespace PoAutoRobo.Core.Services;

/// <summary>The prompts and reply shapes sent to the model. Kept apart from the plumbing so they can be read and tuned.</summary>
internal static class ScriptSchemas
{
    public const string System = """
        You write the script for a fast-paced, character-driven educational video about the Unitree R1 EDU humanoid robot.
        The narrator is a confident, energetic cartoon version of the R1 itself, speaking in the first person to the viewer.

        Break the topic into between 15 and 20 clips. Each clip covers one self-contained subtopic and still makes sense
        if the clips are reordered. Give every clip a short title of two to five words.

        Write every clip at three depths:
        - a: mainstream and accessible. Everyday analogies and intuitive physical explanations. No jargon.
        - b: applied developer. Practical workflows, sim-to-real considerations, reward terms and operating parameters.
        - c: advanced systems engineer. Control rates, actuator limits, coordinate frames and policy formulation.

        For each depth give:
        - dialogue: what the host says, 60 to 110 words, plain spoken sentences with no lists, headings or stage directions.
        - visualPrompt: one sentence describing a single comic-style panel that illustrates this clip at this depth.
        - pose: a few words for what the host is doing, taken from the subject (for example "pointing at a whiteboard of reward terms").

        Accuracy rules:
        - Use specific numbers, joint names, API names and control rates only when they appear in the reference snippets. Otherwise speak in general terms.
        - Do not say or imply that Isaac Lab officially supports the R1. Unitree's Isaac Lab repository (unitree_rl_lab) lists Go2, H1 and G1 only; R1 training is supported in its MuJoCo repository (unitree_rl_mjlab).
        - Never invent quotes, benchmarks or release dates.
        """;

    public const string Episode = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "clips": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string" },
                  "a": { "$ref": "#/$defs/tier" },
                  "b": { "$ref": "#/$defs/tier" },
                  "c": { "$ref": "#/$defs/tier" }
                },
                "required": ["title", "a", "b", "c"],
                "additionalProperties": false
              }
            }
          },
          "required": ["title", "clips"],
          "additionalProperties": false,
          "$defs": {
            "tier": {
              "type": "object",
              "properties": {
                "dialogue": { "type": "string" },
                "visualPrompt": { "type": "string" },
                "pose": { "type": "string" }
              },
              "required": ["dialogue", "visualPrompt", "pose"],
              "additionalProperties": false
            }
          }
        }
        """;

    public const string DriftSystem = """
        You compare two versions of a narration line from an explainer video. Answer whether the picture drawn for the
        first version would still fit the second. It no longer fits only when the core action, the tool in use, or the
        physical subject has changed. Rewording, tone, pacing and added detail about the same thing do not count.
        """;

    public const string Drift = """
        { "type": "object", "properties": { "coreChanged": { "type": "boolean" } }, "required": ["coreChanged"], "additionalProperties": false }
        """;

    public const string RewriteSystem = """
        You rewrite one narration line for an energetic cartoon robot host so that it takes a different amount of time
        to say. Keep the same subject, facts, voice and depth. To lengthen, add relevant detail on the same subtopic;
        to shorten, summarise. Never add new specific numbers or names. Return only the spoken words.
        """;

    public const string Rewrite = """
        { "type": "object", "properties": { "dialogue": { "type": "string" } }, "required": ["dialogue"], "additionalProperties": false }
        """;
}
