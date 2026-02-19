using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

public class RB_Down_Agent : Agent
{
    [Header("Movement")]
    [SerializeField] private float moveSpeed = 3f;
    [SerializeField] private float turnSpeed = 720f;
    [SerializeField] private float movementSkinWidth = 0.02f;
    [SerializeField] private LayerMask movementBlockMask = ~0;
    [SerializeField] private Transform agentSpawnPoint;
    [SerializeField] private Vector3 initialRotationEuler;

    [Header("Box Prefabs")]
    [SerializeField] private GameObject smallBoxPrefab;
    [SerializeField] private GameObject largeBoxPrefab;

    [Header("Pick / Place Points")]
    [SerializeField] private Transform holdPoint;
    [SerializeField] private Transform conveyorDropPoint;

    [Header("Trigger Tags")]
    [SerializeField] private string truckLoadZoneTag = "TruckLoadZone";
    [SerializeField] private string conveyorInputZoneTag = "ConveyorInputZone";
    [SerializeField] private string upAgentAreaTag = "UpArea";

    [Header("Rewards")]
    [SerializeField] private float loadFromTruckReward = 0.2f;
    [SerializeField] private float placeOnConveyorReward = 1.0f;
    [SerializeField] private float enterTruckZoneReward = 0.05f;
    [SerializeField] private float enterConveyorZoneWithBoxReward = 0.1f;
    [SerializeField] private float invalidActionPenalty = -0.02f;
    [SerializeField] private float noConveyorPlacementPenalty = -1.0f;
    [SerializeField] private float stepPenalty = -0.001f;
    [SerializeField] private float enterUpAreaPenalty = -0.5f;
    [SerializeField] private float stayInUpAreaPenaltyPerStep = -0.01f;
    [SerializeField] private float minAllowedY = -30f;
    [SerializeField] private float outOfMapPenalty = -1.0f;

    private bool isInTruckLoadZone;
    private bool isInConveyorInputZone;
    private bool isInUpAgentArea;
    private bool hasPlacedOnConveyor;

    private GameObject heldBoxObject;
    private Rigidbody heldBoxRb;
    private Rigidbody agentRb;

    public override void Initialize()
    {
        agentRb = GetComponent<Rigidbody>();
    }

    public override void OnEpisodeBegin()
    {
        if (agentRb == null)
        {
            agentRb = GetComponent<Rigidbody>();
        }

        transform.position = agentSpawnPoint != null ? agentSpawnPoint.position : transform.position;
        transform.rotation = Quaternion.Euler(initialRotationEuler);
        isInTruckLoadZone = false;
        isInConveyorInputZone = false;
        isInUpAgentArea = false;
        hasPlacedOnConveyor = false;
        ClearHeldBox();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation(transform.localPosition);
        sensor.AddObservation(isInTruckLoadZone ? 1f : 0f);
        sensor.AddObservation(isInConveyorInputZone ? 1f : 0f);
        sensor.AddObservation(heldBoxObject != null ? 1f : 0f);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (CheckOutOfMapAndRestart())
        {
            return;
        }

        // Discrete action layout:
        // branch 0: Move X (0 stay, 1 left, 2 right)
        // branch 1: Move Z (0 stay, 1 backward, 2 forward)
        // branch 2: Interaction (0 none, 1 take from truck, 2 place on conveyor)
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
            Vector3 currentEuler = transform.eulerAngles;
            float newZ = Mathf.MoveTowardsAngle(currentEuler.z, targetZ, turnSpeed * Time.deltaTime);
            Vector3 nextEuler = new Vector3(currentEuler.x, currentEuler.y, newZ);
            if (agentRb != null)
            {
                agentRb.MoveRotation(Quaternion.Euler(nextEuler));
            }
            else
            {
                transform.eulerAngles = nextEuler;
            }
        }

        if (CheckOutOfMapAndRestart())
        {
            return;
        }

        if (interaction == 1)
        {
            TryTakeRandomBoxFromTruck();
        }
        else if (interaction == 2)
        {
            TryPlaceBoxOnConveyor();
        }

        AddReward(stepPenalty);
        if (isInUpAgentArea)
        {
            AddReward(stayInUpAreaPenaltyPerStep);
        }

        if (StepCount >= MaxStep - 1 && !hasPlacedOnConveyor)
        {
            AddReward(noConveyorPlacementPenalty);
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

    private void TryTakeRandomBoxFromTruck()
    {
        if (!isInTruckLoadZone || heldBoxObject != null)
        {
            AddReward(invalidActionPenalty);
            return;
        }

        bool makeSmallBox = Random.value < 0.5f;
        GameObject prefab = makeSmallBox ? smallBoxPrefab : largeBoxPrefab;
        if (prefab == null || holdPoint == null)
        {
            AddReward(invalidActionPenalty);
            return;
        }

        heldBoxObject = Instantiate(prefab, holdPoint.position, Quaternion.identity);
        heldBoxRb = heldBoxObject.GetComponent<Rigidbody>();
        if (heldBoxRb != null)
        {
            heldBoxRb.isKinematic = true;
        }

        heldBoxObject.transform.SetParent(holdPoint);
        heldBoxObject.transform.localPosition = Vector3.zero;
        AddReward(loadFromTruckReward);
    }

    private void TryPlaceBoxOnConveyor()
    {
        if (!isInConveyorInputZone || heldBoxObject == null || conveyorDropPoint == null)
        {
            AddReward(invalidActionPenalty);
            return;
        }

        heldBoxObject.transform.SetParent(null);
        heldBoxObject.transform.position = conveyorDropPoint.position;

        if (heldBoxRb != null)
        {
            heldBoxRb.isKinematic = false;
            heldBoxRb.velocity = Vector3.zero;
        }

        heldBoxObject = null;
        heldBoxRb = null;
        hasPlacedOnConveyor = true;

        AddReward(placeOnConveyorReward);
        EndEpisode();
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

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag(truckLoadZoneTag))
        {
            isInTruckLoadZone = true;
            if (heldBoxObject == null)
            {
                AddReward(enterTruckZoneReward);
            }
        }
        else if (other.CompareTag(conveyorInputZoneTag))
        {
            isInConveyorInputZone = true;
            if (heldBoxObject != null)
            {
                AddReward(enterConveyorZoneWithBoxReward);
            }
        }
        else if (other.CompareTag(upAgentAreaTag))
        {
            isInUpAgentArea = true;
            AddReward(enterUpAreaPenalty);
        }

    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag(truckLoadZoneTag))
        {
            isInTruckLoadZone = false;
        }
        else if (other.CompareTag(conveyorInputZoneTag))
        {
            isInConveyorInputZone = false;
        }
        else if (other.CompareTag(upAgentAreaTag))
        {
            isInUpAgentArea = false;
        }
    }

    private Vector3 GetSafeMoveDelta(Vector3 moveDirection, float moveDistance)
    {
        if (agentRb == null || moveDistance <= 0f)
        {
            return Vector3.zero;
        }

        // SweepTest prevents passing through colliders when moving fast.
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
            EndEpisode();
            return true;
        }

        return false;
    }
}
