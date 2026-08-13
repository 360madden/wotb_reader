using WotBTreader.Application.Game;
using WotBTreader.Core.Overlay;

namespace WotBTreader.Application.Tests;

[TestClass]
public sealed class PenetrationContextTests
{
    [TestMethod]
    public void NominalArmor_FrontIsThickestPrimaryGroup()
    {
        VehicleArmorProfile profile = new(
            VehicleId: "uk:GB08_Churchill_I",
            HullGroups:
            [
                new ArmorGroup("armor_1", 93.4),
                new ArmorGroup("armor_2", 186.7),
                new ArmorGroup("armor_3", 66.7),
                new ArmorGroup("armor_4", 53.4),
            ],
            TurretGroups: [],
            PrimaryArmorGroups: ["armor_2", "armor_3", "armor_4"]);

        TankArmor armor = PenetrationContext.NominalArmor(profile);

        // Front = the thickest declared primary (frontal) group.
        Assert.AreEqual(186.7, armor.FrontMm, 1e-9);
        // Side/rear are not declared by the armor XML -> 0 = unknown.
        Assert.AreEqual(0, armor.SideMm, 1e-9);
        Assert.AreEqual(0, armor.RearMm, 1e-9);
    }

    [TestMethod]
    public void NominalArmor_NoPrimaryArmor_ZeroFront()
    {
        VehicleArmorProfile profile = new(
            VehicleId: "uk:OddTank",
            HullGroups: [new ArmorGroup("armor_1", 80)],
            TurretGroups: [],
            PrimaryArmorGroups: []);

        TankArmor armor = PenetrationContext.NominalArmor(profile);

        Assert.AreEqual(0, armor.FrontMm, 1e-9);
    }
}
