using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using System.Collections.Generic;
using UnityEngine;

public class RB_Up_Agent : Agent
{
    [Header("Movement")]
    [SerializeField] private float moveSpeed = 3f;
    [SerializeField] private float turnSpeed = 540f;
    [SerializeField] private float movementSkinWidth = 0.02f;
    [SerializeField] private LayerMask movementBlockMask = ~0;
    [SerializeField] private bool enforcePlanarPhysics = true;
    [SerializeField] private Vector3 positionObservationScale = new Vector3(20f, 5f, 20f);
    [SerializeField] private Transform agentSpawnPoint;
    [SerializeField] private Vector3 initialRotationEuler;

    [Header("Pick / Place")]
    [SerializeField] private Transform holdPoint;
    [SerializeField] private Collider cleanupScopeCollider;

    [Header("Trigger Tags")]
    [SerializeField] private string conveyorPickupZoneTag = "ConveyorPickupZone";
    [SerializeField] private string smallTruckZoneTag = "SmallTruckZone";
    [SerializeField] private string largeTruckZoneTag = "LargeTruckZone";

    [Header("Rewards")]
    [SerializeField] private float grabBoxReward = 0.6f;
    [SerializeField] private float correctTruckReward = 1.0f;
    [SerializeField] private float wrongTruckPenalty = -1.0f;
    [SerializeField] private float enterPickupZoneReward = 0.12f;
    [SerializeField] private float enterTruckZoneWithBoxReward = 0.1f;
    [SerializeField] private float invalidActionPenalty = -0.01f;
    [SerializeField] private float noDeliveryPenalty = -1.0f;
    [SerializeField] private float stepPenalty = -0.0005f;
    [SerializeField] private int boxesPerEpisode = 10;
    [SerializeField] private float minAllowedY = -30f;
    [SerializeField] private float outOfMapPenalty = -1.0f;

    private bool isInConveyorPickupZone;
    private bool isInSmallTruckZone;
    private bool isInLargeTruckZone;
    private bool pickupZoneRewardGiven;
    private bool truckZoneRewardGivenForHeldBox;
    private int loadedBoxCount;

    private GameObject heldBoxObject;
    private Rigidbody heldBoxRb;
    private Collider currentPickupZoneCollider;
    private Rigidbody agentRb;
    private float fixedPlaneY;

    public override void Initialize()
    {
        agentRb = GetComponent<Rigidbody>();
        ConfigureAgentPhysics();
    }

    public override void OnEpisodeBegin()
    {
        if (agentRb == null)
        {
            agentRb = GetComponent<Rigidbody>();
        }
        ConfigureAgentPhysics();
        if (heldBoxObject != null)
        {
            ClearHeldBox();
        }
        ClearAllBoxesInLevel();

        Vector3 startPos = agentSpawnPoint != null ? agentSpawnPoint.position : transform.position;
        fixedPlaneY = startPos.y;
        if (agentRb != null)
        {
            agentRb.velocity = Vector3.zero;
            agentRb.angularVelocity = Vector3.zero;
            agentRb.position = startPos;
            agentRb.rotation = Quaternion.Euler(initialRotationEuler);
        }
        else
        {
            transform.position = startPos;
            transform.rotation = Quaternion.Euler(initialRotationEuler);
        }
        isInConveyorPickupZone = false;
        isInSmallTruckZone = false;
        isInLargeTruckZone = false;
        pickupZoneRewardGiven = false;
        truckZoneRewardGivenForHeldBox = false;
        loadedBoxCount = 0;
        currentPickupZoneCollider = null;
        ClearHeldBox();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        Vector3 localPos = transform.localPosition;
        float sx = Mathf.Max(0.0001f, Mathf.Abs(positionObservationScale.x));
        float sy = Mathf.Max(0.0001f, Mathf.Abs(positionObservationScale.y));
        float sz = Mathf.Max(0.0001f, Mathf.Abs(positionObservationScale.z));
        sensor.AddObservation(Mathf.Clamp(localPos.x / sx, -1f, 1f));
        sensor.AddObservation(Mathf.Clamp(localPos.y / sy, -1f, 1f));
        sensor.AddObservation(Mathf.Clamp(localPos.z / sz, -1f, 1f));
        sensor.AddObservation(isInConveyorPickupZone ? 1f : 0f);
        sensor.AddObservation(isInSmallTruckZone ? 1f : 0f);
        sensor.AddObservation(isInLargeTruckZone ? 1f : 0f);
        sensor.AddObservation(heldBoxObject != null ? 1f : 0f);
        BoxItem heldItem = heldBoxObject != null ? heldBoxObject.GetComponent<BoxItem>() : null;
        sensor.AddObservation(heldItem != null && heldItem.Type == BoxType.Small ? 1f : 0f);
        sensor.AddObservation(heldItem != null && heldItem.Type == BoxType.Large ? 1f : 0f);
        sensor.AddObservation((float)loadedBoxCount / Mathf.Max(1, boxesPerEpisode));
    }

