using UnityEngine;

public class Deck : MonoBehaviour
{
    [SerializeField] private Sprite[] deckSprites;
    private Sprite[] shuffledDeck;
    private int topIndex = 0;

    void Start()
    {
        shuffledDeck = (Sprite[])deckSprites.Clone();
        for (int i = shuffledDeck.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (shuffledDeck[i], shuffledDeck[j]) = (shuffledDeck[j], shuffledDeck[i]);
        }
    }

    public Sprite DrawCard()
    {
        if (topIndex >= shuffledDeck.Length) return null;
        return shuffledDeck[topIndex++];
    }

    public void Example()
    {
        print(shuffledDeck[topIndex++].name);
    }
}