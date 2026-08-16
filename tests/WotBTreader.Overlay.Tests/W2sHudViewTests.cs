using System.Windows;
using System.Windows.Media;
using WotBTreader.Overlay.Views;

namespace WotBTreader.Overlay.Tests;

[TestClass]
public sealed class W2sHudViewTests
{
    [TestMethod]
    public void AnchorRect_CentresPlateAboveProjectedPoint()
    {
        // 96x22 plate centred horizontally, 6px above the point (540).
        Rect rect = W2sHudView.AnchorRect(
            screenX: 960, screenY: 540, viewportWidth: 1920, viewportHeight: 1080);

        Assert.AreEqual(960 - 48, rect.Left, 1e-9);
        Assert.AreEqual(540 - 22 - 6, rect.Top, 1e-9);
        Assert.AreEqual(96, rect.Width, 1e-9);
        Assert.AreEqual(22, rect.Height, 1e-9);
    }

    [TestMethod]
    public void AnchorRect_ClampsToViewportEdges()
    {
        // A point near the left edge must not push the plate off-screen.
        Rect left = W2sHudView.AnchorRect(screenX: 5, screenY: 540, viewportWidth: 1920, viewportHeight: 1080);
        Assert.AreEqual(0, left.Left, 1e-9);

        // A point near the top edge keeps the plate fully inside.
        Rect top = W2sHudView.AnchorRect(screenX: 960, screenY: 2, viewportWidth: 1920, viewportHeight: 1080);
        Assert.AreEqual(0, top.Top, 1e-9);

        // A point near the right edge clamps to the far side.
        Rect right = W2sHudView.AnchorRect(screenX: 1919, screenY: 540, viewportWidth: 1920, viewportHeight: 1080);
        Assert.AreEqual(1920 - 96, right.Left, 1e-9);
    }

    [TestMethod]
    public void AnchorRect_TinyViewportNeverProducesNegativePlacement()
    {
        Rect rect = W2sHudView.AnchorRect(screenX: 10, screenY: 10, viewportWidth: 20, viewportHeight: 20);

        Assert.IsTrue(rect.Left >= 0);
        Assert.IsTrue(rect.Top >= 0);
    }

    [TestMethod]
    public void MinimapDotRect_CentresDotOnNormalizedPosition()
    {
        // 150px panel, 4px radius: u=0.5,v=0.5 -> dot rect centred at (75,75).
        Rect rect = W2sHudView.MinimapDotRect(0.5, 0.5, panelSize: 150, dotRadius: 4);

        Assert.AreEqual(75 - 4, rect.Left, 1e-9);
        Assert.AreEqual(75 - 4, rect.Top, 1e-9);
        Assert.AreEqual(8, rect.Width, 1e-9);
        Assert.AreEqual(8, rect.Height, 1e-9);
    }

    [TestMethod]
    public void MinimapDotRect_CornerPositionsLandInsidePanel()
    {
        Rect nw = W2sHudView.MinimapDotRect(0.0, 0.0, panelSize: 150, dotRadius: 4);
        Assert.AreEqual(-4, nw.Left, 1e-9);
        Assert.AreEqual(-4, nw.Top, 1e-9);

        Rect se = W2sHudView.MinimapDotRect(1.0, 1.0, panelSize: 150, dotRadius: 4);
        Assert.AreEqual(150 - 4, se.Left, 1e-9);
        Assert.AreEqual(150 - 4, se.Top, 1e-9);
    }

    [TestMethod]
    public void MinimapImageRect_FillsPanelSquare()
    {
        // The texture is stretched to the panel so normalized coordinates
        // (which map 0..1 onto 0..panelSize) align with terrain features.
        Rect rect = W2sHudView.MinimapImageRect(panelSize: 150);

        Assert.AreEqual(0, rect.Left, 1e-9);
        Assert.AreEqual(0, rect.Top, 1e-9);
        Assert.AreEqual(150, rect.Width, 1e-9);
        Assert.AreEqual(150, rect.Height, 1e-9);
    }

    [TestMethod]
    public void CameraTickApex_YawZeroFacesDownPanel()
    {
        // Yaw 0 faces +Z, which maps to panel-down (Canvas.SetTop grows down).
        Point apex = W2sHudView.CameraTickApex(
            cameraX: 0.5, cameraZ: 0.5, yawRadians: 0.0,
            panelSize: 150, tickLength: 14);

        Assert.AreEqual(0.5 * 150, apex.X, 1e-9);
        Assert.AreEqual(0.5 * 150 + 14, apex.Y, 1e-9);
    }

