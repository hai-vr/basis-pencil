#define PENCIL_BASIS_ALLOWS_RAYCASTS_IN_PROPS
#define BASIS_ALLOWS_LOCAL_CAMERA_DRIVER_CAMERA_ACCESS

using System.Collections.Generic;
using Basis;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
#if BASIS_ALLOWS_LOCAL_CAMERA_DRIVER_CAMERA_ACCESS // (AUDIT): API is not accessible by props
using Basis.Scripts.Drivers;
#endif
using UnityEngine;

namespace Hai.Basis.CilboxPencil
{
    [Cilboxable]
    public partial class Pencil : MonoBehaviour
    {
        private const float CommitThresholdDistance = 0.005f;
        private const float CommitThresholdDistanceSquared = CommitThresholdDistance * CommitThresholdDistance;
        private const float CommitThresholdAngleDeg = 15f;
        private const float ColinearityThreshold = 0.9994f; // An angle of 2 degrees (goes in both directions, therefore it should be making a cone of 4 degrees in total)

        // Framerate Interpolation
        private const float TooFarThresholdDistance = 0.03f;
        private const float TooFarThresholdDistanceSquared = TooFarThresholdDistance * TooFarThresholdDistance;
        private const float RebuildDistance = TooFarThresholdDistance * 0.3f;

        // PressingOnCollider Raycast
        private const int PressingOnColliderRaycastMask = ~((1 << 2) | (1 << 3) | (1 << 5) | (1 << 6) | (1 << 7) | (1 << 8) | (1 << 9) | (1 << 10) | (1 << 11));
        private const float PressingOnColliderRaycastBackingDistance = 0.3f;
        private const float PressingOnColliderRaycastMagnetismDistance = 0.002f;
        private const float PressingOnColliderNormalBackawayDistance = 0.0001f;

        // ForcedPerspective mode
        private const int ForcedPerspectiveRaycastMask = ~((1 << 2) | (1 << 3) | (1 << 6) | (1 << 7) | (1 << 8) | (1 << 10) | (1 << 11)); // Same as PressingOnCollider but we allow the two UI layers
        private const int WantsForcePerspective_None = 0;
        private const int WantsForcePerspective_Left = 1;
        private const int WantsForcePerspective_Right = 2;

        //

        public bool option_PokeDominantEyeToUseForcedPerspectiveRaycast = true;

        public BasisPickupInteractable pickup;
        public Transform tip;
        public LineRenderer mainLineRenderer;
        public Transform modelMover;

        public GameObject instantiateMe;
        public MeshFilter instantiateMe_Filter;

        private bool _isEnabled;

        // General
        private float _defaultTipScale;
        private float _pickupTime;
        private bool _isMisclick;
        private bool _hasPreviousPoint;
        private Vector3 _previouslyCommittedPoint;
        private readonly List<Vector3> _colinearTestHistory = new();

        // Framerate Interpolation
        private bool _hasPreviousPreviousPoint;
        private Vector3 _previousPreviousCommittedPoint;
        private Quaternion _previouslyCommittedQuat = Quaternion.identity;

        // New line rendering
        private float _tipScale;
        private Mesh _currentMeshNullable;
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

        // ForcedPerspective mode
        private int _userWantsForcePerspective_None_Left_Right = WantsForcePerspective_None;
        private bool _isPaintingForcedPerspective;

        // Networking
        private BasisNetworkShim _network;
        private bool _needsNetworkUpdate;
        private bool _isNetworkReady;
        private readonly List<Vector3> _beingDrawnPoints = new();
        private readonly List<Quaternion> _beingDrawnQuats = new();
        private readonly List<float> _beingDrawnScale = new();

        //

