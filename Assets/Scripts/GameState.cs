using System;
using System.Collections.Generic;
using System.Linq;

public enum GamePhase
{
    Dealing,
    DrawingCard,
    CardDrawn,
    SelectingSwapSlot,
    UsingPower,
    CambioCalled,      // transient
    GameOver
}

public enum PowerStep
{
    None,
    LookingOwn,
    LookingOpponent,
    SelectingPowerSwapSource,
    SelectingPowerSwapTarget,
    SelectingTradeOpponent,
    SelectingTradeOwn,
    ConfirmingTrade
}

/// <summary>Observable consequence of applying a command. Drives view events + AI observation.</summary>
public enum EffectKind
{
    CardDrawn,           // Slot = drawer's hand (index -1), Card = drawn card, Success = actorIsPlayer
    SlotRevealed,        // Slot, Card, Success = actorIsPlayer (who learned it)
    MatchResolved,       // Slot, Card, Success = matched, ByPlayer
    PenaltyAdded,        // Slot = new penalty slot, Card, Success = forPlayer
    SlotsSwapped,        // Slot = a, Slot2 = b (Slot2 = None for a one-slot change)
    GiveDone,
    InformedTradeReady,  // Card = opponent card, Card2 = own card
    DrawnDiscarded, // Card = discarded draw, Success = actorIsPlayer
    GameOver
}

public struct GameEffect
{
    public EffectKind Kind;
    public SlotRef Slot;
    public SlotRef Slot2;
    public Card Card;
    public Card Card2;
    public bool Success;
    public bool ByPlayer;
}

public struct MoveResult
{
    public bool Ok;
    public List<GameEffect> Effects;
    public static MoveResult Fail() => new MoveResult { Ok = false, Effects = null };
}

/// <summary>
/// The full game as data plus the rules that mutate it. Everything the AI and PlayerInput
/// need is exposed through public read-only queries so neither can see hidden card values.
/// Apply() is the single mutation path; it returns the observable effects of the command.
/// </summary>
public class GameState
{
    public const int PlayerSide = 0;
    public const int AISide = 1;

    public int HandSize { get; private set; }
    public int PenaltySize { get; private set; }

    // Layout
    private Card[][] _hand;        // [side][index]
    private Card[][] _penalty;     // [side][index]
    private List<Card> _drawPile;  // end of list = top of deck
    private List<Card> _discard;   // end of list = top of discard

    // Current state
    private GamePhase _phase;
    private PowerStep _powerStep;
    private bool _isPlayerTurn;
    private Card _drawn;             // card just picked up
    private CardPower _activePower;  // power currently being resolved, if any

    // Endgame
    private bool _cambioCalled;
    private int _cambioCallerSide;
    private int _finalRoundTurnsLeft;

    // Per-turn flags
    private bool _matchedThisTurn;
    private bool _awaitingGiveCard;
    private SlotRef _matchReceiver;      // opponent slot the giver must fill
    private int _giverSide = -1; // for matching in opponents turn
    private bool _awaitingPeekConfirm;   // a look-power peeked; only FinishPeeking is legal now

    private SlotRef _powerSource;
    private SlotRef _tradeOpponent;
    private SlotRef _tradeOwn;

    private Random _rng;

    // Public reads
    public GamePhase Phase => _phase;
    public PowerStep PowerStep => _powerStep;
    public bool IsPlayerTurn => _isPlayerTurn;
    public int ActiveSide => _isPlayerTurn ? PlayerSide : AISide;
    public int OpponentSide => _isPlayerTurn ? AISide : PlayerSide;
    public Card Drawn => _drawn;
    public CardPower ActivePower => _activePower;
    public bool CambioCalled => _cambioCalled;
    public bool PlayerCalledCambio => _cambioCallerSide == PlayerSide;
    public int FinalRoundTurnsLeft => _finalRoundTurnsLeft;
    public bool AwaitingGiveCard => _awaitingGiveCard;
    public int  GiverSide    => _giverSide >= 0 ? _giverSide : ActiveSide;
    public bool GiveByPlayer => GiverSide == PlayerSide;
    public bool AwaitingPeekConfirm => _awaitingPeekConfirm;
    public bool MatchedThisTurn => _matchedThisTurn;
    public bool IsTerminal => _phase == GamePhase.GameOver;
    public Card TopDiscard => _discard.Count > 0 ? _discard[^1] : Card.None;
    public int DrawPileCount => _drawPile.Count;
    public int DiscardCount => _discard.Count;

