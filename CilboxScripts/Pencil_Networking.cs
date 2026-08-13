using System;
using System.Collections;
using System.Collections.Generic;
using Basis.Network.Core;
using Basis.Scripts.Networking.NetworkedAvatar;
using UnityEngine;
using Quaternion = UnityEngine.Quaternion;
using Random = UnityEngine.Random;
using Vector3 = UnityEngine.Vector3;

namespace Hai.Basis.CilboxPencil
{
    public partial class Pencil
    {
        // INDX: Data index, a random number.
        // PLY: Player ID, supplied by Basis.

        private const int SizeOfInt = 4;
        private const int SizeOfUInt = 4;
        private const int SizeOfFloat = 4;
        private const int SizeOfVector3 = 3 * SizeOfFloat;
        private const int SizeOfCompressedQuaternion = SizeOfUInt;
        private const int SizeOfBadQuaternion = 3 * SizeOfFloat;

        private const byte Packet_C2O_RequestInitialization = 101;
        private const byte Packet_O2C_NewINDX = 11;
        private const byte Packet_O2C_DeleteTempINDX = 19;
        private const byte Packet_A2A_BeingDrawn = 1;
        private const byte Packet_A2A_BeingTerminated = 2;

        private const float DelayBetweenCatchupsSecondsWhenNoOneIsDrawing = 1 / 30f;
        private const float DelayBetweenCatchupsSecondsWhileSomeoneIsDrawing = 1 / 10f;

        private const int MinimumIndx = 1000;
        private const int MaximumIndx = 2_000_000_000;

        private readonly Dictionary<int, List<Vector3>> _indxToPoints = new();
        private readonly Dictionary<int, List<Quaternion>> _indxToQuats = new();
        private readonly Dictionary<int, List<float>> _indxToScale = new();
        private readonly Dictionary<int, GameObject> _indxToGameObject = new();

        private readonly HashSet<ushort> _playerIdsWhoRequestedInitialization = new();
        private readonly Dictionary<int, HashSet<ushort>> _indxToPlayerIdCatchup = new();
        private readonly Queue<int> _indxToCatchUp = new(); // TODO: This should be a queue, but I'm not sure it's in Cilbox
        private bool _hasPendingCatchup;
        private float _nextCatchupTime = float.MinValue;

        private int _beingDrawnTempIndx;

        private void Owner_NewINDX(List<Vector3> points, List<Quaternion> quats, List<float> scales)
        {
            int indx;
            do
            {
                indx = Random.Range(MinimumIndx, MaximumIndx);
            } while (_indxToPoints.ContainsKey(indx));

            _indxToPoints[indx] = new List<Vector3>(points);
            _indxToQuats[indx] = new List<Quaternion>(quats);
            _indxToScale[indx] = new List<float>(scales);

            if (_playerIdsWhoRequestedInitialization.Count > 0)
            {
                _network.SendCustomNetworkEvent(
                    EncodeINDXPacketFormat(indx, points, quats, scales, Packet_O2C_NewINDX),
                    DeliveryMethod.ReliableOrdered
                );
            }

            WhenNewINDX(indx);
        }

        private void Any_SubmitBeingDrawn(int startIndex, bool isTerminal)
        {
            if (!_isNetworkReady) return;

            if (_beingDrawnNextIndex == 0)
            {
                _beingDrawnTempIndx = Random.Range(MinimumIndx, MaximumIndx);
            }

            var points = new List<Vector3>();
            var quats = new List<Quaternion>();
            var scales = new List<float>();
            for (var i = startIndex; i < _beingDrawnPoints.Count; i++)
            {
                points.Add(_beingDrawnPoints[i]);
                quats.Add(_beingDrawnQuats[i]);
                scales.Add(_beingDrawnScale[i]);
            }

            var isLocalOwner = _network.IsLocalOwner();
            if (!isLocalOwner
                // The owner does not send the termination packet, as they can just send the finished line.
                || !isTerminal)
            {
                _network.SendCustomNetworkEvent(
                    EncodeINDXPacketFormat(_beingDrawnTempIndx, points, quats, scales, isTerminal ? Packet_A2A_BeingTerminated : Packet_A2A_BeingDrawn),
                    DeliveryMethod.ReliableOrdered
                );
            }
        }

