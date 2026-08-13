namespace WotBTreader.Overlay.ViewModels;

/// <summary>
/// The penetration indicator's renderable state: the aimed enemy and the
/// banded verdict with its numeric readout. <see cref="Band"/> is one of the
/// fail-closed bands (<c>Pen</c> green, <c>Marginal</c> yellow, <c>NoPen</c>
/// red); <c>Unknown</c> is never rendered — the view model drops it so the
/// HUD cannot paint a verdict it cannot derive. Numeric fields are null when
/// the verdict has no diagnostics. <see cref="Shell"/> is the short label of
/// the shell the badge was scored with (e.g. <c>AP</c>), or null when the
/// shell family is unknown.
/// </summary>
public sealed record PenBadgeItem(
    long AimedEntityId,
    string Band,
    double? EffectiveArmorMm,
    double? PenetrationMmAtRange,
    bool Ricochet,
    string? Shell = null,
    string? Face = null);

/// <summary>
/// One available shell the HUD can cycle the pen badge through: the install
/// shell name and its family (from the frame response).
/// </summary>
public sealed record PenShellOption(string Name, string Kind)
{
    /// <summary>Short family label for the badge/shell selector (<c>AP</c>,
    /// <c>APCR</c>, <c>HE</c>, <c>HEAT</c>).</summary>
    public string ShortLabel => Kind switch
    {
        "ArmorPiercing" => "AP",
        "ArmorPiercingCr" => "APCR",
        "HighExplosive" => "HE",
        "HollowCharge" => "HEAT",
        _ => "?",
    };
}
