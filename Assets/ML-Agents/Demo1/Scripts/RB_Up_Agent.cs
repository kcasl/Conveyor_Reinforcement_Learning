using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using System.Collections.Generic;
using UnityEngine;

public class RB_Up_Agent : Agent
{
    [Header("Movement")]
    [SerializeField] private float moveSpeed = 3f;
    [SerializeField] private float turnSpeed = 720f;
    [SerializeField] private Transform agentSpawnPoint;
    [SerializeField] private Vector3 initialRotationEuler;

    [Header("Pick / Place")]
    [SerializeField] private Transform holdPoint;

    [Header("Trigger Tags")]
    [SerializeField] private string conveyorPickupZoneTag = "ConveyorPickupZone";
    [SerializeField] private string smallTruckZoneTag = "SmallTruckZone";
    [SerializeField] private string largeTruckZoneTag = "LargeTruckZone";
    [SerializeField] private string convTag = "Conv";

    [Header("Rewards")]
    [SerializeField] private float grabBoxReward = 0.2f;
    [SerializeField] private float correctTruckReward = 1.0f;
    [SerializeField] private float wrongTruckPenalty = -1.0f;
    [SerializeField] private float enterPickupZoneReward = 0.05f;
    [SerializeField] private float enterTruckZoneWithBoxReward = 0.1f;
    [SerializeField] private float invalidActionPenalty = -0.02f;
    [SerializeField] private float noDeliveryPenalty = -1.0f;
    [SerializeField] private float stepPenalty = -0.001f;
    [SerializeField] private float convContactPenalty = -0.3f;
    [SerializeField] private int boxesPerEpisode = 10;

    private bool isInConveyorPickupZone;
    private bool isInSmallTruckZone;
    private bool isInLargeTruckZone;
    private bool hasDeliveredToTruck;
    private int loadedBoxCount;

    private GameObject heldBoxObject;
    private Rigidbody heldBoxRb;
    private Collider currentPickupZoneCollider;

