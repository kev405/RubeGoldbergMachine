using TMPro;
using UnityEngine;

public class HitCounter : MonoBehaviour
{
    public static HitCounter Instance { get; private set; }

    [SerializeField] private TMP_Text label;
    [SerializeField] private int total = 13;
    private int count;

    private void Awake()
    {
        Instance = this;
        label.text = $"Golpes: 0/{total}";
    }

    public void Register(string hitName)
    {
        count++;
        label.text = $"Golpe {count}/{total}\n<size=60%>{hitName}</size>";
    }
}
