using System.Collections.Generic;

public interface IAgent
{
    /// <summary>Return the single command this agent wants to play right now.</summary>
    GameCommand ChooseMove(GameState publicState);

    /// <summary>Observe an applied effect. Used to update beliefs. iAmActor = this agent caused it.</summary>
    void Observe(GameEffect effect, bool iAmActor);

    /// <summary>Reset per-game memory.</summary>
    void OnNewGame(int mySide, GameState initialState);
}
