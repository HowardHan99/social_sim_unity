using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CameraCollision : MonoBehaviour
{
    public float minDistance = 1.0f;
    public float maxDistance = 4.0f;
    public float smooth = 10.0f;
    public float returnSmooth = 4.0f;
    public float distanceDeadzone = 0.08f;
    public float collisionBuffer = 0.12f;

    Vector3 playerDir;
    public float distance;

    private float targetDistance;

    void Awake()
    {
        playerDir = transform.localPosition.normalized;
        distance = transform.localPosition.magnitude;
        targetDistance = distance;
    }

    void Update()
    {
        Vector3 desiredCameraPos = transform.parent.TransformPoint(playerDir * maxDistance);
        Vector3 playerPos = transform.parent.position;

        RaycastHit hit;

        if (Physics.Linecast(playerPos, desiredCameraPos, out hit))
        {
            targetDistance = Mathf.Clamp(hit.distance - collisionBuffer, minDistance, maxDistance);
        }
        else
        {
            targetDistance = maxDistance;
        }

        if (Mathf.Abs(targetDistance - distance) <= distanceDeadzone)
        {
            targetDistance = distance;
        }

        float currentSmooth = targetDistance < distance ? smooth : returnSmooth;
        distance = Mathf.Lerp(distance, targetDistance, Time.deltaTime * currentSmooth);
        transform.localPosition = playerDir * distance;
    }
}
