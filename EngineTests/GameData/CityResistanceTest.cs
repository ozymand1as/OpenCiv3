using System;
using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using Xunit;

namespace EngineTests.GameData;

/// Resistance and quelling for captured cities (spec 17 section 7): the
/// commerce exclusion, the capture-time initial roll and its culture-opinion
/// level boundary, the quelling pass's attempt budget, the continuation
/// roll's threshold, and the government-pair modifier. The level tables here
/// are deliberately tiny two-row tables so the level selection is observable;
/// the shipped CULT/GOVT/BIQ values are asserted separately in
/// ResistanceRulesDataTest.
public class CityResistanceTest {

	private static CultureLevel AweLevel() => new CultureLevel {
		name = "in awe of",
		chanceOfSuccessfulPropaganda = 30,
		cultureRatioPercentage = 300,
		initialResistanceChance = 40,
		continuedResistanceChance = 30,
	};

	private static CultureLevel DisdainfulLevel() => new CultureLevel {
		name = "disdainful of",
		chanceOfSuccessfulPropaganda = 3,
		cultureRatioPercentage = 33,
		initialResistanceChance = 90,
		continuedResistanceChance = 80,
	};

	/// Two rows, so a ratio at or above 300 picks the first and every lower
	/// ratio picks the second.
	private static void SetTwoLevelCulture(C7GameData.GameData gd) {
		gd.cultureLevels = new List<CultureLevel> { AweLevel(), DisdainfulLevel() };
	}

	/// A city owned by `defender` with `resistanceFrom` set, whose residents
	/// are flagged as resisting, plus `garrisonUnits` owning land combat
	/// units on the city tile. With `specialistResidents` the resisters are
	/// specialists, so a successful quell does not drag in the
	/// citizen-assignment machinery.
	private static City SetupResistanceCity(C7GameData.GameData gd, Player defender, Player resistanceFrom,
			int population, bool specialistResidents, int garrisonUnits = 1) {
		SetTwoLevelCulture(gd);
		Tile tile = gd.map.tileAt(4, 4);
		City city = CityCaptureTest.Game.AddCity(gd, defender, tile, population);
		city.resistanceFrom = resistanceFrom;

		foreach (CityResident resident in city.residents) {
			if (specialistResidents) {
				resident.citizenType = gd.citizenTypes.Find(c => !c.IsDefaultCitizen);
			}
			resident.isResisting = true;
		}

		for (int i = 0; i < garrisonUnits; ++i) {
			CityCaptureTest.Game.PlaceUnit(gd, defender, tile);
		}
		return city;
	}

	/// Builds a game whose culture pairs are pinned for the initial roll:
	/// the resisting city's owner holds `ownCulture` accumulated culture
	/// (a second city of theirs), the captured-from player holds 100, and
	/// the level table is the two-row table above. Returns the resisting
	/// city, with each resident working its own tile.
	private static City BuildInitialRollCity(int ownCulture) {
		C7GameData.GameData gd = CityCaptureTest.Game.NewGameData(out _, out Player defender);
		Player attacker = gd.players[0];
		SetTwoLevelCulture(gd);

		City city = CityCaptureTest.Game.AddCity(gd, defender, gd.map.tileAt(4, 4), population: 2);
		city.resistanceFrom = attacker;

		// The owner's accumulated culture lives in a second city of theirs.
		CityCaptureTest.Game.AddCity(gd, defender, gd.map.tileAt(0, 0), population: 1, cultureForOwner: ownCulture);
		// The captured-from player's culture.
		CityCaptureTest.Game.AddCity(gd, attacker, gd.map.tileAt(7, 7), population: 1, cultureForOwner: 100);

		TerrainType grass = CityCaptureTest.Game.Grass();
		int[] xs = { 3, 5 };
		for (int i = 0; i < city.residents.Count; i++) {
			Tile worked = new Tile(ID.None("worked-" + i)) {
				XCoordinate = xs[i],
				YCoordinate = 4,
				baseTerrainType = grass,
				overlayTerrainType = grass,
			};
			city.residents[i].tileWorked = worked;
			worked.personWorkingTile = city.residents[i];
		}
		return city;
	}

