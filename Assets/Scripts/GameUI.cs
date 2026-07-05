using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Presentation layer, wired to the new pure-C# game core.
///
/// The SERIALIZED FIELDS and the public On*Pressed() methods below are kept IDENTICAL to
/// the original GameUI so your existing scene/Inspector wiring (panels, arrows, penalty
/// slots, button OnClick events) carries over with no re-wiring. Only the *internals*
/// were ported to the new API:
///   - card sprites come from the Deck catalog via deck.SpriteFor(card)
///   - events now carry Card structs / int side+index instead of CardSlot objects
///   - actions route through GameManager.Player.PressX()
///
/// AI "thinking" is no longer a canned phrase picked from an array. GameManager now runs
/// the AI's search as a coroutine and forwards structured IsmctsReport snapshots via
/// OnAiSearchProgress (live, mid-search) and OnAiSearchDecision (final, once chosen).
/// Those drive the two ismcts*Text panels below.
/// </summary>
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
    [SerializeField] private GameObject[] penaltyArrowsPlayer;
    [SerializeField] private GameObject[] penaltyArrowsAI;

    [Header("Game Over UI")]
    [SerializeField] private GameObject gameOverPanel;
    [SerializeField] private TextMeshProUGUI gameOverText;

    [Header("Action AI Sign")]
    [SerializeField] private TextMeshProUGUI actionText;
    [SerializeField] private string[] idlePhrases;
    [SerializeField] private string[] actionPhrases;

    [Header("AI ISMCTS Debug Panels")]
    // Live view of the search while Ben is choosing a move: root visits, nodes expanded,
    // and a ranked table of the top candidate moves with visits/avgReward/avail so you can
    // watch the tree converge in real time.
    [SerializeField] private TextMeshProUGUI ismctsLiveStatsText;
    // Snapshot taken once the decision is made: the chosen move's stats plus its closest
    // runner-up, so you can see exactly why one move beat another.
    [SerializeField] private TextMeshProUGUI ismctsDecisionText;
    [SerializeField] private int ismctsTopMovesShown = 5;

    private GameManager _gm;
    private Deck _deck;
    private Coroutine _matchFlash;

    // ----------------------------------------------------------------------
    // AI action sign  —  fill actionPhrases[] in this exact order:
    //   0  (unused — thinking is now shown via the ISMCTS panels, not a phrase)
    //   1  draw from deck        2  draw from discard
    //   3  discard drawn         4  swap drawn into hand
    //   5  attempt match         6  give a card
    //   7  power: look own       8  power: look opponent
    //   9  power: blind swap     10 power: look & swap (trade)
    //   11 call cambio
    // idlePhrases[] = flavour lines shown on the player's turn.
    // ----------------------------------------------------------------------
    private const int A_THINKING            = 0; // no longer used directly — kept so array indices below stay stable
    private const int A_DRAW_DECK           = 1;
    private const int A_DRAW_DISCARD        = 2;
    private const int A_DISCARD_DRAWN       = 3;
    private const int A_SWAP_DRAWN          = 4;
    private const int A_MATCH               = 5;
    private const int A_GIVE                = 6;
    private const int A_POWER_LOOK_OWN      = 7;
    private const int A_POWER_LOOK_OPP      = 8;
    private const int A_POWER_BLIND_SWAP    = 9;
    private const int A_POWER_LOOK_AND_SWAP = 10;
    private const int A_CAMBIO              = 11;
    void Start()
    {
        _gm = GameManager.Instance;
        _deck = _gm.Catalog;                 // was _gm.Getdeck()

        _gm.OnPhaseChanged += HandlePhaseChanged;
        _gm.OnCardDrawn += HandleCardDrawn;
        _gm.OnSlotRevealed += HandleSlotRevealed;
        _gm.OnSlotsSwapped += HandleSlotsSwapped;
        _gm.OnInformedTradeReady += HandleInformedTradeReady;
        _gm.OnMatchResolved += HandleMatchResolved;
        _gm.OnAwaitingGiveCard += HandleAwaitingGiveCard;
        _gm.OnGiveCardDone += HandleGiveCardDone;
        _gm.OnPenaltyAdded += HandlePenaltyAdded;
        _gm.OnCommandApplied += HandleCommandApplied;
        _gm.OnAiSearchProgress += HandleAiSearchProgress;
        _gm.OnAiSearchDecision += HandleAiSearchDecision;

        ShowIdle();

        ResetPenaltyUI();
        SetImageAlpha(matchSlotImage, 0f);

        // If GameManager.Start already ran, State exists and we can show the peek now.
        // If it hasn't, we'll catch the Dealing OnPhaseChanged event below instead.
        if (_gm.State != null) ShowInitialPeek();
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
        _gm.OnCommandApplied -= HandleCommandApplied;
        _gm.OnAiSearchProgress -= HandleAiSearchProgress;
        _gm.OnAiSearchDecision -= HandleAiSearchDecision;
    }

    // ----------------------------------------------------------------------
    // Button hooks (wire these to your Button OnClick events in the Inspector,
    // exactly as before — names and signatures are unchanged).
    // ----------------------------------------------------------------------

    public void OnStartGamePressed()
    {
        initialCardsView.SetActive(false);
        turnUI.SetActive(true);
        _gm.StartPlay();                                  // was _gm.SetPhase(GamePhase.DrawingCard, true)
    }

    public void OnDrawFromDeckPressed()    => _gm.Player.PressDrawDeck();
    public void OnDrawFromDiscardPressed() => _gm.Player.PressDrawDiscard();
    public void OnDiscardDrawnPressed()    => _gm.Player.PressDiscardDrawn();
    public void OnSwapDrawnPressed()       => _gm.Player.PressBeginSwap();
    public void OnCambioPressed()          => _gm.Player.PressCambio();

    public void OnHidePeekedCardPressed()
    {
        slotRevealedUI.SetActive(false);
        HideAllArrows();
        _gm.Player.PressFinishPeek();                     // was _gm.FinishPeeking()
    }

    public void OnInformedTradeConfirmPressed()
    {
        informedTradeView.SetActive(false);
        _gm.Player.PressConfirmTrade();                   // was _gm.ConfirmInformedTrade()
    }

    // ----------------------------------------------------------------------
    // Event handlers (signatures updated to the new core's events)
    // ----------------------------------------------------------------------

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
                if (isPlayerTurn) ShowIdle();
                // else: no canned phrase here anymore — the ISMCTS live panel
                // (ismctsLiveStatsText) shows what Ben is actually doing while he searches.
                break;

            case GamePhase.CardDrawn:
                // Only show the drawn card on the human's turn — never leak the AI's draw.
                if (isPlayerTurn) drawnCardView.SetActive(true);
                break;

            case GamePhase.SelectingSwapSlot:
                if (isPlayerTurn)
                    ShowArrows(playerOnly: true, opponentOnly: false);
                break;

            case GamePhase.UsingPower:
                if (isPlayerTurn)
                    ShowPowerPrompt();
                else
                {
                    int p = PowerActionIndex(_gm.State.ActivePower);
                    if (p >= 0) SetAction(p);
                }
                break;

            case GamePhase.GameOver:
                ShowGameOver();
                break;
        }
    }

    private void HandleCardDrawn(Card card)
    {
        if (!_gm.IsPlayerTurn) return;                    // guard: hide AI's drawn card
        drawnCardImage.sprite = _deck.SpriteFor(card);    // was card.sprite
        RefreshDiscardDisplay();
    }

    private void HandleSlotRevealed(int side, int index, Card card)
    {
        if (!_gm.IsPlayerTurn) return;
        slotRevealedImage.sprite = _deck.SpriteFor(card); // was slot.Card.sprite
        slotRevealedUI.SetActive(true);
        HideAllArrows();
    }

    private void HandleSlotsSwapped()
    {
        // No visual needed — slot visibility is reconciled by GameManager.SyncViews().
    }

    private void HandleInformedTradeReady(Card opponentCard, Card ownCard)
    {
        if (!_gm.IsPlayerTurn) return;
        informedTradeOpponentCard.sprite = _deck.SpriteFor(opponentCard);
        informedTradePlayerCard.sprite = _deck.SpriteFor(ownCard);
        informedTradeView.SetActive(true);
        HideAllArrows();
    }

    private void HandleMatchResolved(int side, int index, Card card, bool success, bool byPlayer)
    {
        // The matched card going to discard is public info — safe to flash for both sides.
        if (_matchFlash != null) StopCoroutine(_matchFlash);
        _matchFlash = StartCoroutine(FlashMatch(_deck.SpriteFor(card)));
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
            cards[index].sprite = _deck.CardBack;          // penalty cards stay face-down
            cards[index].gameObject.SetActive(true);
        }
    }

    // ----------------------------------------------------------------------
    // ISMCTS debug panels
    // ----------------------------------------------------------------------

    /// <summary>Live, mid-search snapshot: fired repeatedly while Ben is thinking.</summary>
    private void HandleAiSearchProgress(IsmctsReport report)
    {
        if (!ismctsLiveStatsText) return;
        string header = $"Ben is thinking... {report.IterationsDone}/{report.IterationsTarget} iterations ({report.ElapsedMs} ms)";
        ismctsLiveStatsText.text = FormatMoveTable(report, header);
    }

    /// <summary>Final snapshot: fired once, after the move has been chosen. Refreshes the
    /// live panel with the completed table and fills the decision panel with the chosen
    /// move vs. its closest runner-up.</summary>
    private void HandleAiSearchDecision(IsmctsReport report)
    {
        if (ismctsLiveStatsText)
        {
            string header = $"Search complete: {report.IterationsDone} iterations in {report.ElapsedMs} ms";
            ismctsLiveStatsText.text = FormatMoveTable(report, header);
        }

        if (!ismctsDecisionText) return;

        MoveStat chosen = default;
        MoveStat runnerUp = default;
        bool hasChosen = false, hasRunnerUp = false;

        // Moves is sorted by visits descending; pick out the flagged chosen row and the
        // highest-visit row that isn't it, wherever each happens to sit in the ranking.
        foreach (var m in report.Moves)
        {
            if (m.IsChosen && !hasChosen) { chosen = m; hasChosen = true; continue; }
            if (!hasRunnerUp) { runnerUp = m; hasRunnerUp = true; }
        }

        var sb = new System.Text.StringBuilder();

        if (hasChosen)
        {
            sb.AppendLine($"Chosen move: {chosen.Move}");
            sb.AppendLine($"  visits = {chosen.Visits}");
            sb.AppendLine($"  avg reward = {chosen.AvgReward:F3}");
            sb.AppendLine($"  avail (times seen as a legal sibling) = {chosen.Avail}");
        }
        else
        {
            sb.AppendLine("Chosen move: (no search run — only one legal move)");
        }

        if (hasRunnerUp)
        {
            sb.AppendLine();
            sb.AppendLine($"Runner-up: {runnerUp.Move}");
            sb.AppendLine($"  visits = {runnerUp.Visits}   (Δ {chosen.Visits - runnerUp.Visits})");
            sb.AppendLine($"  avg reward = {runnerUp.AvgReward:F3}   (Δ {(chosen.AvgReward - runnerUp.AvgReward):F3})");
        }

        ismctsDecisionText.text = sb.ToString();
    }

    /// <summary>Renders the header line + a ranked table of the top N root moves. Shared by
    /// both the live progress panel and the "search complete" refresh of that same panel.</summary>
    private string FormatMoveTable(IsmctsReport report, string header)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine($"root visits = {report.RootVisits}   nodes expanded = {report.NodesExpanded}");
        sb.AppendLine($"root moves explored = {report.ExpandedRootMoves}/{report.LegalCount}");
        sb.AppendLine();

        int shown = 0;
        foreach (var m in report.Moves)
        {
            if (shown >= ismctsTopMovesShown) break;
            shown++;
            string mark = m.IsChosen ? "  <== CHOSEN" : "";
            sb.AppendLine($"{m.Move,-28} v={m.Visits,4}  avgR={m.AvgReward:F3}  avail={m.Avail}{mark}");
        }

        return sb.ToString();
    }

    // ----------------------------------------------------------------------
    // Helpers (unchanged from the original, except for the new data sources)
    // ----------------------------------------------------------------------

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
        if (_gm == null || _gm.State == null) return;
        var (a, b) = _gm.PeekInitialIds();                 // was _gm.GetPlayerSlots()[0/1].Card
        peekCardLeft.sprite = _deck.SpriteFor(a);
        peekCardRight.sprite = _deck.SpriteFor(b);
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
        Card top = _gm.State.TopDiscard;                   // discard now lives in GameState
        discardTopImage.sprite = top.IsNone ? emptyDiscardSprite : _deck.SpriteFor(top);
    }

    private void ShowPowerPrompt()
    {
        switch (_gm.State.PowerStep)                        // was _gm.CurrentPowerStep
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

        ShowPenaltyArrows(penaltyArrowsPlayer, penaltyCardsPlayer, showPlayer);
        ShowPenaltyArrows(penaltyArrowsAI, penaltyCardsAI, showAI);
    }

    private void HideAllArrows()
    {
        playerSlotArrowsUI.SetActive(false);
        aiSlotArrowsUI.SetActive(false);

        foreach (var arrow in slotArrowsPlayer)
            arrow.SetActive(false);

        foreach (var arrow in slotArrowsAI)
            arrow.SetActive(false);

        HidePenaltyArrows(penaltyArrowsPlayer);
        HidePenaltyArrows(penaltyArrowsAI);
    }

    private void ShowPenaltyArrows(GameObject[] arrows, Image[] cards, bool show)
    {
        if (arrows == null) return;
        for (int i = 0; i < arrows.Length; i++)
        {
            if (arrows[i] == null) continue;
            bool cardExists = show
                              && cards != null
                              && i < cards.Length
                              && cards[i] != null
                              && cards[i].gameObject.activeSelf;
            arrows[i].SetActive(cardExists);
        }
    }

    private void HidePenaltyArrows(GameObject[] arrows)
    {
        if (arrows == null) return;
        foreach (var arrow in arrows)
            if (arrow != null) arrow.SetActive(false);
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
        string result = playerScore < aiScore ? "You win!" : playerScore > aiScore ? "Ben wins!" : "Draw!";
        gameOverText.text = $"{result}\nYou: {playerScore}  |  Ben: {aiScore}";
        gameOverPanel.SetActive(true);
    }

    public void RestartGame()
    {
        SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }

    private void SetAction(int index)
    {
        if (!actionText || actionPhrases == null) return;
        if (index < 0 || index >= actionPhrases.Length) return;
        actionText.text = actionPhrases[index];
    }

    private void ShowIdle()
    {
        if (actionText == null) return;
        if (idlePhrases == null || idlePhrases.Length == 0) { actionText.text = ""; return; }
        actionText.text = idlePhrases[Random.Range(0, idlePhrases.Length)];
    }

    private int PowerActionIndex(CardPower power) => power switch
    {
        CardPower.LookOwnCard      => A_POWER_LOOK_OWN,
        CardPower.LookOpponentCard => A_POWER_LOOK_OPP,
        CardPower.BlindSwap        => A_POWER_BLIND_SWAP,
        CardPower.LookAndSwap      => A_POWER_LOOK_AND_SWAP,
        _                          => -1
    };

    private void HandleCommandApplied(CommandType type, bool byPlayer)
    {
        if (byPlayer) return;   // the sign narrates Ben only

        switch (type)
        {
            case CommandType.DrawFromDeck:      SetAction(A_DRAW_DECK);    break;
            case CommandType.DrawFromDiscard:   SetAction(A_DRAW_DISCARD); break;

            case CommandType.DiscardDrawn:
                // If that discard triggered a power, the UsingPower phase shows it instead.
                if (_gm.State.Phase != GamePhase.UsingPower) SetAction(A_DISCARD_DRAWN);
                break;

            case CommandType.SwapDrawnIntoSlot: SetAction(A_SWAP_DRAWN); break;
            case CommandType.AttemptMatch:      SetAction(A_MATCH);      break;
            case CommandType.GiveCard:          SetAction(A_GIVE);       break;
            case CommandType.CallCambio:        SetAction(A_CAMBIO);     break;

            // UsePowerOnSlot / ConfirmTrade / FinishPeeking: keep the current power text.
        }
    }
}