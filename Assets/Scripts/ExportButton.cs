using System;
using UnityEngine;

public class ExportButton : MonoBehaviour
{
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.P))
        {
            print("Exporting...");
            Export();
        }
    }

    public void Export()
    {
        if (MatchTracker.Instance != null)
            MatchTracker.Instance.ExportCsv();
        else
            Debug.LogWarning("[TrackerExportButton] No MatchTracker in the scene/session yet.");
    }
}