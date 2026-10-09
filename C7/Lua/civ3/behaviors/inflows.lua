local inflows = {}

-- The Wealth build's shields-to-gold conversion now lives in
-- C7Engine/C7GameData/City.cs (City.WealthBuildGoldIncome), because the rate is
-- driven by the produced item's Capitalization flag rather than by this inflow
-- hook. This function stays defined so that the
-- "inflows.result.wealth.commerce" path in ruleset.json still resolves, and it
-- returns 0 so the two implementations cannot double count.
local function extra_commerce_calculation(context)
  return 0
end

-- Any and all of the table values below should return an integer
-- that will be added to the respective base value.
--
-- for example commerce will be added to the overall commerce income, culture to the current culture per turn.
--
-- maintenance, unitsupport and corruption are the ones that will be subtracted by their current base value
-- so if you want MORE corruption/maintenance/unit support, the number should be negative

inflows.result = {
  wealth = {
    commerce = function(context)
      return extra_commerce_calculation(context)
    end,
  },
  -- add new inflow(s) as the example below.
  -- The respective json entry with the yieldCalculation having the path to this new item
  -- should be added in the json save file, under inflows
  -- not all fields like commerce, culture, science etc are mandatory, as long as they reflect the ones in the json
  
  --placeholder = {
  --  commerce = function(context)
  --    -- replace with a hadcoded value, a method call, etc
  --    return 0
  --  end,
  --  culture = function(context)
  --    -- replace with a hadcoded value, a method call, etc
  --    return 0
  --  end,
  --  science = function(context)
  --    -- replace with a hadcoded value, a method call, etc
  --    return 0
  --  end,
  --  happiness = function(context)
  --    -- replace with a hadcoded value, a method call, etc
  --    return 0
  --  end,
  --  maintenance = function(context)
  --    -- replace with a hadcoded value, a method call, etc
  --    return 0
  --  end,
  --  unitsupport = function(context)
  --    -- replace with a hadcoded value, a method call, etc
  --    return 0
  --  end,
  --  corruption = function(context)
  --    -- replace with a hadcoded value, a method call, etc
  --    return 0
  --  end,
  --},
  
  
  -- json example of an entry
  
  --{
  --  "name": "Placeholder", <- this is the display name in the production box
  --  "iconRowIndex": 29,
  --  "localYield": [
  --    {
  --      "yieldType": "commerce",
  --      "yieldCalculation": "inflows.result.placeholder.commerce" 
  --    },
  --    {
  --      "yieldType": "culture",
  --      "yieldCalculation": "inflows.result.placeholder.culture"
  --    },
  --    {
  --      "yieldType": "science",
  --      "yieldCalculation": "inflows.result.placeholder.science"
  --    },
  --    {
  --      "yieldType": "happiness",
  --      "yieldCalculation": "inflows.result.placeholder.happiness"
  --    },
  --    {
  --      "yieldType": "maintenance",
  --      "yieldCalculation": "inflows.result.placeholder.maintenance"
  --    },
  --    {
  --      "yieldType": "unitsupport",
  --      "yieldCalculation": "inflows.result.placeholder.unitsupport"
  --    },
  --    {
  --      "yieldType": "corruption",
  --      "yieldCalculation": "inflows.result.placeholder.corruption"
  --    }
  --  ]
  --}
}

return inflows