        private void WhenNewINDX(int indx)
        {
            var go = BuildMeshImmediate(indx, _indxToPoints[indx], _indxToQuats[indx], _indxToScale[indx]);
            _indxToGameObject[indx] = go;
        }

        private void WhenNetworkReady()
        {
            _isNetworkReady = true;
            if (!_network.IsLocalOwner())
            {
                Debug.Log($"Sending {Packet_C2O_RequestInitialization} to {_network.CurrentOwnerId}");
                _network.SendCustomNetworkEvent(new []{ Packet_C2O_RequestInitialization }, DeliveryMethod.ReliableOrdered, new []{ _network.CurrentOwnerId });
            }
        }

        private void WhenPlayerLeft(BasisNetworkPlayer player)
        {
            var thatPlayerExisted = _playerIdsWhoRequestedInitialization.Remove(player.playerId);

            if (!thatPlayerExisted) return;

            var keys = new List<int>(_indxToPlayerIdCatchup.Keys);
            foreach (var key in keys)
            {
                var playerIds = _indxToPlayerIdCatchup[key];
                if (playerIds.Remove(player.playerId))
                {
                    // The count can't be pulled up, as the if() condition above modifies the HashSet.
                    if (playerIds.Count == 0)
                    {
                        _indxToPlayerIdCatchup.Remove(key);
                    }
                }
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
                    Debug.Log($"Received {Packet_C2O_RequestInitialization} from {playerID}");
                    var playerHadAlreadyRequestedInitialization = !_playerIdsWhoRequestedInitialization.Add(playerID);
                    if (playerHadAlreadyRequestedInitialization)
                    {
                        // We want to avoid players asking for initialization multiple times.
                        // If Basis Props implements locally toggling a prop off and on again, we may have to update this logic.
                        Debug.LogWarning($"Player {playerID} has sent us a request for initialization, but they've already asked it before. Why?");
                        return;
                    }

                    if (_indxToPoints.Count > 0)
                    {
                        _hasPendingCatchup = true;

                        foreach (var indx in _indxToPoints.Keys)
                        {
                            if (indx >= MinimumIndx && indx < MaximumIndx)
                            {
                                if (_indxToPlayerIdCatchup.TryGetValue(indx, out var playerIds))
                                {
                                    playerIds.Add(playerID);
                                }
                                else
                                {
                                    _indxToPlayerIdCatchup[indx] = new HashSet<ushort>() { playerID };
                                    _indxToCatchUp.Enqueue(indx); // We put in a queue, so that if Player B joins a few seconds after Player A, then we won't stall Player A's progress while still providing Player B with progress.
                                }
                            }
                        }
                    }

                    return;
                }
            }
            else
            {
                if (_network.CurrentOwnerId == playerID)
                {
                    if (packetId == Packet_O2C_NewINDX)
                    {
                        DecodeINDXPacketFormat(buffer);
                        if (_decodeNewINDX_INDX >= MinimumIndx && _decodeNewINDX_INDX < MaximumIndx)
                        {
                            Debug.LogWarning($"Received a {packetId} message from {playerID}, but the INDX is {_decodeNewINDX_INDX}, which is outside the range of valid INDXs.");
                            return;
                        }

                        Debug.Log($"Received INDX message from {playerID}, INDX is {_decodeNewINDX_INDX} and there are {_decodeNewINDX_Points.Count} points.");

                        // ReSharper disable once CanSimplifyDictionaryLookupWithTryAdd
                        if (!_indxToPoints.ContainsKey(_decodeNewINDX_INDX))
                        {
                            _indxToPoints[_decodeNewINDX_INDX] = _decodeNewINDX_Points;
                            _indxToQuats[_decodeNewINDX_INDX] = _decodeNewINDX_Quats;
                            _indxToScale[_decodeNewINDX_INDX] = _decodeNewINDX_Scale;
                            WhenNewINDX(_decodeNewINDX_INDX);
                        }
                        else
                        {
                            // Sometimes we might receive the same INDX, e.g. a line is being drawn as the player is joining,
                            // this might not be an error, but we need to make sure not to build the mesh twice.
                        }
                    }
                }
            }

