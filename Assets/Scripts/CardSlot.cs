using System;
using UnityEngine;
using UnityEngine.UI;

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

    public void OnClicked()
    {
        GameManager.Instance.OnSlotClicked(this);
    }
}