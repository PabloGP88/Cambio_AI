using System.Collections;
using TMPro;
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

    [Header("Informed Trade View")]
    [SerializeField] private GameObject informedTradeView;
    [SerializeField] private Image informedTradeOpponentCard;
    [SerializeField] private Image informedTradePlayerCard;

    [Header("Discard Pile UI")]
    [SerializeField] private Image discardTopImage;
    [SerializeField] private Sprite emptyDiscardSprite;

    [Header("Slot Arrows UI")]
    [SerializeField] private GameObject playerSlotArrowsUI;
    [SerializeField] private GameObject aiSlotArrowsUI;
    [SerializeField] private GameObject[] slotArrowsPlayer;
    [SerializeField] private GameObject[] slotArrowsAI;

    [Header("Match UI")]
    [SerializeField] private Image matchSlotImage;
    [SerializeField] private float matchFlashSeconds = 0.8f;

    [Header("Penalty UI")]
    [SerializeField] private GameObject[] penaltyOutlinesPlayer;
    [SerializeField] private Image[] penaltyCardsPlayer;
    [SerializeField] private GameObject[] penaltyOutlinesAI;
    [SerializeField] private Image[] penaltyCardsAI;

    [Header("Game Over UI")]
    [SerializeField] private GameObject gameOverPanel;
    [SerializeField] private TextMeshProUGUI gameOverText;

    private GameManager _gm;
    private Deck _deck;
    private Coroutine _matchFlash;

    void Start()
    {
        _gm = GameManager.Instance;
        _deck = _gm.Getdeck();

        _gm.OnPhaseChanged += HandlePhaseChanged;
        _gm.OnCardDrawn += HandleCardDrawn;
        _gm.OnSlotRevealed += HandleSlotRevealed;
        _gm.OnSlotsSwapped += HandleSlotsSwapped;
        _gm.OnInformedTradeReady += HandleInformedTradeReady;
        _gm.OnMatchResolved += HandleMatchResolved;
        _gm.OnAwaitingGiveCard += HandleAwaitingGiveCard;
        _gm.OnGiveCardDone += HandleGiveCardDone;
        _gm.OnPenaltyAdded += HandlePenaltyAdded;

        ResetPenaltyUI();
        SetImageAlpha(matchSlotImage, 0f);
        ShowInitialPeek();
    }

    void OnDestroy()
    {
        if (_gm == null) return;
        _gm.OnPhaseChanged -= HandlePhaseChanged;
        _gm.OnCardDrawn -= HandleCardDrawn;
        _gm.OnSlotRevealed -= HandleSlotRevealed;
        _gm.OnSlotsSwapped -= HandleSlotsSwapped;
        _gm.OnInformedTradeReady -= HandleInformedTradeReady;
        _gm.OnMatchResolved -= HandleMatchResolved;
        _gm.OnAwaitingGiveCard -= HandleAwaitingGiveCard;
        _gm.OnGiveCardDone -= HandleGiveCardDone;
        _gm.OnPenaltyAdded -= HandlePenaltyAdded;
    }

    public void OnStartGamePressed()
    {
        initialCardsView.SetActive(false);
        turnUI.SetActive(true);
        _gm.SetPhase(GamePhase.DrawingCard, true);
    }

    public void OnDrawFromDeckPressed() => _gm.DrawFromDeck();
    public void OnDrawFromDiscardPressed() => _gm.DrawFromDiscard();
    public void OnDiscardDrawnPressed() => _gm.DiscardDrawnCard();
    public void OnSwapDrawnPressed() => _gm.BeginSwapDrawnCard();
    public void OnCambioPressed() => _gm.CallCambio();

    public void OnHidePeekedCardPressed()
    {
        slotRevealedUI.SetActive(false);
        HideAllArrows();
        _gm.FinishPeeking();
    }

    public void OnInformedTradeConfirmPressed()
    {
        informedTradeView.SetActive(false);
        _gm.ConfirmInformedTrade();
    }

    private void HandlePhaseChanged(GamePhase phase, bool isPlayerTurn)
    {
        HideAllArrows();

        if (phase != GamePhase.CardDrawn)
            drawnCardView.SetActive(false);

        if (phase != GamePhase.UsingPower)
            informedTradeView.SetActive(false);

        switch (phase)
        {
            case GamePhase.Dealing:
                ShowInitialPeek();
                break;

            case GamePhase.DrawingCard:
                SetTurnLabel(isPlayerTurn);
                RefreshDiscardDisplay();
                break;

            case GamePhase.CardDrawn:
                drawnCardView.SetActive(true);
                break;

            case GamePhase.SelectingSwapSlot:
                if (isPlayerTurn)
                    ShowArrows(playerOnly: true, opponentOnly: false);
                break;

            case GamePhase.UsingPower:
                if (isPlayerTurn)
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
        if (!_gm.IsPlayerTurn) return;
        slotRevealedImage.sprite = slot.Card.sprite;
        slotRevealedUI.SetActive(true);
        HideAllArrows();
    }

    private void HandleSlotsSwapped(CardSlot a, CardSlot b)
    {
        
    }

    private void HandleInformedTradeReady(CardSlot opponentSlot, CardSlot ownSlot)
    {
        if (!_gm.IsPlayerTurn) return;
        informedTradeOpponentCard.sprite = opponentSlot.Card.sprite;
        informedTradePlayerCard.sprite = ownSlot.Card.sprite;
        informedTradeView.SetActive(true);
        HideAllArrows();
    }

    private void HandleMatchResolved(CardSlot slot, bool success, bool byPlayer)
    {
        if (_matchFlash != null) StopCoroutine(_matchFlash);
        _matchFlash = StartCoroutine(FlashMatch(slot.Card.sprite));
        if (success) RefreshDiscardDisplay();
    }

    private void HandleAwaitingGiveCard(bool byPlayer)
    {
        if (!byPlayer) return;
        ShowArrows(playerOnly: true, opponentOnly: false);
    }

    private void HandleGiveCardDone()
    {
        HideAllArrows();
    }

    private void HandlePenaltyAdded(bool forPlayer, int index)
    {
        GameObject[] outlines = forPlayer ? penaltyOutlinesPlayer : penaltyOutlinesAI;
        Image[] cards = forPlayer ? penaltyCardsPlayer : penaltyCardsAI;
        if (cards == null || index < 0 || index >= cards.Length) return;

        int outlineIndex = index / 2;
        if (outlines != null && outlineIndex < outlines.Length && outlines[outlineIndex] != null)
            outlines[outlineIndex].SetActive(true);

        if (cards[index] != null)
        {
            cards[index].sprite = _deck.CardBack;
            cards[index].gameObject.SetActive(true);
        }
    }

    private IEnumerator FlashMatch(Sprite sprite)
    {
        if (matchSlotImage == null) yield break;
        matchSlotImage.sprite = sprite;
        SetImageAlpha(matchSlotImage, 1f);
        yield return new WaitForSeconds(matchFlashSeconds);
        SetImageAlpha(matchSlotImage, 0f);
        _matchFlash = null;
    }

    private void ShowInitialPeek()
    {
        CardSlot[] playerSlots = _gm.GetPlayerSlots();
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
        discardTopImage.sprite = _deck.TopDiscard != null
            ? _deck.TopDiscard.sprite
            : emptyDiscardSprite;
    }

    private void ShowPowerPrompt()
    {
        switch (_gm.CurrentPowerStep)
        {
            case PowerStep.LookingOwn:
                ShowArrows(playerOnly: true, opponentOnly: false);
                break;

            case PowerStep.LookingOpponent:
                ShowArrows(playerOnly: false, opponentOnly: true);
                break;

            case PowerStep.SelectingPowerSwapSource:
                ShowArrows(playerOnly: true, opponentOnly: false);
                break;

            case PowerStep.SelectingPowerSwapTarget:
                ShowArrows(playerOnly: false, opponentOnly: true);
                break;

            case PowerStep.SelectingTradeOpponent:
                ShowArrows(playerOnly: false, opponentOnly: true);
                break;

            case PowerStep.SelectingTradeOwn:
                ShowArrows(playerOnly: true, opponentOnly: false);
                break;

            case PowerStep.ConfirmingTrade:
                HideAllArrows();
                break;
        }
    }

    private void ShowArrows(bool playerOnly, bool opponentOnly)
    {
        bool showPlayer = !opponentOnly;
        bool showAI = !playerOnly;

        playerSlotArrowsUI.SetActive(showPlayer);
        aiSlotArrowsUI.SetActive(showAI);

        foreach (var arrow in slotArrowsPlayer)
            arrow.SetActive(showPlayer);

        foreach (var arrow in slotArrowsAI)
            arrow.SetActive(showAI);
    }

    private void HideAllArrows()
    {
        playerSlotArrowsUI.SetActive(false);
        aiSlotArrowsUI.SetActive(false);

        foreach (var arrow in slotArrowsPlayer)
            arrow.SetActive(false);

        foreach (var arrow in slotArrowsAI)
            arrow.SetActive(false);
    }

    private void ResetPenaltyUI()
    {
        if (penaltyOutlinesPlayer != null)
            foreach (var o in penaltyOutlinesPlayer) if (o != null) o.SetActive(false);
        if (penaltyOutlinesAI != null)
            foreach (var o in penaltyOutlinesAI) if (o != null) o.SetActive(false);
        if (penaltyCardsPlayer != null)
            foreach (var c in penaltyCardsPlayer) if (c != null) c.gameObject.SetActive(false);
        if (penaltyCardsAI != null)
            foreach (var c in penaltyCardsAI) if (c != null) c.gameObject.SetActive(false);
    }

    private void SetImageAlpha(Image img, float alpha)
    {
        if (!img) return;
        Color c = img.color;
        c.a = alpha;
        img.color = c;
    }

    private void ShowGameOver()
    {
        int playerScore = _gm.GetScore(true);
        int aiScore = _gm.GetScore(false);
        string result = playerScore < aiScore ? "You win!" : playerScore > aiScore ? "Wallace wins!" : "Draw!";
        gameOverText.text = $"{result}\nYou: {playerScore}  |  Wallace: {aiScore}";
        gameOverPanel.SetActive(true);
    }
}