using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class AiActionVisualFeedback : MonoBehaviour
{
    [Header("Peek (Look power) timing")]
    [SerializeField] private float peekMoveInSeconds  = 0.30f;  // Lerp travel to hand centre
    [SerializeField] private float peekHoldSeconds    = 2.00f;  // "stay like that for 2 seconds"
    [SerializeField] private float peekMoveOutSeconds = 0.30f;  // Lerp back home
    [SerializeField] private float peekScale = 1.2f;            // 1 -> 1.2

    [Header("Swap power timing")]
    [SerializeField] private float swapSeconds = 2.00f;         // full cross-over, Lerp

    private GameManager _gm;

    // effects fire synchronously and back-to-back, so we queue and play one at a time
    private enum JobType { Peek, Swap }
    private struct VisualJob
    {
        public JobType Type;
        public SlotRef A;
        public SlotRef B;
    }
    private readonly Queue<VisualJob> _queue = new();
    private bool _playing;

    private void Start()
    {
        _gm = GameManager.Instance;
        if (_gm == null) { enabled = false; return; }
        _gm.OnEffectApplied += HandleEffect;
    }

    private void OnDestroy()
    {
        if (_gm != null) _gm.OnEffectApplied -= HandleEffect;
    }

    // ---- listen ---------------------------------------------------------------

    private void HandleEffect(GameEffect fx, int actorSide)
    {
        // only the AI's actions get this feedback; the human already has on-screen prompts
        if (actorSide != GameState.AISide) return;

        switch (fx.Kind)
        {
            // a Look power peeked a card (Reveal() is only produced by the look steps)
            case EffectKind.SlotRevealed:
                Enqueue(new VisualJob { Type = JobType.Peek, A = fx.Slot });
                break;

            // a swap effect. keep only the genuine two-card power swaps:
            //   - Slot2 set          -> not a drawn-card swap (that leaves Slot2 = None)
            //   - both slots active  -> not a give (a give empties its source slot)
            // this isolates BlindSwap and LookAndSwap.
            case EffectKind.SlotsSwapped:
                if (!fx.Slot2.IsNone &&
                    _gm.State.IsActive(fx.Slot) &&
                    _gm.State.IsActive(fx.Slot2))
                {
                    Enqueue(new VisualJob { Type = JobType.Swap, A = fx.Slot, B = fx.Slot2 });
                }
                break;
        }
    }

    // ---- queue pump -----------------------------------------------------------

    private void Enqueue(VisualJob job)
    {
        _queue.Enqueue(job);
        if (!_playing) StartCoroutine(Drive());
    }

    private IEnumerator Drive()
    {
        _playing = true;
        while (_queue.Count > 0)
        {
            var job = _queue.Dequeue();
            if (job.Type == JobType.Peek) yield return PeekRoutine(job.A);
            else                          yield return SwapRoutine(job.A, job.B);
        }
        _playing = false;
    }

    // ---- animations -----------------------------------------------------------

    private IEnumerator PeekRoutine(SlotRef slotRef)
    {
        CardSlot slot = _gm.GetSlotView(slotRef);
        if (slot == null) yield break;

        Transform t = slot.transform;

        // (0,0) of the slot's parent -> the centre of that hand (player's or AI's)
        Vector3 homePos   = t.localPosition;
        Vector3 homeScale = t.localScale;
        Vector3 targetPos = Vector3.zero;
        Vector3 targetScale = homeScale * peekScale;

        // render above its neighbours while it floats (no face flip; it stays face down)
        int siblingIndex = t.GetSiblingIndex();
        t.SetAsLastSibling();

        // slide to hand centre + scale up
        for (float e = 0f; e < peekMoveInSeconds; e += Time.deltaTime)
        {
            float k = peekMoveInSeconds <= 0f ? 1f : Mathf.Clamp01(e / peekMoveInSeconds);
            t.localPosition = Vector3.Lerp(homePos, targetPos, k);
            t.localScale    = Vector3.Lerp(homeScale, targetScale, k);
            yield return null;
        }
        t.localPosition = targetPos;
        t.localScale    = targetScale;

        // hold so the player sees which card the AI is looking at
        yield return new WaitForSeconds(peekHoldSeconds);

        // return home
        for (float e = 0f; e < peekMoveOutSeconds; e += Time.deltaTime)
        {
            float k = peekMoveOutSeconds <= 0f ? 1f : Mathf.Clamp01(e / peekMoveOutSeconds);
            t.localPosition = Vector3.Lerp(targetPos, homePos, k);
            t.localScale    = Vector3.Lerp(targetScale, homeScale, k);
            yield return null;
        }

        // restore everything exactly as it was
        t.localPosition = homePos;
        t.localScale    = homeScale;
        t.SetSiblingIndex(siblingIndex);
    }

    private IEnumerator SwapRoutine(SlotRef aRef, SlotRef bRef)
    {
        CardSlot a = _gm.GetSlotView(aRef);
        CardSlot b = _gm.GetSlotView(bRef);
        if (a == null || b == null) yield break;

        Transform ta = a.transform;
        Transform tb = b.transform;
        Vector3 homeA = ta.position;   // world space: the two cards may live in different hands
        Vector3 homeB = tb.position;

        int siA = ta.GetSiblingIndex();
        int siB = tb.GetSiblingIndex();
        ta.SetAsLastSibling();
        tb.SetAsLastSibling();

        // the two sprites physically trade positions
        for (float e = 0f; e < swapSeconds; e += Time.deltaTime)
        {
            float k = swapSeconds <= 0f ? 1f : Mathf.Clamp01(e / swapSeconds);
            ta.position = Vector3.Lerp(homeA, homeB, k);
            tb.position = Vector3.Lerp(homeB, homeA, k);
            yield return null;
        }

        // settle back into the fixed home slots. the card data was already swapped by the
        // game logic, and blind-swap cards are face-down, so returning each slot to its home
        // leaves the board in its correct final state.
        ta.position = homeA;
        tb.position = homeB;
        ta.SetSiblingIndex(siA);
        tb.SetSiblingIndex(siB);
    }
}