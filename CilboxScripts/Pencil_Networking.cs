using System;
using System.Collections.Generic;
using Basis.Network.Core;
using Basis.Scripts.Networking.Compression;
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
        private const int SizeOfHalf = 2;
        private const int SizeOfVector3 = 3 * SizeOfFloat;
        private const int SizeOfQuaternion = SizeOfUInt;

        private const byte Packet_C2O_RequestInitialization = 101;
        private const byte Packet_O2C_NewINDX = 11;
        private const byte Packet_A2A_Serial = 1;

        private const float DelayBetweenCatchupsSeconds = 0.1f;

        private readonly Dictionary<int, List<Vector3>> _indxToPoints = new();
        private readonly Dictionary<int, List<Quaternion>> _indxToQuats = new();
        private readonly Dictionary<int, List<float>> _indxScale = new();

        private readonly HashSet<ushort> _playerIdsWhoRequestedInitialization = new();
        private readonly Dictionary<int, HashSet<ushort>> _indxToPlayerIdCatchup = new();
        private bool _hasPendingCatchup;
        private float _nextCatchupTime = float.MinValue;

        private void Owner_NewINDX()
        {
            int indx;
            do
            {
                indx = Random.Range(1_000, 2147483647);
            } while (_indxToPoints.ContainsKey(indx));

            _indxToPoints[indx] = new List<Vector3>(_beingDrawnPoints);
            _indxToQuats[indx] = new List<Quaternion>(_beingDrawnQuats);
            _indxScale[indx] = new List<float>(_beingDrawnScale);

            _network.SendCustomNetworkEvent(
                EncodeNewINDX(_beingDrawnPoints, _beingDrawnQuats, _beingDrawnScale),
                DeliveryMethod.ReliableOrdered
            );

            WhenNewINDX(indx);
        }

        private void WhenNewINDX(int indx)
        {
            BuildMeshImmediate(_indxToPoints[indx], _indxToQuats[indx], _indxScale[indx]);
        }

        private void WhenNetworkReady()
        {
            _isNetworkReady = true;
            if (!_network.IsLocalOwner())
            {
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
                    if (_indxToPoints.Count == 0) return; // Nothing has been drawn.
                    var playerHadAlreadyRequestedInitialization = !_playerIdsWhoRequestedInitialization.Add(playerID);
                    if (playerHadAlreadyRequestedInitialization)
                    {
                        // We want to avoid players asking for initialization multiple times.
                        // If Basis Props implements locally toggling a prop off and on again, we may have to update this logic.
                        Debug.LogWarning($"Player {playerID} has sent us a request for initialization, but they've already asked it before. Why?");
                        return;
                    }

                    _hasPendingCatchup = true;

                    foreach (var indx in _indxToPoints.Keys)
                    {
                        if (_indxToPlayerIdCatchup.TryGetValue(indx, out var playerIds))
                        {
                            playerIds.Add(playerID);
                        }
                        else
                        {
                            _indxToPlayerIdCatchup[indx] = new HashSet<ushort>() { playerID };
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
                        DecodeNewINDX(buffer);

                        // ReSharper disable once CanSimplifyDictionaryLookupWithTryAdd
                        if (!_indxToPoints.ContainsKey(_decodeNewINDX_INDX))
                        {
                            _indxToPoints[_decodeNewINDX_INDX] = _decodeNewINDX_Points;
                            _indxToQuats[_decodeNewINDX_INDX] = _decodeNewINDX_Quats;
                            _indxScale[_decodeNewINDX_INDX] = _decodeNewINDX_Scale;
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

            if (packetId == Packet_A2A_Serial)
            {
                return;
            }
        }

        private void Update_Owner_NetCatchup()
        {
            if (!_hasPendingCatchup) return;
            if (Time.time < _nextCatchupTime) return;

            // We try to interleave real lines being drawn with catchups.
            _nextCatchupTime = Time.time + DelayBetweenCatchupsSeconds;

            if (_indxToPlayerIdCatchup.Count == 0)
            {
                _hasPendingCatchup = false;
                return;
            }

            // This isn't a loop
            foreach (var pair in _indxToPlayerIdCatchup)
            {
                var indx = pair.Key; // It's really awkward to get the "first key" of a dictionary???
                var playerIds = pair.Value;

                var playerIdsArray = new ushort[playerIds.Count];
                playerIds.CopyTo(playerIdsArray);

                _network.SendCustomNetworkEvent(
                    EncodeNewINDX(_beingDrawnPoints, _beingDrawnQuats, _beingDrawnScale),
                    DeliveryMethod.ReliableUnordered,
                    playerIdsArray
                );

                _indxToPlayerIdCatchup.Remove(indx);
                return; // It's a bit of a weird foreach, we only need the first key.
            }
        }

        private byte[] EncodeNewINDX(List<Vector3> points, List<Quaternion> quats, List<float> scale)
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
              + SizeOfQuaternion * numberOfPoints // Quats
              + SizeOfHalf * numberOfPoints // Scale
            ];

            buffer[0] = Packet_O2C_NewINDX;
            WriteInt(buffer, 1, numberOfPoints);
            for (var i = 0; i < numberOfPoints; i++)
            {
                WriteVector3(buffer, 5 + i * SizeOfVector3, points[i]);
                WriteQuaternion(buffer, 5 + numberOfPoints * SizeOfVector3 + i * SizeOfQuaternion, quats[i]);
                WriteHalf(buffer, 5 + numberOfPoints * (SizeOfVector3 + SizeOfQuaternion) + i * SizeOfHalf, scale[i]);
            }

            return buffer;
        }

        private int _decodeNewINDX_INDX;
        private List<Vector3> _decodeNewINDX_Points = new();
        private List<Quaternion> _decodeNewINDX_Quats = new();
        private List<float> _decodeNewINDX_Scale = new();
        private void DecodeNewINDX(byte[] buffer)
        {
            if (buffer.Length < 1 + SizeOfInt + SizeOfInt)
            {
                Debug.LogError("Invalid payload size.");
                return;
            }

            _decodeNewINDX_Points.Clear();
            _decodeNewINDX_Quats.Clear();
            _decodeNewINDX_Scale.Clear();
            _decodeNewINDX_INDX = ReadInt(buffer, 1);
            var numberOfPoints = ReadInt(buffer, 1 + SizeOfInt);

            var isScaled = false;
            var unscaledPacketLength = 1 + SizeOfInt + SizeOfInt + SizeOfVector3 * numberOfPoints + SizeOfQuaternion * numberOfPoints;
            if (buffer.Length != unscaledPacketLength)
            {
                var scaledPacketLength = unscaledPacketLength + SizeOfHalf * numberOfPoints;
                if (buffer.Length != scaledPacketLength)
                {
                    Debug.LogError("Invalid payload size.");
                    return;
                }
                else
                {
                    isScaled = true;
                }
            }

            for (var i = 0; i < numberOfPoints; i++)
            {
                _decodeNewINDX_Points.Add(ReadVector3(buffer, 5 + i * SizeOfVector3));
                _decodeNewINDX_Quats.Add(ReadQuaternion(buffer, 5 + numberOfPoints * SizeOfVector3 + i * SizeOfQuaternion));
                _decodeNewINDX_Scale.Add(isScaled
                    ? ReadHalf(buffer, 5 + numberOfPoints * (SizeOfVector3 + SizeOfQuaternion) + i * SizeOfHalf)
                    : 1f);
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

        private void WriteHalf(byte[] buffer, int offset, float value)
        {
            var intBits = BitConverter.ToInt16(BitConverter.GetBytes(value), 0);
            buffer[offset] = (byte)(intBits >> 8);
            buffer[offset + 1] = (byte)intBits;
        }

        private float ReadHalf(byte[] buffer, int offset)
        {
            var intBits = (buffer[offset] << 8) | buffer[offset + 1];
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

        private void WriteQuaternion(byte[] buffer, int offset, Quaternion value)
        {
            uint compressed = BasisCompression.QuaternionCompressor.CompressQuaternion(ref value);
            WriteUInt(buffer, offset, compressed);
        }

        private Quaternion ReadQuaternion(byte[] buffer, int offset)
        {
            uint compressed = ReadUInt(buffer, offset);
            return BasisCompression.QuaternionCompressor.DecompressQuaternion(compressed);
        }
    }
}