    public static int OpponentOf(int side) => side == PlayerSide ? AISide : PlayerSide;

    /// <param name="shuffledIds">A pre-shuffled ordering of every physical card id.</param>
    public GameState(IReadOnlyList<int> shuffledIds, int handSize, int penaltySize, int seed)
    {
        HandSize = handSize;
        PenaltySize = penaltySize;
        _rng = new Random(seed);

        _drawPile = new List<Card>(shuffledIds.Count);
        foreach (var id in shuffledIds) _drawPile.Add(new Card(id));
        _discard = new List<Card>();

        _hand = new[] { NewSlots(handSize), NewSlots(handSize) };
        _penalty = new[] { NewSlots(penaltySize), NewSlots(penaltySize) };

        for (var i = 0; i < handSize; i++) _hand[PlayerSide][i] = DrawNoReshuffle();
        for (var i = 0; i < handSize; i++) _hand[AISide][i] = DrawNoReshuffle();
        // penalties start empty (None)

        _phase = GamePhase.Dealing;
        _isPlayerTurn = true;
        _drawn = Card.None;
        _powerStep = PowerStep.None;
        _matchReceiver = SlotRef.None;
        _giverSide = -1;
        _powerSource = SlotRef.None;
        _tradeOpponent = SlotRef.None;
        _tradeOwn = SlotRef.None;
    }

    private GameState() { } // for Clone

    private static Card[] NewSlots(int n)
    {
        var a = new Card[n];
        for (int i = 0; i < n; i++) a[i] = Card.None;
        return a;
    }

    public void StartPlay()
    {
        if (_phase != GamePhase.Dealing) return;
        _phase = GamePhase.DrawingCard;
        _isPlayerTurn = true;
    }

    /// <summary>Deep copy for search. Pass a fresh seed so determinizations diverge.</summary>
    public GameState Clone(int seed)
    {
        return new GameState
        {
            HandSize = HandSize,
            PenaltySize = PenaltySize,
            _hand = new[] { (Card[])_hand[0].Clone(), (Card[])_hand[1].Clone() },
            _penalty = new[] { (Card[])_penalty[0].Clone(), (Card[])_penalty[1].Clone() },
            _drawPile = new List<Card>(_drawPile),
            _discard = new List<Card>(_discard),
            _phase = _phase,
            _powerStep = _powerStep,
            _isPlayerTurn = _isPlayerTurn,
            _drawn = _drawn,
            _activePower = _activePower,
            _cambioCalled = _cambioCalled,
            _cambioCallerSide = _cambioCallerSide,
            _finalRoundTurnsLeft = _finalRoundTurnsLeft,
            _matchedThisTurn = _matchedThisTurn,
            _awaitingGiveCard = _awaitingGiveCard,
            _matchReceiver = _matchReceiver,
            _giverSide = _giverSide,
            _awaitingPeekConfirm = _awaitingPeekConfirm,
            _powerSource = _powerSource,
            _tradeOpponent = _tradeOpponent,
            _tradeOwn = _tradeOwn,
            _rng = new Random(seed)
        };
    }

    // Slot access
    public Card GetCard(SlotRef slotRef)
    {
        if (slotRef.IsNone) return Card.None;
        var cards = slotRef.Zone == Zone.Hand ? _hand[slotRef.Side] : _penalty[slotRef.Side];
        if (slotRef.Index < 0 || slotRef.Index >= cards.Length) return Card.None;
        return cards[slotRef.Index];
    }

    private void SetCard(SlotRef slotRef, Card card)
    {
        var arr = slotRef.Zone == Zone.Hand ? _hand[slotRef.Side] : _penalty[slotRef.Side];
        arr[slotRef.Index] = card;
    }

    /// <summary>A slot is "active" iff it currently holds a card.</summary>
    public bool IsActive(SlotRef s) => !GetCard(s).IsNone;

    public List<Card> DiscardPile => _discard;

    private IEnumerable<SlotRef> ActiveSlotsOf(int side)
    {
        for (int i = 0; i < _hand[side].Length; i++)
            if (!_hand[side][i].IsNone) yield return new SlotRef(side, Zone.Hand, i);
        for (int i = 0; i < _penalty[side].Length; i++)
            if (!_penalty[side][i].IsNone) yield return new SlotRef(side, Zone.Penalty, i);
    }

    public IEnumerable<SlotRef> GetActiveSlots(int side) => ActiveSlotsOf(side);

