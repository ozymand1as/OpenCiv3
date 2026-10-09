using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using C7Engine;
using MoonSharp.Interpreter;

namespace C7GameData {
	public class CityBuilding {
		public Building building;
		public Player builtByPlayer;
		public int year;
		public int totalCulture; // This represents the total culture produced by the building.
								 // In Civ3, this value is displayed in the cultural advisor tab
	}

	public struct CommerceBreakdown {
		public int corrupted;
		public int taxes;
		public int beakers;
		public int happiness;
		public int wealth;
	}

	public struct CorruptableValue {
		public CorruptableValue(int value, float corruption) {
			// Apply the corruption amount, ensuring that there is always at
			// least one shield/commerce if we started with at least one.
			useful = (int)Math.Round(value * (1 - corruption));
			if (value > 0) {
				useful = Math.Max(1, useful);
			}

			// Whatever is left over is corrupt.
			corrupt = value - useful;
		}

		public int useful;
		public int corrupt;
	}

	public partial class City {
		private static ILogger log = Log.ForContext<City>();

		public ID id { get; set; }
		public Tile location { get; internal set; }
		public string name;
		public Dictionary<Player, int> perPlayerCulture = new();

		//Temporary production code because production is fun.
		public IProducible itemBeingProduced;
		public int shieldsStored { get; private set; } = 0;

		public int foodStored = 0;

		public bool capital = false;
		public Player owner { get; set; }
		public List<CityResident> residents = new List<CityResident>();

		// The list of buildings built in this city. You probably want to use
		// GetBuildings, which can also include buildings granted by wonders.
		public List<CityBuilding> constructed_buildings;

		// The order of this city within all the cities of a player for the
		// purposes of rank corruption calculations.
		//
		// This is updated each turn to avoid each city needing to do an O(n)
		// scan of the list, which would cause an O(n^2) overall calcuation.
		public int rankIndex = -1;

		// The amount of corruption, between 0 and 1.
		public float corruption = 0;

		// The number of turns of unhappiness this city will experience due to
		// pop rushing. Larger values result in larger numbers of citizens being
		// unhappy as well, in addition to the time penalty.
		public int turnsOfUnhappinessDueToPopRushing = 0;

		public bool isInCivilDisorder = false;

		// The number of turns of unhappiness this city will experience due to
		// drafting. Larger values result in larger numbers of citizens being
		// unhappy as well, in addition to the time penalty (spec 15 §4.2).
		public int turnsOfUnhappinessDueToDrafting = 0;

		// The unhappy faces this city carries because of enemy propaganda: a
		// campaign that boils or flips the city raises it to max(old,
		// subverted citizens) (spec 24 §6.6 steps 3-4), and
		// RecalculateCitizenMoods subtracts it from the happy-face total (spec
		// 15 §4.5). The shipped engine also decays it by one per turn; that
		// decay is not implemented yet.
		public int unhappyFacesDueToPropaganda = 0;

		// Whether this city is currently celebrating We Love The King Day
		// (spec 15 §5).
		public bool isWeLoveTheKingDay = false;

		public static City NONE = new City(Tile.NONE, null, "Dummy City", ID.None("city"));

		public static bool IsValidCity(City city) {
			return city != null && city != City.NONE;
		}

		public City(Tile location, Player owner, string name, ID id) {
			this.id = id;
			this.location = location;
			this.owner = owner;
			this.name = name;
			if (owner != null) {
				this.perPlayerCulture.Add(owner, 0);
			}
			constructed_buildings = new();
		}

		internal City() {
			constructed_buildings = new();
		}

		public void SetItemBeingProduced(IProducible producible) {
			this.itemBeingProduced = producible;
		}

		public bool IsCapital() {
			return capital;
		}

		/// <summary>
		/// Sets the current shield amount in the production box. If the add parameter is true, the shields get appended.<br/>
		/// </summary>
		/// <param name="shields">The number of shields to be added</param>
		/// <param name="add">If true, the shields get appended, otherwise they overwrite the current value</param>
		[LuaMethod]
		public void SetStoredShields(int shields, bool add = false) {
			// TODO: account for overflow if needed
			if (add)
				this.shieldsStored += shields;
			else
				this.shieldsStored = shields;
		}

		public List<CityBuilding> GetBuildings() {
			HashSet<Building> buildingsSeen = new();
			List<CityBuilding> result = new();
			foreach (CityBuilding cb in constructed_buildings) {
				result.Add(cb);
				buildingsSeen.Add(cb.building);
			}

			// Loop through all the wonders we control that aren't obsolete.
			foreach ((City c, CityBuilding cb) in owner.GetActiveWonders()) {
				Building b = cb.building;

				if (b.greatWonderProperties.buildingGainedInEveryCity != null
					&& !buildingsSeen.Contains(b.greatWonderProperties.buildingGainedInEveryCity)) {
					result.Add(new CityBuilding() {
						building = b.greatWonderProperties.buildingGainedInEveryCity,
						builtByPlayer = cb.builtByPlayer,
						year = cb.year,
						totalCulture = 0, // TODO: calculate this
					});
				}
				if (b.greatWonderProperties.buildingGainedInEveryCityOnContinent != null
					&& c.location.continent == location.continent
					&& !buildingsSeen.Contains(b.greatWonderProperties.buildingGainedInEveryCityOnContinent)) {
					result.Add(new CityBuilding() {
						building = b.greatWonderProperties.buildingGainedInEveryCityOnContinent,
						builtByPlayer = cb.builtByPlayer,
						year = cb.year,
						totalCulture = 0, // TODO: calculate this
					});
				}
			}

			return result;
		}

		public IEnumerable<IProducible> ListProductionOptions(GameData gameData) {
			HashSet<Resource> accessibleResources = GetAccessibleResources(gameData);

			IEnumerable<IProducible> producibles = gameData.unitPrototypes.Cast<IProducible>()
													.Concat(gameData.Buildings.Cast<IProducible>()
														.Concat(gameData.Inflows.Cast<IProducible>()));

			return producibles.Where(p => p.CanProduce(this, accessibleResources));
		}

		private HashSet<Resource> GetAccessibleResources(GameData gameData) {
			return gameData.GetTradeNetwork().GetResourcesAvailableToCity(owner, this).Keys.ToHashSet();
		}

		public int FoodNeededToGrow() {
			Rules rules = owner.rules;
			if (residents.Count <= rules.MaximumLevel1CitySize) {
				return rules.FoodNeededToGrowForLevel1Cities;
			} else if (residents.Count <= rules.MaximumLevel2CitySize) {
				return rules.FoodNeededToGrowForLevel2Cities;
			} else {
				return rules.FoodNeededToGrowForLevel3Cities;
			}
		}

