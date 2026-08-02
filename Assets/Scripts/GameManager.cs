using System;
using System.Collections;
using UnityEngine;

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
    [Tooltip("On = belief-weighted sampling. Off = plain ISMCTS.")]
    [SerializeField] private bool aiUseBayesianLayer = true;
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
    public event Action<CommandType, bool, int> OnCommandApplied;
    public event Action<GameEffect, int> OnEffectApplied;

    private bool _aiRoutineActive;
    private bool _aiReactiveSnapUsed;   // AI may reactive-snap at most once per opponent turn
    public event Action<IsmctsReport> OnAiSearchDecision;  // once, when a move has been chosen
    public event Action<string> OnAiNarration; // Tell player whats going on
    public event Action<BeliefReport> OnAiBeliefSnapshot;
    public bool AiUsesBayesian => aiUseBayesianLayer;

    private struct Pre
    {
        public GamePhase Phase; 
        public bool Turn;
        public PowerStep Step; 
        public bool AwaitGive;
    }
    private Pre Capture() => new Pre
    {
        Phase = State.Phase, 
        Turn = State.IsPlayerTurn,
        Step = State.PowerStep, 
        AwaitGive = State.AwaitingGiveCard
    };
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

        _ai = new AICambioAgent(seed)
        {
            UseBayesianLayer = aiUseBayesianLayer
        };
        _ai.OnSearchDecision += HandleAiSearchDecision;
        _ai.OnNewGame(GameState.AISide, State);
        
        if (_ai is AICambioAgent concrete)
            concrete.OnBeliefSnapshot += r => OnAiBeliefSnapshot?.Invoke(r);
        
        SyncViews();
        OnPhaseChanged?.Invoke(State.Phase, State.IsPlayerTurn); // -> GameUI shows opening peek
    }

    private void InitSlotViews(CardSlot[] slots, int side, Zone zone)
    {
        if (slots == null) return;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] != null) slots[i].Init(side, zone, i);
    }

    private void Commit(MoveResult result, Pre pre, CommandType cmdType, int actorSide)
    {
        if (!result.Ok) return;

        foreach (var fx in result.Effects)
        {
            DispatchEffect(fx);
            _ai?.Observe(fx, iAmActor: actorSide == GameState.AISide);
            OnEffectApplied?.Invoke(fx, actorSide);  // Keep track who applied for csv graphs
        }

        if (State.IsPlayerTurn != pre.Turn)
            _aiReactiveSnapUsed = false;   // new turn -> the AI may reactive-snap once again

        if (State.Phase != pre.Phase || State.IsPlayerTurn != pre.Turn || State.PowerStep != pre.Step)
            OnPhaseChanged?.Invoke(State.Phase, State.IsPlayerTurn);

        if (State.AwaitingGiveCard && !pre.AwaitGive)
            OnAwaitingGiveCard?.Invoke(State.GiveByPlayer);

        OnCommandApplied?.Invoke(cmdType, pre.Turn, actorSide);

        if (actorSide == GameState.AISide)
        {
            string say = AiNarrator.Describe(cmdType, result.Effects, State);
            OnAiNarration?.Invoke(say);
        }

        SyncViews();
        MaybePromptAI();
        MaybePromptAiSnap();
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

    // ReSharper disable Unity.PerformanceAnalysis
    private void Submit(GameCommand cmd)
    {
        if (State == null || State.IsTerminal) return;
        int actorSide = State.ActiveSide;
        var pre = Capture();
        Commit(State.Apply(cmd), pre, cmd.Type, actorSide);
    }

    public void SubmitSnap(int snapperSide, SlotRef slot)
    {
        if (State == null || State.IsTerminal) return;
        var pre = Capture();
        Commit(State.TrySnap(snapperSide, slot), pre, CommandType.AttemptMatch, snapperSide);
    }

    public void SubmitGiveOutOfTurn(int giverSide, SlotRef slot)
    {
        if (State == null || State.IsTerminal) return;
        var pre = Capture();
        Commit(State.Apply(GameCommand.Give(slot)), pre, CommandType.GiveCard, giverSide);
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
        Player?.ClearArmed(); 
        
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
        if (State.AwaitingGiveCard && State.GiveByPlayer) return; // human owes a give from an out-of-turn snap
        if (!IsDecisionPhase(State.Phase)) return;
        if (_aiRoutineActive) return;
        
        
        StartCoroutine(RunAiTurn());

    }
    
    private void MaybePromptAiSnap()
    {
        if (State == null || State.IsTerminal || !State.IsPlayerTurn) return;
        if (State.Phase != GamePhase.DrawingCard || State.AwaitingGiveCard) return;
        if (_aiRoutineActive || _aiReactiveSnapUsed) return;   // one reactive snap per opponent turn
        if (_ai is AICambioAgent agent)
        {
            SlotRef s = agent.SnapOwn(State);
            if (!s.IsNone)
            {
                _aiReactiveSnapUsed = true;
                StartCoroutine(AiSnapRoutine(s));
            }
        }
    }

    private IEnumerator AiSnapRoutine(SlotRef s)
    {
        _aiRoutineActive = true;
        yield return new WaitForSeconds(aiThinkSeconds);
        _aiRoutineActive = false;
        if (State.IsTerminal || State.Phase != GamePhase.DrawingCard || State.AwaitingGiveCard) yield break;
        SubmitSnap(GameState.AISide, s);
    }

    private static bool IsDecisionPhase(GamePhase p) =>
        p == GamePhase.DrawingCard || p == GamePhase.CardDrawn ||
        p == GamePhase.SelectingSwapSlot || p == GamePhase.UsingPower;

    // ReSharper disable Unity.PerformanceAnalysis
    /// <summary>Drives the AI's decision as a coroutine. The search itself runs in one
    /// shot (no mid-search reporting), so this mainly exists to keep the IAgent contract
    /// uniform and to let aiThinkSeconds pace the reveal after the move is decided.</summary>
    private IEnumerator RunAiTurn()
    {
        _aiRoutineActive = true;
        
        GameCommand chosen = default;
        bool done = false;

        IEnumerator routine = _ai.ChooseMoveRoutine(State, cmd => { chosen = cmd; done = true; });
        while (routine.MoveNext())
        {
            yield return routine.Current;
        }
            
        yield return new WaitForSeconds(aiThinkSeconds);
        _aiRoutineActive = false;
        
        if (!done) yield break; // shouldn't happen, but never submit garbage

        if (!done || State.IsTerminal || State.IsPlayerTurn) yield break;
        if (State.AwaitingGiveCard && State.GiveByPlayer) yield break;      // human still owes a give

        var legalNow = State.LegalMoves();
        if (legalNow.Count == 0) yield break;      // nothing the AI can legally do — don't spin
        if (!legalNow.Contains(chosen))
        {
            MaybePromptAI(); yield break;           // state changed under us; re-decide once
        }

        SubmitAI(chosen);
    }

    private void HandleAiSearchDecision(IsmctsReport report) => OnAiSearchDecision?.Invoke(report);
    
    public void RevealAllHands()
    {
        if (State == null) return;
    
        RevealSideFaces(playerSlots, GameState.PlayerSide);
        RevealSideFaces(aiSlots, GameState.AISide);
    }

    private void RevealSideFaces(CardSlot[] slots, int side)
    {
        if (slots == null) return;
        
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == null) continue;
            
            var s = new SlotRef(side, Zone.Hand, i);
            if (!State.IsActive(s)) continue;
            slots[i].RevealFace(deck.SpriteFor(State.GetCard(s)));
        }
    }
}