namespace LufiaForge.Core.Lufia2.Snes;

internal static class L2AnimSeed
{
    /// <summary>Work RAM of a battle (a new game's first one, Maxim against one monster) at the moment the game starts a spell
    /// animation, as the game's code leaves it: $0000-$1FFF, $2800-$2EFF, $4F00-$4FFF, deflated, base64. Measured in
    /// BizHawk. Graphics, colours and texts are put in by <see cref="L2AnimPreview"/>.</summary>
    public const string Wram =
        "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";
}