        public void Start()
        {
            pickup.OnInteractEndEvent.AddListener(WhenDrop);
            pickup.OnInteractStartEvent.AddListener(WhenPickup);
            pickup.OnPickupUse.AddListener(WhileUsing);

            _network = SafeUtil.MakeNetworkable(this);
            _network.NetworkReady += WhenNetworkReady;
            _network.NetworkMessageReceived += WhenNetworkMessageReceived;

            _defaultTipScale = tip.lossyScale.x;

            WhenEnable();
        }
        public void OnEnable() { WhenEnable(); }
        private void OnDisable() { _isEnabled = false; }
        private void WhenEnable()
        {
            if (_isEnabled) return; // Cilbox quirk
            _isEnabled = true;

            _tipScale = _defaultTipScale;

            // We need the root to be stuck at the origin so that the meshes stay fixed in world space.
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

            _userWantsForcePerspective_None_Left_Right = WantsForcePerspective_None;
        }

        private void Update()
        {
            UpdateWhilePickedUpAndTriggerIsNotPressed();
        }

        private void UpdateWhilePickedUpAndTriggerIsNotPressed()
        {
            if (!_cannotExecutePressingOnCollider) return; // This is usually true when the user is pressing the trigger.

            var needToUnpressCollider = false;
            if (_isPickedUp)
            {
                // Detect when the user is poking their dominant eye.
                if (option_PokeDominantEyeToUseForcedPerspectiveRaycast && _userWantsForcePerspective_None_Left_Right == WantsForcePerspective_None)
                {
                    if (Vector3.Distance(FetchEyePositionInWorldSpace(WantsForcePerspective_Right), tip.position) < 0.15f)
                    {
                        _userWantsForcePerspective_None_Left_Right = WantsForcePerspective_Right;
                    }
                    else if (Vector3.Distance(FetchEyePositionInWorldSpace(WantsForcePerspective_Left), tip.position) < 0.15f)
                    {
                        _userWantsForcePerspective_None_Left_Right = WantsForcePerspective_Left;
                    }
                }

#if PENCIL_BASIS_ALLOWS_RAYCASTS_IN_PROPS // (AUDIT): Code sometimes disabled because Raycasts are not allowed on Basis Props as of this time of writing
                // Detect when the pen is pressing onto to a wall:
                var raycastPos = tip.position - tip.forward * PressingOnColliderRaycastBackingDistance;
                if (Physics.Raycast(raycastPos, tip.forward, out var hitInfo, PressingOnColliderRaycastBackingDistance + PressingOnColliderRaycastMagnetismDistance, PressingOnColliderRaycastMask))
                {
                    modelMover.position = hitInfo.point;
                    _lastPressingOnColliderRotation = RotationOnSurface(hitInfo);
                    _lastPressingOnColliderPosition = PositionOnSurface(hitInfo);
                    _isPressingOnCollider = true;
                    StartOrContinue(_lastPressingOnColliderPosition, _lastPressingOnColliderRotation);
                }
#else
                if (false)
                {}
#endif
                else
                {
                    needToUnpressCollider = _isPressingOnCollider;
                }
            }
            else
            {
                needToUnpressCollider = _isPressingOnCollider;
            }

            if (needToUnpressCollider)
            {
                modelMover.localPosition = Vector3.zero;
                Terminate(_lastPressingOnColliderPosition, _lastPressingOnColliderRotation);
                _isPressingOnCollider = false;
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

                    bool isForcedPerspectivePass = false;
                    if (_userWantsForcePerspective_None_Left_Right != WantsForcePerspective_None)
                    {
                        var shootingEyePosition = FetchEyePositionInWorldSpace(_userWantsForcePerspective_None_Left_Right);
                        var forwardVector = (tip.position - shootingEyePosition).normalized;
#if PENCIL_BASIS_ALLOWS_RAYCASTS_IN_PROPS // (AUDIT): Code sometimes disabled because Raycasts are not allowed on Basis Props as of this time of writing
                        if (Physics.Raycast(tip.position, forwardVector, out var hitInfo, 100, ForcedPerspectiveRaycastMask))
                        {
                            // We're hijacking the lastPressing* variables for the ForcedPerspective mode
                            _lastPressingOnColliderRotation = RotationOnSurface(hitInfo);
                            _lastPressingOnColliderPosition = PositionOnSurface(hitInfo);
                            _isPressingOnCollider = true;
                            _tipScale = ((shootingEyePosition - hitInfo.point).magnitude / (shootingEyePosition - tip.position).magnitude) * _defaultTipScale;
                            StartOrContinue(_lastPressingOnColliderPosition, _lastPressingOnColliderRotation);
                            _isPaintingForcedPerspective = true;
                            isForcedPerspectivePass = true;
                        }
#endif
                    }

                    if (!isForcedPerspectivePass && !_isPaintingForcedPerspective)
                    {
                        StartOrContinue(tip.position, tip.rotation);
                    }
                }
            }
            else
            {
                if (!_isMisclick)
                {
                    if (_isPaintingForcedPerspective)
                    {
                        Terminate(_lastPressingOnColliderPosition, _lastPressingOnColliderRotation);
                        _tipScale = _defaultTipScale;
                    }
                    else
                    {
                        Terminate(tip.position, tip.rotation);
                    }
                }
                _isMisclick = false;
                _cannotExecutePressingOnCollider = false;
                _isPaintingForcedPerspective = false;
            }
        }

