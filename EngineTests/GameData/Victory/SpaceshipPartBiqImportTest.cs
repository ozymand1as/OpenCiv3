using System.IO;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;

namespace EngineTests.GameData.Victory;

/// <summary>
/// The BIQ-import half of the space-race production gate (spec 13 sections 2
/// and 6, spec 25 section 2.6): the shipped conquests.biq values the import
/// maps. The gate lives in the small-wonder flag word at BLDG +0xf4, bit
/// 0x10, which QueryCiv3 exposes as BLDG.BuildSpaceshipParts; the quantity
/// comes from the RULE section's General.SpaceshipPartsNeeded array.
///
/// These read the shipped BIQ directly. They are skipped only when no Civ3
/// install is present, the same visible skip the rest of the Civ3-dependent
/// suite uses.
/// </summary>
public class SpaceshipPartBiqImportTest {
	private const string SkipReason = "No Civ3 install found.";

	private static string ConquestsBiqPath =>
		Path.Combine(Civ3Location.GetCiv3Path(), "Conquests", "conquests.biq");

	[SkippableFact]
	public void ShippedConquestsBiq_GatesThePartsOnApollosWholeFlagSet() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), SkipReason);

		BiqData biq = BiqData.LoadFile(ConquestsBiqPath);

		// Apollo Program is the only building in the shipped rules that carries
		// BuildSpaceshipParts, so it is the whole gate.
		Assert.Equal("Apollo Program", biq.Bldg.Single(b => b.BuildSpaceshipParts).Name);

		BLDG apollo = biq.Bldg.Single(b => b.Name == "Apollo Program");
		// The whole flag set the probe reports for Apollo, so a dropped or
		// mis-mapped neighbour bit cannot pass: SmallWonder, Scientific and
		// BuildSpaceshipParts are set, nothing else in the small-wonder flag
		// word is.
		Assert.True(apollo.SmallWonder);
		Assert.False(apollo.Wonder);
		Assert.True(apollo.Scientific);
		Assert.True(apollo.BuildSpaceshipParts);
		Assert.False(apollo.IncreasesLeaderChance);
		Assert.False(apollo.AllowsBuildArmy);
		Assert.False(apollo.AllowsLargerArmies);
		Assert.False(apollo.TreasuryEarnsInterest);
		Assert.False(apollo.ForbiddenPalace);
		Assert.False(apollo.DecreasesMissileSuccess);
		Assert.False(apollo.AllowsSpyMissions);
		Assert.False(apollo.AllowsEnemyTerritoryHealing);
		Assert.False(apollo.GoodsMustBeInCityRadius);
		Assert.False(apollo.RequiresVictoriousArmy);
		Assert.False(apollo.RequiresEliteShip);

		// The mapping keeps that whole set: exactly one SaveBuilding flag.
		Assert.Equal(new[] { SaveBuilding.Flag.BuildSpaceshipParts }, ImportCiv3.LoadBuildingFlags(apollo));
	}

	[SkippableFact]
	public void ShippedConquestsBiq_LeavesTheTenPartsFlaglessAndQuantified() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), SkipReason);

		BiqData biq = BiqData.LoadFile(ConquestsBiqPath);

		// Ten part slots, each required once, and ten buildings carrying the
		// part indices 0..9. The per-civ cap reads both numbers.
		Assert.Equal(10, biq.Rule[0].NumberOfSpaceshipParts);
		Assert.Equal(Enumerable.Repeat(1, 10), biq.RuleSpaceship[0]);

		for (int part = 0; part < 10; ++part) {
			BLDG building = biq.Bldg.Single(b => b.SpaceshipPart == part);
			Assert.False(building.BuildSpaceshipParts);
			Assert.Empty(ImportCiv3.LoadBuildingFlags(building));
		}
	}
}