    public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
    {
        bool hasBox = heldBoxObject != null;
        bool inAnyTruckZone = isInSmallTruckZone || isInLargeTruckZone;

        // branch 2: 0 none, 1 grab, 2 load
        if (hasBox)
        {
            actionMask.SetActionEnabled(2, 1, false); // cannot grab while holding a box
            if (!inAnyTruckZone)
            {
                actionMask.SetActionEnabled(2, 2, false); // load only in a truck zone
            }
        }
        else
        {
            actionMask.SetActionEnabled(2, 2, false); // cannot load without a box
            if (!isInConveyorPickupZone || currentPickupZoneCollider == null)
            {
                actionMask.SetActionEnabled(2, 1, false); // grab only in pickup zone
            }
        }
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        MaintainPlanarMotion();
        if (CheckOutOfMapAndRestart())
        {
            return;
        }

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
            float moveDistance = moveSpeed * Time.fixedDeltaTime;
            Vector3 delta = normalizedMoveDir * moveDistance;
            if (agentRb != null)
            {
                Vector3 safeDelta = GetSafeMoveDelta(normalizedMoveDir, moveDistance);
                agentRb.MovePosition(agentRb.position + safeDelta);
            }
            else
            {
                transform.position += delta;
            }

            float targetZ = -Mathf.Atan2(normalizedMoveDir.x, normalizedMoveDir.z) * Mathf.Rad2Deg;
            Vector3 currentEuler = agentRb != null ? agentRb.rotation.eulerAngles : transform.rotation.eulerAngles;
            float nextZ = Mathf.MoveTowardsAngle(currentEuler.z, targetZ, turnSpeed * Time.fixedDeltaTime);
            Quaternion targetRotation = Quaternion.Euler(currentEuler.x, currentEuler.y, nextZ);
            if (agentRb != null)
            {
                agentRb.MoveRotation(targetRotation);
            }
            else
            {
                transform.rotation = targetRotation;
            }
        }

        if (CheckOutOfMapAndRestart())
        {
            return;
        }
        MaintainPlanarMotion();

        if (interaction == 1)
        {
            TryGrabBoxFromConveyor();
        }
        else if (interaction == 2)
        {
            TryLoadBoxToTruck();
        }

        AddReward(stepPenalty);

        if (MaxStep > 0 && StepCount >= MaxStep - 1 && loadedBoxCount < boxesPerEpisode)
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
        truckZoneRewardGivenForHeldBox = false;

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

        ConveyorSafetyUtil.UnregisterFromConveyors(heldBoxObject);
        Destroy(heldBoxObject);
        heldBoxObject = null;
        heldBoxRb = null;
        pickupZoneRewardGiven = false;
        truckZoneRewardGivenForHeldBox = false;
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
            ConveyorSafetyUtil.UnregisterFromConveyors(heldBoxObject);
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
            if (allBoxes[i] != null && IsBoxInMyCleanupScope(allBoxes[i].transform.position))
            {
                ConveyorSafetyUtil.UnregisterFromConveyors(allBoxes[i].gameObject);
                Destroy(allBoxes[i].gameObject);
            }
        }

        currentPickupZoneCollider = null;
        heldBoxObject = null;
        heldBoxRb = null;
    }

    private bool IsBoxInMyCleanupScope(Vector3 boxPosition)
    {
        if (cleanupScopeCollider == null)
        {
            return false;
        }

        return cleanupScopeCollider.bounds.Contains(boxPosition);
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

            Collider boxCollider = box.GetComponent<Collider>();
            if (boxCollider != null)
            {
                if (zoneBounds.Intersects(boxCollider.bounds))
                {
                    candidates.Add(box);
                }
            }
            else if (zoneBounds.Contains(box.transform.position))
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
            if (heldBoxObject == null && !pickupZoneRewardGiven)
            {
                AddReward(enterPickupZoneReward);
                pickupZoneRewardGiven = true;
            }
        }
        else if (other.CompareTag(smallTruckZoneTag))
        {
            isInSmallTruckZone = true;
            if (heldBoxObject != null && !truckZoneRewardGivenForHeldBox)
            {
                AddReward(enterTruckZoneWithBoxReward);
                truckZoneRewardGivenForHeldBox = true;
            }
        }
        else if (other.CompareTag(largeTruckZoneTag))
        {
            isInLargeTruckZone = true;
            if (heldBoxObject != null && !truckZoneRewardGivenForHeldBox)
            {
                AddReward(enterTruckZoneWithBoxReward);
                truckZoneRewardGivenForHeldBox = true;
            }
        }
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

    private Vector3 GetSafeMoveDelta(Vector3 moveDirection, float moveDistance)
    {
        if (agentRb == null || moveDistance <= 0f)
        {
            return Vector3.zero;
        }

        if (agentRb.SweepTest(moveDirection, out RaycastHit hit, moveDistance + movementSkinWidth, QueryTriggerInteraction.Ignore))
        {
            bool blockedByMask = (movementBlockMask.value & (1 << hit.collider.gameObject.layer)) != 0;
            if (blockedByMask)
            {
                float allowedDistance = Mathf.Max(0f, hit.distance - movementSkinWidth);
                return moveDirection * allowedDistance;
            }
        }

        return moveDirection * moveDistance;
    }
    private bool CheckOutOfMapAndRestart()
    {
        if (transform.position.y < minAllowedY)
        {
            AddReward(outOfMapPenalty);
            EndEpisodeAndCleanup();
            return true;
        }

        return false;
    }

    private void ConfigureAgentPhysics()
    {
        if (agentRb == null || !enforcePlanarPhysics)
        {
            return;
        }

        agentRb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        agentRb.interpolation = RigidbodyInterpolation.Interpolate;
        agentRb.constraints = RigidbodyConstraints.FreezePositionY
                            | RigidbodyConstraints.FreezeRotationX
                            | RigidbodyConstraints.FreezeRotationY;
    }

    private void MaintainPlanarMotion()
    {
        if (agentRb == null || !enforcePlanarPhysics)
        {
            return;
        }

        Vector3 velocity = agentRb.velocity;
        velocity.y = 0f;
        agentRb.velocity = velocity;

        Vector3 position = agentRb.position;
        position.y = fixedPlaneY;
        agentRb.position = position;
    }

}
