using UnityEngine;

/// <summary>Moves this object along its local +Z axis every frame.</summary>
public class LocalForwardMotion : MonoBehaviour
{
    [SerializeField] private float speed = 2f;

    void Update()
    {
        transform.Translate(0f, 0f, speed * Time.deltaTime, Space.Self);
    }
}