		public int TurnsUntilGrowth() {
			int foodGrowthPerTurn = FoodGrowthPerTurn();
			int foodNeededToGrow = FoodNeededToGrow();
			if (foodGrowthPerTurn == 0 || foodStored > foodNeededToGrow) {
				return int.MaxValue;
			} else if (foodGrowthPerTurn < 0) {
				return int.MinValue;
			}

			int additionalFoodNeeded = foodNeededToGrow - foodStored;
			int turnsRoundedDown = additionalFoodNeeded / foodGrowthPerTurn;
			if (additionalFoodNeeded % foodGrowthPerTurn != 0) {
				return turnsRoundedDown + 1;
			}
			return turnsRoundedDown;
		}

		public int TurnsToProduce(IProducible item) {
			int additionalProductionNeeded = owner.ShieldCost(item) - shieldsStored;
			int usefulShields = CurrentProductionYield().useful;
			if (usefulShields == 0) {
				return int.MaxValue;
			}

			int turnsRoundedDown = additionalProductionNeeded / usefulShields;
			if (additionalProductionNeeded % usefulShields != 0) {
				return Math.Max(turnsRoundedDown + 1, 1);
			}
			return Math.Max(turnsRoundedDown, 1);
		}

		public int TurnsUntilProductionFinished() {
			return TurnsToProduce(itemBeingProduced);
		}

		private int ShieldCostForHurrying() {
			// If there are no shields in the box, hurrying costs double.
			if (shieldsStored == 0) {
				return owner.ShieldCost(itemBeingProduced) * 2;
			}
			return owner.ShieldCost(itemBeingProduced) - shieldsStored;
		}

		// Returns the feasibility of hurrying production
		public class HurryProductionDetails {
			public string? errorMessage;
			public string? costMessage;

			public int popCost = -1;
			public int goldCost = -1;
		}
		public HurryProductionDetails GetHurryProductionDetails() {
			Rules rules = EngineStorage.gameData.rules;
			int shieldCost = ShieldCostForHurrying();

			if (isInCivilDisorder) {
				return new HurryProductionDetails() { errorMessage = "The city is in disorder and cannot hurry production." };
			}

			switch (owner.government.hurryingType) {
				case Government.HurryProductionType.CannotHurry:
					return new HurryProductionDetails() { errorMessage = "We cannot hurry production with this government." };

				case Government.HurryProductionType.ForcedLabor:
					int popCost = (int)Math.Ceiling((float)shieldCost / rules.CitizenValueInShields);
					if (popCost > residents.Count / 2f) {
						return new HurryProductionDetails() { errorMessage = $"Hurrying production would take the lives of too many citizens ({popCost})." };
					}
					return new HurryProductionDetails() {
						costMessage = $"Hurrying production will take the lives of {popCost} citizen(s), are you sure?",
						popCost = popCost,
					};

				case Government.HurryProductionType.PaidLabor:
					int goldCost = shieldCost * rules.ShieldValueInGold;
					if (goldCost > owner.gold) {
						return new HurryProductionDetails() { errorMessage = $"Hurrying production would cost too much gold! ({goldCost})." };
					}
					return new HurryProductionDetails() {
						costMessage = $"Hurrying production will cost {goldCost} gold, are you sure?",
						goldCost = goldCost,
					};
			}
			throw new Exception($"Unknown hurrying type: {owner.government.hurryingType}");
		}

		public void HurryProduction() {
			Rules rules = EngineStorage.gameData.rules;
			HurryProductionDetails details = GetHurryProductionDetails();

			switch (owner.government.hurryingType) {
				case Government.HurryProductionType.CannotHurry:
					throw new Exception("Unexpectedly trying to hurry production with a government that doesn't support it.");

				case Government.HurryProductionType.ForcedLabor:
					if (details.popCost <= 0) {
						throw new Exception(details.errorMessage);
					}
					RemoveCitizens(details.popCost);
					turnsOfUnhappinessDueToPopRushing += rules.TurnPenaltyForEachHurrySacrifice * details.popCost;
					shieldsStored = owner.ShieldCost(itemBeingProduced);
					break;

				case Government.HurryProductionType.PaidLabor:
					if (details.goldCost <= 0) {
						throw new Exception(details.errorMessage);
					}
					owner.gold -= details.goldCost;
					shieldsStored = owner.ShieldCost(itemBeingProduced);
					break;
			}
		}

		public void HandleCityProduction(GameData gameData) {
			IProducible producedItem = ComputeTurnProduction();
			if (producedItem == null) {
				return;
			}

			log.Debug($"Produced {producedItem} in {this}");
			if (producedItem is UnitPrototype prototype) {
				AddUnit(prototype, gameData);
			} else if (producedItem is Building building) {
				AddBuilding(building);

				// A new harbour or airport can open trade routes, so the cached
				// network is no longer valid.
				gameData.InvalidateCachedTradeNetwork();

				// If we completed a great wonder, mark it as completed so no
				// other civ can build it. If any other cities are building the
				// wonder then change production to the most expensive option,
				// to avoid wasting the shields.
				//
				// TODO: This should interrupt the player's turn to let them
				// make the choice. And does the game allow wonder shuffling in
				// this situation?
				if (building.greatWonderProperties != null) {
					gameData.GreatWondersBuilt.Add(building.name);

					foreach (Player p in gameData.players) {
						if (p == this.owner) {
							continue;
						}

						foreach (City c in p.cities) {
							if (c.itemBeingProduced.name == building.name) {
								c.SetItemBeingProduced(c.GetMostExpensiveItemToProduce());
							}
						}
					}
				}
			} else if (producedItem is Inflow inflow) {
				// we don't want to "complete" the inflow production
				// unless the player has a reason to change it, this should be ongoing
				return;
			}

			SetItemBeingProduced(ChooseProducible.Choose(this, owner));
		}

		private IProducible GetMostExpensiveItemToProduce() {
			IProducible best = null;
			foreach (IProducible ip in ListProductionOptions(EngineStorage.gameData)) {
				if (best == null || owner.ShieldCost(ip) > owner.ShieldCost(best)) {
					best = ip;
				}
			}
			return best;
		}

		public void HandleCityGrowth(GameData gameData) {
			int foodNeededToGrow = FoodNeededToGrow();
			int foodGrowth = FoodGrowthPerTurn();
			bool hasGranary = HasGranary();

			foodStored += foodGrowth;
			foodStored = Math.Min(foodStored, foodNeededToGrow);

			// Handle the city starving.
			if (foodStored < 0) {
				RemoveLastCitizen();
				foodStored = 0;
				// A city whose size reaches 0 is razed (spec 13 section 5).
				if (residents.Count == 0) {
					RazeIfEmpty();
				}
				return;
			}

			// No growth necessary.
			if (foodStored < foodNeededToGrow) {
				return;
			}

			if (CanGrowPopulationByOne(gameData)) {
				CityResident newResident = new CityResident();
				newResident.nationality = owner.civilization;
				newResident.city = this;
				newResident.citizenType = gameData.citizenTypes.Find(x => x.IsDefaultCitizen);
				AddCitizen(newResident);
				C7Engine.AI.CityTileAssignmentAI.AssignNewCitizenToTile(gameData, newResident);

				if (hasGranary) {
					foodStored /= 2;
				} else {
					foodStored = 0;
				}
			}
		}

