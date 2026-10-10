using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// The BIQ-import and ruleset halves of the nuclear-launch data (26_nuclear_weapons.md
/// section 10): every value the launch reads must exist on BOTH paths. This is
/// the rule the project has been burned on - a launch reading NuclearWeapon,
/// the SDI flag or the meltdown flag off only one path silently reads a value
/// that does not exist in the other.
///
/// There are two population distinguished here on purpose: the ABILITY word
/// (record +0x88, ability id 16) that the launch gate reads, and the AI-strategy
/// word (record +0x8c). The shipped nuclear types carry both, and the tests
/// below pin that the gate is the ability, not the strategy.
/// </summary>
public class NuclearWeaponParityTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;

	public NuclearWeaponParityTest(SaveGameFixture fixture) {
		this.fixture = fixture;
	}

	private List<UnitPrototype> RuntimePrototypes() {
		return SaveGameFixture.HydrateSaveGame(fixture.saveGame).unitPrototypes;
	}

	// ---- The ruleset.json path ----

	[Fact]
	public void Ruleset_TacticalNukeCarriesTheNuclearWeaponAbilityAndStrategySeparately() {
		UnitPrototype tacticalNuke = RuntimePrototypes().Single(p => p.name == "Tactical Nuke");

		// The launch gate is the ability (isNuclearWeapon), and it is present.
		Assert.True(tacticalNuke.isNuclearWeapon);
		Assert.True(tacticalNuke.isTacticalMissile);
		Assert.False(tacticalNuke.isICBM);

		// The AI strategy is a separate field; the shipped type carries it too,
		// so the two coexist without being the same storage.
		Assert.True(tacticalNuke.HasAIStrategy(SaveUnitPrototype.AIStrategy.TacticalNuke));
	}

	[Fact]
	public void Ruleset_ICBMCarriesTheNuclearWeaponAbilityAndIsRangeExempt() {
		UnitPrototype icbm = RuntimePrototypes().Single(p => p.name == "ICBM");

		Assert.True(icbm.isNuclearWeapon);
		Assert.True(icbm.isICBM);
		Assert.False(icbm.isTacticalMissile);
		Assert.True(icbm.HasAIStrategy(SaveUnitPrototype.AIStrategy.ICBM));
		// The shipped ICBM carries BombardRange 0 and relies on the ability-20
		// exemption, not on a wide range.
		Assert.Equal(0, icbm.bombardRange);
	}

	[Fact]
	public void Ruleset_OnlyTheTwoNuclearTypesCarryTheLaunchGate() {
		List<UnitPrototype> protos = RuntimePrototypes();
		List<string> nuclear = protos.Where(p => p.isNuclearWeapon).Select(p => p.name).OrderBy(n => n).ToList();

		Assert.Equal(new[] { "ICBM", "Tactical Nuke" }, nuclear);
	}

	[Fact]
	public void Ruleset_TheMissileDefenceAndMeltdownFlagsExist() {
		C7GameData.GameData gd = SaveGameFixture.HydrateSaveGame(fixture.saveGame);

		// Strategic Missile Defense is the only carrier of the enemy
		// interception flag; Nuclear Plant is the only carrier of the meltdown
		// flag (26_nuclear_weapons.md section 6 and section 10).
		List<Building> withSdi = gd.Buildings.Where(b => b.decreasesMissileSuccess).ToList();
		Assert.Single(withSdi);
		Assert.Equal("Strategic Missile Defense", withSdi[0].name);

		List<Building> withMeltdown = gd.Buildings.Where(b => b.canMeltdown).ToList();
		Assert.Single(withMeltdown);
		Assert.Equal("Nuclear Plant", withMeltdown[0].name);
	}

	// ---- The BIQ import path (skipped without a Civ3 install) ----

	[SkippableFact]
	public void Biq_TheNuclearUnitTypesCarryTheAbilitiesAndTheStrategy() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);

		PRTO tactical = biq.Prto.First(p => p.Name == "Tactical Nuke");
		Assert.True(tactical.NuclearWeapon);       // ability 16
		Assert.True(tactical.TacticalMissile);      // ability 23
		Assert.True(tactical.AITacticalNuke);       // strategy, a different field

		PRTO icbm = biq.Prto.First(p => p.Name == "ICBM");
		Assert.True(icbm.NuclearWeapon);             // ability 16
		Assert.True(icbm.ICBM);                      // ability 20
		Assert.True(icbm.AIICBM);                    // strategy
	}

	[SkippableFact]
	public void Biq_NuclearUnitImportsTheAbilitiesNotTheStrategy() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);

		List<SaveUnitPrototype.Flag> tacticalFlags = ImportCiv3.GetNuclearWeaponFlags(biq.Prto.First(p => p.Name == "Tactical Nuke")).ToList();
		Assert.Contains(SaveUnitPrototype.Flag.NuclearWeapon, tacticalFlags);
		Assert.Contains(SaveUnitPrototype.Flag.TacticalMissile, tacticalFlags);
		Assert.DoesNotContain(SaveUnitPrototype.Flag.ICBM, tacticalFlags);

		List<SaveUnitPrototype.Flag> icbmFlags = ImportCiv3.GetNuclearWeaponFlags(biq.Prto.First(p => p.Name == "ICBM")).ToList();
		Assert.Contains(SaveUnitPrototype.Flag.NuclearWeapon, icbmFlags);
		Assert.Contains(SaveUnitPrototype.Flag.ICBM, icbmFlags);
		Assert.DoesNotContain(SaveUnitPrototype.Flag.TacticalMissile, icbmFlags);

		// A non-nuclear unit type carries none of them.
		List<SaveUnitPrototype.Flag> workerFlags = ImportCiv3.GetNuclearWeaponFlags(biq.Prto.First(p => p.Name == "Worker")).ToList();
		Assert.DoesNotContain(SaveUnitPrototype.Flag.NuclearWeapon, workerFlags);
	}

	[SkippableFact]
	public void Biq_TheDefenceAndMeltdownBuildingFlagsImport() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);

		BLDG sdi = biq.Bldg.First(b => b.Name == "Strategic Missile Defense");
		Assert.True(sdi.DecreasesMissileSuccess);
		Assert.Contains(SaveBuilding.Flag.DecreasesMissileSuccess, ImportCiv3.LoadBuildingFlags(sdi));

		BLDG plant = biq.Bldg.First(b => b.Name == "Nuclear Plant");
		Assert.True(plant.CanMeltdown);
		Assert.Contains(SaveBuilding.Flag.CanMeltdown, ImportCiv3.LoadBuildingFlags(plant));

		// These two flags are carried by exactly one building each in the
		// shipped rules (26_nuclear_weapons.md section 12.6).
		Assert.Single(biq.Bldg, b => b.DecreasesMissileSuccess);
		Assert.Single(biq.Bldg, b => b.CanMeltdown);
	}
}
