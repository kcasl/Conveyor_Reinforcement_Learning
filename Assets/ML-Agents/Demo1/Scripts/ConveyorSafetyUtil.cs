using UnityEngine;
using SplineMeshTools.Misc;

public static class ConveyorSafetyUtil
{
    public static void UnregisterFromConveyors(GameObject targetObject)
    {
        if (targetObject == null)
        {
            return;
        }

        Rigidbody rb = targetObject.GetComponent<Rigidbody>();
        if (rb == null)
        {
            return;
        }

        ConveyorBeltMover[] conveyors = Object.FindObjectsOfType<ConveyorBeltMover>();
        for (int i = 0; i < conveyors.Length; i++)
        {
            conveyors[i].RemoveTrackedRigidbody(rb);
        }
    }
}