    public override void OnEpisodeBegin()
    {
        ClearAllBoxesInLevel();

        transform.position = agentSpawnPoint != null ? agentSpawnPoint.position : transform.position;
        transform.rotation = Quaternion.Euler(initialRotationEuler);
        isInConveyorPickupZone = false;
        isInSmallTruckZone = false;
        isInLargeTruckZone = false;
        hasDeliveredToTruck = false;
        loadedBoxCount = 0;
        currentPickupZoneCollider = null;
        ClearHeldBox();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation(transform.localPosition);
        sensor.AddObservation(isInConveyorPickupZone ? 1f : 0f);
        sensor.AddObservation(isInSmallTruckZone ? 1f : 0f);
        sensor.AddObservation(isInLargeTruckZone ? 1f : 0f);
        sensor.AddObservation(heldBoxObject != null ? 1f : 0f);
        sensor.AddObservation((float)loadedBoxCount / Mathf.Max(1, boxesPerEpisode));
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        // Discrete action layout:
        // branch 0: Move X (0 stay, 1 left, 2 right)
        // branch 1: Move Z (0 stay, 1 backward, 2 forward)
        // branch 2: Interaction (0 none, 1 grab box, 2 load truck)
        int moveX = actions.DiscreteActions[0];
        int moveZ = actions.DiscreteActions[1];
        int interaction = actions.DiscreteActions[2];

        Vector3 moveDir = Vector3.zero;
        if (moveX == 1) moveDir.x = -1f;
        else if (moveX == 2) moveDir.x = 1f;

        if (moveZ == 1) moveDir.z = -1f;
        else if (moveZ == 2) moveDir.z = 1f;

        if (moveDir.sqrMagnitude > 0.0001f)
        {
            Vector3 normalizedMoveDir = moveDir.normalized;
            transform.position += normalizedMoveDir * moveSpeed * Time.deltaTime;

            float targetZ = -Mathf.Atan2(normalizedMoveDir.x, normalizedMoveDir.z) * Mathf.Rad2Deg;
            Vector3 currentEuler = transform.eulerAngles;
            float newZ = Mathf.MoveTowardsAngle(currentEuler.z, targetZ, turnSpeed * Time.deltaTime);
            transform.eulerAngles = new Vector3(currentEuler.x, currentEuler.y, newZ);
        }

        if (interaction == 1)
        {
            TryGrabBoxFromConveyor();
        }
        else if (interaction == 2)
        {
            TryLoadBoxToTruck();
        }

        AddReward(stepPenalty);

        if (StepCount >= MaxStep - 1 && !hasDeliveredToTruck)
        {
            AddReward(noDeliveryPenalty);
            EndEpisodeAndCleanup();
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        ActionSegment<int> discreteActions = actionsOut.DiscreteActions;
        discreteActions[0] = 0;
        discreteActions[1] = 0;
        discreteActions[2] = 0;

        if (Input.GetKey(KeyCode.A)) discreteActions[0] = 1;
        else if (Input.GetKey(KeyCode.D)) discreteActions[0] = 2;

        if (Input.GetKey(KeyCode.S)) discreteActions[1] = 1;
        else if (Input.GetKey(KeyCode.W)) discreteActions[1] = 2;

        if (Input.GetKey(KeyCode.Alpha1)) discreteActions[2] = 1;
        else if (Input.GetKey(KeyCode.Alpha2)) discreteActions[2] = 2;
    }

    private void TryGrabBoxFromConveyor()
    {
        if (!isInConveyorPickupZone || heldBoxObject != null || holdPoint == null || currentPickupZoneCollider == null)
        {
            AddReward(invalidActionPenalty);
            return;
        }

        BoxItem randomBox = GetRandomBoxInPickupZone();
        if (randomBox == null)
        {
            AddReward(invalidActionPenalty);
            return;
        }

        heldBoxObject = randomBox.gameObject;
        heldBoxRb = heldBoxObject.GetComponent<Rigidbody>();
        if (heldBoxRb != null)
        {
            heldBoxRb.isKinematic = true;
        }

        heldBoxObject.transform.SetParent(holdPoint);
        heldBoxObject.transform.localPosition = Vector3.zero;

        AddReward(grabBoxReward);
    }

    private void TryLoadBoxToTruck()
    {
        if (heldBoxObject == null)
        {
            AddReward(invalidActionPenalty);
            return;
        }

        BoxItem heldItem = heldBoxObject.GetComponent<BoxItem>();
        if (heldItem == null)
        {
            AddReward(invalidActionPenalty);
            return;
        }

        bool inAnyTruckZone = isInSmallTruckZone || isInLargeTruckZone;
        if (!inAnyTruckZone)
        {
            AddReward(invalidActionPenalty);
            return;
        }

        BoxType targetType = isInSmallTruckZone ? BoxType.Small : BoxType.Large;
        heldBoxObject.transform.SetParent(null);

        if (heldItem.Type == targetType)
        {
            AddReward(correctTruckReward);
        }
        else
        {
            AddReward(wrongTruckPenalty);
        }

        Destroy(heldBoxObject);
        heldBoxObject = null;
        heldBoxRb = null;
        hasDeliveredToTruck = true;
        loadedBoxCount++;

        if (loadedBoxCount >= boxesPerEpisode)
        {
            EndEpisodeAndCleanup();
        }
    }

    private void ClearHeldBox()
    {
        if (heldBoxObject != null)
        {
            Destroy(heldBoxObject);
            heldBoxObject = null;
            heldBoxRb = null;
        }
    }

    private void ClearAllBoxesInLevel()
    {
        BoxItem[] allBoxes = FindObjectsOfType<BoxItem>();
        for (int i = 0; i < allBoxes.Length; i++)
        {
            if (allBoxes[i] != null)
            {
                Destroy(allBoxes[i].gameObject);
            }
        }

        currentPickupZoneCollider = null;
        heldBoxObject = null;
        heldBoxRb = null;
    }

    private BoxItem GetRandomBoxInPickupZone()
    {
        Bounds zoneBounds = currentPickupZoneCollider.bounds;
        BoxItem[] allBoxes = FindObjectsOfType<BoxItem>();
        List<BoxItem> candidates = new List<BoxItem>();

        for (int i = 0; i < allBoxes.Length; i++)
        {
            BoxItem box = allBoxes[i];
            if (box == null || box.gameObject == heldBoxObject)
            {
                continue;
            }

            if (zoneBounds.Contains(box.transform.position))
            {
                candidates.Add(box);
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        int randomIndex = Random.Range(0, candidates.Count);
        return candidates[randomIndex];
    }

    private void EndEpisodeAndCleanup()
    {
        ClearAllBoxesInLevel();
        EndEpisode();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag(conveyorPickupZoneTag))
        {
            isInConveyorPickupZone = true;
            currentPickupZoneCollider = other;
            if (heldBoxObject == null)
            {
                AddReward(enterPickupZoneReward);
            }
        }
        else if (other.CompareTag(smallTruckZoneTag))
        {
            isInSmallTruckZone = true;
            if (heldBoxObject != null)
            {
                AddReward(enterTruckZoneWithBoxReward);
            }
        }
        else if (other.CompareTag(largeTruckZoneTag))
        {
            isInLargeTruckZone = true;
            if (heldBoxObject != null)
            {
                AddReward(enterTruckZoneWithBoxReward);
            }
        }

        TryApplyConvContactPenalty(other);
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag(conveyorPickupZoneTag))
        {
            isInConveyorPickupZone = false;
            if (currentPickupZoneCollider == other)
            {
                currentPickupZoneCollider = null;
            }
        }
        else if (other.CompareTag(smallTruckZoneTag))
        {
            isInSmallTruckZone = false;
        }
        else if (other.CompareTag(largeTruckZoneTag))
        {
            isInLargeTruckZone = false;
        }

    }

    private void OnCollisionEnter(Collision collision)
    {
        TryApplyConvContactPenalty(collision.collider);
    }

    private void TryApplyConvContactPenalty(Component contact)
    {
        if (contact != null && contact.CompareTag(convTag))
        {     
            AddReward(convContactPenalty);
        }
    }
}
