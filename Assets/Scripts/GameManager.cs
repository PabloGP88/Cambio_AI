using System;
using UnityEngine;

public enum GamePhase
{
    Dealing,            // Cards being distributed, player peeks 2
    DrawingCard,        // Active player must draw (from deck or discard)
    CardDrawn,          // Card in hand — choose to discard or swap into a slot
    SelectingSwapSlot,  // Player chose to swap — now picking which slot
    DiscardingDrawn,    // Card going to discard — power triggers if applicable
    UsingPower,         // Resolving a card power (look/swap)
    CambioCalled,       // Cambio declared — each other player gets one last turn
    GameOver
}

// Describes which step within a two-step power we're on (e.g. LookAndSwap: look first, then optionally swap)
public enum PowerStep { None, LookingOwn, LookingOpponent, SelectingPowerSwapSource, SelectingPowerSwapTarget }

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

    // Who called Cambio and how many turns remain in the final round
    public bool CambioHasBeenCalled { get; private set; }
    public bool IsPlayerCambioCaller { get; private set; }
    public int FinalRoundTurnsLeft { get; private set; }

    // UI and AI subscribe to these
    public event Action<GamePhase, bool> OnPhaseChanged;   // phase, isPlayerTurn
    public event Action<Card> OnCardDrawn;                 // the drawn card
    public event Action<CardSlot> OnSlotRevealed;          // a slot was peeked
    public event Action<CardSlot, CardSlot> OnSlotsSwapped; // two slots were swapped

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        DealInitialHands();
    }

    // -------------------------------------------------------------------------
    // Public API — both the player (via UI clicks) and the AI call these
    // -------------------------------------------------------------------------

    public void SetPhase(GamePhase phase, bool playerTurn)
    {
        IsPlayerTurn = playerTurn;
        CurrentPhase = phase;
        OnPhaseChanged?.Invoke(phase, playerTurn);
    }

    // Draw from the face-down deck
    public void DrawFromDeck()
    {
        print(CurrentPhase);
        if (CurrentPhase != GamePhase.DrawingCard) return;
        DrawnCard = deck.DrawFromDeck();
        SetPhase(GamePhase.CardDrawn, IsPlayerTurn);
        OnCardDrawn?.Invoke(DrawnCard);
    }

    // Draw the top card of the discard pile
    public void DrawFromDiscard()
    {
        if (CurrentPhase != GamePhase.DrawingCard) return;
        DrawnCard = deck.DrawFromDiscard();
        if (DrawnCard == null) return;
        SetPhase(GamePhase.CardDrawn, IsPlayerTurn);
        OnCardDrawn?.Invoke(DrawnCard);
    }

    // Discard the drawn card (and trigger its power if it has one)
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

    // Announce intent to swap the drawn card into a hand slot
    public void BeginSwapDrawnCard()
    {
        if (CurrentPhase != GamePhase.CardDrawn) return;
        SetPhase(GamePhase.SelectingSwapSlot, IsPlayerTurn);
    }

    // Slot clicked — meaning depends on current phase
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
        }
    }

    // Call Cambio instead of drawing — only at the start of your turn
    public void CallCambio()
    {
        if (CurrentPhase != GamePhase.DrawingCard) return;
        CambioHasBeenCalled = true;
        IsPlayerCambioCaller = IsPlayerTurn;
        // Each opponent gets one more turn; for a 2-player game that is 1 turn
        FinalRoundTurnsLeft = 1;
        SetPhase(GamePhase.CambioCalled, IsPlayerTurn);
        EndTurn();
    }

    public void EndTurn()
    {
        DrawnCard = null;
        ActivePower = CardPower.None;
        CurrentPowerStep = PowerStep.None;

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

    // -------------------------------------------------------------------------
    // Scoring
    // -------------------------------------------------------------------------

    public int GetScore(bool forPlayer)
    {
        CardSlot[] slots = forPlayer ? playerSlots : aiSlots;
        int total = 0;
        foreach (var slot in slots) total += slot.Card.Value;
        return total;
    }

    // -------------------------------------------------------------------------
    // Slot accessors for AI
    // -------------------------------------------------------------------------

    public CardSlot[] GetPlayerSlots() => playerSlots;
    public CardSlot[] GetAISlots() => aiSlots;

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

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
        // Only allow swapping into your own slots
        if (IsPlayerTurn && !slot.BelongsToPlayer) return;
        if (!IsPlayerTurn && slot.BelongsToPlayer) return;

        Card displaced = slot.SwapCard(DrawnCard);
        deck.Discard(displaced);
        // Swapped-in card powers do NOT trigger — only discarded cards trigger powers
        EndTurn();
    }

    private void BeginPower(CardPower power)
    {
        SetPhase(GamePhase.UsingPower, IsPlayerTurn);

        CurrentPowerStep = power switch
        {
            CardPower.LookOwnCard => PowerStep.LookingOwn,
            CardPower.LookOpponentCard => PowerStep.LookingOpponent,
            CardPower.BlindSwap => PowerStep.SelectingPowerSwapSource,
            CardPower.LookAndSwap => PowerStep.LookingOpponent, // look first
            _ => PowerStep.None
        };
    }

    // Tracks the first slot selected during a two-slot power
    private CardSlot powerSourceSlot;

    private void HandlePowerSlotSelection(CardSlot slot)
    {
        switch (CurrentPowerStep)
        {
            case PowerStep.LookingOwn:
                // Reveal one of your own slots
                if (IsPlayerTurn && !slot.BelongsToPlayer) return;
                if (!IsPlayerTurn && slot.BelongsToPlayer) return;
                OnSlotRevealed?.Invoke(slot);
                EndTurn();
                break;

            case PowerStep.LookingOpponent:
                // Reveal one opponent slot
                if (IsPlayerTurn && slot.BelongsToPlayer) return;
                if (!IsPlayerTurn && !slot.BelongsToPlayer) return;
                OnSlotRevealed?.Invoke(slot);
                // LookAndSwap gets to swap after looking
                if (ActivePower == CardPower.LookAndSwap)
                {
                    powerSourceSlot = slot;
                    CurrentPowerStep = PowerStep.SelectingPowerSwapTarget;
                }
                else
                {
                    EndTurn();
                }
                break;

            case PowerStep.SelectingPowerSwapSource:
                powerSourceSlot = slot;
                CurrentPowerStep = PowerStep.SelectingPowerSwapTarget;
                break;

            case PowerStep.SelectingPowerSwapTarget:
                // Swap the two selected slots
                Card temp = powerSourceSlot.SwapCard(slot.Card);
                slot.SwapCard(temp);
                OnSlotsSwapped?.Invoke(powerSourceSlot, slot);
                powerSourceSlot = null;
                EndTurn();
                break;
        }
    }

    public Deck Getdeck()
    {
        return deck;
    }
}