using System;
using System.Collections.Generic;
using Basis;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using UnityEngine;

namespace Hai.Basis.CilboxPencil
{
    [Cilboxable]
    public class Pencil : MonoBehaviour
    {
        private const float CommitThresholdDistance = 0.005f;
        private const float CommitThresholdDistanceSquared = CommitThresholdDistance * CommitThresholdDistance;
        private const float CommitThresholdAngleDeg = 15f;

        // Framerate Interpolation
        private const float TooFarThresholdDistance = 0.03f;
        private const float TooFarThresholdDistanceSquared = TooFarThresholdDistance * TooFarThresholdDistance;
        private const float RebuildDistance = TooFarThresholdDistance * 0.3f;

        // PressingOnCollider Raycast
        private const int PressingOnColliderRaycastMask = ~((1 << 2) | (1 << 3) | (1 << 5) | (1 << 6) | (1 << 7) | (1 << 8) | (1 << 9) | (1 << 10) | (1 << 11));
        private const float PressingOnColliderRaycastBackingDistance = 0.3f;
        private const float PressingOnColliderRaycastMagnetismDistance = 0.002f;
        private const float PressingOnColliderNormalBackawayDistance = 0.0001f;

        // Networking
        private const int CapacityIncrease = 200;
        private readonly Vector3 TerminationMagicVector = new(0, -10_000, 0);

        //

        public BasisPickupInteractable pickup;
        public Transform tip;
        public LineRenderer mainLineRenderer;
        public Transform modelMover;

        public GameObject instantiateMe;
        public MeshFilter instantiateMe_Filter;
        public MeshRenderer instantiateMe_Renderer;

        private bool _isEnabled;
        private bool _hasPreviousPoint;

        // General
        private float _pickupTime;
        private bool _isMisclick;
        private Vector3 _previouslyCommittedPoint;

        // Framerate Interpolation
        private bool _hasPreviousPreviousPoint;
        private Vector3 _previousPreviousCommittedPoint;
        private Quaternion _previouslyCommittedQuat = Quaternion.identity;

        // New line rendering
        private float _tipScale;
        public Mesh _currentMeshNullable;
        private readonly List<Vector3> _currentVerts = new();
        private readonly List<Vector3> _currentNormals = new();
        private readonly List<ushort> _currentTris = new();
        private Vector3 _boundsMin;
        private Vector3 _boundsMax;
        private ushort _nextVert;

        private MeshRenderer _holderMeshRenderer;

        // Pressing on Collider
        private bool _cannotExecutePressingOnCollider;
        private bool _isPickedUp;
        private bool _isPressingOnCollider;
        private Quaternion _lastPressingOnColliderRotation;
        private Vector3 _lastPressingOnColliderPosition;

        // Networking
        private BasisNetworkShim _network;
        private bool _needsNetworkUpdate;
        private bool _isNetworkReady;
        private Vector3[] _points = new Vector3[10];
        private Quaternion[] _quats = new Quaternion[10];
        private int _memoryLength = 0;
        // private int _numberOfGroups;
        // private Dictionary<int, List<Vector3>> _groupToNetworkedPoints = new();
        // private Dictionary<int, List<Quaternion>> _groupToNetworkedQuats = new();

        //

        public void Start() { WhenEnable(); }
        public void OnEnable() { WhenEnable(); }
        private void OnDisable() { _isEnabled = false; }
        private void WhenEnable()
        {
            if (_isEnabled) return; // Cilbox quirk
            _isEnabled = true;

            pickup.OnInteractEndEvent.AddListener(WhenDrop);
            pickup.OnInteractStartEvent.AddListener(WhenPickup);
            pickup.OnPickupUse.AddListener(WhileUsing);

            _network = SafeUtil.MakeNetworkable(this);
            _network.NetworkReady += WhenNetworkReady;
            _network.NetworkMessageReceived += WhenNetworkMessageReceived;

            _tipScale = tip.lossyScale.x;

            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        }