    [TestMethod]
    public void CameraTickApex_PiOverTwoFacesRightPanel()
    {
        // Yaw +pi/2 faces +X, which maps to panel-right.
        Point apex = W2sHudView.CameraTickApex(
            cameraX: 0.5, cameraZ: 0.5, yawRadians: Math.PI / 2.0,
            panelSize: 150, tickLength: 14);

        Assert.AreEqual(0.5 * 150 + 14, apex.X, 1e-9);
        Assert.AreEqual(0.5 * 150, apex.Y, 1e-9);
    }

    [TestMethod]
    public void CameraTickApex_PiFacesUpPanel()
    {
        // Yaw pi faces -Z, which maps to panel-up.
        Point apex = W2sHudView.CameraTickApex(
            cameraX: 0.5, cameraZ: 0.5, yawRadians: Math.PI,
            panelSize: 150, tickLength: 14);

        Assert.AreEqual(0.5 * 150, apex.X, 1e-9);
        Assert.AreEqual(0.5 * 150 - 14, apex.Y, 1e-9);
    }

    [TestMethod]
    public void PlaybackFillWidth_ScalesAndClamps()
    {
        Assert.AreEqual(0, W2sHudView.PlaybackFillWidth(trackWidth: 320, progress: 0.0), 1e-9);
        Assert.AreEqual(160, W2sHudView.PlaybackFillWidth(trackWidth: 320, progress: 0.5), 1e-9);
        Assert.AreEqual(320, W2sHudView.PlaybackFillWidth(trackWidth: 320, progress: 1.0), 1e-9);
        // Out-of-range progress clamps instead of overflowing the track.
        Assert.AreEqual(320, W2sHudView.PlaybackFillWidth(trackWidth: 320, progress: 1.5), 1e-9);
        Assert.AreEqual(0, W2sHudView.PlaybackFillWidth(trackWidth: 320, progress: -0.5), 1e-9);
    }

    [TestMethod]
    public void FormatPlaybackLabel_ClockStyle()
    {
        Assert.AreEqual("0:47 / 4:12", W2sHudView.FormatPlaybackLabel(47, 252));
        Assert.AreEqual("10:00 / 10:00", W2sHudView.FormatPlaybackLabel(600, 600));
    }

    [TestMethod]
    public void FormatPlaybackLabel_UnknownDuration_Null()
    {
        Assert.IsNull(W2sHudView.FormatPlaybackLabel(47, 0));
        Assert.IsNull(W2sHudView.FormatPlaybackLabel(47, -5));
        Assert.IsNull(W2sHudView.FormatPlaybackLabel(double.NaN, 252));
    }

    [TestMethod]
    public void NameplateTotalsLabel_InvariantNumbers()
    {
        Assert.AreEqual("1200 dmg · 2 kills", W2sHudView.NameplateTotalsLabel(1200, 2));
        Assert.AreEqual("0 dmg · 0 kills", W2sHudView.NameplateTotalsLabel(0, 0));
    }

    [TestMethod]
    public void NameplateMetaLabel_JoinsHpRangeAndTotals()
    {
        // Exact HP, range, then damage/kills in one muted line.
        Assert.AreEqual(
            "438/700 HP · 210 m · 1200 dmg · 2 kills",
            W2sHudView.NameplateMetaLabel(
                distanceMeters: 210, maxHealth: 700, currentHealth: 438, damageDealt: 1200, kills: 2));
    }

    [TestMethod]
    public void NameplateMetaLabel_OmitsHpWhenMaxUnknown()
    {
        // No type-5 max-HP evidence -> HP part is dropped, rest remains.
        Assert.AreEqual(
            "210 m · 0 dmg · 0 kills",
            W2sHudView.NameplateMetaLabel(
                distanceMeters: 210, maxHealth: 0, currentHealth: 0, damageDealt: 0, kills: 0));
    }

    [TestMethod]
    public void NameplateMetaLabel_NonFiniteDistance_DegradesToZero()
    {
        // A NaN distance must not render as "NaN m" in the meta line.
        Assert.AreEqual(
            "0 m · 0 dmg · 0 kills",
            W2sHudView.NameplateMetaLabel(
                distanceMeters: double.NaN, maxHealth: 0, currentHealth: 0, damageDealt: 0, kills: 0));
    }

    [TestMethod]
    public void ResolveMarkerColor_ValidHex_ReturnsColor()
    {
        Assert.AreEqual(Color.FromRgb(0xFF, 0x00, 0x00), W2sHudView.ResolveMarkerColor("#FF0000"));
        Assert.AreEqual(Color.FromRgb(0xFF, 0xD7, 0x00), W2sHudView.ResolveMarkerColor("#FFD700"));
    }

