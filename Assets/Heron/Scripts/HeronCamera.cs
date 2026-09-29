using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HeronCamera : MonoBehaviour
{
    public Vector3 offset;
    public float SmoothSpeed = 1.0f;
    public Transform target;



    void Update()
    {
        Vector3 DesiredPosition = target.position + offset;
        Vector3 SmoothedPosition = Vector3.Lerp(transform.position, DesiredPosition, SmoothSpeed * Time.deltaTime);
        transform.position = SmoothedPosition;
        transform.LookAt(target);
    }
}
