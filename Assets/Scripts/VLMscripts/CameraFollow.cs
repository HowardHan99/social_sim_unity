using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CameraFollow : MonoBehaviour
{
    Transform playerPosi;
    public float offsetZ = 5.0f;
    public float smoothFactor = 2.0f;

    // Start is called before the first frame update
    void Start()
    {
        playerPosi = FindObjectOfType<PlayerController>().transform;
    }

    // Update is called once per frame
    void Update()
    {
        FollowPlayer();
    }

    public void FollowPlayer()
    {
        Vector3 targetPosi = new Vector3(playerPosi.position.x, transform.position.y, playerPosi.position.z - offsetZ);

        //cam smooth
        transform.position = Vector3.Lerp(transform.position, targetPosi, smoothFactor * Time.deltaTime);
    }
}
