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
    /// Same decision as ChooseMove, but exposed as a coroutine so a caller (GameManager)
    /// can run it and get OnSearchDecision once the search finishes. Calls onDecided
    /// exactly once, with the chosen command. Agents with nothing else to do can just do:
    ///   onDecided(ChooseMove(state)); yield break;
    /// </summary>
    IEnumerator ChooseMoveRoutine(GameState publicState, Action<GameCommand> onDecided);

    /// <summary>Fired once per decision, with the final tree state: which move was chosen,
    /// its stats, and how it compares to the runner-up.</summary>
    event Action<IsmctsReport> OnSearchDecision;

    /// <summary>Observe an applied effect. Used to update beliefs. iAmActor = this agent caused it.</summary>
    void Observe(GameEffect effect, bool iAmActor);

    /// <summary>Reset per-game memory.</summary>
    void OnNewGame(int mySide, GameState initialState);
}