		private bool CanGrowPopulationByOne(GameData gD) {
			// We can always grow up to size 6.
			if (residents.Count + 1 <= gD.rules.MaximumLevel1CitySize) {
				return true;
			}

			// If the city doesn't have fresh water and doesn't have an aqueduct
			// then it can't grow into a city.
			//
			// TODO: lakes are bodies of water under 20 tiles (https://civilization.fandom.com/wiki/Fresh_Water_Lake_(Civ3))
			bool hasFreshwaterAccess = location.BordersRiver();
			foreach (Tile t in location.neighbors.Values) {
				if (!t.IsLand() && t.isFreshWater) {
					hasFreshwaterAccess = true;
					break;
				}
			}

			bool canGrowIntoCity = hasFreshwaterAccess;
			if (!hasFreshwaterAccess) {
				foreach (CityBuilding cb in GetBuildings()) {
					if (cb.building.allowsCitySize2 || cb.building.allowsCitySize3) {
						canGrowIntoCity = true;
						break;
					}
				}
			}

			if (!canGrowIntoCity) {
				return false;
			}

			// With fresh water or an aqueduct we can grow to size 2.
			if (residents.Count + 1 <= gD.rules.MaximumLevel2CitySize) {
				return true;
			}

			// If we're a city trying to grow into a metropolis, we need a
			// hospital.
			foreach (CityBuilding cb in GetBuildings()) {
				if (cb.building.allowsCitySize3) {
					return true;
				}
			}
			return false;
		}

		public bool HasGranary() {
			foreach (CityBuilding cb in GetBuildings()) {
				if (cb.building.doublesCityGrowthRate) {
					return true;
				}
			}
			return false;
		}

		public bool HasWalls() {
			foreach (CityBuilding cb in GetBuildings()) {
				if (cb.building.providesWalls) {
					return true;
				}
			}
			return false;
		}

		public IEnumerable<StrengthBonus> GetDefenseBonuses() {
			GameData gD = EngineStorage.gameData;

			// Cities give defense bonuses based on their size.
			if (residents.Count > gD.rules.MaximumLevel2CitySize) {
				yield return gD.cityLevel3DefenseBonus;
			} else if (residents.Count > gD.rules.MaximumLevel1CitySize) {
				yield return gD.cityLevel2DefenseBonus;
			} else {
				yield return gD.cityLevel1DefenseBonus;
			}

			bool isTown = residents.Count <= gD.rules.MaximumLevel1CitySize;

			// Buildings, such as walls, can also give bonuses.
			foreach (CityBuilding cb in GetBuildings()) {
				if (cb.building.combatDefenseBonus is not StrengthBonus defenseBonus) {
					continue;
				}

				// If this building doesn't have the "only useful in towns" flag
				// we can always provide the bonus regardless of the city size.
				//
				// But if the building is only useful in towns we need to check
				// the city size before providing the bonus.
				if (!cb.building.onlyUsefulInTowns || isTown) {
					yield return defenseBonus;
				}
			}
		}

		/**
		 * Computes turn production.  If the production queue finishes,
		 * returns the item that is built.  Otherwise, returns null.
		 */
		public IProducible ComputeTurnProduction() {
			shieldsStored += CurrentProductionYield().useful;
			if (shieldsStored >= owner.ShieldCost(itemBeingProduced) && residents.Count > itemBeingProduced.populationCost) {
				shieldsStored = 0;
				RemoveCitizens(itemBeingProduced.populationCost);
				return itemBeingProduced;
			}

			shieldsStored = Math.Min(shieldsStored, owner.ShieldCost(itemBeingProduced));
			return null;
		}

		public int CurrentFoodYield() {
			int yield = location.FoodYield(this).yield;
			foreach (CityResident r in residents) {
				yield += r.tileWorked.FoodYield(this).yield;
			}
			return yield;
		}

		public CorruptableValue CurrentProductionYield() {
			int yield = location.ProductionYield(this).yield;
			foreach (CityResident r in residents) {
				yield += r.tileWorked.ProductionYield(this).yield;
			}
			CorruptableValue result = new(yield, corruption);

			// Using our value of corruption, figure out how much useful
			// production we have to work with. Special case anarchy, where no
			// useful production is available. We do this here rather than
			// setting corruption to 100% because CorruptableValue would give us
			// one useful commerce in that situation.
			//
			// The same is true for civil disorder.
			if (owner.government.transitionType || isInCivilDisorder) {
				result.useful = 0;
				result.corrupt = yield;
			}

			// TODO: add specialist shields here. Do specialists still work in
			// civil disorder?

			// The city's production buildings multiply the net shields, applied
			// after waste (spec 14 §4.1 step 3). The corrupt value stays the
			// pre-multiplier waste.
			result.useful = result.useful * ProductionMultiplierNumerator() / 4;

			return result;
		}

		// The shield multiplier numerator from the city's own buildings: it
		// starts at 4 (= 100 %) and each non-obsolete building adds its
		// Production field in quarters (the shipped Factory is 2, i.e. +50 %).
		// A building with ReplacesOtherBuildings only counts when the city also
		// has its required building, and then only the best one of them counts -
		// the power plants never stack with each other (spec 14 §4.1 step 3).
		private int ProductionMultiplierNumerator() {
			int numerator = 4;
			int maxReplacement = 0;
			foreach (CityBuilding cb in constructed_buildings) {
				if (IsObsoleteForOwner(cb.building)) {
					continue;
				}
				if (!cb.building.replacesOtherBuildings) {
					numerator += cb.building.production;
				} else if (cb.building.requiredBuilding != null
						&& constructed_buildings.Exists(x => x.building == cb.building.requiredBuilding)) {
					maxReplacement = Math.Max(maxReplacement, cb.building.production);
				}
			}
			return numerator + maxReplacement;
		}

