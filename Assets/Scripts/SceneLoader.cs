using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneLoader : MonoBehaviour
{
    [SerializeField] private string sceneName;

    public void LoadScenebyName()
    {
        SceneManager.LoadScene(sceneName);
    }
}