        private void WhenPickup(BasisInput input)
        {
            _pickupTime = Time.time;
            _isPickedUp = true;
        }

        private void WhenDrop(BasisInput input)
        {
            _isMisclick = false;
            _isPickedUp = false;
        }

        private void Update()
        {
            if (_cannotExecutePressingOnCollider) return;

            if (_isPickedUp)
            {
                var raycastPos = tip.position - tip.forward * PressingOnColliderRaycastBackingDistance;
                if (Physics.Raycast(raycastPos, tip.forward, out var hitInfo, PressingOnColliderRaycastBackingDistance + PressingOnColliderRaycastMagnetismDistance, PressingOnColliderRaycastMask))
                {
                    modelMover.position = hitInfo.point;
                    _lastPressingOnColliderRotation = Quaternion.LookRotation(-hitInfo.normal, tip.up);
                    _lastPressingOnColliderPosition = hitInfo.point + hitInfo.normal * PressingOnColliderNormalBackawayDistance;
                    _isPressingOnCollider = true;
                    StartOrContinue(_lastPressingOnColliderPosition, _lastPressingOnColliderRotation);
                }
                else
                {
                    modelMover.localPosition = Vector3.zero;
                    if (_isPressingOnCollider)
                    {
                        Terminate(_lastPressingOnColliderPosition, _lastPressingOnColliderRotation);
                        _isPressingOnCollider = false;
                    }
                }
            }
            else
            {
                if (_isPressingOnCollider)
                {
                    Terminate(_lastPressingOnColliderPosition, _lastPressingOnColliderRotation);
                    _isPressingOnCollider = false;
                }
            }
        }

        private void WhileUsing(BasisPickUpUseMode useMode)
        {
            if (useMode == BasisPickUpUseMode.OnPickUpUseDown || useMode == BasisPickUpUseMode.OnPickUpStillDown)
            {
                if (Time.time - _pickupTime < 0.1f)
                {
                    _isMisclick = true;
                }

                if (!_isMisclick)
                {
                    _cannotExecutePressingOnCollider = true;
                    _isPressingOnCollider = false;
                    StartOrContinue(tip.position, tip.rotation);
                }
            }
            else
            {
                if (!_isMisclick) Terminate(tip.position, tip.rotation);
                _isMisclick = false;
                _cannotExecutePressingOnCollider = false;
            }
        }

