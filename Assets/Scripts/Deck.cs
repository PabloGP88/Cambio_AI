using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What used to be the Deck MonoBehaviour is now purely a *card catalog*: it maps physical
/// card ids (0..53) to sprites and produces the initial shuffled ordering. All deck LOGIC
/// (drawing, discard, reshuffle) moved into GameState, because the AI must be able to run
/// that logic thousands of times per move without touching the scene.
///
/// Inspector setup: drop the 54 face sprites into cardSprites in id order
///   0..12 black A..K, 13..25 red A..K, 26..38 red A..K, 39..51 black A..K, 52..53 jokers
/// and assign cardBack + emptyDiscard.
/// </summary>
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

    /// <summary>Real-game shuffle (uses Unity Random). GameState does deterministic reshuffles itself.</summary>
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