		public CommerceBreakdown CurrentCommerceYieldRaw(bool respectCivilDisorder = true) {
			int uncorruptedCommerce = location.CommerceYield(this).yield;
			foreach (CityResident r in residents) {
				// Resisting citizens are excluded from the city's commerce
				// (spec 17 section 7: City_recompute_commerce only counts
				// citizens without the resistance flag).
				if (r.isResisting) {
					continue;
				}
				uncorruptedCommerce += r.tileWorked.CommerceYield(this).yield;
			}

			// Using our value of corruption, figure out how much useful
			// commerce we have to work with. Special case anarchy, where no
			// useful commerce is available. We do this here rather than setting
			// corruption to 100% because CorruptableValue would give us one
			// useful commerce in that situation.
			//
			// The same is true in civil disorder, but we allow ignoring civil
			// disorder for the purpose of calculating whether a city would be
			// happy at a certain luxury slider value even while the city is in
			// civil disorder.
			CorruptableValue commerce = new CorruptableValue(uncorruptedCommerce, corruption);
			if (owner.government.transitionType || (isInCivilDisorder && respectCivilDisorder)) {
				commerce.useful = 0;
				commerce.corrupt = uncorruptedCommerce;
			}

			// TODO: Science/Luxury commerce doesn't seem to be tabulating correctly, can be negative in some cases with specialists, might be ImportCiv3 issue?
			CommerceBreakdown result = new();
			result.corrupted = commerce.corrupt;

			// The buildings that boost commerce multiply the city's slider share
			// of its own commerce, after corruption. They add extra beakers/gold
			// rather than taking them from the other shares, so each share is
			// still computed from the unmultiplied split (spec 14 §4.2 steps
			// 4-5).
			int sliderBeakers = (int)Math.Floor(commerce.useful * owner.scienceRate / 10.0);
			int sliderHappiness = (int)Math.Floor(commerce.useful * owner.luxuryRate / 10.0);
			int sliderTaxes = commerce.useful - sliderBeakers - sliderHappiness;

			var numerators = CommerceMultiplierNumerators();
			result.beakers = sliderBeakers * numerators.science / 2;
			result.happiness = sliderHappiness * numerators.luxury / 2;
			result.taxes = sliderTaxes * numerators.tax / 2;

			foreach (CityResident cr in residents) {
				// Resisting citizens contribute no specialist output either
				// (spec 14 section 4.3: only citizens whose flag is 0 are
				// counted).
				if (cr.isResisting) {
					continue;
				}
				result.beakers += cr.citizenType.Research;
				result.happiness += cr.citizenType.Luxuries;
				result.taxes += cr.citizenType.Taxes;
			}

			result.wealth = WealthBuildGoldIncome();

			return result;
		}

		// The three commerce-multiplier counters from the city's own buildings,
		// each with a denominator of two: the counter starts at 2 (= 100 %) and
		// every non-obsolete building with the matching flag adds 1 (spec 14
		// §4.2 step 5). The bonus is additive in halves, so Library + University
		// is 2x, not 1.5 * 1.5 = 2.25x, and a doubling building adds a flat
		// +100 % to the science counter. Buildings made obsolete by a technology
		// the owner knows contribute nothing.
		private (int luxury, int science, int tax) CommerceMultiplierNumerators() {
			int luxury = 2;
			int science = 2;
			int tax = 2;
			foreach (CityBuilding cb in constructed_buildings) {
				if (IsObsoleteForOwner(cb.building)) {
					continue;
				}
				if (cb.building.plus50PercentLuxury) {
					++luxury;
				}
				if (cb.building.plus50PercentResearch) {
					++science;
				}
				if (cb.building.doublesResearchOutput) {
					science += 2;
				}
				if (cb.building.plus50PercentCommerce) {
					++tax;
				}
			}
			return (luxury, science, tax);
		}

		// Civ3's City_get_income_from_wealth_build (spec 14 §4.2 step 6): a city
		// producing the Wealth build turns its net shields into gold, at
		// RULE.ShieldsCostPerGold shields per gold and identically max(1, K/2)
		// for an owner that knows a technology flagged 0x1000
		// (ATF_Doubles_Effect_Of_Wealth, Economics in the shipped rules). The
		// division truncates, and a shortfall below one full gold yields exactly
		// one gold rather than rounding up.
		private int WealthBuildGoldIncome() {
			if (!IsProducingWealthBuild()) {
				return 0;
			}

			int netShields = CurrentProductionYield().useful;
			if (netShields <= 0) {
				return 0;
			}

			int shieldsPerGold = owner.rules.ShieldCostPerGold;
			if (owner.GetKnownTechs().Any(t => t.DoublesWealthProduction)) {
				shieldsPerGold = Math.Max(1, shieldsPerGold / 2);
			}

			return Math.Max(1, netShields / shieldsPerGold);
		}

		// Whether the city's current production order carries the Capitalization
		// flag: the original engine reads the flag off the produced improvement
		// (BLDG+0xec bit 19). The shipped Wealth item is modelled as an inflow,
		// but a mod may still define an ordinary building with the flag.
		private bool IsProducingWealthBuild() {
			return itemBeingProduced switch {
				Building b => b.capitalization,
				Inflow i => i.capitalization,
				_ => false,
			};
		}

		[MoonSharpHidden]
		public CommerceBreakdown CurrentCommerceYield(bool respectCivilDisorder = true) {
			CommerceBreakdown result = CurrentCommerceYieldRaw(respectCivilDisorder);

			// The Age of Science makes the city's research 25% more productive.
			if (owner.AgeOfScienceActive) {
				result.beakers = (int)(result.beakers * Player.AgeOfScienceResearchMultiplier);
			}

			// commerce lua infow
			if (this.itemBeingProduced is Inflow inflowCommerce && inflowCommerce.TryGetInflowYieldFunc(InflowYield.commerce, out var commerceYieldFunc)) {
				int extraCommerce = commerceYieldFunc.Invoke(new ScriptContext(this.owner, this));
				result.wealth += extraCommerce;
			}

			// science lua infow
			if (this.itemBeingProduced is Inflow inflowScience && inflowScience.TryGetInflowYieldFunc(InflowYield.science, out var scienceYieldFunc)) {
				int extraBeakers = scienceYieldFunc.Invoke(new ScriptContext(this.owner, this));
				result.beakers += extraBeakers;
			}

			// happiness lua infow
			if (this.itemBeingProduced is Inflow inflowHappiness && inflowHappiness.TryGetInflowYieldFunc(InflowYield.happiness, out var happinessYieldFunc)) {
				int extraHappiness = happinessYieldFunc.Invoke(new ScriptContext(this.owner, this));
				result.happiness += extraHappiness;
			}

			// corruption lua infow
			if (this.itemBeingProduced is Inflow inflowCorruption && inflowCorruption.TryGetInflowYieldFunc(InflowYield.corruption, out var corruptionYieldFunc)) {
				int lessCorruption = corruptionYieldFunc.Invoke(new ScriptContext(this.owner, this));
				result.corrupted -= lessCorruption;
			}

			return result;
		}

		public int MaintenanceCostsRaw() {
			int result = 0;
			foreach (CityBuilding cb in constructed_buildings) {
				result += cb.building.maintenanceCost;
			}
			return result;
		}

		[MoonSharpHidden]
		public int MaintenanceCosts() {
			int result = 0;
			result += MaintenanceCostsRaw();
			// maintenance lua infow
			if (this.itemBeingProduced is Inflow inflowMaintenance && inflowMaintenance.TryGetInflowYieldFunc(InflowYield.maintenance, out var maintenanceYieldFunc)) {
				int lessMaintenance = maintenanceYieldFunc.Invoke(new ScriptContext(this.owner, this));
				result -= lessMaintenance;
			}
			return result;
		}

		public int FoodGrowthPerTurn() {
			return CurrentFoodYield() - FoodConsumedPerTurn();
		}

		public int FoodConsumedPerTurn() {
			// TODO: exclude resisters in the future.
			return residents.Count * 2;
		}


