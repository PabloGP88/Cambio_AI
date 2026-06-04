using UnityEngine;
using UnityEngine.UI;

public class CardSlot : MonoBehaviour
{
    [SerializeField] private Image highlight;
    [SerializeField] private Color armColor = Color.green;

    public Card Card { get; private set; }
    public int SlotIndex { get; private set; }
    public bool BelongsToPlayer { get; private set; }
    public bool IsActive { get; private set; } = true;

    public void Assign(Card card, int index, bool isPlayer)
    {
        Card = card;
        SlotIndex = index;
        BelongsToPlayer = isPlayer;
        IsActive = true;
    }

    public Card SwapCard(Card incoming)
    {
        Card previous = Card;
        Card = incoming;
        return previous;
    }

    public void SetCard(Card card)
    {
        Card = card;
    }

    public void SetInactive()
    {
        IsActive = false;
        SetArmed(false);
        gameObject.SetActive(false);
    }

    public void Reactivate()
    {
        IsActive = true;
        gameObject.SetActive(true);
    }
    public void SetArmed(bool on)
    {
        if (highlight == null) return;
        highlight.color = armColor;
        highlight.enabled = on;
    }

    public void OnClicked()
    {
        GameManager.Instance.OnSlotClicked(this);
    }
}