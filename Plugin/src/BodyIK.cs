using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SCP106 {

public class BodyIK : MonoBehaviour
{
    protected Animator animator;
    public bool ikActive = false;
    public Transform targetPlayer;

    void Start() {
        animator = GetComponent<Animator>();
    }

    void OnAnimatorIK() {
        if (animator) {
            if (ikActive) {
                animator.SetLookAtWeight(1);
                animator.SetLookAtPosition(targetPlayer.position);
            }
            else {
                animator.SetLookAtWeight(0);
            }
        }
    }
}
}