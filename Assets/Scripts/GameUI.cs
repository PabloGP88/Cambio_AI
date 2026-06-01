
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public class GameUI : MonoBehaviour
{
    [Header("Turn UI")]
    [SerializeField] private GameObject turnUI;
    [SerializeField] private Image turnLabel;
    [SerializeField] private TextMeshProUGUI turnText;
    [SerializeField] private Color playerTurnColor;
    [SerializeField] private Color aiTurnColor;

    [Header("Initial Peek UI")]
    [SerializeField] private GameObject initialCardsView;
    [SerializeField] private Image peekCardLeft;
    [SerializeField] private Image peekCardRight;

    [Header("Drawn Card UI")]
    [SerializeField] private GameObject drawnCardView;
    [SerializeField] private Image drawnCardImage;
    
    [Header("Slot Revealed UI")]
    [SerializeField] private GameObject slotRevealedUI;
    [SerializeField] private Image slotRevealedImage;

    [Header("Discard Pile UI")]
    [SerializeField] private Image discardTopImage;
    [SerializeField] private Sprite emptyDiscardSprite;

    [Header("Game Over UI")]
    [SerializeField] private GameObject gameOverPanel;
    [SerializeField] private TextMeshProUGUI gameOverText;

    private GameManager gm;
    private Deck deck;

    void Start()
    {
        gm = GameManager.Instance;
        deck = gm.Getdeck();
        gm.OnPhaseChanged += HandlePhaseChanged;
        gm.OnCardDrawn += HandleCardDrawn;
        gm.OnSlotRevealed += HandleSlotRevealed;
        gm.OnSlotsSwapped += HandleSlotsSwapped;

        ShowInitialPeek();
    }

    void OnDestroy()
    {
        if (gm == null) return;
        gm.OnPhaseChanged -= HandlePhaseChanged;
        gm.OnCardDrawn -= HandleCardDrawn;
        gm.OnSlotRevealed -= HandleSlotRevealed;
        gm.OnSlotsSwapped -= HandleSlotsSwapped;
    }

    // Called by the "Start Game" button after the player finishes peeking
    public void OnStartGamePressed()
    {
        initialCardsView.SetActive(false);
        turnUI.SetActive(true);
        gm.SetPhase(GamePhase.DrawingCard, true);
    }

    // Called by the deck button
    public void OnDrawFromDeckPressed() => gm.DrawFromDeck();

    // Called by the discard pile button
    public void OnDrawFromDiscardPressed() => gm.DrawFromDiscard();

    // Called by the "Discard" button shown when a card is in hand
    public void OnDiscardDrawnPressed() => gm.DiscardDrawnCard();

    // Called by the "Swap" button shown when a card is in hand
    public void OnSwapDrawnPressed() => gm.BeginSwapDrawnCard();

    // Called by the "Cambio" button
    public void OnCambioPressed() => gm.CallCambio();
    
    public void OnHidePeekedCardPressed()
    {
        slotRevealedUI.SetActive(false);
        gm.FinishPeeking();
    }

    // -------------------------------------------------------------------------
    // Event handlers
    // -------------------------------------------------------------------------

    private void HandlePhaseChanged(GamePhase phase, bool isPlayerTurn)
    {
        // Hide drawn card view whenever we leave CardDrawn/SelectingSwapSlot
        if (phase != GamePhase.CardDrawn && phase != GamePhase.SelectingSwapSlot)
            drawnCardView.SetActive(false);

        switch (phase)
        {
            case GamePhase.Dealing:
                // Dealt — show initial peek (player sees their two cards)
                ShowInitialPeek();
                break;

            case GamePhase.DrawingCard:
                SetTurnLabel(isPlayerTurn);
                RefreshDiscardDisplay();
                break;

            case GamePhase.CardDrawn:
                // drawnCardImage is set by HandleCardDrawn
                drawnCardView.SetActive(true);
                break;

            case GamePhase.SelectingSwapSlot:
                // Card view stays visible so player can see what they are swapping in
                drawnCardView.SetActive(true);
                break;

            case GamePhase.UsingPower:
                // Instruct player which slot to tap based on the active power
                ShowPowerPrompt();
                break;

            case GamePhase.GameOver:
                ShowGameOver();
                break;
        }
    }

    private void HandleCardDrawn(Card card)
    {
        drawnCardImage.sprite = card.sprite;
        RefreshDiscardDisplay();
    }

    private void HandleSlotRevealed(CardSlot slot)
    {
        if (!gm.IsPlayerTurn) return;
        
        slotRevealedImage.sprite = slot.Card.sprite;
        slotRevealedUI.SetActive(true);
    }

    private void HandleSlotsSwapped(CardSlot a, CardSlot b)
    {
        // Refresh both slot visuals after a power swap.
        // Extend here when you add per-slot visuals.
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private void ShowInitialPeek()
    {
        CardSlot[] playerSlots = gm.GetPlayerSlots();
        if (playerSlots.Length < 2) return;
        peekCardLeft.sprite = playerSlots[0].Card.sprite;
        peekCardRight.sprite = playerSlots[1].Card.sprite;
        initialCardsView.SetActive(true);
        turnUI.SetActive(false);
    }

    private void SetTurnLabel(bool isPlayerTurn)
    {
        turnLabel.color = isPlayerTurn ? playerTurnColor : aiTurnColor;
        turnText.text = isPlayerTurn ? "Your Turn!" : "Ben's Turn!";
    }

    private void RefreshDiscardDisplay()
    {
        discardTopImage.sprite = deck.TopDiscard != null 
            ? deck.TopDiscard.sprite 
            : emptyDiscardSprite;    
    }

    private void ShowPowerPrompt()
    {
        // Show a contextual prompt based on the active power and power step.
        // Extend here when you add the power instruction overlay.
    }

    private void ShowGameOver()
    {
        int playerScore = gm.GetScore(true);
        int aiScore = gm.GetScore(false);
        string result = playerScore < aiScore ? "You win!" : playerScore > aiScore ? "Wallace wins!" : "Draw!";
        gameOverText.text = $"{result}\nYou: {playerScore}  |  Wallace: {aiScore}";
        gameOverPanel.SetActive(true);
    }
}