        private void BuildMeshImmediate(List<Vector3> points, List<Quaternion> quats, List<float> scales)
        {
            var currentVerts = new List<Vector3>();
            var currentNormals = new List<Vector3>();
            var currentTris = new List<ushort>();
            var boundsMin = Vector3.zero;
            var boundsMax = Vector3.zero;
            ushort nextVert = 0;

            for (var i = 0; i < points.Count; i++)
            {
                var tipPosition = points[i];
                var tipRotation = quats[i];
                var tipScale = scales[i];

                if (i == 0)
                {
                    currentVerts.Add(tipPosition + tipRotation * (new Vector3(1, -0.05f, 0) * tipScale));
                    currentVerts.Add(tipPosition + tipRotation * (new Vector3(-1, -0.05f, 0) * tipScale));
                    currentVerts.Add(tipPosition + tipRotation * (new Vector3(1, 0f, 0) * tipScale));
                    currentVerts.Add(tipPosition + tipRotation * (new Vector3(-1, 0f, 0) * tipScale));
                    boundsMin = currentVerts[0];
                    boundsMax = currentVerts[3];
                    currentTris.Clear();
                    currentTris.Add(0); currentTris.Add(1); currentTris.Add(2);
                    currentTris.Add(3); currentTris.Add(2); currentTris.Add(1);
                    var normal = tipRotation * -Vector3.forward;
                    currentNormals.Add(normal);
                    currentNormals.Add(normal);
                    currentNormals.Add(normal);
                    currentNormals.Add(normal);
                    nextVert = 4;
                }
                else
                {
                    var n = nextVert;
                    // (N-2) ... (N)
                    // (N-1) ... (N+1)
                    var newPos0 = tipPosition + tipRotation * (new Vector3(1, 0f, 0) * tipScale);
                    var newPos1 = tipPosition + tipRotation * (new Vector3(-1, 0f, 0) * tipScale);
                    var normal = tipRotation * -Vector3.forward;

                    currentVerts.Add(newPos0);
                    currentVerts.Add(newPos1);
                    currentNormals.Add(normal);
                    currentNormals.Add(normal);

                    // (N-2) ... (N)
                    // (N-1)
                    //             /
                    //                      (N)
                    //            (N-1) ... (N+1)
                    currentTris.Add((ushort)(n - 2)); currentTris.Add((ushort)(n - 1)); currentTris.Add(n);
                    currentTris.Add((ushort)(n + 1)); currentTris.Add(n); currentTris.Add((ushort)(n - 1));

                    if (newPos1.x < boundsMin.x) { boundsMin.x = newPos1.x; }
                    if (newPos1.x > boundsMax.x) { boundsMax.x = newPos1.x; }
                    if (newPos1.y < boundsMin.y) { boundsMin.y = newPos1.y; }
                    if (newPos1.y > boundsMax.y) { boundsMax.y = newPos1.y; }
                    if (newPos1.z < boundsMin.z) { boundsMin.z = newPos1.z; }
                    if (newPos1.z > boundsMax.z) { boundsMax.z = newPos1.z; }

                    nextVert = (ushort)(n + 2);
                }
            }

            var mesh = new Mesh();
            mesh.SetVertices(currentVerts);
            mesh.SetNormals(currentNormals);
            mesh.SetTriangles(currentTris, 0);

            var bounds = new Bounds(boundsMin + (boundsMax - boundsMin) * 0.5f, boundsMax - boundsMin);

            GameObject holder;
            {
                // This should be the only place we extract instance variables (instantiateMe, instantiateMe_Filter, transform)
                instantiateMe_Filter.mesh = mesh;
                holder = Instantiate(instantiateMe, transform);
                instantiateMe_Filter.mesh = null;
            }

            var holderMeshRenderer = holder.GetComponent<MeshRenderer>();
            holderMeshRenderer.bounds = bounds;

            holder.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            holder.transform.localScale = Vector3.one;
            holder.SetActive(true);
        }

