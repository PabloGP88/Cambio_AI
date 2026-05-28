using TMPro;
using UnityEngine;
using UnityEngine.UI;

public enum GamePhase
{
    Dealing,
    PlayerTurn,
    AITurn,
    GameOver
    // more phases added later
}

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [Header("Card References")]
    [SerializeField] private Deck deck;
    [SerializeField] private CardSlot[] playerSlots;  
    [SerializeField] private CardSlot[] aiSlots;
    
    [Header("UI References")]
    [SerializeField] private GameObject turnUI;
    [SerializeField] private Image turnLabel;
    [SerializeField] private TextMeshProUGUI turnText;
    [SerializeField] private Color playerTurnColor;
    [SerializeField] private Color aiTurnColor;
    
    [Header("Initial Card View References")]
    [SerializeField] private GameObject initialCardsView;
    [SerializeField] private Image cardLeft;
    [SerializeField] private Image cardRight;
    
    [Header("Selecting View References")]
    [SerializeField] private GameObject selectingView;


    private string playerTurnText = "Your Turn!";
    private string aiTurnText = "Wallace Turn!";
    public GamePhase CurrentPhase { get; private set; }

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        DealInitialHands();
    }

    private void SetPhase(GamePhase phase)
    {
        CurrentPhase = phase;
        OnPhaseChanged(phase);
    }

    private void OnPhaseChanged(GamePhase phase)
    {
        switch (phase)
        {
            case GamePhase.Dealing:
                HandleDealingState();
                break;
            case GamePhase.PlayerTurn:
                break;

            case GamePhase.AITurn:
                break;

            case GamePhase.GameOver:
                break;
        }
    }
    private void DealInitialHands()
    {
        for (int i = 0; i < playerSlots.Length; i++)
            playerSlots[i].Assign(deck.DrawCard(), i, true);

        for (int i = 0; i < aiSlots.Length; i++)
            aiSlots[i].Assign(deck.DrawCard(), i, false);
        
        SetPhase(GamePhase.Dealing);
    }
    public void OnSlotClicked(CardSlot slot)
    {
        // Later you'll gate this by phase — for now just show it
        if (!slot.BelongsToPlayer) return;
        selectingView.SetActive(true);
    }

    public void StartGame()
    {
        initialCardsView.SetActive(false);
        turnUI.SetActive(true);
        SetPhase(GamePhase.PlayerTurn);    
    }
    
    // ----------------------------------------------------------------------------------------------------------------- Game States Methods

    private void HandleDealingState()
    {
        cardLeft.sprite = playerSlots[0].Card.sprite;
        cardRight.sprite = playerSlots[1].Card.sprite;
        initialCardsView.SetActive(true);
    }

    private void HandlePlayerTurn()
    {
        turnLabel.color = playerTurnColor;
        turnText.text = playerTurnText;
    }
    
}