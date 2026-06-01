
using System.Collections.Generic;
using UnityEngine;

public class Deck : MonoBehaviour
{
    [SerializeField] private Sprite[] cardSprites; // 54 sprites ordered: clubs A-K, diamonds A-K, hearts A-K, spades A-K, joker x2
    [SerializeField] private Sprite cardBack;

    private Card[] shuffledDeck;
    private int topIndex = 0;
    private Stack<Card> discardPile = new();

    public Sprite CardBack => cardBack;
    public Card TopDiscard => discardPile.Count > 0 ? discardPile.Peek() : null;

    void Awake()
    {
        BuildAndShuffle();
    }

    private void BuildAndShuffle()
    {
        shuffledDeck = new Card[54];
        for (int i = 0; i < 54; i++)
        {
            shuffledDeck[i] = new Card
            {
                sprite = cardSprites[i],
                displayNumber = GetNumber(i),
                isRed = GetIsRed(i)
            };
        }

        for (int i = shuffledDeck.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (shuffledDeck[i], shuffledDeck[j]) = (shuffledDeck[j], shuffledDeck[i]);
        }
    }

    // Draw from the top of the draw pile
    public Card DrawFromDeck()
    {
        if (topIndex >= shuffledDeck.Length) return null;
        return shuffledDeck[topIndex++];
    }

    // Draw the top card from the discard pile
    public Card DrawFromDiscard()
    {
        return discardPile.Count > 0 ? discardPile.Pop() : null;
    }

    public void Discard(Card card)
    {
        discardPile.Push(card);
    }

    private int GetNumber(int index)
    {
        if (index >= 52) return 0;
        return (index % 13) + 1;
    }

    private bool GetIsRed(int index)
    {
        if (index >= 52) return false;
        int suit = index / 13;
        return suit == 1 || suit == 2;
    }
}