		private void RemoveLastCitizen() {
			RemoveCitizenAt(residents.Count - 1);
		}

		public void RemoveRandomCitizen() {
			if (residents.Count == 1)
				return; // TODO: Handle extreme case

			var idx = GameData.rng.Next(residents.Count);
			RemoveCitizenAt(idx);
		}

		private void RemoveCitizenAt(int index) {
			residents[index].tileWorked.personWorkingTile = null;
			residents.RemoveAt(index);
		}

		public void AddCitizen(CityResident cr) {
			residents.Add(cr);
		}

		public void RemoveCitizens(int number) {
			for (int i = 0; i < number; i++) {
				if (residents.Count > 0) {
					RemoveLastCitizen();
				} else {
					Log.Warning("Trying to remove last citizen from " + name);
					break;
				}
			}
			// Population loss through this path (a pop-rush sacrifice or a
			// completed settler/worker) razes the city when it empties it
			// (spec 13 section 5); the starvation step checks for itself.
			// The reassignment paths that empty a city on purpose go through
			// RemoveAllCitizens, which does not take this check.
			if (residents.Count == 0) {
				RazeIfEmpty();
			}
		}

		// Destroys the city by the size-zero rule (spec 13 section 5), but
		// only while the city is still registered on its tile: the
		// reassignment flows empty a city temporarily and refill it.
		private void RazeIfEmpty() {
			if (location != null && location.cityAtTile == this) {
				CityInteractions.DestroyCity(this);
			}
		}

		public void RemoveAllCitizens() {
			while (residents.Count > 0) {
				RemoveLastCitizen();
			}
		}

		public override string ToString() {
			return $"{name} ({residents.Count})";
		}

		public int GetCulture() {
			return perPlayerCulture[owner];
		}

		public int GetCultureFor(Player player) {
			return perPlayerCulture.GetValueOrDefault(player, 0);
		}

		public int GetCulturePerTurnRaw() {
			int result = 0;
			foreach (CityBuilding cb in GetBuildings()) {
				var multiplier = AgeMultiplier(cb);
				result += cb.building.culturePerTurn * multiplier;
			}
			return result;
		}

		private int AgeMultiplier(CityBuilding cb) {
			int gameYear = EngineStorage.gameData.timeOptions.GetRawNumber(EngineStorage.gameData.turn);
			int ageInMillennia = (int) Math.Floor((gameYear - cb.year) / 1000f);

			if (ageInMillennia < 0) // Workaround hack , TODO: record build year correctly
				ageInMillennia = 0;

			return 1 << ageInMillennia;
		}

		[MoonSharpHidden]
		public int GetCulturePerTurn() {
			int result = GetCulturePerTurnRaw();
			// culture lua infow
			if (this.itemBeingProduced is Inflow inflow && inflow.TryGetInflowYieldFunc(InflowYield.culture, out var cultureYieldFunc)) {
				int extraCulture = cultureYieldFunc.Invoke(new ScriptContext(this.owner, this));
				result += extraCulture;
			}

			return result;
		}

		public int GetBorderExpansionLevel() {
			// Give ourselves a minimum of 1 culture to avoid taking the log of 0
			int culture = Math.Max(1, GetCulture());

			// Take the log10 of culture, rounding down (so a culture of 123
			// would be 2, a culture of 5 would be 0, etc) and then add one to
			// get the expansion level. With 0-9 culture our culture goal is 10^1
			// and we have one tile of borders, with 10-99 our culture goal is
			// 10^2 and we have two tiles of borders.
			return (int)Math.Floor(Math.Log10(culture)) + 1;
		}

		public void AddBuilding(Building building) {
			constructed_buildings.Add(new CityBuilding {
				building = building,
				builtByPlayer = owner,
				year = 1, // TODO: Implement in-game year tracking
				totalCulture = 0
			});

			// A Great Wonder carrying all of the owner's traits opens their Golden
			// Age.
			owner.CheckGoldenAgeFromWonders();
			RecordSpaceshipPartBuilt(building);

			// Completing a Great Wonder also awards victory points (spec 25 section
			// 4). Civ3 does this in the same function that adds the improvement, so
			// it runs for every path that completes a building.
			owner.AwardVictoryPointsForGreatWonder(EngineStorage.gameData, building);
		}

		// Civ3 models the ten spaceship parts as ordinary buildings that carry a
		// part index. Completing one adds it to the owner's ship, which is what
		// the space-race victory counts. A part can be completed more than once
		// when the rules require a quantity above one.
		private void RecordSpaceshipPartBuilt(Building building) {
			if (building is null || building.spaceshipPart < 0) {
				return;
			}

			while (owner.spaceshipPartsBuilt.Count <= building.spaceshipPart) {
				owner.spaceshipPartsBuilt.Add(0);
			}

			owner.spaceshipPartsBuilt[building.spaceshipPart] += 1;
		}
		public void RemoveBuilding(CityBuilding building) {
			constructed_buildings.Remove(building);
		}

		public void AddUnit(UnitPrototype proto, GameData gameData) {
			MapUnit newUnit = proto.GetInstance(gameData.GenerateID(proto.name), proto, owner, location: location);
			newUnit.experienceLevelKey = gameData.defaultExperienceLevelKey;
			newUnit.experienceLevel = gameData.defaultExperienceLevel;

			location.unitsOnTile.Add(newUnit);
			gameData.mapUnits.Add(newUnit);
			owner.AddUnit(newUnit);

			GetBuildings().ForEach(b => b.building.onFinishedUnitProduction?.Invoke(newUnit));
		}

		// The list of tiles that could be worked by this city.
		// This isn't necessarily a subset of our borders, because we're allowed
		// to work tiles owned by our civ in our big fat cross, even if our
		// borders haven't expanded yet.
		public List<Tile> GetWorkableTiles() {
			List<Tile> result = new();
			foreach (Tile t in GetTilesOfRank(owner.rules.MaxRankOfWorkableTiles)) {
				// Skip tiles not owned by this player.
				if (t.owningCity == null || t.owningCity.owner != this.owner) {
					continue;
				}

				// Skip tiles with cities on them.
				if (t.HasCity()) {
					continue;
				}

				result.Add(t);
			}
			return result;
		}

		// The list of tiles that are within the borders of this city, without
		// taking into account border collisions with other cities.
		public List<Tile> GetTilesWithinBorders() {
			return GetTilesOfRank(GetBorderExpansionLevel());
		}

		// Like GetTilesWithinRankDistance, but with the filtering of ocean
		// tiles for city border calculations.
		private List<Tile> GetTilesOfRank(int rank) {
			List<Tile> result = new();
			foreach (Tile t in location.GetTilesWithinRankDistance(rank)) {
				// Law II
				// Ocean tiles may only hold claims of rank 2.
				if (t.baseTerrainType.Key == "ocean" && t.RankDistanceTo(location) > 2) {
					continue;
				}
				result.Add(t);
			}
			return result;
		}

