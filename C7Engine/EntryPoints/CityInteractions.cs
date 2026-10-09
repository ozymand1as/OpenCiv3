using System.Collections.Generic;
using System.Linq;
using C7Engine.AI;

namespace C7Engine {
	using System;
	using C7GameData;

	public class CityInteractions {
		public static City BuildCity(Tile tileWithNewCity, Player owner, string name) {
			GameData gameData = EngineStorage.gameData;
			City newCity = new City(tileWithNewCity, owner, name, gameData.ids.CreateID("city"));
			if (owner.cities.Count == 0) {
				newCity.capital = true;
				newCity.AddBuilding(gameData.Buildings.Find(x => x.isCenterOfEmpire));
			}
			gameData.cities.Add(newCity);
			owner.cities.Add(newCity);
			tileWithNewCity.cityAtTile = newCity;

			CityResident firstResident = new CityResident();
			firstResident.city = newCity;
			firstResident.citizenType = gameData.citizenTypes.Find(x => x.IsDefaultCitizen);
			newCity.AddCitizen(firstResident);

			// Update owners before we assign the citizen so the tile owners are
			// accurate. We do this after adding the resident though, because
			// cities with zero residents are considered destroyed.
			gameData.UpdateTileOwners();

			// Now that the city exists and its borders have been established,
			// invalidate the trade network so it can be recomputed with this
			// new information.
			gameData.InvalidateCachedTradeNetwork();

			// Assigning citizens to tiles requires knowing luxuries, so this
			// has to happen after invalidating the trade network.
			CityTileAssignmentAI.AssignNewCitizenToTile(gameData, firstResident);

			newCity.SetItemBeingProduced(ChooseProducible.Choose(newCity, owner));

			// Redo corruption calculations after a city is created, since it
			// may change rank corruption values.
			owner.DoCorruptionCalculations(gameData);

			return newCity;
		}

		public static void DestroyCity(City city) {
			DestroyCity(city.location);
		}
		public static void DestroyCity(Tile tile) {
			DestroyCity(tile.XCoordinate, tile.YCoordinate);
		}

		public static void DestroyCity(int X, int Y) {
			GameData gameData = EngineStorage.gameData;
			Tile tile = gameData.map.tileAt(X, Y);
			City city = tile.cityAtTile;
			Player owner = city.owner;

			// TODO: this will get removed eventually, since we will be capturing non-combat units,
			// plus, it doesn't what it says, if the city is abandoned for example, ALL units are removed.
			// I am leaving it as it is for the moment.
			tile.DisbandNonDefendingUnits(owner);

			city.RemoveAllCitizens();
			owner.cities.Remove(city);

			gameData.cities.Remove(city);
			gameData.UpdateTileOwnersOnCityDestruction(city);

			new MsgCityDestroyed(city).send();

			gameData.CheckForCivDestructionAndNotifyUi(owner);

			tile.cityAtTile = null;

			// Now that the city has been destroyed and tile owners updated,
			// invalidate the trade network in case removing this city cut off
			// resource access.
			gameData.InvalidateCachedTradeNetwork();

			// Redo corruption calculations after a city is destroyed, since it
			// may change rank corruption values.
			owner.DoCorruptionCalculations(gameData);

			// The city-loss decisions (instant elimination, regicide) run after
			// the city is fully detached, so the elimination cascade never sees
			// this city again.
			HandleCityLost(city, owner);
		}

		/// <summary>
		/// Transfers a live city to another player: the capture act itself
		/// (spec 17 section 7). The city keeps its population, its buildings
		/// and every per-player culture entry - only the owner changes, the
		/// new owner gains an (initially zero) culture slot of its own, and
		/// the citizens roll for initial resistance against the player the
		/// city was taken from.
		///
		/// The capture-time raze decision is deliberately absent: the spec
		/// describes the AI's raze test only as a culture-total comparison
		/// plus a population roll (spec 17 section 8) without fixing which
		/// outcome razes, and the human's raze dialog is not covered by the
		/// read sections, so a captured city is always kept here.
		/// </summary>
		public static void CaptureCity(City city, Player newOwner) {
			if (city == null || newOwner == null || city.owner == newOwner) {
				return;
			}
			GameData gameData = EngineStorage.gameData;
			Player previousOwner = city.owner;

			previousOwner.cities.Remove(city);
			city.owner = newOwner;
			newOwner.cities.Add(city);

			// The per-player culture array is keyed per player: add the new
			// owner's slot (which starts at zero) without touching any other
			// player's entry. Nothing here clears culture, because a captured
			// city keeps its culture (spec 17 section 7).
			city.perPlayerCulture.TryAdd(newOwner, 0);

			// The city was the previous owner's capital, not the new owner's
			// (the new owner keeps whatever capital it already had). What the
			// original does about re-designating the loser's capital is not
			// specified in the read sections.
			city.capital = false;

			city.resistanceFrom = previousOwner;
			city.StartResistance(gameData);

			// Borders and the trade network depend on who owns the city, and
			// the rank corruption of both players changes with the swap. The
			// sweep is told the owner the city's tiles held until now: it
			// compares a tile's stored owner against the owner it is about to
			// write (spec 17 section 5.1), and a capture rewrites the owner of
			// every tile the city already holds.
			gameData.UpdateTileOwners(new Dictionary<City, Player> { { city, previousOwner } });
			gameData.InvalidateCachedTradeNetwork();
			previousOwner.DoCorruptionCalculations(gameData);
			newOwner.DoCorruptionCalculations(gameData);

			// Points by Conquest (spec 25 section 4), at the city's population
			// when it changed hands.
			newOwner.AwardVictoryPointsForCityCapture(gameData, city);

			// The city-loss path decides instant elimination and regicide for
			// the player that lost the city (spec 25 section 4 and D12).
			HandleCityLost(city, previousOwner);

			// And the ordinary destruction rules still apply when the loser
			// has no cities and no settlers left.
			gameData.CheckForCivDestructionAndNotifyUi(previousOwner);
		}

		/// <summary>
		/// The decisions that fire when a player loses a city (spec 25
		/// section 4: both endings are decided in the city-loss path, not in
		/// the victory check). With the CityElimination rule on, the loser's
		/// lost-city count is compared against CityEliminationCount; with
		/// Regicide or Mass Regicide on, a loser left without any king unit
		/// is eliminated.
		/// </summary>
		public static void HandleCityLost(City city, Player loser) {
			if (loser == null || loser.defeated || loser.isBarbarians) {
				return;
			}
			GameData gameData = EngineStorage.gameData;
			loser.citiesLost++;

			VictoryConditions rules = gameData.victoryConditions;
			if (rules == null) {
				return;
			}

			if (rules.CityElimination && loser.citiesLost >= Math.Max(1, rules.CityEliminationCount)) {
				gameData.EliminatePlayer(loser);
				return;
			}

			if (rules.Regicide || rules.MassRegicide) {
				if (!loser.units.Any(u => u.unitType.HasAIStrategy(C7GameData.Save.SaveUnitPrototype.AIStrategy.King))) {
					gameData.EliminatePlayer(loser);
				}
			}
		}
	}
}
