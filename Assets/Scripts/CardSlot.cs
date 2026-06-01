using UnityEngine;

public class CardSlot : MonoBehaviour
{
    public Card Card { get; private set; }
    public int SlotIndex { get; private set; }
    public bool BelongsToPlayer { get; private set; }

    public void Assign(Card card, int index, bool isPlayer)
    {
        Card = card;
        SlotIndex = index;
        BelongsToPlayer = isPlayer;
    }

    // Returns the card that was here before the swap
    public Card SwapCard(Card incoming)
    {
        Card previous = Card;
        Card = incoming;
        return previous;
    }

    public void OnClicked()
    {
        GameManager.Instance.OnSlotClicked(this);
    }
}