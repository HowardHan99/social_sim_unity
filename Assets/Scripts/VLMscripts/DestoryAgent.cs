using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class DestoryAgent : MonoBehaviour
{
    public float destroyDelay = 10f;
    void OnTriggerEnter(Collider other)
    {
        if(other.CompareTag("DestoryObj"))
        {
            Destroy(other.gameObject, destroyDelay);
        }
    }
}
