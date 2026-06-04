using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public enum GamePhase
{
    Dealing,
    DrawingCard,
    CardDrawn,
    SelectingSwapSlot,
    DiscardingDrawn,
    UsingPower,
    CambioCalled,
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

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [Header("Card References")]
    [SerializeField] private Deck deck;
    [SerializeField] private CardSlot[] playerSlots;
    [SerializeField] private CardSlot[] aiSlots;

    public GamePhase CurrentPhase { get; private set; }
    public bool IsPlayerTurn { get; private set; }
    public Card DrawnCard { get; private set; }
    public CardPower ActivePower { get; private set; }
    public PowerStep CurrentPowerStep { get; private set; }

    public bool CambioHasBeenCalled { get; private set; }
    public bool IsPlayerCambioCaller { get; private set; }
    public int FinalRoundTurnsLeft { get; private set; }

    public bool MatchedThisTurn => _matchedThisTurn;
    public bool AwaitingGiveCard => _awaitingGiveCard;

    public event Action<GamePhase, bool> OnPhaseChanged;
    public event Action<Card> OnCardDrawn;
    public event Action<CardSlot> OnSlotRevealed;
    public event Action<CardSlot, CardSlot> OnSlotsSwapped;
    public event Action<CardSlot, CardSlot> OnInformedTradeReady;
    public event Action<CardSlot, bool, bool> OnMatchResolved;
    public event Action<bool> OnAwaitingGiveCard;
    public event Action OnGiveCardDone;
    public event Action<bool, int> OnPenaltyAdded;

    private CardSlot _powerSourceSlot;
    private CardSlot _tradeOpponentSlot;
    private CardSlot _tradeOwnSlot;

    private bool _matchedThisTurn;
    private CardSlot _armedMatchSlot;
    private bool _awaitingGiveCard;
    private bool _giveByPlayer;
    private CardSlot _opponentMatchedSlot;

    private readonly List<Card> _playerPenalties = new();
    private readonly List<Card> _aiPenalties = new();

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        DealInitialHands();
    }

    public void SetPhase(GamePhase phase, bool playerTurn)
    {
        if (phase != GamePhase.DrawingCard) ClearArmedSlot();
        IsPlayerTurn = playerTurn;
        CurrentPhase = phase;
        OnPhaseChanged?.Invoke(phase, playerTurn);
    }

    public void DrawFromDeck()
    {
        if (CurrentPhase != GamePhase.DrawingCard) return;
        if (_awaitingGiveCard) return;
        DrawnCard = deck.DrawFromDeck();
        SetPhase(GamePhase.CardDrawn, IsPlayerTurn);
        OnCardDrawn?.Invoke(DrawnCard);
    }

    public void DrawFromDiscard()
    {
        if (CurrentPhase != GamePhase.DrawingCard) return;
        if (_awaitingGiveCard) return;
        DrawnCard = deck.DrawFromDiscard();
        if (DrawnCard == null) return;
        SetPhase(GamePhase.CardDrawn, IsPlayerTurn);
        OnCardDrawn?.Invoke(DrawnCard);
    }

    public void DiscardDrawnCard()
    {
        if (CurrentPhase != GamePhase.CardDrawn) return;
        deck.Discard(DrawnCard);
        ActivePower = DrawnCard.Power;
        SetPhase(GamePhase.DiscardingDrawn, IsPlayerTurn);

        if (ActivePower != CardPower.None)
            BeginPower(ActivePower);
        else
            EndTurn();
    }

    public void BeginSwapDrawnCard()
    {
        if (CurrentPhase != GamePhase.CardDrawn) return;
        SetPhase(GamePhase.SelectingSwapSlot, IsPlayerTurn);
    }

    public void OnSlotClicked(CardSlot slot)
    {
        switch (CurrentPhase)
        {
            case GamePhase.SelectingSwapSlot:
                HandleSwapDrawnIntoSlot(slot);
                break;

            case GamePhase.UsingPower:
                HandlePowerSlotSelection(slot);
                break;

            case GamePhase.DrawingCard:
                if (_awaitingGiveCard)
                    HandleGiveCardSelection(slot, true);
                else
                    HandleMatchClick(slot);
                break;
        }
    }

    public void CallCambio()
    {
        if (CurrentPhase != GamePhase.DrawingCard) return;
        if (_awaitingGiveCard) return;
        CambioHasBeenCalled = true;
        IsPlayerCambioCaller = IsPlayerTurn;
        FinalRoundTurnsLeft = 1;
        SetPhase(GamePhase.CambioCalled, IsPlayerTurn);
        EndTurn();
    }

    public void EndTurn()
    {
        DrawnCard = null;
        ActivePower = CardPower.None;
        CurrentPowerStep = PowerStep.None;

        _matchedThisTurn = false;
        _awaitingGiveCard = false;
        _opponentMatchedSlot = null;
        ClearArmedSlot();

        if (CambioHasBeenCalled)
        {
            FinalRoundTurnsLeft--;
            if (FinalRoundTurnsLeft <= 0)
            {
                SetPhase(GamePhase.GameOver, IsPlayerTurn);
                return;
            }
        }

        SetPhase(GamePhase.DrawingCard, !IsPlayerTurn);
    }

    public void FinishPeeking()
    {
        if (CurrentPhase != GamePhase.UsingPower) return;
        EndTurn();
    }

    public void ConfirmInformedTrade()
    {
        if (CurrentPhase != GamePhase.UsingPower || CurrentPowerStep != PowerStep.ConfirmingTrade)
            return;

        SwapSlots(_tradeOpponentSlot, _tradeOwnSlot);
        _tradeOpponentSlot = null;
        _tradeOwnSlot = null;
        EndTurn();
    }

    public void AttemptMatch(CardSlot slot, bool byPlayer)
    {
        ClearArmedSlot();

        if (CurrentPhase != GamePhase.DrawingCard) return;
        if (_matchedThisTurn || _awaitingGiveCard) return;
        if (slot == null || !slot.IsActive || slot.Card == null) return;
        if (deck.TopDiscard == null) return;

        bool success = slot.Card.displayNumber == deck.TopDiscard.displayNumber;

        if (!success)
        {
            // Failed: card stays exactly where it is, attempter just takes a penalty.
            ApplyPenalty(byPlayer);
            OnMatchResolved?.Invoke(slot, false, byPlayer);
            return;
        }

        _matchedThisTurn = true;
        bool matchersOwn = slot.BelongsToPlayer == byPlayer;
        deck.Discard(slot.Card);

        if (matchersOwn)
        {
            // Matched your own card -> it leaves play, you have one fewer card.
            slot.SetInactive();
            OnMatchResolved?.Invoke(slot, true, byPlayer);
        }
        else
        {
            // Matched opponent's card -> theirs leaves play, you must hand one of
            // yours into the now-empty slot.
            _opponentMatchedSlot = slot;
            _awaitingGiveCard = true;
            _giveByPlayer = byPlayer;
            OnMatchResolved?.Invoke(slot, true, byPlayer);
            OnAwaitingGiveCard?.Invoke(byPlayer);
        }
    }

    public void GiveCardToOpponent(CardSlot giverSlot)
    {
        if (!_awaitingGiveCard || giverSlot == null) return;
        if (giverSlot.BelongsToPlayer != _giveByPlayer || !giverSlot.IsActive) return;

        Card given = giverSlot.Card;
        giverSlot.SetInactive();
        _opponentMatchedSlot.SetCard(given);

        CardSlot receiver = _opponentMatchedSlot;
        _awaitingGiveCard = false;
        _opponentMatchedSlot = null;

        OnSlotsSwapped?.Invoke(giverSlot, receiver);
        OnGiveCardDone?.Invoke();
    }

    private void HandleMatchClick(CardSlot slot)
    {
        if (_matchedThisTurn) return;
        if (deck.TopDiscard == null) return;
        if (slot == null || !slot.IsActive || slot.Card == null) return; 

        if (_armedMatchSlot == null)
        {
            _armedMatchSlot = slot;
            slot.SetArmed(true);
        }
        else if (_armedMatchSlot == slot)
        {
            AttemptMatch(slot, true);
        }
        else
        {
            ClearArmedSlot();
        }
    }

    private void HandleGiveCardSelection(CardSlot slot, bool byPlayer)
    {
        if (!byPlayer || slot == null) return;
        if (slot.BelongsToPlayer != _giveByPlayer || !slot.IsActive) return;
        GiveCardToOpponent(slot);
    }

    private void ApplyPenalty(bool forPlayer)
    {
        List<Card> list = forPlayer ? _playerPenalties : _aiPenalties;
        if (list.Count >= 4) return;
        Card pen = deck.DrawFromDeck();
        if (pen == null) return;
        list.Add(pen);
        OnPenaltyAdded?.Invoke(forPlayer, list.Count - 1);
    }

    private void ClearArmedSlot()
    {
        if (_armedMatchSlot == null) return;
        _armedMatchSlot.SetArmed(false);
        _armedMatchSlot = null;
    }

    public int GetScore(bool forPlayer)
    {
        CardSlot[] slots = forPlayer ? playerSlots : aiSlots;
        int total = 0;
        foreach (var slot in slots)
            if (slot.IsActive) total += slot.Card.Value;

        List<Card> pens = forPlayer ? _playerPenalties : _aiPenalties;
        foreach (var c in pens) total += c.Value;
        return total;
    }

    public CardSlot[] GetPlayerSlots() => playerSlots;
    public CardSlot[] GetAISlots() => aiSlots;
    public Deck Getdeck() => deck;

    private void DealInitialHands()
    {
        for (int i = 0; i < playerSlots.Length; i++)
            playerSlots[i].Assign(deck.DrawFromDeck(), i, true);

        for (int i = 0; i < aiSlots.Length; i++)
            aiSlots[i].Assign(deck.DrawFromDeck(), i, false);

        SetPhase(GamePhase.Dealing, true);
    }

    private void HandleSwapDrawnIntoSlot(CardSlot slot)
    {
        if (!IsOwnSlot(slot)) return;
        Card displaced = slot.SwapCard(DrawnCard);
        deck.Discard(displaced);
        EndTurn();
    }

    private void BeginPower(CardPower power)
    {
        CurrentPowerStep = power switch
        {
            CardPower.LookOwnCard      => PowerStep.LookingOwn,
            CardPower.LookOpponentCard => PowerStep.LookingOpponent,
            CardPower.BlindSwap        => PowerStep.SelectingPowerSwapSource,
            CardPower.LookAndSwap      => PowerStep.SelectingTradeOpponent,
            _                          => PowerStep.None
        };

        SetPhase(GamePhase.UsingPower, IsPlayerTurn);
    }

    private void HandlePowerSlotSelection(CardSlot slot)
    {
        if (slot == null || !slot.IsActive || slot.Card == null) return;
        
        switch (CurrentPowerStep)
        {
            case PowerStep.LookingOwn:
                if (!IsOwnSlot(slot)) return;
                OnSlotRevealed?.Invoke(slot);
                break;

            case PowerStep.LookingOpponent:
                if (!IsOpponentSlot(slot)) return;
                OnSlotRevealed?.Invoke(slot);
                break;

            case PowerStep.SelectingPowerSwapSource:
                if (!IsOwnSlot(slot)) return;
                _powerSourceSlot = slot;
                CurrentPowerStep = PowerStep.SelectingPowerSwapTarget;
                SetPhase(GamePhase.UsingPower, IsPlayerTurn);
                break;

            case PowerStep.SelectingPowerSwapTarget:
                if (!IsOpponentSlot(slot)) return;
                SwapSlots(_powerSourceSlot, slot);
                _powerSourceSlot = null;
                EndTurn();
                break;

            case PowerStep.SelectingTradeOpponent:
                if (!IsOpponentSlot(slot)) return;
                _tradeOpponentSlot = slot;
                CurrentPowerStep = PowerStep.SelectingTradeOwn;
                SetPhase(GamePhase.UsingPower, IsPlayerTurn);
                break;

            case PowerStep.SelectingTradeOwn:
                if (!IsOwnSlot(slot)) return;
                _tradeOwnSlot = slot;
                CurrentPowerStep = PowerStep.ConfirmingTrade;
                OnInformedTradeReady?.Invoke(_tradeOpponentSlot, _tradeOwnSlot);
                SetPhase(GamePhase.UsingPower, IsPlayerTurn);
                break;
        }
    }

    private void SwapSlots(CardSlot a, CardSlot b)
    {
        Card temp = a.SwapCard(b.Card);
        b.SwapCard(temp);
        OnSlotsSwapped?.Invoke(a, b);
    }

    private bool IsOwnSlot(CardSlot slot) => slot.BelongsToPlayer == IsPlayerTurn;
    private bool IsOpponentSlot(CardSlot slot) => slot.BelongsToPlayer != IsPlayerTurn;

    private void Update()
    {
        if (Keyboard.current.rKey.isPressed)
            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }
}