using System;
using System.Collections;
using System.Collections.Generic;

public interface IAgent
{
    /* return the single command this agent wants to play right now. blocking, since it
       runs the whole search in one call. kept for callers that don't need live progress
       such as tests or non-Unity contexts */
    GameCommand ChooseMove(GameState publicState);
    
    IEnumerator ChooseMoveRoutine(GameState publicState, Action<GameCommand> onDecided);


    event Action<IsmctsReport> OnSearchDecision;

    // observe an applied effect to update beliefs; iAmActor means this agent caused it
    void Observe(GameEffect effect, bool iAmActor);

    // reset per-game memory
    void OnNewGame(int mySide, GameState initialState);
}