        public void StartOrContinue(Vector3 tipPosition, Quaternion tipRotation)
        {
            if (!_hasPreviousPoint || IsDifferentEnoughFromPrevious(tipPosition, tipRotation))
            {
                if (!_hasPreviousPoint)
                {
                    if (null != mainLineRenderer) mainLineRenderer.positionCount = 0;
                }
                if (_hasPreviousPreviousPoint && IsTooDifferentFromPrevious(tipPosition))
                {
                    // TODO: This interpolation doesn't do a great job at curves, (partly) because it's immediately building the current point
                    // without taking into account future information about the next point in the curve.
                    // Waiting until the next point to refine the curve would help, rather than having all points being final the moment they are drawn.

                    var numberOfThings = 2 + Mathf.FloorToInt((tipPosition - _previouslyCommittedPoint).magnitude / RebuildDistance);
                    if (numberOfThings > 20) numberOfThings = 20;
                    // In theory, numberOfThings should have at least 3 values.

                    var prePrevious = _previousPreviousCommittedPoint;
                    var previous = _previouslyCommittedPoint;
                    var current = tipPosition;
                    // var direction0 = _lastHasDirectionVector
                        // ? _directionVector
                        // : (previous - prePrevious) * 0.5f; // FIXME: A bezier interpolation is probably unnecessary if the direction vectors are correlated
                    var direction0 = (previous - prePrevious) * 0.25f; // FIXME: A bezier interpolation is probably unnecessary if the direction vectors are correlated
                    var direction1 = (previous - current) * 0.25f;
                    // var direction1 = (previous + direction0) - current;
                    // var direction1 = Vector3.zero;
                    PrepareSeilerInterpolation(previous, current, direction0, direction1);
                    var b0 = PrepareSeilerInterpolation_result[0];
                    var b3 = PrepareSeilerInterpolation_result[1];
                    var s1 = PrepareSeilerInterpolation_result[2];
                    var s2 = PrepareSeilerInterpolation_result[3];
                    var quatFrom = _previouslyCommittedQuat;

                    var k = 1;
                    while (k < numberOfThings)
                    {
                        // k will never be equal to numberOfThings, so the last t is not equal to 1.
                        // The point for t=1 is handled outside the loop.
                        var t = ((float)k) / numberOfThings;

                        var virtualPosition = SeilerInterpolate(b0, b3, s1, s2, t);
                        var virtualRotation = Quaternion.Slerp(quatFrom, tipRotation, t);
                        EnsureCapacity();
                        _points[_memoryLength] = virtualPosition; // TODO: Maybe we could network just the input tip points, and have the remote simulate the interpolation too
                        _quats[_memoryLength] = virtualRotation;
                        _memoryLength++;
                        _needsNetworkUpdate = true;

                        // FIXME: We should apply the mesh to the renderer and calculate the bounds only at the end of this entire function.
                        BuildMeshProgressively(virtualPosition, tipRotation);
                        k++;
                    }
                }

                EnsureCapacity();
                _points[_memoryLength] = tipPosition;
                _quats[_memoryLength] = tipRotation;
                _memoryLength++;
                _needsNetworkUpdate = true;
                _previousPreviousCommittedPoint = _previouslyCommittedPoint; // Just because this executes doesn't mean we actually had a previous committed point.
                _previouslyCommittedPoint = tipPosition;
                _previouslyCommittedQuat = tipRotation;

                BuildMeshProgressively(tipPosition, tipRotation);

                if (null != mainLineRenderer)
                {
                    mainLineRenderer.positionCount++;
                    mainLineRenderer.SetPosition(mainLineRenderer.positionCount - 1, tipPosition);
                }
            }
            else
            {
                _currentVerts[_nextVert - 2] = tipPosition + tipRotation * (new Vector3(1, 0f, 0) * _tipScale);
                _currentVerts[_nextVert - 1] = tipPosition + tipRotation * (new Vector3(-1, 0f, 0) * _tipScale);

                var normal = tipRotation * -Vector3.forward;
                _currentNormals[_nextVert - 2] = normal;
                _currentNormals[_nextVert - 1] = normal;
                _currentMeshNullable.SetVertices(_currentVerts);

                if (null != mainLineRenderer)
                {
                    mainLineRenderer.SetPosition(mainLineRenderer.positionCount - 1, tipPosition);
                }
            }

            _hasPreviousPreviousPoint = _hasPreviousPoint;
            _hasPreviousPoint = true;
        }

        private bool IsDifferentEnoughFromPrevious(Vector3 tipPosition, Quaternion tipRotation)
        {
            return Vector3.SqrMagnitude(_previouslyCommittedPoint - tipPosition) > CommitThresholdDistanceSquared
                || Quaternion.Angle(_previouslyCommittedQuat, tipRotation) > CommitThresholdAngleDeg;
        }

        private bool IsTooDifferentFromPrevious(Vector3 tipPosition)
        {
            return Vector3.SqrMagnitude(_previouslyCommittedPoint - tipPosition) > TooFarThresholdDistanceSquared;
        }

        public void Terminate(Vector3 tipPosition, Quaternion tipRotation)
        {
            if (!_hasPreviousPoint) return;

            StartOrContinue(tipPosition, tipRotation);

            EnsureCapacity();
            _points[_memoryLength] = TerminationMagicVector;
            _quats[_memoryLength] = Quaternion.identity;
            _memoryLength++;
            _needsNetworkUpdate = true;


            _hasPreviousPoint = false;
            _hasPreviousPreviousPoint = false;
            _currentMeshNullable = null;
            _holderMeshRenderer = null;
        }

