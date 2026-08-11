using System.Windows;
using WotBTreader.Overlay.Views;

namespace WotBTreader.Overlay.Tests;

[TestClass]
public sealed class W2sHudViewTests
{
    [TestMethod]
    public void AnchorRect_CentresPlateAboveProjectedPoint()
    {
        // 64x20 plate centred horizontally, 6px above the point (540).
        Rect rect = W2sHudView.AnchorRect(
            screenX: 960, screenY: 540, viewportWidth: 1920, viewportHeight: 1080);

        Assert.AreEqual(960 - 32, rect.Left, 1e-9);
        Assert.AreEqual(540 - 20 - 6, rect.Top, 1e-9);
        Assert.AreEqual(64, rect.Width, 1e-9);
        Assert.AreEqual(20, rect.Height, 1e-9);
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
        Assert.AreEqual(1920 - 64, right.Left, 1e-9);
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
}