    [TestMethod]
    public void ResolveMarkerColor_NullEmptyOrMalformed_ReturnsNull()
    {
        Assert.IsNull(W2sHudView.ResolveMarkerColor(null));
        Assert.IsNull(W2sHudView.ResolveMarkerColor(string.Empty));
        Assert.IsNull(W2sHudView.ResolveMarkerColor("   "));
        Assert.IsNull(W2sHudView.ResolveMarkerColor("banana"));
    }

    [TestMethod]
    public void PipAnimation_BirthIsFullOpacityAtAnchor()
    {
        (double rise, double opacity) = W2sHudView.PipAnimation(ageFrames: 0, durationFrames: 16);

        Assert.AreEqual(0, rise, 1e-9);
        Assert.AreEqual(1, opacity, 1e-9);
    }

    [TestMethod]
    public void PipAnimation_EndOfLifeIsFullyRisenAndFaded()
    {
        (double rise, double opacity) = W2sHudView.PipAnimation(ageFrames: 16, durationFrames: 16);

        Assert.AreEqual(W2sHudView.PipRisePixels, rise, 1e-9);
        Assert.AreEqual(0, opacity, 1e-9);
    }

    [TestMethod]
    public void PipAnimation_EaseOutRisesFasterThanLinear()
    {
        // At the midpoint the eased rise is ahead of a linear 50% rise.
        (double rise, _) = W2sHudView.PipAnimation(ageFrames: 8, durationFrames: 16);

        Assert.AreEqual(W2sHudView.PipRisePixels * 0.75, rise, 1e-9);
    }

    [TestMethod]
    public void PipAnimation_ClampsOutOfRangeAges()
    {
        // Over-aged pips stay fully risen and faded.
        (double rise, double opacity) = W2sHudView.PipAnimation(ageFrames: 100, durationFrames: 16);
        Assert.AreEqual(W2sHudView.PipRisePixels, rise, 1e-9);
        Assert.AreEqual(0, opacity, 1e-9);

        // A negative age clamps back to birth.
        (double rise2, double opacity2) = W2sHudView.PipAnimation(ageFrames: -5, durationFrames: 16);
        Assert.AreEqual(0, rise2, 1e-9);
        Assert.AreEqual(1, opacity2, 1e-9);
    }

    [TestMethod]
    public void PipAnimation_ZeroDuration_IsFullyFaded()
    {
        (double rise, double opacity) = W2sHudView.PipAnimation(ageFrames: 3, durationFrames: 0);

        Assert.AreEqual(0, rise, 1e-9);
        Assert.AreEqual(0, opacity, 1e-9);
    }

    [TestMethod]
    public void HpGhostEase_DamageLagsAboveLiveFill()
    {
        // A hit from full to 50%: the ghost stays above the live fill and
        // eases only partway down in one step.
        double ghost = W2sHudView.HpGhostEase(1.0, 0.5);

        Assert.IsTrue(ghost > 0.5);
        Assert.IsTrue(ghost < 1.0);
    }

    [TestMethod]
    public void HpGhostEase_SettlesToTargetOverFrames()
    {
        double ghost = 1.0;
        for (int i = 0; i < 100; i++)
        {
            ghost = W2sHudView.HpGhostEase(ghost, 0.5);
        }

        Assert.AreEqual(0.5, ghost, 1e-9);
    }

    [TestMethod]
    public void HpGhostEase_HealSnapsForward()
    {
        // Regen must move the ghost up instantly so it never lags behind.
        Assert.AreEqual(0.8, W2sHudView.HpGhostEase(0.5, 0.8), 1e-9);
    }

    [TestMethod]
    public void HpGhostEase_NonFiniteInputsAreSafe()
    {
        Assert.AreEqual(0, W2sHudView.HpGhostEase(double.NaN, double.NaN), 1e-9);
        Assert.AreEqual(1, W2sHudView.HpGhostEase(double.PositiveInfinity, 1.0), 1e-9);
        Assert.AreEqual(0.5, W2sHudView.HpGhostEase(double.NegativeInfinity, 0.5), 1e-9);
    }

    [TestMethod]
    public void FeedEntryAnimation_BirthIsOffLeftAndFaded()
    {
        (double slide, double opacity) = W2sHudView.FeedEntryAnimation(ageFrames: 0, durationFrames: 8);

        Assert.AreEqual(-W2sHudView.FeedSlidePixels, slide, 1e-9);
        Assert.AreEqual(0, opacity, 1e-9);
    }

    [TestMethod]
    public void FeedEntryAnimation_SettlesToPositionAndOpacity()
    {
        (double slide, double opacity) = W2sHudView.FeedEntryAnimation(ageFrames: 8, durationFrames: 8);

        Assert.AreEqual(0, slide, 1e-9);
        Assert.AreEqual(1, opacity, 1e-9);
    }