        private void BuildMeshProgressively(Vector3 tipPosition, Quaternion tipRotation)
        {
            if (_currentMeshNullable == null)
            {
                _currentVerts.Clear();
                _currentNormals.Clear();
                // +  0 2 ... 4 6 8
                // -  1 3 ... 5 7 9
                // Triangles: CCW
                _currentVerts.Add(tipPosition + tipRotation * (new Vector3(1, -0.05f, 0) * _tipScale));
                _currentVerts.Add(tipPosition + tipRotation * (new Vector3(-1, -0.05f, 0) * _tipScale));
                _currentVerts.Add(tipPosition + tipRotation * (new Vector3(1, 0f, 0) * _tipScale));
                _currentVerts.Add(tipPosition + tipRotation * (new Vector3(-1, 0f, 0) * _tipScale));
                _boundsMin = _currentVerts[0];
                _boundsMax = _currentVerts[3];
                _currentTris.Clear();
                _currentTris.Add(0); _currentTris.Add(1); _currentTris.Add(2);
                _currentTris.Add(3); _currentTris.Add(2); _currentTris.Add(1);
                var normal = tipRotation * -Vector3.forward;
                _currentNormals.Add(normal);
                _currentNormals.Add(normal);
                _currentNormals.Add(normal);
                _currentNormals.Add(normal);
                _nextVert = 4;

                _currentMeshNullable = new Mesh();
                _currentMeshNullable.MarkDynamic();
                _currentMeshNullable.SetVertices(_currentVerts);
                _currentMeshNullable.SetNormals(_currentNormals);
                _currentMeshNullable.SetTriangles(_currentTris, 0);

                instantiateMe_Filter.mesh = _currentMeshNullable;
                var holder = Instantiate(instantiateMe, transform);
                instantiateMe_Filter.mesh = null;
                _holderMeshRenderer = holder.GetComponent<MeshRenderer>();
                _holderMeshRenderer.bounds = CalculateBounds();

                holder.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                holder.transform.localScale = Vector3.one;
                holder.SetActive(true);
            }
            else
            {
                var n = _nextVert;
                // (N-2) ... (N)
                // (N-1) ... (N+1)
                _currentVerts.Add(tipPosition + tipRotation * (new Vector3(1, 0f, 0) * _tipScale));
                var newPos2 = tipPosition + tipRotation * (new Vector3(-1, 0f, 0) * _tipScale);
                _currentVerts.Add(newPos2);

                var normal = tipRotation * -Vector3.forward;
                _currentNormals.Add(normal);
                _currentNormals.Add(normal);

                var anyBoundsChanged = false;
                if (newPos2.x < _boundsMin.x) { _boundsMin.x = newPos2.x; anyBoundsChanged = true; }
                if (newPos2.x > _boundsMax.x) { _boundsMax.x = newPos2.x; anyBoundsChanged = true; }
                if (newPos2.y < _boundsMin.y) { _boundsMin.y = newPos2.y; anyBoundsChanged = true; }
                if (newPos2.y > _boundsMax.y) { _boundsMax.y = newPos2.y; anyBoundsChanged = true; }
                if (newPos2.z < _boundsMin.z) { _boundsMin.z = newPos2.z; anyBoundsChanged = true; }
                if (newPos2.z > _boundsMax.z) { _boundsMax.z = newPos2.z; anyBoundsChanged = true; }

                // (N-2) ... (N)
                // (N-1)
                //             /
                //                      (N)
                //            (N-1) ... (N+1)
                _currentTris.Add((ushort)(n - 2)); _currentTris.Add((ushort)(n - 1)); _currentTris.Add(n);
                _currentTris.Add((ushort)(n + 1)); _currentTris.Add(n); _currentTris.Add((ushort)(n - 1));

                _currentMeshNullable.SetVertices(_currentVerts);
                _currentMeshNullable.SetNormals(_currentNormals);
                _currentMeshNullable.SetTriangles(_currentTris, 0);

                if (anyBoundsChanged)
                {
                    _holderMeshRenderer.bounds = CalculateBounds();
                }

                _nextVert = (ushort)(n + 2);
            }
        }

