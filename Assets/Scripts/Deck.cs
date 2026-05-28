using UnityEngine;

public class Deck : MonoBehaviour
{
    [SerializeField] private Sprite[] cardSprites; // 54 sprites
    [SerializeField] private Sprite cardBack;

    private Card[] shuffledDeck;
    private int topIndex = 0;

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

        // Fisher-Yates Algorithm
        for (int i = shuffledDeck.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (shuffledDeck[i], shuffledDeck[j]) = (shuffledDeck[j], shuffledDeck[i]);
        }
    }

    public Card DrawCard()
    {
        if (topIndex >= shuffledDeck.Length) return null;
        return shuffledDeck[topIndex++];
    }

    private int GetNumber(int index)
    {
        if (index >= 52) return 0; // Joker
        return (index % 13) + 1;  // 1–13
    }

    private bool GetIsRed(int index)
    {
        if (index >= 52) return false;
        int suit = index / 13; // 0=clubs, 1=diamonds, 2=hearts, 3=spades
        return suit == 1 || suit == 2;
    }
}