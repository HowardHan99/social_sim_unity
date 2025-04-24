using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

public class FreeCamera : MonoBehaviour
{
    public float movementSpeed = 10f;
    public float fastMovementSpeed = 100f;
    public float freeLookSensitivity = 3f;
    public float zoomSensitivity = 10f;
    public float fastZoomSensitivity = 50f;
    private bool looking = false;

    private Vector3 lastPosition;
    private Vector3 velocity;
    private Vector3 lastVelocity;
    private Vector3 acceleration;

    void Start()
    {
        lastPosition = transform.position;
        velocity = Vector3.zero;
        lastVelocity = Vector3.zero;
        acceleration = Vector3.zero;
    }

    void Update()
    {
        var fastMode = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        var movementSpeed = fastMode ? this.fastMovementSpeed : this.movementSpeed;
        /*
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))
                {
                    transform.position = transform.position + (-transform.right * movementSpeed * Time.deltaTime);
                }

                if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow))
                {
                    transform.position = transform.position + (transform.right * movementSpeed * Time.deltaTime);
                }

                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))
                {
                    transform.position = transform.position + (transform.forward * movementSpeed * Time.deltaTime);
                }

                if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))
                {
                    transform.position = transform.position + (-transform.forward * movementSpeed * Time.deltaTime);
                }

                if (Input.GetKey(KeyCode.Q))
                {
                    transform.position = transform.position + (transform.up * movementSpeed * Time.deltaTime);
                }

                if (Input.GetKey(KeyCode.E))
                {
                    transform.position = transform.position + (-transform.up * movementSpeed * Time.deltaTime);
                }

                if (Input.GetKey(KeyCode.R) || Input.GetKey(KeyCode.PageUp))
                {
                    transform.position = transform.position + (Vector3.up * movementSpeed * Time.deltaTime);
                }

                if (Input.GetKey(KeyCode.F) || Input.GetKey(KeyCode.PageDown))
                {
                    transform.position = transform.position + (-Vector3.up * movementSpeed * Time.deltaTime);
                }*/

        Vector3 moveDelta = Vector3.zero;

        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) moveDelta += -transform.right;
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) moveDelta += transform.right;
        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) moveDelta += transform.forward;
        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) moveDelta += -transform.forward;
        if (Input.GetKey(KeyCode.Q)) moveDelta += transform.up;
        if (Input.GetKey(KeyCode.E)) moveDelta += -transform.up;
        if (Input.GetKey(KeyCode.R) || Input.GetKey(KeyCode.PageUp)) moveDelta += Vector3.up;
        if (Input.GetKey(KeyCode.F) || Input.GetKey(KeyCode.PageDown)) moveDelta += -Vector3.up;

        var moveSpeed = fastMode ? fastMovementSpeed : movementSpeed;
        transform.position += moveDelta * moveSpeed * Time.deltaTime;


        if (looking)
        {
            float newRotationX = transform.localEulerAngles.y + Input.GetAxis("Mouse X") * freeLookSensitivity;
            float newRotationY = transform.localEulerAngles.x - Input.GetAxis("Mouse Y") * freeLookSensitivity;
            transform.localEulerAngles = new Vector3(newRotationY, newRotationX, 0f);
        }

        float axis = Input.GetAxis("Mouse ScrollWheel");
        if (axis > 0)
        {
            GetComponent<Camera>().fieldOfView--;
        }
        else if (axis < 0)
        {
            GetComponent<Camera>().fieldOfView++;
        }

        if (Input.GetKeyDown(KeyCode.Mouse1))
        {
            StartLooking();
        }
        else if (Input.GetKeyUp(KeyCode.Mouse1))
        {
            StopLooking();
        }


        velocity = (transform.position - lastPosition) / Time.deltaTime;
        acceleration = (velocity - lastVelocity) / Time.deltaTime;

        // Store current values for next frame
        lastPosition = transform.position;
        lastVelocity = velocity;
    }

    void OnDisable()
    {
        StopLooking();
    }
    public void StartLooking()
    {
        looking = true;
        Cursor.visible = false;
        Cursor.lockState = CursorLockMode.Locked;
    }
    public void StopLooking()
    {
        looking = false;
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
    }


    public string GetMovementStatus()
    {
        // Calculate speed and acceleration magnitudes
        float speed = velocity.magnitude;
        float accelerationMagnitude = acceleration.magnitude;

        // Determine primary and secondary directions
        List<string> directions = new List<string>();
        Vector3 dir = velocity.normalized;

        if (speed < 0.01f)
        {
            return "speed: 0.0 m/s, acceleration: 0.0 m/s², direction: stationary";
        }

        // Check forward/backward movement
        if (Vector3.Dot(dir, transform.forward) > 0.3f)
            directions.Add("forward");
        else if (Vector3.Dot(dir, -transform.forward) > 0.3f)
            directions.Add("backward");

        // Check right/left movement
        if (Vector3.Dot(dir, transform.right) > 0.3f)
            directions.Add("right");
        else if (Vector3.Dot(dir, -transform.right) > 0.3f)
            directions.Add("left");

        // Check up/down movement
        if (Vector3.Dot(dir, transform.up) > 0.3f)
            directions.Add("up");
        else if (Vector3.Dot(dir, -transform.up) > 0.3f)
            directions.Add("down");

        string directionString = string.Join(" and ", directions);
        if (string.IsNullOrEmpty(directionString))
            directionString = "mixed";

        return string.Format("speed: {0:F1} m/s, acceleration: {1:F1} m/s², direction: {2}",
            speed,
            accelerationMagnitude,
            directionString
        );
    }
}