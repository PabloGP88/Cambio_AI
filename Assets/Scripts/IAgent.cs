using System;
using System.Collections;
using System.Collections.Generic;

public interface IAgent
{
    /// <summary>Return the single command this agent wants to play right now. Blocking —
    /// runs the whole search in one call. Kept for callers that don't care about live
    /// progress (tests, non-Unity contexts).</summary>
    GameCommand ChooseMove(GameState publicState);

    /// <summary>
    /// Same decision as ChooseMove, but spread across frames: yields periodically so a
    /// caller (GameManager) can run it as a coroutine and let OnSearchProgress /
    /// OnSearchDecision actually reach the screen mid-search instead of only after the
    /// whole thing finishes. Calls onDecided exactly once, with the chosen command.
    /// Agents with nothing incremental to show can just do:
    ///   onDecided(ChooseMove(state)); yield break;
    /// </summary>
    IEnumerator ChooseMoveRoutine(GameState publicState, Action<GameCommand> onDecided);

    /// <summary>Fired periodically while a search is in progress (agents that don't run a
    /// search can simply never fire this).</summary>
    event Action<IsmctsReport> OnSearchProgress;

    /// <summary>Fired once, with the final tree state including which move was chosen.</summary>
    event Action<IsmctsReport> OnSearchDecision;

    /// <summary>Observe an applied effect. Used to update beliefs. iAmActor = this agent caused it.</summary>
    void Observe(GameEffect effect, bool iAmActor);

    /// <summary>Reset per-game memory.</summary>
    void OnNewGame(int mySide, GameState initialState);
}