        private Bounds CalculateBounds()
        {
            return new Bounds(_boundsMin + (_boundsMax - _boundsMin) * 0.5f, _boundsMax - _boundsMin);
        }

        private Vector3[] PrepareSeilerInterpolation_result = new Vector3[4]; // CILBOX: Cilbox doesn't accept "out var" nor "tuple" return values at this time of writing.
        private void PrepareSeilerInterpolation(Vector3 pos0, Vector3 pos1, Vector3 direction0, Vector3 direction1)
        {
            var b0 = pos0;
            var b3 = pos1;
            var b1 = b0 + direction0;
            var b2 = b3 + direction1;

            FromBezierToSeiler(b0, b1, b2, b3);
            PrepareSeilerInterpolation_result[0] = b0;
            PrepareSeilerInterpolation_result[1] = b3;
        }

        private void FromBezierToSeiler(Vector3 b0, Vector3 b1, Vector3 b2, Vector3 b3)
        {
            var s1 = 3 * b1 - b0 - b3;
            var s2 = 3 * b2 - b3 - b0;
            PrepareSeilerInterpolation_result[2] = s1;
            PrepareSeilerInterpolation_result[3] = s2;
        }

        /// Based on https://www.cemyuksel.com/research/seilers_interpolation/
        private Vector3 SeilerInterpolate(Vector3 b0, Vector3 b3, Vector3 s1, Vector3 s2, float t)
        {
            var b03 = Vector3.Lerp(b0, b3, t);
            var s12 = Vector3.Lerp(s1, s2, t);
            return Vector3.Lerp(b03, s12, (1 - t) * t);
        }

#region Networking
        private const byte Packet_C2O_RequestInitialization = 101;
        private const byte Packet_A2A_Write = 1;

        private void WhenNetworkReady()
        {
            _isNetworkReady = true;
            if (!_network.IsLocalOwner())
            {
                _network.SendCustomNetworkEvent(new []{ Packet_C2O_RequestInitialization }, DeliveryMethod.ReliableSequenced, new []{ _network.CurrentOwnerId });
            }
        }

        private void WhenNetworkMessageReceived(ushort playerID, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            if (buffer.Length == 0) return;

            var packetId = buffer[0];

            if (_network.IsLocalOwner())
            {
                if (packetId == Packet_C2O_RequestInitialization)
                {
                    // TODO: The owner should have an network update loop that:
                    // - Ensures players who have not initialized the prop don't receive the prop.
                    // - Collects which players still need data.
                    // - For each data packet that a player needs, find which other players need that data.
                    // - Send the data to only those players.
                    // The aim is for the owner not to send the same packet multiple times,
                    // and for the server to dispatch packets to only those who are missing the data.
                    Submit(new []{ playerID });
                    return;
                }
            }

            if (packetId == Packet_A2A_Write)
            {
                return;
            }
        }

        private void EnsureCapacity()
        {
            if (_memoryLength == _points.Length)
            {
                var tempV = new Vector3[_points.Length + CapacityIncrease];
                var tempQ = new Quaternion[_points.Length + CapacityIncrease];
                Array.Copy(_points, tempV, _points.Length);
                Array.Copy(_quats, tempQ, _points.Length);
                _points = tempV;
                _quats = tempQ;
            }
        }

        private void Submit(ushort[] recipientsNullable)
        {
            _network.SendCustomNetworkEvent(new []{ Packet_A2A_Write }, DeliveryMethod.ReliableSequenced, recipientsNullable);
        }
#endregion
    }
}
