using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    // GET /api/v1/player — run-level detail for the local player: vitals, gold,
    // relics, potions, and the full master deck. Deliberately run-mode agnostic
    // (like the profile endpoints) rather than split behind the 409 guard: it only
    // reads the local player and enqueues nothing, so it is sync-safe in co-op.
    // Combat-only fields (energy, hand, piles, block, status) stay out — those
    // belong to the state endpoints.
    private static void HandleGetPlayerDetail(HttpListenerResponse response)
    {
        try
        {
            var task = RunOnMainThread(BuildPlayerDetail);
            SendJson(response, task.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Failed to build player detail: {ex.Message}");
        }
    }

    // Main thread only.
    internal static Dictionary<string, object?> BuildPlayerDetail()
    {
        var result = new Dictionary<string, object?>();

        var run = RunManager.Instance;
        if (run is not { IsInProgress: true })
        {
            result["in_run"] = false;
            result["error"] = "No run in progress.";
            return result;
        }

        var runState = run.DebugOnlyGetState();
        var player = runState != null ? LocalContext.GetMe(runState) : null;
        if (player == null)
        {
            result["in_run"] = false;
            result["error"] = "Run is in progress but the local player is not available yet.";
            return result;
        }

        var creature = player.Creature;

        result["in_run"] = true;
        result["is_multiplayer"] = IsMultiplayerRun();
        result["character"] = SafeGetText(() => player.Character.Title);
        result["hp"] = creature.CurrentHp;
        result["max_hp"] = creature.MaxHp;
        result["gold"] = player.Gold;
        result["relics"] = BuildRelicsList(player);
        result["potions"] = BuildPotionsList(player);
        result["max_potion_slots"] = player.MaxPotionCount;
        result["deck"] = BuildDeckState(player);

        return result;
    }

    /// <summary>
    /// The master deck (<see cref="PileType.Deck"/>), grouped so identical copies
    /// collapse into a single entry with a quantity. Combat operates on copies that
    /// point back at their deck original via <c>CardModel.DeckVersion</c>, so this
    /// pile stays whole mid-fight and reads as the run-level deck at any point.
    /// </summary>
    private static Dictionary<string, object?> BuildDeckState(Player player)
    {
        var deck = new Dictionary<string, object?>();

        List<CardModel> cards;
        try
        {
            cards = player.Deck.Cards.ToList();
        }
        catch (Exception ex)
        {
            deck["error"] = $"Failed to read the deck: {ex.Message}";
            return deck;
        }

        var groups = new Dictionary<string, Dictionary<string, object?>>();
        var ordered = new List<(int TypeOrder, string Name, int UpgradeLevel, Dictionary<string, object?> Entry)>();
        var countsByType = new Dictionary<string, int>();
        int upgradedCount = 0;

        foreach (var card in cards)
        {
            var entry = BuildDeckCardInfo(card);

            string typeName = entry["type"] as string ?? "Unknown";
            countsByType[typeName] = countsByType.GetValueOrDefault(typeName) + 1;
            if (entry["is_upgraded"] is true)
                upgradedCount++;

            string key = BuildDeckGroupKey(entry);
            if (groups.TryGetValue(key, out var existing))
            {
                existing["quantity"] = (existing["quantity"] as int? ?? 0) + 1;
                MergeFloorsAdded(existing, entry);
                continue;
            }

            entry["quantity"] = 1;
            groups[key] = entry;
            ordered.Add((
                (int)card.Type,
                entry["name"] as string ?? "",
                entry["current_upgrade_level"] as int? ?? 0,
                entry));
        }

        // Card type first (Attack, Skill, Power, Status, Curse), then name, then
        // upgrade level — deterministic, so repeated calls return the same order.
        ordered.Sort((a, b) =>
        {
            int byType = a.TypeOrder.CompareTo(b.TypeOrder);
            if (byType != 0) return byType;
            int byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            if (byName != 0) return byName;
            return a.UpgradeLevel.CompareTo(b.UpgradeLevel);
        });

        deck["count"] = cards.Count;
        deck["unique_count"] = ordered.Count;
        deck["upgraded_count"] = upgradedCount;
        deck["counts_by_type"] = countsByType;
        deck["cards"] = ordered.Select(o => o.Entry).ToList();
        return deck;
    }

    // Two copies collapse only when everything an agent would act on matches -
    // rules text included, since enchantments and modifiers can diverge two cards
    // that share an id and upgrade level.
    private static string BuildDeckGroupKey(Dictionary<string, object?> entry)
        => string.Join("|",
            entry["id"] as string ?? "?",
            (entry["current_upgrade_level"] as int? ?? 0).ToString(),
            (entry["enchantment"] as Dictionary<string, object?>)?["id"] as string ?? "-",
            (entry["affliction"] as Dictionary<string, object?>)?["id"] as string ?? "-",
            entry["cost"] as string ?? "-",
            entry["star_cost"] as string ?? "-",
            entry["description"] as string ?? "-");

    // floor_added is the one field that legitimately differs between otherwise
    // identical copies, so grouping keeps the union instead of dropping it.
    private static void MergeFloorsAdded(
        Dictionary<string, object?> target,
        Dictionary<string, object?> source)
    {
        if (source["floors_added"] is not List<int> incoming || incoming.Count == 0)
            return;

        if (target["floors_added"] is not List<int> existing)
        {
            target["floors_added"] = new List<int>(incoming);
            return;
        }

        foreach (int floor in incoming)
        {
            if (!existing.Contains(floor))
                existing.Add(floor);
        }
        existing.Sort();
    }

    // A deck entry is the standard card object plus the floors its copies arrived on.
    private static Dictionary<string, object?> BuildDeckCardInfo(CardModel card)
    {
        var info = BuildCardInfo(card, PileType.Deck);

        if (card.FloorAddedToDeck is { } floorAdded)
            info["floors_added"] = new List<int> { floorAdded };

        return info;
    }
}