	[Fact]
	public void ResistersAreExcludedFromCommerce() {
		C7GameData.GameData gd = CityCaptureTest.Game.NewGameData(out _, out Player defender);
		Tile tile = gd.map.tileAt(4, 4);
		City city = CityCaptureTest.Game.AddCity(gd, defender, tile, population: 2);
		defender.scienceRate = 0;
		defender.luxuryRate = 0;
		defender.taxRate = 0;

		TerrainType grass = CityCaptureTest.Game.Grass();
		Tile workedTile = new Tile(ID.None("worked")) {
			XCoordinate = 3,
			YCoordinate = 4,
			baseTerrainType = grass,
			overlayTerrainType = grass,
		};
		CityResident worker = city.residents[0];
		worker.tileWorked = workedTile;
		workedTile.personWorkingTile = worker;
		CityResident scientist = city.residents[1];
		scientist.citizenType = gd.citizenTypes.Find(c => !c.IsDefaultCitizen);

		int centreCommerce = city.location.CommerceYield(city).yield;
		int workerCommerce = workedTile.CommerceYield(city).yield;

		// Baseline: the worker's tile commerce and the specialist's output
		// both count (the scientist type is Research 3, Luxuries 1).
		CommerceBreakdown baseline = city.CurrentCommerceYield();
		Assert.Equal(centreCommerce + workerCommerce + scientist.citizenType.Taxes, baseline.taxes);
		Assert.Equal(scientist.citizenType.Research, baseline.beakers);
		Assert.Equal(scientist.citizenType.Luxuries, baseline.happiness);

		// With the resistance flag set (spec 17 section 7: only citizens
		// whose flag is 0 are counted), the worked tile's commerce and the
		// specialist output both drop out; the city centre's own commerce
		// remains.
		worker.isResisting = true;
		scientist.isResisting = true;
		CommerceBreakdown resisters = city.CurrentCommerceYield();
		Assert.Equal(centreCommerce, resisters.taxes);
		Assert.Equal(0, resisters.beakers);
		Assert.Equal(0, resisters.happiness);
	}

	[Fact]
	public void InitialResistance_UsesTheCultureOpinionLevel_AtTheBoundary() {
		// Ratio 299 falls below the 300 threshold and selects the low row
		// (initial chance 90); ratio 300 reaches the top row (chance 40).
		// 100 x own / other with other = 100, so own culture is the ratio.
		City justBelow = BuildInitialRollCity(ownCulture: 299);
		C7GameData.GameData.rng = new FixedRandom(89);
		justBelow.StartResistance(EngineStorage.gameData);
		Assert.Equal(2, justBelow.residents.Count(r => r.isResisting));
		// Resisting citizens stop working their tile (spec 15 section 4.7).
		Assert.All(justBelow.residents, r => Assert.Equal(Tile.NONE, r.tileWorked));

		City atThreshold = BuildInitialRollCity(ownCulture: 300);
		C7GameData.GameData.rng = new FixedRandom(39);
		atThreshold.StartResistance(EngineStorage.gameData);
		Assert.Equal(2, atThreshold.residents.Count(r => r.isResisting));

		City rolledOut = BuildInitialRollCity(ownCulture: 300);
		C7GameData.GameData.rng = new FixedRandom(40);
		rolledOut.StartResistance(EngineStorage.gameData);
		Assert.Equal(0, rolledOut.residents.Count(r => r.isResisting));
		Assert.All(rolledOut.residents, r => Assert.NotEqual(Tile.NONE, r.tileWorked));
	}

