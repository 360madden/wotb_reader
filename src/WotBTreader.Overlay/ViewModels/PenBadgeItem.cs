namespace WotBTreader.Overlay.ViewModels;

/// <summary>
/// The penetration indicator's renderable state: the aimed enemy and the
/// banded verdict with its numeric readout. <see cref="Band"/> is one of the
/// fail-closed bands (<c>Pen</c> green, <c>Marginal</c> yellow, <c>NoPen</c>
/// red); <c>Unknown</c> is never rendered — the view model drops it so the
/// HUD cannot paint a verdict it cannot derive. Numeric fields are null when
/// the verdict has no diagnostics.
/// </summary>
public sealed record PenBadgeItem(
    long AimedEntityId,
    string Band,
    double? EffectiveArmorMm,
    double? PenetrationMmAtRange,
    bool Ricochet);