        private void StartOrContinue(Vector3 tipPosition, Quaternion tipRotation, bool forceCommit = false)
        {
            var mustCommit = forceCommit || !_hasPreviousPoint || IsAngleDifferentEnoughFromPrevious(tipRotation);
            var isCommitCausedByNonColinearity = false;
            if (!mustCommit)
            {
                if (IsPositionDifferentEnoughFromPrevious(tipPosition))
                {
                    _colinearTestHistory.Add(tipPosition);
                    if (IsColinearTestHistoryNonColinear())
                    {
                        mustCommit = true;
                        isCommitCausedByNonColinearity = _colinearTestHistory.Count >= 2;
                    }
                }
            }
            if (mustCommit)
            {
                _colinearTestHistory.Clear();
                if (!_hasPreviousPoint)
                {
                    if (null != mainLineRenderer) mainLineRenderer.positionCount = 0;
                }
                if (
                    // If there was a colinear test, then it cannot be interpolated.
                    !isCommitCausedByNonColinearity
                    && _hasPreviousPreviousPoint && IsTooDifferentFromPrevious(tipPosition)
                    && (
                        // If it's NOT forced perspective, we can execute the interpolation.
                        !_isPaintingForcedPerspective
                        // Otherwise, if it IS forced perspective, we can only interpolate if the plane normal is similar to the previous plane normal.
                        || Vector3.Dot(tipRotation * Vector3.forward, _previouslyCommittedQuat * Vector3.forward) > 0.99f
                        ))
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

                    // FIXME: This block probably creates empty triangles, but I'm not entirely sure of this
                    {
                        _currentVerts[_nextVert - 2] = b0 + quatFrom * (new Vector3(1, 0f, 0) * _tipScale);
                        _currentVerts[_nextVert - 1] = b0 + quatFrom * (new Vector3(-1, 0f, 0) * _tipScale);

                        var normal = quatFrom * -Vector3.forward;
                        _currentNormals[_nextVert - 2] = normal;
                        _currentNormals[_nextVert - 1] = normal;
                    }

                    var k = 1;
                    while (k < numberOfThings)
                    {
                        // k will never be equal to numberOfThings, so the last t is not equal to 1.
                        // The point for t=1 is handled outside the loop.
                        var t = ((float)k) / numberOfThings;

                        var virtualPosition = SeilerInterpolate(b0, b3, s1, s2, t);
                        var virtualRotation = Quaternion.Slerp(quatFrom, tipRotation, t);

                        _beingDrawnPoints.Add(virtualPosition);
                        _beingDrawnQuats.Add(virtualRotation);
                        _beingDrawnScale.Add(1f);

                        InitializeOrAddTwoVerticesToMesh(virtualPosition, tipRotation, false);
                        k++;
                    }
                }

                _beingDrawnPoints.Add(tipPosition);
                _beingDrawnQuats.Add(tipRotation);
                _beingDrawnScale.Add(_tipScale);

                _needsNetworkUpdate = true;
                _previousPreviousCommittedPoint = _previouslyCommittedPoint; // Just because this executes doesn't mean we actually had a previous committed point.
                _hasPreviousPreviousPoint = _hasPreviousPoint;
                _previouslyCommittedPoint = tipPosition;
                _previouslyCommittedQuat = tipRotation;

                InitializeOrAddTwoVerticesToMesh(tipPosition, tipRotation, true);

                if (null != mainLineRenderer)
                {
                    mainLineRenderer.positionCount++;
                    mainLineRenderer.SetPosition(mainLineRenderer.positionCount - 1, tipPosition);
                }
            }
            else
            {
                _currentVerts[_nextVert - 2] = tipPosition + tipRotation * (new Vector3(1, 0f, 0) * _tipScale);
                var secondVert = tipPosition + tipRotation * (new Vector3(-1, 0f, 0) * _tipScale);
                _currentVerts[_nextVert - 1] = secondVert;

                var normal = tipRotation * -Vector3.forward;
                _currentNormals[_nextVert - 2] = normal;
                _currentNormals[_nextVert - 1] = normal;
                _currentMeshNullable.SetVertices(_currentVerts);
                _currentMeshNullable.SetNormals(_currentNormals);

                RecalculateBoundsAndApplyToHolderMeshRenderer(secondVert);

                if (null != mainLineRenderer)
                {
                    mainLineRenderer.SetPosition(mainLineRenderer.positionCount - 1, tipPosition);
                }
            }

