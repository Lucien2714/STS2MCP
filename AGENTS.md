# STS2 MCP — AI Gameplay Guide

## MCP Tool Calling Tips

### State Polling
- Every action response already embeds the resulting state under `state` — the mod waits for the game to settle (action queue drained, and in combat the player back in the play phase) before capturing it, so `combat_end_turn` returns the state of your **next** turn, after the enemies acted. You normally do not need a follow-up `get_game_state`.
- A screen waiting on *you* counts as settled and returns right away — blocking popups and the selection screens (`hand_select`, `card_select`, `bundle_select`, `relic_select`). The response then shows that screen, not the resolved effect: keep selecting/confirming until `state_type` moves on.
- The settle wait is capped at 8 seconds. If a response carries `state_wait_timed_out: true` (or `is_play_phase: false`), call `get_game_state` again until the play phase is back.
- Use `format: "json"` during combat for structured data; `format: "markdown"` for map/event overview.

### Reading Your Deck
- `get_player` is the only way to see your **master deck**. `get_game_state` shows combat piles (hand / draw / discard / exhaust), which are per-combat copies — they do not tell you what the run-level deck holds.
- Call it before any deck-shaping decision: card rewards, shop buys, removals, upgrades, transforms, and Neow choices.
- Identical copies are grouped into one entry with `quantity`. Check `current_upgrade_level` / `is_upgradable` before spending a rest-site upgrade, and `counts_by_type` to see whether the deck is starving for block or damage.
- Every card object — in hand, in a pile, in a reward, in the deck — carries `id`, `is_upgraded`, `current_upgrade_level`, `max_upgrade_level`, `is_upgradable`, and `enchantment` / `affliction` (both omitted when absent). Don't infer upgrades from a `+` in the name.
- It also returns HP, gold, relics, and potions, and works in both singleplayer and multiplayer.

### Card Index Shifting
- **CRITICAL**: Playing a card removes it from hand and shifts all indices. Play cards from RIGHT to LEFT (highest index first) to keep lower indices stable, or re-check state between plays.
- When targeting, always provide `target` for single-target cards. Entity IDs are UPPER_SNAKE_CASE with a `_0` suffix (e.g. `KIN_PRIEST_0`).

### Event & Reward Flow
- Events: `event_choose_option`. After choosing, there's often a "Proceed" option at index 0.
- Rest sites: `rest_choose_option`, then `proceed_to_map`.
- Rewards: claim from right-to-left (highest index first) to avoid index shifting. Card rewards open a sub-screen; use `rewards_pick_card` or `rewards_skip_card`.

### Potions
- `use_potion(slot=N)` — slot is the potion slot index, not a card index.
- `discard_potion(slot=N)` — discard a potion to free up the slot when full.
- Potions don't cost energy or count as card plays. Use buff potions BEFORE playing cards.

---

## General Strategy

### Core Principles
1. **HP is a resource, not a score.** Take calculated damage to deal more. Don't waste energy on block when enemies aren't attacking.
2. **Deck quality > deck size.** Skip card rewards if nothing synergizes. A lean deck draws key cards more often.
3. **Front-load damage.** Killing enemies faster means less total damage taken.
4. **Read intents carefully.** Sleep/Buff = go all-out offense. Attack = balance block and damage. Debuff = usually no damage, offense turn.

### Combat Sequencing (General)
1. Play 0-cost utility/setup cards first.
2. Play skills before attacks when possible — many mechanics reward this order (e.g. Slow debuff on enemies stacks per card played).
3. Play biggest attacks last to benefit from accumulated buffs/debuffs.
4. Check enemy HP — if you can kill this turn, skip blocking entirely.

### Map Pathing
- **Elites** give relics — fight them when healthy (>70% HP).
- **Rest before Boss** — heal if below 80% HP. Boss fights are long and punishing.
- **Unknown nodes** are safer than Elites. Good at medium HP.
- **Shops** — visit with 100+ gold.
- **Deck quality matters more than quantity** — don't add cards just because they're offered.

### Boss Fights
- **Kill the leader, not the minions.** Enemies with "Minion" power flee when their leader dies.
- Use potions aggressively in boss fights — they don't carry between acts.
- Boss fights are wars of attrition. The longer they go, the more enemies scale with Strength buffs.

### Potion Usage
- Don't hoard potions. Dying with full potions is the worst outcome.
- Use permanent-value potions (Fruit Juice = +5 Max HP) early in any combat.
- Use buff potions (Flex Potion) on turns with multiple attacks.

### Common Mistakes
- Blocking when enemies are sleeping/buffing — waste of energy.
- Not checking card indices after playing — indices shift left.
- Taking too long to kill bosses — enemies scale every turn.
- Adding mediocre cards that dilute the deck before boss fights.