    /// <summary>The action space the AI and PlayerInput pick from, for the current phase.</summary>
    public List<GameCommand> LegalMoves()
    {
        var moves = new List<GameCommand>();
        if (_phase == GamePhase.GameOver || _phase == GamePhase.Dealing) return moves;

        switch (_phase)
        {
            case GamePhase.DrawingCard:
                if (_awaitingGiveCard)
                {
                    int giver = _giverSide >= 0 ? _giverSide : ActiveSide;
                    foreach (var s in ActiveSlotsOf(giver))
                        moves.Add(GameCommand.Give(s));
                }
                else
                {
                    if (CanDraw()) moves.Add(GameCommand.DrawFromDeck());
                    if (!_cambioCalled) moves.Add(GameCommand.CallCambio());
                    if (!_matchedThisTurn && _discard.Count > 0)
                    {
                        // Snap: any active card (either side) may be matched to the top discard.
                        foreach (var s in ActiveSlotsOf(PlayerSide)) moves.Add(GameCommand.Match(s));
                        foreach (var s in ActiveSlotsOf(AISide)) moves.Add(GameCommand.Match(s));
                    }
                }
                break;

            case GamePhase.CardDrawn:
                moves.Add(GameCommand.DiscardDrawn());
                foreach (var s in ActiveSlotsOf(ActiveSide))
                    moves.Add(GameCommand.SwapDrawnInto(s));
                break;

            case GamePhase.UsingPower:
                if (_awaitingPeekConfirm)
                {
                    moves.Add(GameCommand.FinishPeeking());
                    break;
                }

                switch (_powerStep)
                {
                    case PowerStep.LookingOwn:
                    case PowerStep.SelectingPowerSwapSource:
                    case PowerStep.SelectingTradeOwn:
                        foreach (var s in ActiveSlotsOf(ActiveSide))
                            moves.Add(GameCommand.UsePowerOn(s));
                        break;

                    case PowerStep.LookingOpponent:
                    case PowerStep.SelectingPowerSwapTarget:
                    case PowerStep.SelectingTradeOpponent:
                        foreach (var s in ActiveSlotsOf(OpponentSide))
                            moves.Add(GameCommand.UsePowerOn(s));
                        break;

                    case PowerStep.ConfirmingTrade:
                        moves.Add(GameCommand.ConfirmTrade());
                        break;
                }
                break;
        }
        return moves;
    }

    private bool CanDraw() => _drawPile.Count > 0;

    /// <summary>The single mutation path. Dispatches to the matching Do*, which validates
    /// and either applies (returning true + effects) or rejects (returning false).</summary>
    public MoveResult Apply(GameCommand cmd)
    {
        var fx = new List<GameEffect>();
        bool ok = cmd.Type switch
        {
            CommandType.DrawFromDeck      => DoDraw(fx),
            CommandType.DiscardDrawn      => DoDiscardDrawn(fx),
            CommandType.SwapDrawnIntoSlot => DoSwapDrawn(cmd.Slot, fx),
            CommandType.UsePowerOnSlot    => DoUsePower(cmd.Slot, fx),
            CommandType.AttemptMatch      => DoAttemptMatch(cmd.Slot, fx),
            CommandType.GiveCard          => DoGiveCard(cmd.Slot, fx),
            CommandType.ConfirmTrade      => DoConfirmTrade(fx),
            CommandType.FinishPeeking     => DoFinishPeeking(fx),
            CommandType.CallCambio        => DoCallCambio(fx),
            _ => false
        };
        return new MoveResult { Ok = ok, Effects = fx };
    }

    private bool DoDraw(List<GameEffect> fx)
    {
        if (_phase != GamePhase.DrawingCard || _awaitingGiveCard) return false;

        Card card = DrawCard();
        if (card.IsNone) return false;

        _drawn = card;
        _phase = GamePhase.CardDrawn;

        fx.Add(new GameEffect
        {
            Kind = EffectKind.CardDrawn,
            Slot = new SlotRef(ActiveSide, Zone.Hand, -1),
            Card = card,
            Success = _isPlayerTurn
        });
        return true;
    }