		// Whether an improvement has been made obsolete by a technology the
		// owner knows. Obsolete improvements no longer count towards
		// corruption reduction.
		private bool IsObsoleteForOwner(Building building) {
			return building.renderedObsoleteBy != null
					&& owner.knownTechs.Contains(building.renderedObsoleteBy.id);
		}

		// Civ3's "decorruption points" for this city. They raise the effective
		// optimal city number and set the cap on how much of a yield the city
		// can lose. See re/specs/14_commerce.md section 2, phase 1.
		private int DecorruptionPoints() {
			List<CityBuilding> buildings = GetBuildings();

			// One point per non-obsolete corruption-reducing improvement type
			// (the Courthouse and Police Station in the shipped rules).
			int points = buildings
				.Where(x => x.building.reducesCorruption && !IsObsoleteForOwner(x.building))
				.Select(x => x.building)
				.Distinct()
				.Count();

			// The capital's palace is worth ten points, which is what keeps the
			// capital free of corruption through the cap.
			if (IsCapital()) {
				points += 10;
			}

			// Seven more for each corruption-reducing small wonder the city
			// itself holds (the Forbidden Palace, and the Secret Police HQ in
			// the shipped rules).
			points += 7 * buildings.Count(x => x.building.isForbiddenPalace);

			return points;
		}

		// See https://forums.civfanatics.com/threads/everything-about-corruption-c3c-edition.76619/
		private float CalculateDistanceCorruption(GameData gameData, int decorruptionPoints) {
			// Phase 5's cap on the distance term.
			int maxD = (location.map.numTilesWide + location.map.numTilesTall) / 4;

			// Phase 3: the distance to the nearest corruption-reducing centre,
			// which is the capital or a city holding a Forbidden Palace.
			int distanceToPalace = owner.citiesWithCorruptionWonders.Min(x => location.RankDistanceTo(x.location));

			// Civ3 multiplies the distance term by 5/4 when the city is not
			// trade-connected to its capital. The trade network is cached on
			// GameData and invalidated whenever cities or roads change, so it is
			// current here.
			bool connectedTocapital = gameData.GetTradeNetwork()
				.ConnectedToCapital(owner, this, owner.GetCapitalCity());

			// Some corruption models cap the loss outright. Civ3 uses the
			// maximum distance term for a government whose corruption is "off";
			// OpenCiv3 has always modelled it as no corruption and that is
			// deliberately left unchanged here (14_commerce.md section 2, phase 4).
			if (owner.government.corruptionType == Government.CorruptionType.Off) {
				return 0;
			}

			// Phase 4: the government's corruption model scales the distance.
			// Communal replaces it with a constant, distance-independent term.
			int distance = owner.government.corruptionType switch {
				Government.CorruptionType.Minimal => distanceToPalace * 3 / 4,
				Government.CorruptionType.Rampant => distanceToPalace * 3 / 2,
				Government.CorruptionType.Communal => maxD / 4,
				_ => distanceToPalace
			};

			if (!connectedTocapital) {
				distance = distance * 5 / 4;
			}

			// Phase 5: clamp to [2, maxD].
			distance = Math.Max(2, Math.Min(distance, maxD));

			// Phase 6b: each decorruption point halves the distance, rounding
			// up, so the term converges to 1 rather than to 0.
			for (int i = 0; i < decorruptionPoints; ++i) {
				distance = (distance + 1) / 2;
			}

			return (float)distance / maxD;
		}

		// See https://forums.civfanatics.com/threads/everything-about-corruption-c3c-edition.76619/
		private float CalculateRankCorruption(GameData gameData, int decorruptionPoints) {
			int rank = rankIndex;
			if (owner.government.corruptionType == Government.CorruptionType.Communal) {
				rank = owner.cities.Count / 2;
			}

			// Phase 2: every decorruption point adds a quarter of the map's
			// optimal city count to the effective optimal city number.
			int nOpt = Math.Max(1, owner.GetAdjustedOptimalCityNumber(gameData))
					+ gameData.map.optimalNumberOfCities * decorruptionPoints / 4;

			if (rank < nOpt) {
				return rank / (2f * nOpt);
			} else {
				return (2f * rank - nOpt) / (2f * nOpt);
			}
		}

		public void CalculateCorruption(GameData gameData) {
			// Phase 1: the city's decorruption points drive both the effective
			// optimal city number and the cap on the loss.
			int decorruptionPoints = DecorruptionPoints();

			corruption = CalculateDistanceCorruption(gameData, decorruptionPoints)
					+ CalculateRankCorruption(gameData, decorruptionPoints);
			// TODO: re/specs/14_commerce.md section 2, phase 9 subtracts
			// CitizenType.Corruption for each non-resisting policeman specialist
			// before the cap. That needs the phase 8 arithmetic in yield units,
			// not the fraction-of-gross model used here.

			// Phase 10: the loss is capped at max(0, 9 - points) tenths of the
			// yield, so a capital's ten points make it immune.
			float maxCorruption = Math.Max(0, 9 - decorruptionPoints) / 10.0f;
			corruption = Math.Max(corruption, 0);
			corruption = Math.Min(corruption, maxCorruption);
		}

		// Does the per turn culture updating for the city and returns whether
		// the borders need to be updated.
		public bool UpdateCultureAndCheckForExpansion() {
			int start = GetBorderExpansionLevel();
			foreach (CityBuilding cb in GetBuildings()) {
				cb.totalCulture += cb.building.culturePerTurn;
			}

			perPlayerCulture[owner] += GetCulturePerTurn();

			return start != GetBorderExpansionLevel();
		}

		// Initializes the citizen moods, before positive and negative
		// influcences are added. A fixed number of citizens are born content,
		// based on the difficulty level, and after that all citizens are born
		// unhappy. Specialists and resisters are excluded from this.
		private void InitializeMoodsForDifficulty(Difficulty gameDifficulty) {
			int numLaborers = residents.Count(x => x.citizenType.IsDefaultCitizen);
			int content = Math.Min(gameDifficulty.NumberOfCitizensBornContent, numLaborers);

			foreach (CityResident r in residents) {
				if (!r.citizenType.IsDefaultCitizen) {
					continue;
				}

				if (content > 0) {
					--content;
					r.mood = CityResident.Mood.Content;
				} else {
					r.mood = CityResident.Mood.Unhappy;
				}
			}
		}

		// Attemps to move the specified number of citizens that have mood `from`
		// to mood `to`, returning the actual number changed.
		private int ApplyMoodChange(int count, CityResident.Mood from, CityResident.Mood to) {
			int result = 0;

			foreach (CityResident r in residents) {
				if (!r.citizenType.IsDefaultCitizen) {
					continue;
				}

				if (count <= 0) {
					break;
				}

				if (r.mood == from) {
					--count;
					++result;
					r.mood = to;
				}
			}

			return result;
		}