            _hasPreviousPoint = true;
        }

        private bool IsColinearTestHistoryNonColinear()
        {
            if (_colinearTestHistory.Count < 2)
            {
                return false;
            }

            var firstLoop = true;
            var majorDirection = Vector3.zero;
            var previousPoint = _previouslyCommittedPoint;
            var previousDirection = Vector3.zero;
            foreach (var testMe in _colinearTestHistory)
            {
                var direction = (testMe - previousPoint).normalized;
                if (firstLoop)
                {
                    firstLoop = false;
                    majorDirection = direction;
                }
                else
                {
                    if (
                        // Test for colinearity with the first direction
                        Vector3.Dot(direction, majorDirection) < ColinearityThreshold ||
                        // The following is needed to make sure lines that go back and forth
                        Vector3.Dot(direction, previousDirection) < ColinearityThreshold)
                    {
                        return true; // This is either non-colinear, or it's going the other direction.
                    }
                }

                previousPoint = testMe;
                previousDirection = direction;
            }

            return false; // This is colinear and going in the same direction.
        }

        private bool IsAngleDifferentEnoughFromPrevious(Quaternion tipRotation)
        {
            return Quaternion.Angle(_previouslyCommittedQuat, tipRotation) > CommitThresholdAngleDeg;
        }

        private bool IsPositionDifferentEnoughFromPrevious(Vector3 tipPosition)
        {
            return Vector3.SqrMagnitude(_previouslyCommittedPoint - tipPosition) > CommitThresholdDistanceSquared;
        }

        private bool IsTooDifferentFromPrevious(Vector3 tipPosition)
        {
            return Vector3.SqrMagnitude(_previouslyCommittedPoint - tipPosition) > TooFarThresholdDistanceSquared;
        }

        private void Terminate(Vector3 tipPosition, Quaternion tipRotation)
        {
            if (!_hasPreviousPoint) return;

            StartOrContinue(tipPosition, tipRotation, true);

            _beingDrawnPoints.Add(tipPosition);
            _beingDrawnQuats.Add(tipRotation);
            _beingDrawnScale.Add(_tipScale);
            // --------------
            StoreInNetworkDictionary();
            // --------------
            _beingDrawnPoints.Clear();
            _beingDrawnQuats.Clear();
            _beingDrawnScale.Clear();

            _needsNetworkUpdate = true;

            _hasPreviousPoint = false;
            _hasPreviousPreviousPoint = false;
            _currentMeshNullable = null;
            _holderMeshRenderer = null;
        }

        private void InitializeOrAddTwoVerticesToMesh(Vector3 tipPosition, Quaternion tipRotation, bool recalculateBounds)
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
                var newPos0 = tipPosition + tipRotation * (new Vector3(1, 0f, 0) * _tipScale);
                var newPos1 = tipPosition + tipRotation * (new Vector3(-1, 0f, 0) * _tipScale);
                var normal = tipRotation * -Vector3.forward;

                // We rewrite the previous two verts, locking in their last position.
                _currentVerts[n - 2] = newPos0;
                _currentVerts[n - 1] = newPos1;
                _currentNormals[n - 2] = normal;
                _currentNormals[n - 1] = normal;

                _currentVerts.Add(newPos0);
                _currentVerts.Add(newPos1);
                _currentNormals.Add(normal);
                _currentNormals.Add(normal);

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

                if (recalculateBounds)
                {
                    // FIXME: Why does this fail to update the bounds when the line is being drawn out by a long stretch of colinear points?
                    RecalculateBoundsAndApplyToHolderMeshRenderer(newPos1);
                }

                _nextVert = (ushort)(n + 2);
            }
        }

        private void RecalculateBoundsAndApplyToHolderMeshRenderer(Vector3 secondVertex)
        {
            var anyBoundsChanged = false;
            if (secondVertex.x < _boundsMin.x) { _boundsMin.x = secondVertex.x; anyBoundsChanged = true; }
            if (secondVertex.x > _boundsMax.x) { _boundsMax.x = secondVertex.x; anyBoundsChanged = true; }
            if (secondVertex.y < _boundsMin.y) { _boundsMin.y = secondVertex.y; anyBoundsChanged = true; }
            if (secondVertex.y > _boundsMax.y) { _boundsMax.y = secondVertex.y; anyBoundsChanged = true; }
            if (secondVertex.z < _boundsMin.z) { _boundsMin.z = secondVertex.z; anyBoundsChanged = true; }
            if (secondVertex.z > _boundsMax.z) { _boundsMax.z = secondVertex.z; anyBoundsChanged = true; }
            if (anyBoundsChanged)
            {
                _holderMeshRenderer.bounds = CalculateBounds();
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

        private Quaternion RotationOnSurface(RaycastHit hitInfo)
        {
            return Quaternion.LookRotation(-hitInfo.normal, tip.up);
        }

        private static Vector3 PositionOnSurface(RaycastHit hitInfo)
        {
            return hitInfo.point + hitInfo.normal * PressingOnColliderNormalBackawayDistance;
        }

        private Vector3 FetchEyePositionInWorldSpace(int wantsForcePerspective)
        {
#if BASIS_ALLOWS_LOCAL_CAMERA_DRIVER_CAMERA_ACCESS // (AUDIT): API is not accessible by props, needs a shim that works
            // return BasisLocalCameraDriver.RightEyePosition(); // ????????????? This always returns the same vector? This contradicts the documentation??????
            // Unity.Mathematics.float3 currentRightEyePosition = BasisEyeTrackingManager.Current.RightEyePosition; // Not working, always returns 0
            var cam = BasisLocalCameraDriver.CameraInstance;
            if (cam != null)
            {
                if (cam.stereoEnabled)
                {
                    var rightEyeViewMatrix = cam.GetStereoViewMatrix(wantsForcePerspective == WantsForcePerspective_Left ? Camera.StereoscopicEye.Left : Camera.StereoscopicEye.Right);
                    var pos = rightEyeViewMatrix.inverse.MultiplyPoint(Vector3.zero);
                    return pos;
                }

                return cam.transform.position;
            }
#endif
            return Vector3.zero;
        }
    }
}
