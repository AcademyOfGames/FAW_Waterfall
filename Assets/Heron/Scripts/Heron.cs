using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Heron : MonoBehaviour
{
    private Animator heron;

    // Start is called before the first frame update
    void Start()
    {
        heron = GetComponent<Animator>();
    }

    // Update is called once per frame
    void Update()
    {
        if (heron.GetCurrentAnimatorStateInfo(0).IsName("idle"))
        {
            heron.SetBool("walkleft", false);
            heron.SetBool("walkright", false);
            heron.SetBool("walk", false);
            heron.SetBool("takeoff", false);
            heron.SetBool("eat", false);
            heron.SetBool("flap", false);
            heron.SetBool("scratch", false);
        }
        if (heron.GetCurrentAnimatorStateInfo(0).IsName("fly"))
        {
            heron.SetBool("landing", false);
        }
        if (Input.GetKeyDown(KeyCode.W))
        {
            heron.SetBool("idle", false);
            heron.SetBool("walk", true);
        }
        if (Input.GetKeyUp(KeyCode.W))
        {
            heron.SetBool("idle", true);
            heron.SetBool("walk", false);
        }
        if (Input.GetKeyDown(KeyCode.A))
        {
            heron.SetBool("walk", false);
            heron.SetBool("walkleft", true);
            heron.SetBool("idle", false);
            heron.SetBool("flyleft", true);
            heron.SetBool("fly", false);
        }
        if (Input.GetKeyUp(KeyCode.A))
        {
            heron.SetBool("walk", true);
            heron.SetBool("walkleft", false);
            heron.SetBool("fly", true);
            heron.SetBool("flyleft", false);
        }
        if (Input.GetKeyDown(KeyCode.D))
        {
            heron.SetBool("walk", false);
            heron.SetBool("walkright", true);
            heron.SetBool("idle", false);
            heron.SetBool("flyright", true);
            heron.SetBool("fly", false);
        }
        if (Input.GetKeyUp(KeyCode.D))
        {
            heron.SetBool("walk", true);
            heron.SetBool("walkright", false);
            heron.SetBool("fly", true);
            heron.SetBool("flyright", false);
        }
        if (Input.GetKeyDown(KeyCode.Space))
        {
            heron.SetBool("takeoff", true);
            heron.SetBool("idle", false);
            heron.SetBool("landing", true);
            heron.SetBool("fly", false);
            heron.SetBool("flyleft", false);
            heron.SetBool("flyright", false);
            heron.SetBool("glide", false);
        }
        if (Input.GetMouseButtonDown(0))
        {
            heron.SetBool("glide", true);
            heron.SetBool("fly", false);
            heron.SetBool("flyleft", false);
            heron.SetBool("flyright", false);
        }
        if (Input.GetMouseButtonUp(0))
        {
            heron.SetBool("glide", false);
            heron.SetBool("fly", true);
        }
        if (Input.GetKeyUp(KeyCode.F))
        {
            heron.SetBool("fly", true);
            heron.SetBool("glide", false);
        }
        if (Input.GetMouseButton(0))
        {
            heron.SetBool("eat", true);
            heron.SetBool("idle", false);
        }
        if (Input.GetMouseButton(1))
        {
            heron.SetBool("flap", true);
            heron.SetBool("idle", false);
        }
        if (Input.GetMouseButton(2))
        {
            heron.SetBool("scratch", true);
            heron.SetBool("idle", false);
        }
    }
}