    private bool DoDiscardDrawn(List<GameEffect> fx)
    {
        if (_phase != GamePhase.CardDrawn || _drawn.IsNone) return false;

        // Discarding a card whose rank matches the top discard counts as a match: no power, end turn.
        Card top = TopDiscard;
        if (!top.IsNone && _drawn.Number == top.Number)
        {
            Card matched = _drawn;
            Discard(matched);
            _drawn = Card.None;
            _activePower = CardPower.None;

            fx.Add(new GameEffect
            {
                Kind = EffectKind.MatchResolved,
                Slot = SlotRef.None,       // the drawn card lives in no slot
                Card = matched,
                Success = true,
                ByPlayer = _isPlayerTurn
            });

            EndTurn(fx);
            return true;
        }
        
        Card discarded = _drawn;
        Discard(discarded);
        _activePower = discarded.Power;
        _drawn = Card.None;

        fx.Add(new GameEffect
        {
            Kind = EffectKind.DrawnDiscarded,
            Card = discarded,
            Success = _isPlayerTurn
        });

        if (_activePower != CardPower.None) BeginPower(_activePower);
        else EndTurn(fx);

        return true;
    }

    private bool DoSwapDrawn(SlotRef s, List<GameEffect> fx)
    {
        if (_phase != GamePhase.CardDrawn && _phase != GamePhase.SelectingSwapSlot) return false;
        if (s.Side != ActiveSide || !IsActive(s)) return false;

        Card displaced = GetCard(s);
        Card placed = _drawn;

        SetCard(s, _drawn);
        Discard(displaced);
        _drawn = Card.None;

        fx.Add(new GameEffect
        {
            Kind = EffectKind.SlotsSwapped,
            Slot = s,
            Slot2 = SlotRef.None,
            Card = placed,
            Card2 = displaced,
        });

        EndTurn(fx);
        return true;
    }

    private bool DoUsePower(SlotRef s, List<GameEffect> fx)
    {
        if (_phase != GamePhase.UsingPower || _awaitingPeekConfirm) return false;
        if (!IsActive(s)) return false;

        switch (_powerStep)
        {
            case PowerStep.LookingOwn:
                if (s.Side != ActiveSide) return false;
                _awaitingPeekConfirm = true;
                fx.Add(Reveal(s));
                return true;

            case PowerStep.LookingOpponent:
                if (s.Side != OpponentSide) return false;
                _awaitingPeekConfirm = true;
                fx.Add(Reveal(s));
                return true;

            case PowerStep.SelectingPowerSwapSource:
                if (s.Side != ActiveSide) return false;
                _powerSource = s;
                _powerStep = PowerStep.SelectingPowerSwapTarget;
                return true;

            case PowerStep.SelectingPowerSwapTarget:
                if (s.Side != OpponentSide) return false;
                SwapSlots(_powerSource, s, fx);
                _powerSource = SlotRef.None;
                EndTurn(fx);
                return true;

            case PowerStep.SelectingTradeOpponent:
                if (s.Side != OpponentSide) return false;
                _tradeOpponent = s;
                _powerStep = PowerStep.SelectingTradeOwn;
                return true;

            case PowerStep.SelectingTradeOwn:
                if (s.Side != ActiveSide) return false;
                _tradeOwn = s;
                _powerStep = PowerStep.ConfirmingTrade;
                fx.Add(new GameEffect
                {
                    Kind = EffectKind.InformedTradeReady,
                    Slot = _tradeOpponent,
                    Slot2 = _tradeOwn,
                    Card = GetCard(_tradeOpponent),
                    Card2 = GetCard(_tradeOwn)
                });
                return true;
        }
        return false;
    }

    private bool DoAttemptMatch(SlotRef s, List<GameEffect> fx)
    {
        if (_phase != GamePhase.DrawingCard || _matchedThisTurn || _awaitingGiveCard) return false;
        if (!IsActive(s)) return false;
        Card top = TopDiscard;
        if (top.IsNone) return false;

        Card c = GetCard(s);
        bool success = c.Number == top.Number;

        if (!success)
        {
            _matchedThisTurn = true;
            ApplyPenalty(ActiveSide, fx);
            fx.Add(new GameEffect { Kind = EffectKind.MatchResolved, Slot = s, Card = c, Success = false, ByPlayer = _isPlayerTurn });
            return true;
        }

        _matchedThisTurn = true;
        bool matchersOwn = s.Side == ActiveSide;
        Discard(c);
        SetCard(s, Card.None);
        fx.Add(new GameEffect { Kind = EffectKind.MatchResolved, Slot = s, Card = c, Success = true, ByPlayer = _isPlayerTurn });

        if (!matchersOwn)
        {
            // Matched an opponent card -> you must hand one of yours into the gap.
            _awaitingGiveCard = true;
            _giverSide = ActiveSide;
            _matchReceiver = s;
        }
        return true;
    }

