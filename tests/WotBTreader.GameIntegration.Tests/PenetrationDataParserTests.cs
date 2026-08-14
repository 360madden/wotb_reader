using System.Text;
using WotBTreader.Application.Game;
using WotBTreader.GameIntegration.Metadata;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class PenetrationDataParserTests
{
    private const long MaxCharacters = 64 * 1024;

    private static readonly string[] ExpectedPrimaryArmor =
        ["armor_2", "armor_3", "armor_4"];
    private static readonly string[] ExpectedStockShells = ["_shared_shell"];

    [TestMethod]
    public void ParseVehicleArmor_HullAndTurret_ParsesGroupsAndPrimary()
    {
        // Shape mirrors the install's {nation}/{tank}.xml.dvpl: a root with a
        // hull block (armor_1..16 + primaryArmor) and a turrets0 block whose
        // first turret carries the turret armor groups + primaryArmor. Group
        // entries with a trailing <vehicleDamageFactor> child (a real shape)
        // still read only their leading thickness text.
        VehicleArmorProfile profile = PenetrationDataParser.ParseVehicleArmor(
            Encoding.UTF8.GetBytes(
                """
                <root>
                  <hull>
                    <armor>
                      <armor_1>93.4</armor_1>
                      <armor_2>186.7</armor_2>
                      <armor_11>53.4<vehicleDamageFactor>0.0</vehicleDamageFactor>
                      </armor_11>
                      <armor_13>0</armor_13>
                    </armor>
                    <primaryArmor>armor_2 armor_3 armor_4</primaryArmor>
                  </hull>
                  <turrets0>
                    <Turret_1_GB08_Churchill_I>
                      <armor>
                        <armor_1>102</armor_1>
                        <armor_2>90</armor_2>
                      </armor>
                      <primaryArmor>armor_1 armor_3 armor_4</primaryArmor>
                    </Turret_1_GB08_Churchill_I>
                  </turrets0>
                </root>
                """),
            "uk:GB08_Churchill_I",
            MaxCharacters);

        Assert.AreEqual("uk:GB08_Churchill_I", profile.VehicleId);
        Assert.HasCount(4, profile.HullGroups);
        Assert.AreEqual(93.4, profile.HullGroups[0].ThicknessMm, 0.001);
        Assert.AreEqual("armor_1", profile.HullGroups[0].Name);
        Assert.AreEqual(53.4, profile.HullGroups[2].ThicknessMm, 0.001);
        Assert.AreEqual(0, profile.HullGroups[3].ThicknessMm, 0.001);
        CollectionAssert.AreEqual(
            ExpectedPrimaryArmor,
            profile.PrimaryArmorGroups.ToArray());

        Assert.HasCount(2, profile.TurretGroups);
        Assert.AreEqual(102, profile.TurretGroups[0].ThicknessMm, 0.001);
    }

    [TestMethod]
    public void ParseVehicleArmor_NoArmorOrNoRoot_ReturnsEmptyProfile()
    {
        VehicleArmorProfile empty = PenetrationDataParser.ParseVehicleArmor(
            Encoding.UTF8.GetBytes("<root><hull><weight>19438</weight></hull></root>"),
            "uk:EmptyTank",
            MaxCharacters);

        Assert.AreEqual("uk:EmptyTank", empty.VehicleId);
        Assert.IsEmpty(empty.HullGroups);
        Assert.IsEmpty(empty.TurretGroups);
        Assert.IsEmpty(empty.PrimaryArmorGroups);
    }

    [TestMethod]
    public void ParseVehicleArmor_NonNumericArmorGroup_IsSkipped()
    {
        // armor_1 carries only a child element (no leading thickness text) and
        // must be skipped rather than parsed as 0 — a real malformed shape the
        // parser must not turn into a fabricated plate.
        VehicleArmorProfile profile = PenetrationDataParser.ParseVehicleArmor(
            Encoding.UTF8.GetBytes(
                """
                <root>
                  <hull>
                    <armor>
                      <armor_1><vehicleDamageFactor>0.0</vehicleDamageFactor></armor_1>
                      <armor_2>80</armor_2>
                    </armor>
                  </hull>
                </root>
                """),
            "uk:OddTank",
            MaxCharacters);

        Assert.HasCount(1, profile.HullGroups);
        Assert.AreEqual("armor_2", profile.HullGroups[0].Name);
    }

    [TestMethod]
    public void ParseShells_SkipsIconsAndParsesStats()
    {
        IReadOnlyList<ShellProfile> shells = PenetrationDataParser.ParseShells(
            Encoding.UTF8.GetBytes(
                """
                <root>
                  <icons>
                    <ap>ARMOR_PIERCING.png 0 0</ap>
                  </icons>
                  <_15mm_AP_W_Mk1>
                    <kind>ARMOR_PIERCING</kind>
                    <caliber>15</caliber>
                    <normalizationAngle>15</normalizationAngle>
                    <ricochetAngle>70</ricochetAngle>
                  </_15mm_AP_W_Mk1>
                  <_40mm_SAP>
                    <kind>ARMOR_PIERCING</kind>
                    <caliber>40</caliber>
                    <normalizationAngle>5</normalizationAngle>
                    <ricochetAngle>70</ricochetAngle>
                  </_40mm_SAP>
                </root>
                """),
            MaxCharacters);

        Assert.HasCount(2, shells);
        Assert.AreEqual("_15mm_AP_W_Mk1", shells[0].Name);
        Assert.AreEqual("ARMOR_PIERCING", shells[0].Kind);
        Assert.AreEqual(15, shells[0].CaliberMm, 0.001);
        Assert.AreEqual(15, shells[0].NormalizationDegrees, 0.001);
        Assert.AreEqual(70, shells[0].RicochetDegrees, 0.001);
    }

    [TestMethod]
    public void ParseShells_MissingOrNonNumericCaliber_IsSkipped()
    {
        IReadOnlyList<ShellProfile> shells = PenetrationDataParser.ParseShells(
            Encoding.UTF8.GetBytes(
                """
                <root>
                  <_noCaliber>
                    <kind>ARMOR_PIERCING</kind>
                  </_noCaliber>
                  <_nonNumeric>
                    <kind>ARMOR_PIERCING</kind>
                    <caliber>wide</caliber>
                  </_nonNumeric>
                  <_zero>
                    <kind>HIGH_EXPLOSIVE</kind>
                    <caliber>0</caliber>
                  </_zero>
                </root>
                """),
            MaxCharacters);

        Assert.IsEmpty(shells);
    }

    [TestMethod]
    public void ParseGuns_ParsesShotPiercingPowerPair()
    {
        IReadOnlyList<GunShellProfile> guns = PenetrationDataParser.ParseGuns(
            Encoding.UTF8.GetBytes(
                """
                <root>
                  <nextAvailableId>1060</nextAvailableId>
                  <ids><_15mm_Machine_gun_BESA>5</_15mm_Machine_gun_BESA></ids>
                  <shared>
                    <_15mm_Machine_gun_BESA>
                      <shots>
                        <_15mm_AP_W_Mk1>
                          <speed>884</speed>
                          <maxDistance>350</maxDistance>
                          <piercingPower>25 19</piercingPower>
                        </_15mm_AP_W_Mk1>
                      </shots>
                    </_15mm_Machine_gun_BESA>
                  </shared>
                </root>
                """),
            MaxCharacters);

        Assert.HasCount(1, guns);
        Assert.AreEqual("_15mm_Machine_gun_BESA", guns[0].GunName);
        Assert.AreEqual("_15mm_AP_W_Mk1", guns[0].ShellName);
        Assert.AreEqual(25, guns[0].PiercingPowerNearMm, 0.001);
        Assert.AreEqual(19, guns[0].PiercingPowerFarMm, 0.001);
        Assert.AreEqual(350, guns[0].MaxDistanceMeters, 0.001);
        Assert.AreEqual(884, guns[0].SpeedMetersPerSecond, 0.001);
    }

    [TestMethod]
    public void ParseGuns_MalformedPowerPair_IsSkipped()
    {
        IReadOnlyList<GunShellProfile> guns = PenetrationDataParser.ParseGuns(
            Encoding.UTF8.GetBytes(
                """
                <root>
                  <ids><_badGun>1</_badGun><_noShots>2</_noShots></ids>
                  <shared>
                    <_badGun>
                      <shots>
                        <_badShell>
                          <maxDistance>350</maxDistance>
                          <piercingPower>25</piercingPower>
                        </_badShell>
                      </shots>
                    </_badGun>
                    <_noShots>
                      <weight>5</weight>
                    </_noShots>
                  </shared>
                </root>
                """),
            MaxCharacters);

        Assert.IsEmpty(guns);
    }

    [TestMethod]
    public void ParseStockGunShellName_ReadsFirstShotOfFirstGun()
    {
        string? shell = PenetrationDataParser.ParseStockGunShellName(
            Encoding.UTF8.GetBytes(
                """
                <root>
                  <hull>
                    <turretPositions><turret>0 0 0</turret></turretPositions>
                  </hull>
                  <turrets0>
                    <Turret_1_GB08_Churchill_I>
                      <guns>
                        <_2pdr_Gun_Mk_XT>
                          <shots>
                            <_2pdr_AP_Mk.IXBT_2><shell>shared</shell></_2pdr_AP_Mk.IXBT_2>
                            <_2pdr_APCNR_Mk.2><shell>shared</shell></_2pdr_APCNR_Mk.2>
                          </shots>
                        </_2pdr_Gun_Mk_XT>
                      </guns>
                    </Turret_1_GB08_Churchill_I>
                  </turrets0>
                </root>
                """),
            MaxCharacters);

        Assert.AreEqual("_2pdr_AP_Mk.IXBT_2", shell);
    }

    [TestMethod]
    public void ParseStockGunShellName_NoGuns_Null()
    {
        Assert.IsNull(PenetrationDataParser.ParseStockGunShellName(
            Encoding.UTF8.GetBytes("<root><hull><weight>10</weight></hull></root>"),
            MaxCharacters));
    }

    [TestMethod]
    public void ParseGunShotNames_ReadsAllShotsOfFirstGun()
    {
        IReadOnlyList<string> shots = PenetrationDataParser.ParseGunShotNames(
            Encoding.UTF8.GetBytes(
                """
                <root>
                  <turrets0>
                    <Turret_1_GB08_Churchill_I>
                      <guns>
                        <_2pdr_Gun_Mk_XT>
                          <shots>
                            <_2pdr_AP_Mk.IXBT_2><shell>shared</shell></_2pdr_AP_Mk.IXBT_2>
                            <_2pdr_APCNR_Mk.2><shell>shared</shell></_2pdr_APCNR_Mk.2>
                            <_2pdr_HE_Mk.1><shell>shared</shell></_2pdr_HE_Mk.1>
                          </shots>
                        </_2pdr_Gun_Mk_XT>
                      </guns>
                    </Turret_1_GB08_Churchill_I>
                  </turrets0>
                </root>
                """),
            MaxCharacters);

        Assert.HasCount(3, shots);
        Assert.AreEqual("_2pdr_AP_Mk.IXBT_2", shots[0]);
        Assert.AreEqual("_2pdr_APCNR_Mk.2", shots[1]);
        Assert.AreEqual("_2pdr_HE_Mk.1", shots[2]);
    }

    [TestMethod]
    public void ParseStockGunProfile_PreservesGunIdentity()
    {
        StockGunProfile? profile = PenetrationDataParser.ParseStockGunProfile(
            Encoding.UTF8.GetBytes(
                """
                <root>
                  <turrets0>
                    <Turret_1>
                      <guns>
                        <_stock_gun>
                          <shots>
                            <_shared_shell><shell>shared</shell></_shared_shell>
                          </shots>
                        </_stock_gun>
                      </guns>
                    </Turret_1>
                  </turrets0>
                </root>
                """),
            MaxCharacters);

        Assert.IsNotNull(profile);
        Assert.AreEqual("_stock_gun", profile!.GunName);
        CollectionAssert.AreEqual(ExpectedStockShells, profile.ShellNames.ToArray());
    }

    [TestMethod]
    public void ParseGunShotNames_NoGuns_Empty()
    {
        Assert.IsEmpty(PenetrationDataParser.ParseGunShotNames(
            Encoding.UTF8.GetBytes("<root><hull><weight>10</weight></hull></root>"),
            MaxCharacters));
    }

    [TestMethod]
    public void ParseVehicleArmor_OversizedResource_Throws()
    {
        string xml = $"<root>{new string('x', (int)(MaxCharacters + 1))}</root>";

        Assert.ThrowsExactly<InvalidDataException>(() =>
            PenetrationDataParser.ParseVehicleArmor(
                Encoding.UTF8.GetBytes(xml),
                "uk:HugeTank",
                MaxCharacters));
    }
}
