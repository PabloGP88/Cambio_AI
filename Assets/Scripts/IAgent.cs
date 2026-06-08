using System.Collections.Generic;

/// <summary>
/// A decision-maker for one side. The player and the AI are symmetric: when it is a
/// side's turn at a decision point, GameManager asks that side's agent for ONE command.
///
/// Important: <paramref name="publicState"/> is the live game state, but an agent must
/// only use it for *structure* (phase, which slots are active, counts, discard top) and
/// LegalMoves(). It must NOT read hidden card values out of it — that's the whole point
/// of the belief layer. PlayerInput sidesteps this entirely (it waits for clicks); the
/// AI must respect it.
/// </summary>
public interface IAgent
{
    /// <summary>Return the single command this agent wants to play right now.</summary>
    GameCommand ChooseMove(GameState publicState);

    /// <summary>Observe an applied effect. Used to update beliefs. iAmActor = this agent caused it.</summary>
    void Observe(GameEffect effect, bool iAmActor);

    /// <summary>Reset per-game memory.</summary>
    void OnNewGame(int mySide, GameState initialState);
}