            if (packetId == Packet_A2A_BeingDrawn || packetId == Packet_A2A_BeingTerminated)
            {
                DecodeINDXPacketFormat(buffer);
                if (_decodeNewINDX_INDX >= MinimumIndx && _decodeNewINDX_INDX < MaximumIndx)
                {
                    Debug.LogWarning($"Received a {packetId} message from {playerID}, but the INDX is {_decodeNewINDX_INDX}, which is outside the range of valid INDXs.");
                    return;
                }

                var negativeIndx = -_decodeNewINDX_INDX;
                if (_indxToPoints.ContainsKey(_decodeNewINDX_INDX))
                {
                    _indxToPoints[negativeIndx].AddRange(_decodeNewINDX_Points);
                    _indxToQuats[negativeIndx].AddRange(_decodeNewINDX_Quats);
                    _indxToScale[negativeIndx].AddRange(_decodeNewINDX_Scale);
                }
                else
                {
                    _indxToPoints[negativeIndx] = new List<Vector3>(_decodeNewINDX_Points);
                    _indxToQuats[negativeIndx] = new List<Quaternion>(_decodeNewINDX_Quats);
                    _indxToScale[negativeIndx] = new List<float>(_decodeNewINDX_Scale);
                }

                if (_network.IsLocalOwner() && packetId == Packet_A2A_BeingTerminated)
                {
                    Owner_NewINDX(_beingDrawnPoints, this._beingDrawnQuats, _beingDrawnScale);
                }
            }
        }

        private void Update_Owner_NetCatchup()
        {
            if (!_hasPendingCatchup) return;
            if (Time.time < _nextCatchupTime) return;

            // We try to interleave real lines being drawn with catchups.
            _nextCatchupTime = Time.time + DelayBetweenCatchupsSecondsWhenNoOneIsDrawing;

            if (_indxToPlayerIdCatchup.Count == 0)
            {
                _hasPendingCatchup = false;
                return;
            }

            if (_indxToCatchUp.Count == 0)
            {
                Debug.LogError("_indxToPlayerIdCatchup is inconsistent with _indxToCatchUp, this shouldn't be happening!!!");
                _hasPendingCatchup = false;
                return;
            }

            // Increasing the budget could exceed the Cilbox limit.
            var lineBudget = 20;
            var pointsBudget = 200;
            var isFirst = true;
            do
            {
                if (!isFirst && pointsBudget - _indxToPoints[_indxToCatchUp.Peek()].Count < 0)
                {
                    break;
                }

                var indx = _indxToCatchUp.Dequeue();
                var playerIds = _indxToPlayerIdCatchup[indx];

                var playerIdsArray = new ushort[playerIds.Count];
                playerIds.CopyTo(playerIdsArray);

                var points = _indxToPoints[indx];
                _network.SendCustomNetworkEvent(
                    EncodeINDXPacketFormat(indx, points, _indxToQuats[indx], _indxToScale[indx], Packet_O2C_NewINDX),
                    DeliveryMethod.ReliableUnordered,
                    playerIdsArray
                );

                _indxToPlayerIdCatchup.Remove(indx);

                pointsBudget -= points.Count;
                lineBudget--;
                isFirst = false;

            } while (pointsBudget > 0 && lineBudget > 0 && _indxToPlayerIdCatchup.Count > 0);
        }

        private byte[] EncodeINDXPacketFormat(int indx, List<Vector3> points, List<Quaternion> quats, List<float> scale, byte packetType)
        {
            var numberOfPoints = points.Count;
            if (numberOfPoints != quats.Count || numberOfPoints != scale.Count)
            {
                Debug.LogError("Invalid state, data must be the same length.");
                return null;
            }

            var buffer = new byte[
                1 // Packet number
              + SizeOfInt // Payload Index
              + SizeOfInt // NumberOfPoints (TODO: It should be possible to deduce this from the packet size)
              + SizeOfVector3 * numberOfPoints // Points
              + SizeOfBadQuaternion * numberOfPoints // Quats
              + SizeOfFloat * numberOfPoints // Scale
            ];

            buffer[0] = packetType;
            WriteInt(buffer, 1, indx);
            WriteInt(buffer, 1 + SizeOfInt, numberOfPoints);
            var start = 1 + SizeOfInt + SizeOfInt;
            for (var i = 0; i < numberOfPoints; i++)
            {
                WriteVector3(buffer, start + i * SizeOfVector3, points[i]);
                WriteBadQuaternion(buffer, start + numberOfPoints * SizeOfVector3 + i * SizeOfBadQuaternion, quats[i]);
                // TODO: We could reduce this to a half, we'll worry about this at another time after everywhing works even without optimizing
                WriteFloat(buffer, start + numberOfPoints * (SizeOfVector3 + SizeOfBadQuaternion) + i * SizeOfFloat, scale[i]);
            }

            return buffer;
        }

