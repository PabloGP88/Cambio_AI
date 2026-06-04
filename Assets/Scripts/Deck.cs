using System.Collections.Generic;
using UnityEngine;

public class Deck : MonoBehaviour
{
    [SerializeField] private Sprite[] cardSprites;
    [SerializeField] private Sprite cardBack;

    private Card[] _shuffledDeck;
    private int _topIndex = 0;
    private Stack<Card> _discardPile = new();

    public Sprite CardBack => cardBack;
    public Card TopDiscard => _discardPile.Count > 0 ? _discardPile.Peek() : null;

    void Awake()
    {
        BuildAndShuffle();
    }

    private void BuildAndShuffle()
    {
        _shuffledDeck = new Card[54];
        for (int i = 0; i < 54; i++)
        {
            _shuffledDeck[i] = new Card
            {
                sprite = cardSprites[i],
                displayNumber = GetNumber(i),
                isRed = GetIsRed(i)
            };
        }

        for (int i = _shuffledDeck.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (_shuffledDeck[i], _shuffledDeck[j]) = (_shuffledDeck[j], _shuffledDeck[i]);
        }
    }

    public Card DrawFromDeck()
    {
        if (_topIndex >= _shuffledDeck.Length) return null;
        return _shuffledDeck[_topIndex++];
    }

    public Card DrawFromDiscard()
    {
        return _discardPile.Count > 0 ? _discardPile.Pop() : null;
    }

    public void Discard(Card card)
    {
        _discardPile.Push(card);
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