    private bool DoGiveCard(SlotRef s, List<GameEffect> fx)
    {
        if (!_awaitingGiveCard) return false;
        int giver = _giverSide >= 0 ? _giverSide : ActiveSide;
        if (s.Side != giver || !IsActive(s)) return false;

        Card given = GetCard(s);
        SetCard(s, Card.None);
        SetCard(_matchReceiver, given);

        var receiver = _matchReceiver;
        _awaitingGiveCard = false;
        _matchReceiver = SlotRef.None;
        _giverSide = -1;

        fx.Add(new GameEffect { Kind = EffectKind.SlotsSwapped, Slot = s, Slot2 = receiver });
        fx.Add(new GameEffect { Kind = EffectKind.GiveDone });
        
        
        return true;
    }

    private bool DoConfirmTrade(List<GameEffect> fx)
    {
        if (_phase != GamePhase.UsingPower || _powerStep != PowerStep.ConfirmingTrade) return false;
        SwapSlots(_tradeOpponent, _tradeOwn, fx);
        _tradeOpponent = SlotRef.None;
        _tradeOwn = SlotRef.None;
        EndTurn(fx);
        return true;
    }

    private bool DoFinishPeeking(List<GameEffect> fx)
    {
        if (_phase != GamePhase.UsingPower || !_awaitingPeekConfirm) return false;
        EndTurn(fx);
        return true;
    }

    private bool DoCallCambio(List<GameEffect> fx)
    {
        if (_phase != GamePhase.DrawingCard || _awaitingGiveCard || _cambioCalled) return false;
        _cambioCalled = true;
        _cambioCallerSide = ActiveSide;
        _finalRoundTurnsLeft = 1;
        _phase = GamePhase.CambioCalled;
        EndTurn(fx);
        return true;
    }

    // Shared mechanics
    private void BeginPower(CardPower power)
    {
        _powerStep = power switch
        {
            CardPower.LookOwnCard      => PowerStep.LookingOwn,
            CardPower.LookOpponentCard => PowerStep.LookingOpponent,
            CardPower.BlindSwap        => PowerStep.SelectingPowerSwapSource,
            CardPower.LookAndSwap      => PowerStep.SelectingTradeOpponent,
            _                          => PowerStep.None
        };
        _phase = GamePhase.UsingPower;
    }

    private void EndTurn(List<GameEffect> fx)
    {
        _drawn = Card.None;
        _activePower = CardPower.None;
        _powerStep = PowerStep.None;
        _matchedThisTurn = false;
        _awaitingGiveCard = false;
        _awaitingPeekConfirm = false;
        _matchReceiver = SlotRef.None;
        _powerSource = SlotRef.None;
        _tradeOpponent = SlotRef.None;
        _tradeOwn = SlotRef.None;

        if (_cambioCalled && ActiveSide != _cambioCallerSide)
        {
            _finalRoundTurnsLeft--;
            if (_finalRoundTurnsLeft <= 0)
            {
                _phase = GamePhase.GameOver;
                fx.Add(new GameEffect { Kind = EffectKind.GameOver });
                return;
            }
        }

        _isPlayerTurn = !_isPlayerTurn;
        _phase = GamePhase.DrawingCard;

        // End on an empty draw pile only before the player's turn, so both sides get the
        // same number of turns (the player always moves first). Not a reshuffle.
        if (_drawPile.Count == 0 && _isPlayerTurn)
        {
            _phase = GamePhase.GameOver;
            fx.Add(new GameEffect { Kind = EffectKind.GameOver });
        }
    }

    private void SwapSlots(SlotRef a, SlotRef b, List<GameEffect> fx)
    {
        Card ca = GetCard(a);
        SetCard(a, GetCard(b));
        SetCard(b, ca);
        fx.Add(new GameEffect { Kind = EffectKind.SlotsSwapped, Slot = a, Slot2 = b });
    }

    private void ApplyPenalty(int side, List<GameEffect> fx)
    {
        int idx = -1;
        for (int i = 0; i < _penalty[side].Length; i++)
            if (_penalty[side][i].IsNone) { idx = i; break; }
        if (idx < 0) return;

        Card pen = DrawCard();
        if (pen.IsNone) return;

        _penalty[side][idx] = pen;
        fx.Add(new GameEffect
        {
            Kind = EffectKind.PenaltyAdded,
            Slot = new SlotRef(side, Zone.Penalty, idx),
            Card = pen,
            Success = side == PlayerSide
        });
    }

