using UnityEngine;

public enum BoxType
{
    Small = 0,
    Large = 1
}

public class BoxItem : MonoBehaviour
{
    [SerializeField] private BoxType boxType = BoxType.Small;

    public BoxType Type => boxType;
}