		// Handles the process of consuming content to happy moves, first making
		// content faces happy, then unhappy to happy at 1/2 the rate, and then
		// applying the remainder of unhappy->content, if any.
		private void ConsumeContentToHappyMoves(int contentToHappyMoves) {
			CityResident.Mood happy = CityResident.Mood.Happy;
			CityResident.Mood content = CityResident.Mood.Content;
			CityResident.Mood unhappy = CityResident.Mood.Unhappy;

			// Now move content faces to happy faces.
			contentToHappyMoves -= ApplyMoodChange(contentToHappyMoves, content, happy);

			// If there are moves left over we can try moving unhappy
			// faces to happy, but it takes two "points".
			contentToHappyMoves -= 2 * ApplyMoodChange(contentToHappyMoves / 2, unhappy, happy);

			// Account for the remainder of the integer division
			// (ApplyMoodChange ignores a negative input).
			ApplyMoodChange(contentToHappyMoves, unhappy, content);
		}

		public enum Mood {
			Unhappy,
			Happy
		};

		// This function does the heavy lifting of happiness calculations,
		// combining the various bonuses and penalties that affect citizen moods.
		//
		// Some references:
		//  - https://forums.civfanatics.com/threads/how-is-happiness-calculated.74966/
		//  - https://codehappy.net/apolyton/threads/83368-1.htm
		//
		public Mood RecalculateCitizenMoods(GameData gameData) {
			CityResident.Mood happy = CityResident.Mood.Happy;
			CityResident.Mood content = CityResident.Mood.Content;
			CityResident.Mood unhappy = CityResident.Mood.Unhappy;
			InitializeMoodsForDifficulty(gameData.gameDifficulty);

			// We want to track the move deltas from content to happy and unhappy
			// to content. We can also move from unhappy straight to content,
			// but it costs 2 "points" from the contentToHappy counter, and is
			// only applied if there are no content faces.
			int contentToHappyMoves = 0;
			int unhappyToContentMoves = 0;

			// Each citizen lost to pop rushing has a 20 turn penalty, so
			// multiple citizens lost causes multiple unhappy faces. Drafting
			// works the same way (spec 15 §4.2/§4.3).
			if (turnsOfUnhappinessDueToPopRushing > 0) {
				contentToHappyMoves -= (turnsOfUnhappinessDueToPopRushing - 1) / gameData.rules.TurnPenaltyForEachHurrySacrifice + 1;
			}
			if (turnsOfUnhappinessDueToDrafting > 0) {
				contentToHappyMoves -= (turnsOfUnhappinessDueToDrafting - 1) / gameData.rules.TurnPenaltyForEachDraftedCitizen + 1;
			}
			// Enemy propaganda leaves unhappy faces on the city; they are
			// subtracted from the happy-face total in the same pass (spec 15
			// §4.5), which is what makes a boiled city actually boil.
			contentToHappyMoves -= unhappyFacesDueToPropaganda;

			// TODO: add penalty for war weariness
			// TODO: add penalty for aggression against home country

			// Building happiness/unhappiness, which only affects the unhappy to
			// content transition, nothing with happy faces.
			unhappyToContentMoves += CalculateContentFacesFromBuildings(gameData);

			// Depending on the government type, land defensive units can serve
			// as military police.
			unhappyToContentMoves += Math.Min(owner.government.militaryPoliceLimit, location.unitsOnTile.Count(x => x.CanDefendOnLand()));

			// Luxury spending moves content faces to happy faces.
			//
			// Don't respect civil disorder during this calculation, because if
			// we are currently in civil disorder our commerce is all corrupt,
			// but we still need to be able to calculate whether a certain
			// luxury slider value would get us out of civil disorder.
			// Civ3 divides the city's entertainment output (the luxury-rate share
			// of commerce plus the luxuries specialists produce) by
			// RULE.CitizensAffectedByEachHappyFace before it becomes happy faces
			// (spec 15 §3.2).
			contentToHappyMoves += CurrentCommerceYield(respectCivilDisorder: false).happiness
				/ Math.Max(1, gameData.rules.CitizensAffectedByEachHappyFace);

			// As do luxury resources, which can be boosted by marketplaces.
			int effectiveLux = GetLuxuries(gameData).Keys.Count;
			if (GetBuildings().Any(x => x.building.increasesLuxuryTrade)) {
				effectiveLux = (int)(Math.Floor(effectiveLux / 2f) * Math.Ceiling(effectiveLux / 2f) + Math.Ceiling(effectiveLux / 2f));
			}
			contentToHappyMoves += effectiveLux;

			if (contentToHappyMoves >= 0) {
				if (unhappyToContentMoves >= 0) {
					// First apply all the unhappy->content moves. If there are
					// left over content faces that's ok, they can't be used to
					// get a citizen to happy.
					ApplyMoodChange(unhappyToContentMoves, unhappy, content);

					// Then do the same for content->happy moves, which can also
					// make unhappy->happy moves.
					ConsumeContentToHappyMoves(contentToHappyMoves);
				} else {
					int happyPoints = contentToHappyMoves;
					int sadPoints = -unhappyToContentMoves; // Deal with positive numbers

					// Each "sadness point" wipes away a content->happy move,
					// so netralize things out.
					contentToHappyMoves = Math.Max(0, happyPoints - sadPoints);
					sadPoints = Math.Max(0, sadPoints - happyPoints);

					// Use up all our content to happy moves.
					ConsumeContentToHappyMoves(contentToHappyMoves);

					// Now apply any remaining "sadness points", moving happy
					// faces back to content.
					ApplyMoodChange(sadPoints, happy, content);
				}
			} else {
				if (unhappyToContentMoves >= 0) {
					int sadPoints = -contentToHappyMoves; // Deal with positive numbers
					int contentPoints = unhappyToContentMoves;

					// Each H->C move point wipes away a content move, so
					// netralize things out.
					unhappyToContentMoves = Math.Max(0, contentPoints - sadPoints);
					sadPoints = Math.Max(0, sadPoints - contentPoints);

					// Content faces get moved to unhappy by the penalty. We
					// start with some content based on the difficulty level so
					// this is meaningful. We don't need to worry about H->U
					// moves because there is no way to have happy faces on this
					// code path.
					ApplyMoodChange(sadPoints, content, unhappy);

					// Then our positive U->C moves get applied.
					ApplyMoodChange(unhappyToContentMoves, unhappy, content);
				} else {
					int sadPoints = -contentToHappyMoves; // Deal with positive numbers

					// We have buildings that make happy faces go to content
					// faces, but there is no way to have happy faces on this
					// code path, so we just need to move content faces to
					// unhappy faces with the main penalty.
					ApplyMoodChange(sadPoints, content, unhappy);
				}
			}

			UpdateWeLoveTheKingDay(gameData);

			int happyCount = 0;
			int unhappyCount = 0;
			foreach (CityResident cr in residents) {
				if (cr.mood == CityResident.Mood.Happy) { ++happyCount; }
				if (cr.mood == CityResident.Mood.Unhappy) { ++unhappyCount; }
			}
			if (unhappyCount > 0 && unhappyCount > happyCount) {
				return Mood.Unhappy;
			} else {
				return Mood.Happy;
			}
		}

