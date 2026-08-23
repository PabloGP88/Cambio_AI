using System;
using System.Collections;
using System.Collections.Generic;

public interface IAgent
{
    /* return the single command this agent wants to play right now. blocking, since it
       runs the whole search in one call. kept for callers that don't need live progress
       such as tests or non-Unity contexts */
    GameCommand ChooseMove(GameState publicState);

    /* same decision as ChooseMove, but exposed as a coroutine so a caller such as
       GameManager can run it and get onDecided once the search finishes. onDecided is
       called exactly once with the chosen command */
    IEnumerator ChooseMoveRoutine(GameState publicState, Action<GameCommand> onDecided);

    // fired once per decision with the final tree state: chosen move, its stats, runner-up
    event Action<IsmctsReport> OnSearchDecision;

    // observe an applied effect to update beliefs; iAmActor means this agent caused it
    void Observe(GameEffect effect, bool iAmActor);

    // reset per-game memory
    void OnNewGame(int mySide, GameState initialState);
}