        private int _decodeNewINDX_INDX;
        private List<Vector3> _decodeNewINDX_Points = new();
        private List<Quaternion> _decodeNewINDX_Quats = new();
        private List<float> _decodeNewINDX_Scale = new();
        private void DecodeINDXPacketFormat(byte[] buffer)
        {
            var start = 1 + SizeOfInt + SizeOfInt;
            if (buffer.Length < start)
            {
                Debug.LogError("Invalid payload size (A).");
                return;
            }

            _decodeNewINDX_Points.Clear();
            _decodeNewINDX_Quats.Clear();
            _decodeNewINDX_Scale.Clear();
            _decodeNewINDX_INDX = ReadInt(buffer, 1);
            var numberOfPoints = ReadInt(buffer, 1 + SizeOfInt);

            var isScaled = false;
            var unscaledPacketLength = start + SizeOfVector3 * numberOfPoints + SizeOfBadQuaternion * numberOfPoints;
            if (buffer.Length != unscaledPacketLength)
            {
                var scaledPacketLength = unscaledPacketLength + SizeOfFloat * numberOfPoints;
                if (buffer.Length != scaledPacketLength)
                {
                    Debug.LogError("Invalid payload size (B).");
                    return;
                }
                else
                {
                    isScaled = true;
                }
            }

            for (var i = 0; i < numberOfPoints; i++)
            {
                var point = ReadVector3(buffer, start + i * SizeOfVector3);
                var quat = ReadBadQuaternion(buffer, start + numberOfPoints * SizeOfVector3 + i * SizeOfBadQuaternion);
                var scale = isScaled
                    ? ReadFloat(buffer, start + numberOfPoints * (SizeOfVector3 + SizeOfBadQuaternion) + i * SizeOfFloat)
                    : 1f;

                _decodeNewINDX_Points.Add(point);
                _decodeNewINDX_Quats.Add(quat);
                _decodeNewINDX_Scale.Add(scale);
            }
        }

        private void WriteInt(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private int ReadInt(byte[] buffer, int offset)
        {
            return (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
        }

        private void WriteUInt(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private uint ReadUInt(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset] << 24 | buffer[offset + 1] << 16 | buffer[offset + 2] << 8 | buffer[offset + 3]);
        }

        private void WriteFloat(byte[] buffer, int offset, float value)
        {
            var intBits = BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
            buffer[offset] = (byte)(intBits >> 24);
            buffer[offset + 1] = (byte)(intBits >> 16);
            buffer[offset + 2] = (byte)(intBits >> 8);
            buffer[offset + 3] = (byte)intBits;
        }

        private float ReadFloat(byte[] buffer, int offset)
        {
            var intBits = (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
            return BitConverter.ToSingle(BitConverter.GetBytes(intBits), 0);
        }

        private void WriteVector3(byte[] buffer, int offset, Vector3 value)
        {
            WriteFloat(buffer, offset, value.x);
            WriteFloat(buffer, offset + 4, value.y);
            WriteFloat(buffer, offset + 8, value.z);
        }

        private Vector3 ReadVector3(byte[] buffer, int offset)
        {
            return new Vector3(ReadFloat(buffer, offset), ReadFloat(buffer, offset + 4), ReadFloat(buffer, offset + 8));
        }

        // private void WriteQuaternion(byte[] buffer, int offset, Quaternion value)
        // {
            // uint compressed = CompressQuaternion(value);
            // WriteUInt(buffer, offset, compressed);
        // }

        // private Quaternion ReadQuaternion(byte[] buffer, int offset)
        // {
            // uint compressed = ReadUInt(buffer, offset);
            // return DecompressQuaternion(compressed);
        // }

        // FIXME: Quaternion compression is not available to Cilbox, and we can't access the xyzw fields of a quaternion either, so we can't compress this for the time being until an upstream PR is opened.
        private void WriteBadQuaternion(byte[] buffer, int offset, Quaternion value)
        {
            WriteVector3(buffer, offset, value.eulerAngles);
        }

        private Quaternion ReadBadQuaternion(byte[] buffer, int offset)
        {
            return Quaternion.Euler(ReadVector3(buffer, offset));
        }
    }
}
