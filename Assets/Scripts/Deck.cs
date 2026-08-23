using System.Collections.Generic;
using UnityEngine;

public class Deck : MonoBehaviour
{
    [Header("Sprites (index = card id, length 54)")]
    [SerializeField] private Sprite[] cardSprites;
    [SerializeField] private Sprite cardBack;
    [SerializeField] private Sprite emptyDiscard;

    public Sprite CardBack => cardBack;
    public Sprite EmptyDiscard => emptyDiscard;

    public Sprite SpriteForId(int id)
    {
        if (id < 0 || cardSprites == null || id >= cardSprites.Length) return cardBack;
        return cardSprites[id];
    }

    public Sprite SpriteFor(Card c) => c.IsNone ? cardBack : SpriteForId(c.Id);

    // real-game shuffle using Unity Random; GameState handles its own deterministic reshuffles
    public int[] BuildShuffledDeck()
    {
        int n = Card.DeckSize;
        var ids = new List<int>(n);
        for (int i = 0; i < n; i++) ids.Add(i);
        for (int i = n - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (ids[i], ids[j]) = (ids[j], ids[i]);
        }
        return ids.ToArray();
    }
}
