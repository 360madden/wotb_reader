using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WotBTreader.Overlay.Converters;

namespace WotBTreader.Overlay.Tests;

[TestClass]
public sealed class ValueConvertersTests
{
    // ── NullToCollapsedConverter ──────────────────────────────

    [TestMethod]
    public void NullToCollapsed_Null_ReturnsCollapsed()
    {
        NullToCollapsedConverter converter = new();

        object result = converter.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture);

        Assert.AreEqual(Visibility.Collapsed, result);
    }

    [TestMethod]
    public void NullToCollapsed_NonNullObject_ReturnsVisible()
    {
        NullToCollapsedConverter converter = new();

        object result = converter.Convert("anything", typeof(Visibility), null, CultureInfo.InvariantCulture);

        Assert.AreEqual(Visibility.Visible, result);
    }

    [TestMethod]
    public void NullToCollapsed_EmptyString_ReturnsVisible()
    {
        NullToCollapsedConverter converter = new();

        object result = converter.Convert(string.Empty, typeof(Visibility), null, CultureInfo.InvariantCulture);

        Assert.AreEqual(Visibility.Visible, result);
    }

    [TestMethod]
    public void NullToCollapsed_ConvertBack_Throws()
    {
        NullToCollapsedConverter converter = new();

        try
        {
            converter.ConvertBack(Visibility.Visible, typeof(object), null, CultureInfo.InvariantCulture);
            Assert.Fail("Expected NotSupportedException.");
        }
        catch (NotSupportedException)
        {
            // Expected.
        }
    }

    // ── TeamToColorConverter ──────────────────────────────────

    [TestMethod]
    public void TeamToColor_Team1_ReturnsDodgerBlue()
    {
        TeamToColorConverter converter = new();

        object result = converter.Convert(1, typeof(Color), null, CultureInfo.InvariantCulture);

        Assert.AreEqual(Colors.DodgerBlue, result);
    }

    [TestMethod]
    public void TeamToColor_Team2_ReturnsOrangeRed()
    {
        TeamToColorConverter converter = new();

        object result = converter.Convert(2, typeof(Color), null, CultureInfo.InvariantCulture);

        Assert.AreEqual(Colors.OrangeRed, result);
    }

    [TestMethod]
    public void TeamToColor_UnknownTeam_ReturnsGray()
    {
        TeamToColorConverter converter = new();

        object result = converter.Convert(99, typeof(Color), null, CultureInfo.InvariantCulture);

        Assert.AreEqual(Colors.Gray, result);
    }

    [TestMethod]
    public void TeamToColor_NullValue_ReturnsGray()
    {
        TeamToColorConverter converter = new();

        object result = converter.Convert(null, typeof(Color), null, CultureInfo.InvariantCulture);

        Assert.AreEqual(Colors.Gray, result);
    }

    [TestMethod]
    public void TeamToColor_ConvertBack_Throws()
    {
        TeamToColorConverter converter = new();

        try
        {
            converter.ConvertBack(Colors.DodgerBlue, typeof(int), null, CultureInfo.InvariantCulture);
            Assert.Fail("Expected NotSupportedException.");
        }
        catch (NotSupportedException)
        {
            // Expected.
        }
    }
}
