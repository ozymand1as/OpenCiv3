using C7Engine;
using C7GameData;
using Xunit;

namespace EngineTests.GameData;

/// The downstream consumers of the city-loss path: Points by Conquest
/// (spec 25 section 4) and the two endings the original decides where a city
/// is lost rather than in the victory check (spec 25 section 4 and row D12) -
/// instant elimination against CityEliminationCount, and regicide / mass
/// regicide against the loser's remaining king units.
public class CityLossEndingsTest {

	private static MapUnit PlaceKing(C7GameData.GameData gd, Player owner, Tile tile) {
		MapUnit king = CityCaptureTest.Game.PlaceUnit(gd, owner, tile);
		king.unitType.aiStrategies.Add(C7GameData.Save.SaveUnitPrototype.AIStrategy.King);
		king.name = "King";
		return king;
	}

	// ------------------------------------------------------------- conquest VP

	[Fact]
	public void PointsByConquest_AccumulateAcrossCaptures() {
		C7GameData.GameData gd = CityCaptureTest.Game.NewGameData(out Player attacker, out Player defender);
		gd.victoryConditions = new VictoryConditions {
			VictoryLocations = true,
			CityConquestPopulation = 100,
		};

		// The defender keeps a third city throughout, so losing the first
		// two is a capture and not the ordinary last-city destruction.
		City first = CityCaptureTest.Game.AddCity(gd, defender, gd.map.tileAt(4, 4), population: 5);
		City second = CityCaptureTest.Game.AddCity(gd, defender, gd.map.tileAt(0, 0), population: 2);
		CityCaptureTest.Game.AddCity(gd, defender, gd.map.tileAt(7, 7), population: 1);

		CityCaptureTest.Game.PlaceUnit(gd, attacker, first.location).OnEnterTile(first.location);
		Assert.Equal(500, attacker.victoryPoints);
		Assert.False(defender.defeated);

		CityCaptureTest.Game.PlaceUnit(gd, attacker, second.location).OnEnterTile(second.location);
		Assert.Equal(700, attacker.victoryPoints);
	}

	[Fact]
	public void PointsByConquest_AwardsNothingWhenTheVictoryPointGroupIsOff() {
		C7GameData.GameData gd = CityCaptureTest.Game.NewGameData(out Player attacker, out Player defender);
		gd.victoryConditions = new VictoryConditions {
			VictoryLocations = false,
			CityConquestPopulation = 100,
		};

		City city = CityCaptureTest.Game.AddCity(gd, defender, gd.map.tileAt(4, 4), population: 5);
		CityCaptureTest.Game.PlaceUnit(gd, attacker, city.location).OnEnterTile(city.location);

		Assert.Equal(attacker, city.owner);
		Assert.Equal(0, attacker.victoryPoints);
	}

	[Fact]
	public void PointsByConquest_ZeroCostAwardsNothing() {
		C7GameData.GameData gd = CityCaptureTest.Game.NewGameData(out Player attacker, out Player defender);
		gd.victoryConditions = new VictoryConditions {
			VictoryLocations = true,
			CityConquestPopulation = 0,
		};

		City city = CityCaptureTest.Game.AddCity(gd, defender, gd.map.tileAt(4, 4), population: 5);
		CityCaptureTest.Game.PlaceUnit(gd, attacker, city.location).OnEnterTile(city.location);

		Assert.Equal(attacker, city.owner);
		Assert.Equal(0, attacker.victoryPoints);
	}

	// ------------------------------------------------------------ city elimination

	[Fact]
	public void CityElimination_FiresExactlyAtTheLostCityCount() {
		C7GameData.GameData gd = CityCaptureTest.Game.NewGameData(out Player attacker, out Player defender);
		gd.victoryConditions = new VictoryConditions {
			CityElimination = true,
			CityEliminationCount = 2,
		};

		City first = CityCaptureTest.Game.AddCity(gd, defender, gd.map.tileAt(4, 4), population: 2);
		City second = CityCaptureTest.Game.AddCity(gd, defender, gd.map.tileAt(2, 2), population: 2);
		City third = CityCaptureTest.Game.AddCity(gd, defender, gd.map.tileAt(6, 6), population: 2);
		MapUnit defenderUnit = CityCaptureTest.Game.PlaceUnit(gd, defender, third.location);

		// One city lost: below the count, the player plays on.
		CityCaptureTest.Game.PlaceUnit(gd, attacker, first.location).OnEnterTile(first.location);
		Assert.Equal(attacker, first.owner);
		Assert.False(defender.defeated);
		Assert.Equal(1, defender.citiesLost);
		Assert.Equal(2, defender.cities.Count);

		// The second loss reaches the count: the player is eliminated, and
		// its remaining city and units go with it.
		CityCaptureTest.Game.PlaceUnit(gd, attacker, second.location).OnEnterTile(second.location);
		Assert.True(defender.defeated);
		Assert.Equal(2, defender.citiesLost);
		Assert.Empty(defender.cities);
		Assert.Empty(defender.units);
		Assert.Null(third.location.cityAtTile);
		// The loser's cities are gone from the game; the captured ones stay.
		Assert.DoesNotContain(third, gd.cities);
		Assert.Contains(first, gd.cities);
		Assert.Contains(second, gd.cities);
		_ = defenderUnit;
	}

