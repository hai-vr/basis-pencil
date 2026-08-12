using System;
using System.Collections.Generic;
using Basis.Network.Core;
using UnityEngine;

namespace Hai.Basis.CilboxPencil
{
    public partial class Pencil
    {
        private const int SizeOfInt = 4;
        private const int SizeOfFloat = 4;
        private const int SizeOfVector3 = 3 * SizeOfFloat;
        private const int SizeOfBadQuaternion = 3 * SizeOfFloat;

        private const byte Packet_C2O_RequestInitialization = 101;
        private const byte Packet_A2A_Serial = 1;

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

            if (packetId == Packet_A2A_Serial)
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
            _network.SendCustomNetworkEvent(new []{ Packet_A2A_Serial }, DeliveryMethod.ReliableSequenced, recipientsNullable);
        }

        private void SubmitSerial(List<Vector3> points, List<Quaternion> quats, List<float> scale)
        {
            var numberOfPoints = points.Count;
            if (numberOfPoints != quats.Count || numberOfPoints != scale.Count)
            {
                Debug.LogError("Invalid state, data must be the same length.");
                return;
            }

            var buffer = new byte[
                1 // Packet number
              + SizeOfInt // Payload Index
              + SizeOfInt // NumberOfPoints (TODO: It should be possible to deduce this from the packet size)
              + SizeOfVector3 * numberOfPoints // Points
              + SizeOfBadQuaternion * numberOfPoints // Quats
              + SizeOfFloat * numberOfPoints // Scale
            ];

            buffer[0] = Packet_A2A_Serial;
            WriteInt(buffer, 1, numberOfPoints);
            for (var i = 0; i < numberOfPoints; i++)
            {
                WriteVector3(buffer, 5 + i * SizeOfVector3, points[i]);
                WriteBadQuaternion(buffer, 5 + numberOfPoints * SizeOfVector3 + i * SizeOfBadQuaternion, quats[i]);
                WriteFloat(buffer, 5 + numberOfPoints * (SizeOfVector3 + SizeOfBadQuaternion) + i * SizeOfFloat, scale[i]);
            }
        }

        private int _decodePayloadIndex;
        private List<Vector3> _decodePoints = new();
        private List<Quaternion> _decodeQuats = new();
        private List<float> _decodeScale = new();
        private void DecodeSerial(byte[] buffer)
        {
            if (buffer.Length < 1 + SizeOfInt + SizeOfInt)
            {
                Debug.LogError("Invalid payload size.");
                return;
            }

            _decodePoints.Clear();
            _decodeQuats.Clear();
            _decodeScale.Clear();
            _decodePayloadIndex = ReadInt(buffer, 1);
            var numberOfPoints = ReadInt(buffer, 1 + SizeOfInt);

            var isScaled = false;
            var unscaledPacketLength = 1 + SizeOfInt + SizeOfInt + SizeOfVector3 * numberOfPoints + SizeOfBadQuaternion * numberOfPoints;
            if (buffer.Length != unscaledPacketLength)
            {
                var scaledPacketLength = unscaledPacketLength + SizeOfFloat * numberOfPoints;
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
                _decodePoints.Add(ReadVector3(buffer, 5 + i * SizeOfVector3));
                _decodeQuats.Add(ReadBadQuaternion(buffer, 5 + numberOfPoints * SizeOfVector3 + i * SizeOfBadQuaternion));
                _decodeScale.Add(isScaled
                    ? ReadFloat(buffer, 5 + numberOfPoints * (SizeOfVector3 + SizeOfBadQuaternion) + i * SizeOfFloat)
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

        private void WriteQuantizedFloat01(byte[] buffer, int offset, float value)
        {
            var toEncode = Mathf.Clamp01(value) ;
            buffer[offset] = (byte)(toEncode * 255);
        }

        private float ReadQuantizedFloat01(byte[] buffer, int offset)
        {
            return ReadFloat(buffer, offset) / 255;
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

        private void WriteBadQuaternion(byte[] buffer, int offset, Quaternion value)
        {
            // TODO: Replace this very inefficient encoding with the common quaternion compression technique
            WriteVector3(buffer, offset, value.eulerAngles);
        }

        private Quaternion ReadBadQuaternion(byte[] buffer, int offset)
        {
            return Quaternion.Euler(ReadVector3(buffer, offset));
        }
    }
}