    [TestMethod]
    public void FeedEntryAnimation_OverAgeStaysSettled()
    {
        (double slide, double opacity) = W2sHudView.FeedEntryAnimation(ageFrames: 100, durationFrames: 8);

        Assert.AreEqual(0, slide, 1e-9);
        Assert.AreEqual(1, opacity, 1e-9);
    }

    [TestMethod]
    public void FeedEntryAnimation_ZeroDurationIsSettled()
    {
        (double slide, double opacity) = W2sHudView.FeedEntryAnimation(ageFrames: 3, durationFrames: 0);

        Assert.AreEqual(0, slide, 1e-9);
        Assert.AreEqual(1, opacity, 1e-9);
    }

    [TestMethod]
    public void PenPulseScale_BirthOvershoots()
    {
        double scale = W2sHudView.PenPulseScale(ageFrames: 0, durationFrames: 10);

        Assert.AreEqual(1.0 + W2sHudView.PenPulseOvershoot, scale, 1e-9);
    }

    [TestMethod]
    public void PenPulseScale_SettlesToFullSize()
    {
        Assert.AreEqual(1.0, W2sHudView.PenPulseScale(ageFrames: 10, durationFrames: 10), 1e-9);
        Assert.AreEqual(1.0, W2sHudView.PenPulseScale(ageFrames: 100, durationFrames: 10), 1e-9);
        Assert.AreEqual(1.0, W2sHudView.PenPulseScale(ageFrames: 0, durationFrames: 0), 1e-9);
    }

    [TestMethod]
    public void PenPulseScale_EasesOutTowardFullSize()
    {
        double halfway = W2sHudView.PenPulseScale(ageFrames: 5, durationFrames: 10);

        Assert.IsTrue(halfway > 1.0);
        Assert.IsTrue(halfway < 1.0 + W2sHudView.PenPulseOvershoot);
    }

    [TestMethod]
    public void PenBadgeLabel_BandedVerdictWithNumericReadout()
    {
        Assert.AreEqual(
            "PEN  92/93 mm",
            W2sHudView.PenBadgeLabel("Pen", effectiveArmorMm: 93, penetrationMmAtRange: 92, ricochet: false));
        Assert.AreEqual(
            "MARGINAL  50/52 mm",
            W2sHudView.PenBadgeLabel("Marginal", 52, 50, ricochet: false));
        Assert.AreEqual(
            "NO PEN",
            W2sHudView.PenBadgeLabel("NoPen", null, null, ricochet: false));
    }

    [TestMethod]
    public void PenBadgeLabel_RicochetOverridesBand()
    {
        Assert.AreEqual(
            "RICOCHET",
            W2sHudView.PenBadgeLabel("NoPen", 93, 92, ricochet: true));
    }

    [TestMethod]
    public void PenBadgeLabel_UnknownBand_Empty()
    {
        Assert.AreEqual(
            string.Empty,
            W2sHudView.PenBadgeLabel("Unknown", null, null, ricochet: false));
        Assert.AreEqual(
            string.Empty,
            W2sHudView.PenBadgeLabel("Unknown", 93, 92, ricochet: true));
    }

    [TestMethod]
    public void PenBadgeLabel_ShellPrefix_IsIncluded()
    {
        Assert.AreEqual(
            "[HEAT] PEN  92/93 mm",
            W2sHudView.PenBadgeLabel("Pen", 93, 92, ricochet: false, shell: "HEAT"));
        Assert.AreEqual(
            "[HE] RICOCHET",
            W2sHudView.PenBadgeLabel("NoPen", 93, 92, ricochet: true, shell: "HE"));
        Assert.AreEqual(
            "[AP] NO PEN",
            W2sHudView.PenBadgeLabel("NoPen", null, null, ricochet: false, shell: "AP"));
    }

    [TestMethod]
    public void PenBadgeLabel_Face_IsIncluded()
    {
        Assert.AreEqual(
            "FRONT PEN  92/93 mm",
            W2sHudView.PenBadgeLabel("Pen", 93, 92, ricochet: false, face: "Front"));
        Assert.AreEqual(
            "REAR RICOCHET",
            W2sHudView.PenBadgeLabel("NoPen", 93, 92, ricochet: true, face: "Back"));
        Assert.AreEqual(
            "[HEAT] SIDE NO PEN",
            W2sHudView.PenBadgeLabel("NoPen", null, null, ricochet: false, shell: "HEAT", face: "Side"));
        // Unknown/empty face adds no token.
        Assert.AreEqual(
            "NO PEN",
            W2sHudView.PenBadgeLabel("NoPen", null, null, ricochet: false, face: "Unknown"));
    }
}