		// Applies Civ3's City_add_happiness_from_buildings (spec 15 §3.1) and
		// returns the net content faces the owner's buildings contribute to this
		// city. Two sets of faces exist for every building: the faces the city
		// itself gets, and the faces every *other* city of the owner gets. The
		// AllCities faces are counted once per other city that has the building,
		// or once per other city on the same continent when the building has
		// ContinentalMoodEffects. Great wonders only work under their required
		// government, nothing works once it is obsolete, and a wonder whose
		// DoublesHappiness names a building type doubles all of its faces.
		private int CalculateContentFacesFromBuildings(GameData gameData) {
			HashSet<Building> thisCityHas = GetBuildings().Select(cb => cb.building).ToHashSet();

			// Count, once per building type, how many of the owner's cities have
			// it - empire-wide and on this city's continent. GetBuildings includes
			// buildings granted by wonders such as the Pyramids.
			Dictionary<Building, int> ownerCityCounts = new();
			Dictionary<Building, int> continentCityCounts = new();
			foreach (City c in owner.cities) {
				foreach (Building b in c.GetBuildings().Select(cb => cb.building).ToHashSet()) {
					ownerCityCounts[b] = ownerCityCounts.GetValueOrDefault(b) + 1;
					if (c.location.continent == location.continent) {
						continentCityCounts[b] = continentCityCounts.GetValueOrDefault(b) + 1;
					}
				}
			}

			int faces = 0;
			foreach (Building b in gameData.Buildings) {
				// Obsolescence and the wonder government gate (spec 15 §3.1
				// steps 2-3).
				if (b.renderedObsoleteBy != null && owner.knownTechs.Contains(b.renderedObsoleteBy.id)) {
					continue;
				}
				if (b.IsGreatWonder() && b.requiredGovernment != null && b.requiredGovernment != owner.government) {
					continue;
				}

				bool hasHere = thisCityHas.Contains(b);
				int citiesWithBuilding = ownerCityCounts.GetValueOrDefault(b);
				if (!hasHere && citiesWithBuilding == 0) {
					continue;
				}

				int buildingFaces = hasHere ? b.contentFacesInCity - b.unhappyFacesInCity : 0;
				if (b.contentFacesAllCities != 0 || b.unhappyFacesAllCities != 0) {
					int otherCities = citiesWithBuilding - (hasHere ? 1 : 0);
					if (b.continentalMoodEffects) {
						otherCities = continentCityCounts.GetValueOrDefault(b) - (hasHere ? 1 : 0);
					}
					buildingFaces += otherCities * (b.contentFacesAllCities - b.unhappyFacesAllCities);
				}

				if (IsHappinessDoubledByAWonder(b)) {
					buildingFaces *= 2;
				}
				faces += buildingFaces;
			}

			return faces;
		}

		// Leader_has_wonder_doubling_happiness_from (spec 15 §3.1 step 5): a
		// building's faces are doubled when the owner has a great wonder whose
		// DoublesHappiness names that building type and which itself passes the
		// obsolete and government tests. GetActiveWonders already drops the
		// obsolete ones.
		private bool IsHappinessDoubledByAWonder(Building b) {
			foreach ((City _, CityBuilding cb) in owner.GetActiveWonders()) {
				Building wonder = cb.building;
				if (wonder.doublesHappinessFor != b) {
					continue;
				}
				if (wonder.requiredGovernment != null && wonder.requiredGovernment != owner.government) {
					continue;
				}
				return true;
			}
			return false;
		}

		// The We Love The King Day trigger (spec 15 §5): the city celebrates
		// while its population is at least the rule's minimum, no citizen is
		// unhappy, more citizens are happy than content and the city is not
		// starving. The flag is re-derived every time the moods are recomputed,
		// so it clears itself as soon as a condition stops holding.
		private void UpdateWeLoveTheKingDay(GameData gameData) {
			int happy = residents.Count(r => r.citizenType.IsDefaultCitizen && r.mood == CityResident.Mood.Happy);
			int content = residents.Count(r => r.citizenType.IsDefaultCitizen && r.mood == CityResident.Mood.Content);
			int unhappy = residents.Count(r => r.citizenType.IsDefaultCitizen && r.mood == CityResident.Mood.Unhappy);

			isWeLoveTheKingDay = residents.Count >= gameData.rules.MinimumPopulationForWeLoveTheKing
				&& unhappy == 0
				&& happy > content
				&& FoodGrowthPerTurn() >= 0;
		}

		// Spec 15 §6.2: on the second and later turns of civil disorder a city
		// can lose an improvement. Both rolls are rand_int(100); the second is
		// only consulted when neither of the city-size gates already allows the
		// loot.
		public bool RiotLootRollSucceeds(int firstRoll, int secondRoll) {
			Rules rules = owner.rules;
			if (firstRoll >= rules.ChanceOfRioting) {
				return false;
			}
			if (capital) {
				return false;
			}
			if (residents.Count > rules.MaximumLevel2CitySize || residents.Count > rules.MaximumLevel1CitySize) {
				return true;
			}
			return secondRoll < 2 * rules.ChanceOfRioting;
		}

		// Rolls for and applies the disorder loot: one improvement that is not a
		// great or small wonder, the palace, or a city-size improvement is
		// destroyed after at most population + 20 random picks (spec 15 §6.2).
		public void MaybeLootOnRiot(GameData gameData) {
			if (!RiotLootRollSucceeds(GameData.rng.Next(100), GameData.rng.Next(100))) {
				return;
			}

			for (int attempt = 0; attempt <= residents.Count + 20; ++attempt) {
				Building candidate = gameData.Buildings[GameData.rng.Next(gameData.Buildings.Count)];
				CityBuilding owned = constructed_buildings.Find(cb => cb.building == candidate);
				if (owned == null) {
					continue;
				}
				if (candidate.IsGreatWonder() || candidate.isSmallWonder || candidate.isCenterOfEmpire
					|| candidate.allowsCitySize2 || candidate.allowsCitySize3) {
					continue;
				}
				RemoveBuilding(owned);
				return;
			}
		}

		private Dictionary<Resource, int> ListResourceAccess(GameData gameData, ResourceCategory category) {
			Dictionary<Resource, int> availableResources = gameData.GetTradeNetwork().GetResourcesAvailableToCity(owner, this);
			Dictionary<Resource, int> result = new();
			foreach ((Resource r, int count) in availableResources) {
				if (r.Category == category) {
					result[r] = count;
				}
			}
			return result;
		}

		public Dictionary<Resource, int> GetStrategicResources(GameData gameData) {
			return ListResourceAccess(gameData, ResourceCategory.STRATEGIC);
		}

		public Dictionary<Resource, int> GetLuxuries(GameData gameData) {
			return ListResourceAccess(gameData, ResourceCategory.LUXURY);
		}
	}
}