	[Fact]
	public void InitialResistance_RollOf90StopsAtTheNinetyPercentChance() {
		// Same boundary approached from the roll side: at ratio 299 the
		// chance is 90, so a roll of 89 resists and a roll of 90 does not.
		City resists = BuildInitialRollCity(ownCulture: 299);
		C7GameData.GameData.rng = new FixedRandom(90);
		resists.StartResistance(EngineStorage.gameData);
		Assert.Equal(0, resists.residents.Count(r => r.isResisting));
	}

	[Fact]
	public void QuellingPass_MakesExactlyUnitsTimesMilitaryLawAttempts() {
		C7GameData.GameData gd = CityCaptureTest.Game.NewGameData(out _, out Player defender);
		Player attacker = gd.players[0];
		// Two ground combat units; a worker is added below and must not
		// count, so the budget is 2 x MilitaryLaw(1) = 2 attempts
		// (spec 15 section 4.7).
		City city = SetupResistanceCity(gd, defender, attacker, population: 3, specialistResidents: false, garrisonUnits: 2);
		Tile tile = city.location;
		CityCaptureTest.Game.PlaceUnit(gd, defender, tile, attack: 0);
		Assert.Equal(3, tile.unitsOnTile.Count);
		Assert.Equal(2, city.QuellingAttempts(gd));

		// Two scripted rolls: both resisting citizens are asked exactly once
		// and continue (roll 0 is below the threshold), so neither roll is
		// missing nor left over.
		C7GameData.GameData.rng = new SequenceRandom(0, 0);
		int quelled = city.QuellResistance(gd);
		Assert.Equal(0, quelled);
		Assert.All(city.residents, r => Assert.True(r.isResisting));

		// One scripted roll fewer than the budget: the second attempt runs
		// off the end of the sequence, proving the pass really makes
		// units x MilitaryLaw attempts.
		C7GameData.GameData.rng = new SequenceRandom(0);
		Assert.Throws<InvalidOperationException>(() => city.QuellResistance(gd));
	}

	[Fact]
	public void ContinuationRoll_StopsExactlyAtTheThreshold() {
		// The ratio falls below every threshold (neither civ has culture),
		// which selects the disdainful row: ContinuedResistanceChance 80
		// with no government modifier, so 79 continues and 80 stops.
		C7GameData.GameData gd = CityCaptureTest.Game.NewGameData(out _, out Player defender);
		Player attacker = gd.players[0];
		City city = SetupResistanceCity(gd, defender, attacker, population: 1, specialistResidents: true);

		C7GameData.GameData.rng = new SequenceRandom(79);
		Assert.Equal(0, city.QuellResistance(gd));
		Assert.True(city.residents[0].isResisting);

		C7GameData.GameData.rng = new SequenceRandom(80);
		Assert.Equal(1, city.QuellResistance(gd));
		Assert.False(city.residents[0].isResisting);
	}

	[Fact]
	public void GovernmentPairModifier_ShiftsTheContinuationThreshold() {
		C7GameData.GameData gd = CityCaptureTest.Game.NewGameData(out _, out Player defender);
		Player attacker = gd.players[0];

		// The shipped Democracy row against Communism carries +5, so the
		// continuation threshold rises from 80 to 85.
		Government democracy = new Government() {
			name = "Democracy",
			resistanceModifiers = new List<int> { 0, 5 },
		};
		Government communism = new Government() {
			name = "Communism",
			resistanceModifiers = new List<int> { -5, 0 },
		};
		gd.governments = new List<Government> { democracy, communism };
		defender.government = democracy;
		attacker.government = communism;

		City city = SetupResistanceCity(gd, defender, attacker, population: 1, specialistResidents: true);

		C7GameData.GameData.rng = new SequenceRandom(84);
		Assert.Equal(0, city.QuellResistance(gd));
		Assert.True(city.residents[0].isResisting);

		C7GameData.GameData.rng = new SequenceRandom(85);
		Assert.Equal(1, city.QuellResistance(gd));
		Assert.False(city.residents[0].isResisting);
	}
}
