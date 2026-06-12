using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// The bridge between the pure game core and Unity. It:
///   * owns the ONE authoritative GameState,
///   * exposes a single Submit path (SubmitPlayer / SubmitAI both funnel to Apply),
///   * fires C# events the view subscribes to (transient UI: reveals, draws, flashes...),
///   * reconciles slot visibility from state after every move (SyncViews), and
///   * drives the turn loop: after each move, if it's the AI's decision, ask the agent.
///
/// It deliberately holds NO rules. Rules live in GameState; player->command translation
/// lives in PlayerInput; AI->command lives in AICambioAgent.
/// </summary>
public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [Header("Layout")]
    [SerializeField] private int handSize = 4;
    [SerializeField] private Deck deck;

    [Header("Slot views (must match handSize / penalty counts)")]
    [SerializeField] private CardSlot[] playerSlots;
    [SerializeField] private CardSlot[] aiSlots;
    [SerializeField] private CardSlot[] playerPenaltySlots;
    [SerializeField] private CardSlot[] aiPenaltySlots;

    [Header("AI")]
    [SerializeField] private float aiThinkSeconds = 0.6f;

    public GameState State { get; private set; }
    public PlayerInput Player { get; private set; }
    public Deck Catalog => deck;
    public bool IsPlayerTurn => State != null && State.IsPlayerTurn;

    private IAgent _ai;

    // --- View events (the only thing GameUI needs to know about) ---
    public event Action<GamePhase, bool> OnPhaseChanged;          // phase, isPlayerTurn
    public event Action<Card> OnCardDrawn;                        // active side's drawn card
    public event Action<int, int, Card> OnSlotRevealed;          // side, index(+zone via lookup), card
    public event Action<int, int, Card, bool, bool> OnMatchResolved; // side, index, card, success, byPlayer
    public event Action<bool> OnAwaitingGiveCard;                 // byPlayer
    public event Action OnGiveCardDone;
    public event Action<Card, Card> OnInformedTradeReady;         // opponent card, own card
    public event Action<bool, int> OnPenaltyAdded;                // forPlayer, index
    public event Action OnSlotsSwapped;
    public event Action<int> OnGameOver;                          // winnerSide (-1 draw)
    public event Action<CommandType, bool> OnCommandApplied; 

    private void Awake()
    {
        Instance = this;
        Player = new PlayerInput(this);
    }

    private void Start()
    {
        int penaltyCount = playerPenaltySlots != null ? playerPenaltySlots.Length : 0;
        int seed = Environment.TickCount;

        State = new GameState(deck.BuildShuffledDeck(), handSize, penaltyCount, seed);

        InitSlotViews(playerSlots, GameState.PlayerSide, Zone.Hand);
        InitSlotViews(aiSlots, GameState.AISide, Zone.Hand);
        InitSlotViews(playerPenaltySlots, GameState.PlayerSide, Zone.Penalty);
        InitSlotViews(aiPenaltySlots, GameState.AISide, Zone.Penalty);

        _ai = new AICambioAgent(seed);
        _ai.OnNewGame(GameState.AISide, State);

        SyncViews();
        OnPhaseChanged?.Invoke(State.Phase, State.IsPlayerTurn); // -> GameUI shows opening peek
    }

    private void InitSlotViews(CardSlot[] slots, int side, Zone zone)
    {
        if (slots == null) return;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] != null) slots[i].Init(side, zone, i);
    }

    // ----------------------------------------------------------------------
    // Submit
    // ----------------------------------------------------------------------

    public void SubmitPlayer(GameCommand cmd)
    {
        if (!IsPlayerTurn) return;
        Submit(cmd);
    }

    public void SubmitAI(GameCommand cmd)
    {
        if (IsPlayerTurn) return;
        Submit(cmd);
    }

    private void Submit(GameCommand cmd)
    {
        if (State == null || State.IsTerminal) return;

        var prevPhase = State.Phase;
        var prevTurn = State.IsPlayerTurn;
        var prevStep = State.PowerStep;
        bool prevAwaitGive = State.AwaitingGiveCard;

        MoveResult result = State.Apply(cmd);
        if (!result.Ok) return;

        foreach (var fx in result.Effects)
        {
            DispatchEffect(fx);
            _ai?.Observe(fx, iAmActor: !prevTurn); // actor = side that was active before the move
        }

        if (State.Phase != prevPhase || State.IsPlayerTurn != prevTurn || State.PowerStep != prevStep)
            OnPhaseChanged?.Invoke(State.Phase, State.IsPlayerTurn);

        if (State.AwaitingGiveCard && !prevAwaitGive)
            OnAwaitingGiveCard?.Invoke(State.GiveByPlayer);
        
        OnCommandApplied?.Invoke(cmd.Type, prevTurn); 

        SyncViews();
        MaybePromptAI();
    }

    private void DispatchEffect(GameEffect fx)
    {
        switch (fx.Kind)
        {
            case EffectKind.CardDrawn:
                OnCardDrawn?.Invoke(fx.Card);
                break;
            case EffectKind.SlotRevealed:
                OnSlotRevealed?.Invoke(fx.Slot.Side, fx.Slot.Index, fx.Card);
                break;
            case EffectKind.MatchResolved:
                OnMatchResolved?.Invoke(fx.Slot.Side, fx.Slot.Index, fx.Card, fx.Success, fx.ByPlayer);
                break;
            case EffectKind.PenaltyAdded:
                OnPenaltyAdded?.Invoke(fx.Success, fx.Slot.Index);
                break;
            case EffectKind.InformedTradeReady:
                OnInformedTradeReady?.Invoke(fx.Card, fx.Card2);
                break;
            case EffectKind.GiveDone:
                OnGiveCardDone?.Invoke();
                break;
            case EffectKind.SlotsSwapped:
                OnSlotsSwapped?.Invoke();
                break;
            case EffectKind.GameOver:
                OnGameOver?.Invoke(State.WinnerSide());
                break;
        }
    }

    // ----------------------------------------------------------------------
    // Player-facing helpers (called by PlayerInput / GameUI)
    // ----------------------------------------------------------------------

    public void StartPlay()
    {
        if (State == null) return;
        State.StartPlay();
        OnPhaseChanged?.Invoke(State.Phase, State.IsPlayerTurn);
        MaybePromptAI();
    }

    /// <summary>Tell the view to show swap arrows. No state mutation: SwapDrawnIntoSlot is
    /// already legal in CardDrawn, so this is purely a cosmetic phase signal for the UI.</summary>
    public void EnterSwapSelection()
    {
        if (State == null || State.Phase != GamePhase.CardDrawn) return;
        OnPhaseChanged?.Invoke(GamePhase.SelectingSwapSlot, State.IsPlayerTurn);
    }

    public void SetSlotArmed(SlotRef s, bool armed)
    {
        var view = GetSlotView(s);
        if (view != null) view.SetArmed(armed);
    }

    public CardSlot GetSlotView(SlotRef s)
    {
        CardSlot[] arr = (s.Side, s.Zone) switch
        {
            (GameState.PlayerSide, Zone.Hand) => playerSlots,
            (GameState.AISide, Zone.Hand) => aiSlots,
            (GameState.PlayerSide, Zone.Penalty) => playerPenaltySlots,
            (GameState.AISide, Zone.Penalty) => aiPenaltySlots,
            _ => null
        };
        if (arr == null || s.Index < 0 || s.Index >= arr.Length) return null;
        return arr[s.Index];
    }

    /// <summary>The player's allowed opening peek: their first two hand cards.</summary>
    public (Card, Card) PeekInitialIds()
    {
        Card a = State.GetCard(new SlotRef(GameState.PlayerSide, Zone.Hand, 0));
        Card b = State.GetCard(new SlotRef(GameState.PlayerSide, Zone.Hand, 1));
        return (a, b);
    }

    public int GetScore(bool player) => State.Score(player ? GameState.PlayerSide : GameState.AISide);

    // ----------------------------------------------------------------------
    // View reconciliation + AI loop
    // ----------------------------------------------------------------------

    /// <summary>Make every slot view match the truth: visible iff it holds a card. Clears arming.</summary>
    private void SyncViews()
    {
        Reconcile(playerSlots, GameState.PlayerSide, Zone.Hand);
        Reconcile(aiSlots, GameState.AISide, Zone.Hand);
        Reconcile(playerPenaltySlots, GameState.PlayerSide, Zone.Penalty);
        Reconcile(aiPenaltySlots, GameState.AISide, Zone.Penalty);
    }

    private void Reconcile(CardSlot[] slots, int side, Zone zone)
    {
        if (slots == null) return;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == null) continue;
            bool active = State.IsActive(new SlotRef(side, zone, i));
            slots[i].SetVisible(active);
            slots[i].SetArmed(false);
        }
    }

    private void MaybePromptAI()
    {
        if (State.IsTerminal || State.IsPlayerTurn) return;
        if (!IsDecisionPhase(State.Phase)) return;

        GameCommand cmd = _ai.ChooseMove(State);
        StartCoroutine(SubmitAfterDelay(cmd, aiThinkSeconds));
    }

    private static bool IsDecisionPhase(GamePhase p) =>
        p == GamePhase.DrawingCard || p == GamePhase.CardDrawn ||
        p == GamePhase.SelectingSwapSlot || p == GamePhase.UsingPower;

    private IEnumerator SubmitAfterDelay(GameCommand cmd, float seconds)
    {
        yield return new WaitForSeconds(seconds);
        SubmitAI(cmd);
    }
}