    private GameEffect Reveal(SlotRef s) => new GameEffect
    {
        Kind = EffectKind.SlotRevealed,
        Slot = s,
        Card = GetCard(s),
        Success = _isPlayerTurn // the active side is the one learning the card
    };

    // Deck plumbing. There is currently no reshuffle: an empty pile is handled by the
    // legal-move gates (CanDraw) and the end-of-turn terminal check in EndTurn.
    private Card DrawNoReshuffle()
    {
        if (_drawPile.Count == 0) return Card.None;
        Card c = _drawPile[_drawPile.Count - 1];
        _drawPile.RemoveAt(_drawPile.Count - 1);
        return c;
    }

    private Card DrawCard() => DrawNoReshuffle();
    

    private void Discard(Card c)
    {
        if (!c.IsNone) _discard.Add(c);
    }

    // Determinization support (used by the AI search)

    /// <summary>All card ids not known, not in the discard, and not the currently drawn card
    /// — i.e. the pool the AI may sample hidden slots and the draw pile from.</summary>
    public List<int> UnseenCardIds(ICollection<int> knowIds)
    {
        var cardsUsed = new HashSet<int>(knowIds);
        foreach (var cardId in _discard) cardsUsed.Add(cardId.Id);
        if (!_drawn.IsNone) cardsUsed.Add(_drawn.Id);

        var result = new List<int>(Card.DeckSize);
        for (var i = 0; i < Card.DeckSize; i++)
            if (!cardsUsed.Contains(i)) result.Add(i);
        return result;
    }

    public void SetDrawPile(IReadOnlyList<int> orderIds)
    {
        _drawPile.Clear();
        for (var i = 0; i < orderIds.Count; i++)
            _drawPile.Add(new Card(orderIds[i]));
    }

    public void OverwriteHidden(IReadOnlyList<SlotRef> slotRefs, IReadOnlyList<int> cardIds)
    {
        for (var i = 0; i < slotRefs.Count; i++)
            SetCard(slotRefs[i], new Card(cardIds[i]));
    }

    // Scoring / queries
    public int Score(int side)
    {
        var total = _hand[side].Where(c => !c.IsNone).Sum(c => c.Value);
        total += _penalty[side].Where(c => !c.IsNone).Sum(c => c.Value);
        return total;
    }

    public int WinnerSide()
    {
        int p = Score(PlayerSide), a = Score(AISide);
        if (p < a) return PlayerSide;
        if (a < p) return AISide;
        return -1; // draw
    }

    /// <summary>Sanity check for determinization: every card id present exactly once.</summary>
    public bool IsCardSetWorking()
    {
        var seen = new HashSet<int>();
        bool ok = true;

        void Mark(Card card)
        {
            if (!card.IsNone && !seen.Add(card.Id)) ok = false;
        }

        foreach (var side in new[] { PlayerSide, AISide })
        {
            foreach (var c in _hand[side]) Mark(c);
            foreach (var c in _penalty[side]) Mark(c);
        }
        foreach (var c in _drawPile) Mark(c);
        foreach (var c in _discard) Mark(c);
        Mark(_drawn);

        return ok && seen.Count == Card.DeckSize;
    }
    
    public MoveResult TrySnap(int snapperSide, SlotRef s)
    {
        var fx = new List<GameEffect>();

        if (_phase != GamePhase.DrawingCard || _awaitingGiveCard) return new MoveResult { Ok = false, Effects = fx };
        if (!IsActive(s)) return new MoveResult { Ok = false, Effects = fx };
        Card top = TopDiscard;
        if (top.IsNone) return new MoveResult { Ok = false, Effects = fx };

        Card c = GetCard(s);
        bool success = c.Number == top.Number;

        if (!success)
        {
            ApplyPenalty(snapperSide, fx);
            fx.Add(new GameEffect { Kind = EffectKind.MatchResolved, Slot = s, Card = c, Success = false, ByPlayer = snapperSide == PlayerSide });
            return new MoveResult { Ok = true, Effects = fx };
        }

        bool snappersOwn = s.Side == snapperSide;
        Discard(c);
        SetCard(s, Card.None);
        fx.Add(new GameEffect { Kind = EffectKind.MatchResolved, Slot = s, Card = c, Success = true, ByPlayer = snapperSide == PlayerSide });

        if (!snappersOwn)
        {
            _awaitingGiveCard = true;
            _giverSide = snapperSide;
            _matchReceiver = s;
        }
        return new MoveResult { Ok = true, Effects = fx };
    }
}