using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Calliope : MonoBehaviour
{
    private Animator calliope;
    public GameObject MainCamera;
    void Start()
    {
        calliope = GetComponent<Animator>();
    }
    void Update()
    {
        if (calliope.GetCurrentAnimatorStateInfo(0).IsName("idle"))
        {
            calliope.SetBool("takeoff", false);
            calliope.SetBool("fly", false);
            calliope.SetBool("landing", false);
            calliope.SetBool("flyinplace", false);
        }
        if ((Input.GetKeyUp(KeyCode.E))||(Input.GetKeyUp(KeyCode.A))||(Input.GetKeyUp(KeyCode.D)))
        {
            calliope.SetBool("flyinplace", true);
            calliope.SetBool("feeding", false);
            calliope.SetBool("flyleft", false);
            calliope.SetBool("flyright", false);
        }
        if (Input.GetKeyDown(KeyCode.Space))
        {
            calliope.SetBool("takeoff", true);
            calliope.SetBool("idle", false);
            calliope.SetBool("fly", false);
            calliope.SetBool("landing", true);
            calliope.SetBool("flyinplace", false);
        }
        if (Input.GetKeyDown(KeyCode.E))
        {
            calliope.SetBool("feeding", true);
            calliope.SetBool("fly", false);
            calliope.SetBool("flyinplace", false);
        }
        if (Input.GetKeyDown(KeyCode.W))
        {
            calliope.SetBool("fly", true);
            calliope.SetBool("flyinplace", false);
        }
        if (Input.GetKeyDown(KeyCode.A))
        {
            calliope.SetBool("flyleft", true);
            calliope.SetBool("fly", false);
            calliope.SetBool("flyinplace", false);
        }
        if (Input.GetKeyDown(KeyCode.D))
        {
            calliope.SetBool("flyright", true);
            calliope.SetBool("fly", false);
            calliope.SetBool("flyinplace", false);
        }
        if (Input.GetKeyDown(KeyCode.RightControl))
        {
            MainCamera.GetComponent<CameraFollow>().enabled = false;
        }
        if (Input.GetKeyUp(KeyCode.RightControl))
        {
            MainCamera.GetComponent<CameraFollow>().enabled = true;
        }

    }
}
