namespace WotBTreader.Core.Overlay;

/// <summary>
/// One shot to score against the pen model: the reconstructed aim ray, the
/// victim's tank state (world position + hull yaw), the victim's collision
/// parts and nominal armor, the shell, and the DECODED ground-truth outcome
/// (<see cref="Penetrated"/> — the type-32 <c>ShotImpact</c> hit-result byte).
/// The aim ray's SOURCE is the caller's concern: the live CAM-013 camera pose
/// at shot time (the PN-4 aim source), an attacker's hull facing, or a
/// center-line proxy — this module only compares the model's verdict for a
/// given aim to the outcome.
/// </summary>
public readonly record struct ScoredShot(
    AimRay Aim,
    OverlayTankState Victim,
    IReadOnlyList<CollisionMeshPart> Parts,
    TankArmor Armor,
    ShellSpec Shell,
    bool Penetrated);

/// <summary>One shot's scored row in a <see cref="PenValidationReport"/>.</summary>
public readonly record struct PenValidationShotRow(
    bool Penetrated,
    bool PredictedRicochet,
    PenetrationBand Band,
    double? IncidenceDegrees,
    double? EffectiveArmorMm,
    double? PenetrationMmAtRange);

/// <summary>
/// The PN-4 validation report: how well the deterministic pen model predicts
/// the decoded shot outcomes. Geometry-first (shell-independent): a predicted
/// ricochet must be a NON-penetrating hit, so <see cref="RicochetPrecision"/>
/// is the fraction of predicted ricochets that did not penetrate. Full
/// classification (shell-dependent): a determinate <c>Pen</c>/<c>NoPen</c>
/// band must agree with the outcome — <c>Pen</c> ⇒ penetrated, <c>NoPen</c> ⇒
/// not penetrated; <c>Marginal</c> and <c>Unknown</c> are excluded (they are
/// deliberately not a yes/no prediction).
/// </summary>
public sealed record PenValidationReport(
    int TotalShots,
    int PredictedRicochet,
    int RicochetAgreements,
    double RicochetPrecision,
    int ClassifiedShots,
    int BandAgreements,
    double BandAccuracy,
    IReadOnlyList<PenValidationShotRow> Rows);

/// <summary>
/// Scores the pen model's ricochet + band predictions against the decoded
/// shot outcomes (PN-4's scoring core). Pure and fail-closed: a shot whose
/// aim cannot resolve against the victim's mesh yields an
/// <see cref="PenetrationBand.Unknown"/> row (no ricochet, unclassified),
/// which counts toward <see cref="PenValidationReport.TotalShots"/> but never
/// toward a prediction.
/// </summary>
public static class PenValidation
{
    /// <summary>
    /// Scores every shot and reports the geometry-first ricochet agreement and
    /// the full-classification band accuracy, with a per-shot row for
    /// localizing disagreements.
    /// </summary>
    public static PenValidationReport Score(IReadOnlyList<ScoredShot> shots)
    {
        ArgumentNullException.ThrowIfNull(shots);

        List<PenValidationShotRow> rows = new(shots.Count);
        int predictedRicochet = 0;
        int ricochetAgreements = 0;
        int classified = 0;
        int bandAgreements = 0;

        foreach (ScoredShot shot in shots)
        {
            PenetrationVerdict verdict = PenetrationAim.EvaluateAgainstMesh(
                shot.Aim, shot.Victim, shot.Parts, shot.Armor, shot.Shell, out _);

            bool ricochet = verdict.Ricochet;
            if (ricochet)
            {
                predictedRicochet++;
                if (!shot.Penetrated)
                {
                    ricochetAgreements++;
                }
            }

            if (verdict.Band is PenetrationBand.Pen or PenetrationBand.NoPen)
            {
                classified++;
                bool predictedPen = verdict.Band == PenetrationBand.Pen;
                if (predictedPen == shot.Penetrated)
                {
                    bandAgreements++;
                }
            }

            rows.Add(new PenValidationShotRow(
                shot.Penetrated,
                ricochet,
                verdict.Band,
                ToDegrees(verdict.IncidenceRadians),
                verdict.EffectiveArmorMm,
                verdict.PenetrationMmAtRange));
        }

        return new PenValidationReport(
            shots.Count,
            predictedRicochet,
            ricochetAgreements,
            Ratio(ricochetAgreements, predictedRicochet),
            classified,
            bandAgreements,
            Ratio(bandAgreements, classified),
            rows);
    }

    private static double Ratio(int numerator, int denominator) =>
        denominator == 0 ? 0.0 : (double)numerator / denominator;

    private static double? ToDegrees(double? radians) =>
        radians is null ? null : radians.Value * 180.0 / Math.PI;
}
