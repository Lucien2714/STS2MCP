using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    // Every POST response embeds the post-action game state under "state" — the same
    // dict the matching GET endpoint returns. Actions are enqueued onto the game's own
    // action queue and resolve over several frames, so we let the game settle first:
    // queue drained and, in combat, the local player back in the play phase (i.e. not
    // mid enemy turn). Capped below the MCP client's 10s HTTP timeout so a stuck game
    // never hangs the caller; on timeout we return the state as-is plus
    // "state_wait_timed_out": true.
    private const int StateWaitTimeoutMs = 8000;
    private const int StateWaitPollMs = 25;
    // The action may not hit the queue on the very first frame (in multiplayer it round
    // trips through the net service), so never declare "settled" before this.
    private const int StateWaitMinMs = 100;
    // Number of consecutive settled polls required, to avoid catching a gap between
    // two queued actions.
    private const int StateWaitStableChecks = 2;
    // Consecutive settle checks that failed to evaluate (main-thread exception) before we
    // give up and read the state as-is. A blip while a room tears down must not read as
    // "settled", but a persistently broken check must not burn the whole budget either.
    private const int StateWaitMaxUnknownChecks = 40;

    // Called on the HTTP thread. Adds "state" to an action result, waiting for the game
    // to settle first when the action was actually accepted. Rejected actions changed
    // nothing, so their state is captured immediately.
    private static Dictionary<string, object?> WithGameState(
        Dictionary<string, object?> result,
        Func<Dictionary<string, object?>> buildState)
    {
        bool failed = result.TryGetValue("status", out var status) && (status as string) == "error";
        var elapsed = Stopwatch.StartNew();
        bool settled = failed || WaitForSettledGame(elapsed);

        while (true)
        {
            Dictionary<string, object?>? state;
            try
            {
                var task = RunOnMainThread(buildState);
                if (!task.Wait(Math.Max(StateWaitTimeoutMs - (int)elapsed.ElapsedMilliseconds, 1000)))
                {
                    result["state_error"] = "Timed out waiting for the main thread to build the game state";
                    break;
                }
                state = task.Result;
            }
            catch (Exception ex)
            {
                var inner = ex is AggregateException agg && agg.InnerException != null ? agg.InnerException : ex;
                GD.PrintErr($"[STS2 MCP] Failed to build post-action state: {inner}");
                result["state_error"] = $"Failed to read game state: {inner.Message}";
                break;
            }

            // A room transition can still be mid-flight even with an idle action queue and
            // an idle transition overlay (the next screen's own setup runs over a few more
            // frames), and it reads as a transient state. Give it the rest of the budget to
            // land on a real screen.
            bool transitioning = settled && state != null && IsTransientState(state);

            if (!transitioning || elapsed.ElapsedMilliseconds >= StateWaitTimeoutMs)
            {
                result["state"] = state;
                if (transitioning)
                    result["state_wait_timed_out"] = true;
                break;
            }

            Thread.Sleep(StateWaitPollMs * 4);
        }

        if (!settled)
            result["state_wait_timed_out"] = true;

        return result;
    }

    // Polls the main thread until the game is idle and the player can act again.
    // Returns false if it gave up (timeout / unresponsive main thread).
    private static bool WaitForSettledGame(Stopwatch elapsed)
    {
        int stable = 0;
        int unknown = 0;

        while (true)
        {
            int remaining = StateWaitTimeoutMs - (int)elapsed.ElapsedMilliseconds;
            if (remaining <= 0)
                return false;

            bool ready;
            try
            {
                var task = RunOnMainThread(IsGameSettled);
                if (!task.Wait(remaining))
                    return false;
                ready = task.Result;
            }
            catch (Exception ex)
            {
                // Can't evaluate the game. Mid-transition this is exactly when the state is
                // least trustworthy, so keep waiting - but only for a bounded run of them,
                // so a permanently broken check doesn't cost every caller the full budget.
                GD.PrintErr($"[STS2 MCP] Settle check failed: {ex}");
                if (++unknown >= StateWaitMaxUnknownChecks)
                    return true;
                stable = 0;
                Thread.Sleep(StateWaitPollMs);
                continue;
            }

            unknown = 0;

            if (ready && elapsed.ElapsedMilliseconds >= StateWaitMinMs)
            {
                if (++stable >= StateWaitStableChecks)
                    return true;
            }
            else
            {
                stable = 0;
            }

            Thread.Sleep(StateWaitPollMs);
        }
    }

    // menu_select is shared by both endpoints and can itself change which run mode is
    // active (starting, abandoning, or leaving a run), so pick the builder when the
    // state is actually read rather than when the request was routed.
    private static Dictionary<string, object?> BuildStateForCurrentRun()
        => IsMultiplayerRun() ? BuildMultiplayerGameState() : BuildGameState();

    // True when the built state is one the game only passes through, so it is worth
    // rebuilding rather than handing back. The state builders reach these when the action
    // queue and the fade have both gone idle but the next screen has not opened yet.
    private static bool IsTransientState(Dictionary<string, object?> state)
    {
        if (!state.TryGetValue("state_type", out var raw) || raw is not string type)
            return false;

        // No room yet, or one the builders don't recognise: a room swap in flight.
        if (type == "unknown")
            return true;

        // Combat is over but neither the rewards screen nor the map has opened: both state
        // builders report the combat room's type with no "battle" payload and a "Combat
        // ended. Waiting for rewards..." message, which is nothing the agent can act on.
        return (type is "monster" or "elite" or "boss") && !state.ContainsKey("battle");
    }

    // Main thread only. True when nothing is left to resolve and reading the state now
    // gives the agent something it can act on.
    private static bool IsGameSettled()
    {
        // A blocking popup halts everything until it is dismissed via menu_select,
        // so this is as settled as the game will get.
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree?.Root != null && IsAnyFtueVisible(tree.Root))
            return true;

        // Room and act changes (RunManager.EnterRoom / ExitCurrentRoom / EnterAct) are plain
        // async Tasks driven by NTransition's fade, so they never touch the action queue -
        // without this check the queue reads as empty for the whole transition and we report
        // the room the player just left.
        if (NGame.Instance?.Transition is { InTransition: true })
            return false;

        var run = RunManager.Instance;
        if (run is not { IsInProgress: true })
            return true;

        // Same reasoning as the popup check above: a selection screen is waiting on
        // the agent, so this is as settled as the game will get. Waiting on the queue
        // here is actively wrong - the action that opened the screen is parked in it
        // (CardSelectCmd signals the player choice, which leaves the action at the head
        // of its queue until the pick is confirmed, so IsEmpty never turns true), and in
        // combat the card effect that raised it also holds the effect depth above zero.
        if (IsAwaitingPlayerChoice())
            return true;

        if (!run.ActionQueueSet.IsEmpty)
            return false;

        var combat = CombatManager.Instance;
        if (combat is not { IsInProgress: true })
            return true;
        // Combat is wrapping up (win, loss, or player death): wait for the run to
        // land on whatever screen comes next rather than reporting a dying combat.
        if (combat.IsOverOrEnding || combat.IsStarting)
            return false;
        if (combat.PlayerActionsDisabled)
            return false;

        var runState = run.DebugOnlyGetState();
        var player = runState != null ? LocalContext.GetMe(runState) : null;
        // No local player, or a dead one: nothing more for the agent to wait on.
        if (player?.Creature is not { IsAlive: true })
            return true;
        if (combat.IsExecutingCardOrPotionEffect(player))
            return false;

        return IsPlayPhase(player);
    }

    // Main thread only. True when the game is parked on a screen only the agent can
    // clear. NPlayerHand covers the in-combat prompt (state_type "hand_select"); every
    // screen behind "card_select" - the deck grids, choose-a-card and the combat piles -
    // implements ICardSelector, and the bundle and relic choices are awaited the same way
    // without sharing that interface.
    private static bool IsAwaitingPlayerChoice()
    {
        if (NPlayerHand.Instance is { IsInCardSelection: true })
            return true;

        return NOverlayStack.Instance?.Peek() switch
        {
            ICardSelector => true,
            NChooseABundleSelectionScreen => true,
            NChooseARelicSelection => true,
            _ => false
        };
    }
}
