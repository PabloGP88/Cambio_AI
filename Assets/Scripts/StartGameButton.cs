using UnityEngine;

public class StartGameButton : MonoBehaviour
{
    [SerializeField] private GameManager gameManager;

    public void StartGame()
    {
        gameManager.StartGame();
    }
}
