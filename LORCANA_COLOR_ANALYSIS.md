# Disney Lorcana Color Identity Analysis

Based on analysis of 2,455 cards from allCards.json (as of analysis date).

## Color Distribution
- **Amber**: 381 cards (15.5%)
- **Amethyst**: 372 cards (15.2%)  
- **Emerald**: 368 cards (15.0%)
- **Ruby**: 372 cards (15.2%)
- **Sapphire**: 371 cards (15.1%)
- **Steel**: 370 cards (15.1%)

## Amber (Support/Songs/Healing)
**Primary Identity**: Singing, supportive defensive play, healing

**Top Keywords**:
- Shift: 33
- Bodyguard: 28
- Support: 26
- Singer: 24
- Sing Together: 7

**Mechanics**:
- Songs: 231 mentions (HIGHEST of all colors)
- Healing/Damage removal: 45 mentions (2nd highest)
- Bodyguard/Support for defensive team play
- Ready/Exert manipulation: 68

**Character Stats**:
- Avg Lore: 1.58 (moderate questing)
- Avg Strength: 2.40 (low aggression)
- Avg Willpower: 4.06 (HIGHEST - very durable)

**Playstyle**: Defensive support with song synergies, high durability, healing effects.

## Amethyst (Evasive/Control/Card Advantage)
**Primary Identity**: Evasive threats, card filtering, bounce/control

**Top Keywords**:
- Evasive: 43 (HIGHEST of all colors)
- Shift: 31
- Challenger: 20
- Rush: 18

**Mechanics**:
- Draw/Look: 102 mentions (HIGHEST - card advantage color)
- Challenge: 141 (high interaction)
- Songs: 120 (2nd highest)
- Ready/Exert: 98

**Character Stats**:
- Avg Lore: 1.49
- Avg Strength: 2.69
- Avg Willpower: 3.69

**Playstyle**: Evasive threats that avoid challenges, card filtering/selection, tempo/bounce effects.

## Emerald (Aggressive/Ward/Protection)
**Primary Identity**: Ward protection, aggressive challenges, go-wide strategies

**Top Keywords**:
- Evasive: 42
- Shift: 32
- Ward: 25 (2nd highest)
- Boost: 4

**Mechanics**:
- Challenge: 145 (aggressive)
- Damage/Banish: 127 (high removal)
- Songs: 111
- NO healing mechanics (0 heal/move damage mentions)

**Character Stats**:
- Avg Lore: 1.67 (HIGHEST - best questing)
- Avg Strength: 2.58
- Avg Willpower: 3.40 (lowest durability)

**Playstyle**: Fast aggressive questing, Ward for protection, challenges and removal. Glass cannon strategy.

## Ruby (Aggro/Direct Damage/Tempo)
**Primary Identity**: Rush aggression, direct damage/banishment, lore pressure

**Top Keywords**:
- Evasive: 42
- Shift: 35
- Rush: 24 (HIGHEST)
- Reckless: 17 (ONLY color with significant Reckless)

**Mechanics**:
- Damage/Banish: 140 (2nd highest - removal color)
- Challenge: 159 (HIGHEST - most aggressive challenges)
- Lore: 78 mentions (direct lore manipulation/drain)
- Almost NO healing (1 mention)

**Character Stats**:
- Avg Lore: 1.34 (LOWEST - not about questing)
- Avg Strength: 3.38 (HIGHEST - most aggressive)
- Avg Willpower: 3.42 (fragile)

**Playstyle**: Hyper-aggressive Rush/Reckless creatures, direct damage, lore drain effects, challenges over questing.

## Sapphire (Items/Cost Reduction/Defensive Control)
**Primary Identity**: Item synergies, cost reduction, defensive manipulation

**Top Keywords**:
- Support: 30 (tied with Amber)
- Shift: 27
- Ward: 20
- Boost: 6 (HIGHEST)

**Mechanics**:
- Items: 105 mentions (HIGHEST - item matters color)
- Draw/Look: 72
- Ready/Exert: 90 (defensive control)
- Cost Reduction: 23 (HIGHEST)

**Character Stats**:
- Avg Lore: 1.66 (high questing)
- Avg Strength: 2.52
- Avg Willpower: 3.82 (durable)

**Playstyle**: Item-based value engines, cost reduction, defensive control via ready/exert, Ward protection.

## Steel (Removal/Durability/Challenges)
**Primary Identity**: Maximum removal, Resist durability, challenge-focused

**Top Keywords**:
- Shift: 33
- Resist: 29 (HIGHEST - exclusive Steel mechanic)
- Bodyguard: 27
- Challenger: 16

**Mechanics**:
- Damage/Banish: 236 (HIGHEST BY FAR - removal king)
- Challenge: 174 (2nd highest - combat focused)
- Songs: 120
- Almost NO healing (1 mention)

**Character Stats**:
- Avg Lore: 1.55
- Avg Strength: 2.86 (2nd highest)
- Avg Willpower: 4.06 (HIGHEST with Amber - very durable)

**Playstyle**: Removal-heavy control, Resist makes creatures very hard to remove, Bodyguard defense, challenges.

## Color Pair Synergies (Recommendations)

### Popular Competitive Pairs:
1. **Ruby/Amethyst**: Aggressive evasive threats + removal
2. **Amber/Amethyst**: Songs + card draw + evasive threats
3. **Sapphire/Steel**: Items + removal + durability (control)
4. **Emerald/Steel**: Questing + removal + durability
5. **Ruby/Steel**: Aggro + removal (beat down)

### Thematic Pairs:
- **Amber/Sapphire**: Songs + Items + Support (value engine)
- **Amethyst/Sapphire**: Card draw + Items + control
- **Ruby/Emerald**: Maximum aggression (Rush + questing)
- **Steel/Amber**: Durability + healing (defensive)

## Key Insights for Deck Building

1. **For Removal**: Steel (236) > Ruby (140) > Emerald (127)
2. **For Card Draw**: Amethyst (102) > Sapphire (72)
3. **For Songs**: Amber (231) > Amethyst (120) = Steel (120)
4. **For Items**: Sapphire (105) is the clear leader
5. **For Aggression**: Ruby (highest strength, Rush, Reckless)
6. **For Questing**: Emerald (1.67 avg lore) > Sapphire (1.66)
7. **For Durability**: Amber/Steel (4.06 willpower), Resist in Steel
8. **For Evasion**: Amethyst (43) > Emerald/Ruby/Sapphire (42)

## Analysis Methodology

Data extracted from allCards.json using PowerShell:
- Counted keyword abilities from `keywordAbilities` field
- Text-searched `fullText` field for mechanic mentions (case-insensitive regex)
- Calculated average character stats (lore/strength/willpower)
- Analyzed all 2,455 cards across 6 colors
