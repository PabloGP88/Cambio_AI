using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// A slot is now a pure VIEW. It no longer owns a Card or any rules — it just knows its
/// address (side, zone, index), shows/hides itself to mirror GameState, can highlight when
/// "armed" for a match, and forwards taps to PlayerInput as an addressed click.
/// GameManager.SyncViews() drives visibility; nothing here mutates game state.
/// </summary>
public class CardSlot : MonoBehaviour, IPointerClickHandler
{
    [Header("Optional visuals")]
    [SerializeField] private GameObject highlight;   // shown when armed
    [SerializeField] private SpriteRenderer faceOrBack; // optional: a card-back image, etc.

    public int Side { get; private set; }
    public Zone Zone { get; private set; }
    public int Index { get; private set; }

    public void Init(int side, Zone zone, int index)
    {
        Side = side;
        Zone = zone;
        Index = index;
    }

    public void SetVisible(bool visible)
    {
        if (gameObject.activeSelf != visible) gameObject.SetActive(visible);
    }

    public void SetArmed(bool armed)
    {
        if (highlight != null) highlight.SetActive(armed);
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (GameManager.Instance != null)
            GameManager.Instance.Player.ClickSlot(Side, Zone, Index);
    }

    // If you wire clicks via a UI Button or a custom collider instead of IPointerClickHandler,
    // just call this from that handler:
    public void OnClicked()
    {
        if (GameManager.Instance != null)
            GameManager.Instance.Player.ClickSlot(Side, Zone, Index);
    }
}