	[Fact]
	public void CityElimination_RuleOffNeverEliminates() {
		C7GameData.GameData gd = CityCaptureTest.Game.NewGameData(out Player attacker, out Player defender);
		gd.victoryConditions = new VictoryConditions {
			CityElimination = false,
			CityEliminationCount = 1,
		};

		City first = CityCaptureTest.Game.AddCity(gd, defender, gd.map.tileAt(4, 4), population: 2);
		CityCaptureTest.Game.AddCity(gd, defender, gd.map.tileAt(2, 2), population: 2);

		CityCaptureTest.Game.PlaceUnit(gd, attacker, first.location).OnEnterTile(first.location);

		Assert.Equal(attacker, first.owner);
		Assert.Equal(1, defender.citiesLost);
		Assert.False(defender.defeated);
	}

	// ----------------------------------------------------------------- regicide

	[Fact]
	public void MassRegicide_EliminatesTheKinglessPlayerWhenACityIsLost() {
		C7GameData.GameData gd = CityCaptureTest.Game.NewGameData(out Player attacker, out Player defender);
		gd.victoryConditions = new VictoryConditions { MassRegicide = true };

		// Kings exist somewhere in the game (the rival's), but the loser has
		// none, so losing a city ends it - even with a city still standing.
		PlaceKing(gd, attacker, gd.map.tileAt(7, 0));
		City first = CityCaptureTest.Game.AddCity(gd, defender, gd.map.tileAt(4, 4), population: 2);
		CityCaptureTest.Game.AddCity(gd, defender, gd.map.tileAt(2, 2), population: 2);

		CityCaptureTest.Game.PlaceUnit(gd, attacker, first.location).OnEnterTile(first.location);

		Assert.Equal(attacker, first.owner);
		Assert.True(defender.defeated);
		Assert.Empty(defender.cities);
	}

	[Fact]
	public void Regicide_PlayerWithAKingSurvivesTheCityLoss() {
		C7GameData.GameData gd = CityCaptureTest.Game.NewGameData(out Player attacker, out Player defender);
		gd.victoryConditions = new VictoryConditions { Regicide = true };

		PlaceKing(gd, defender, gd.map.tileAt(0, 7));
		City first = CityCaptureTest.Game.AddCity(gd, defender, gd.map.tileAt(4, 4), population: 2);
		CityCaptureTest.Game.AddCity(gd, defender, gd.map.tileAt(2, 2), population: 2);

		CityCaptureTest.Game.PlaceUnit(gd, attacker, first.location).OnEnterTile(first.location);

		Assert.Equal(attacker, first.owner);
		Assert.Equal(1, defender.citiesLost);
		Assert.False(defender.defeated);
		Assert.Single(defender.units);
	}

	[Fact]
	public void RegicideRulesOff_KinglessPlayerSurvivesTheCityLoss() {
		C7GameData.GameData gd = CityCaptureTest.Game.NewGameData(out Player attacker, out Player defender);
		gd.victoryConditions = new VictoryConditions();

		PlaceKing(gd, attacker, gd.map.tileAt(7, 0));
		City first = CityCaptureTest.Game.AddCity(gd, defender, gd.map.tileAt(4, 4), population: 2);
		CityCaptureTest.Game.AddCity(gd, defender, gd.map.tileAt(2, 2), population: 2);

		CityCaptureTest.Game.PlaceUnit(gd, attacker, first.location).OnEnterTile(first.location);

		Assert.Equal(attacker, first.owner);
		Assert.False(defender.defeated);
		Assert.Equal(1, defender.cities.Count);
	}
}
