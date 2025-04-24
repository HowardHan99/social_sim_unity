using System.Collections;
using System.Collections.Generic;
using UnityEngine;


public class PlayerController : MonoBehaviour
{
    private CharacterController controller;
    private Animator animator;

    public float moveSpeed = 4.0f;
    public float rotationSpeed = 5.0f;

    [Header("Movement System")]
    public float walkSpeed = 4.0f;
    public float runSpeed = 8.0f;

    // Start is called before the first frame update
    void Start()
    {
        controller = GetComponent<CharacterController>();
        animator = GetComponentInChildren<Animator>();
    }

    // Update is called once per frame
    void Update()
    {
        PlayerMove();
    }

    /*    public void PlayerMove()
        {
            float hori = Input.GetAxisRaw("Horizontal");
            float vert = Input.GetAxisRaw("Vertical");

            //Movement dir & velocity
            Vector3 dir = new Vector3(hori, 0f, vert).normalized;
            Vector3 moveVelocity = moveSpeed * Time.deltaTime * dir;

            //check running requirements
            if (Input.GetButton("Sprint"))
            {
                moveSpeed = runSpeed;
                animator.SetBool("Running", true);
            }
            else
            {
                moveSpeed = walkSpeed;
                animator.SetBool("Running", false);
            }
            //check movement
            if(dir.magnitude >= 0.1f)
            {
                //look at the dir
                transform.rotation = Quaternion.LookRotation(dir);

                //move
                controller.Move(moveVelocity);
            }

            animator.SetFloat("Speed", moveVelocity.magnitude);
        }*/

    public void PlayerMove()
    {
        float moveInput = Input.GetAxisRaw("Vertical"); // Forward and backward movement
        float rotateInput = Input.GetAxisRaw("Horizontal"); // Left and right rotation

        // Calculate forward movement
        Vector3 moveDirection = transform.forward * moveInput;
        Vector3 moveVelocity = moveSpeed * Time.deltaTime * moveDirection.normalized;

        // Check for sprinting
        if (Input.GetButton("Sprint"))
        {
            moveSpeed = runSpeed;
            animator.SetBool("Running", true);
        }
        else
        {
            moveSpeed = walkSpeed;
            animator.SetBool("Running", false);
        }

        // Apply movement
        if (moveDirection.magnitude >= 0.1f)
        {
            controller.Move(moveVelocity);
        }

        // Calculate rotation
        float rotation = rotateInput * rotationSpeed * Time.deltaTime;
        transform.Rotate(0, rotation, 0);

        // Update animator speed
        animator.SetFloat("Speed", moveVelocity.magnitude);
    }

}
