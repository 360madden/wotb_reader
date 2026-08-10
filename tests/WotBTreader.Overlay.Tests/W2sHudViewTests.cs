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
}
