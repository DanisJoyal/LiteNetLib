using System;
using System.Collections.Generic;
using System.Threading;

namespace LiteNetLib
{
    internal sealed class ReliableChannel
    {
        private sealed class PendingPacket
        {
            public NetPacket Packet;
            public double TimeStamp;
            public int SentCount;
            public PendingPacket Next;

            public override string ToString()
            {
                return (Packet != null).ToString();
            }

            public void Clear()
            {
                Next = null;
                Packet = null;
                SentCount = 0;
            }
        }

        private readonly Queue<NetPacket> _outgoingPackets;
        private readonly NetPacket _outgoingAcks;            //for send acks
        private readonly PendingPacket[] _pendingPackets;    //for unacked packets and duplicates
        private readonly NetPacket[] _receivedPackets;       //for order
        private readonly bool[] _earlyReceived;              //for unordered
        private PendingPacket _headPendingPacket;
        private readonly List<ushort> _packetsToAcknowledge;

        private ushort _localSequence;
        private ushort _remoteSequence;

        private readonly NetPeer _peer;
        private double _mustSendAcksStartTimer;

        private readonly bool _ordered;
        private readonly int _windowSize;
        private const int BitsInByte = 8;
        private readonly int _channel;

        public int PacketsInQueue
        {
            get { return _outgoingPackets.Count; }
        }

        public ReliableChannel(NetPeer peer, bool ordered, int channel)
        {
            _windowSize = NetConstants.DefaultWindowSize;
            _peer = peer;
            _ordered = ordered;
            _channel = channel;

            _outgoingPackets = new Queue<NetPacket>(_windowSize);
            _pendingPackets = new PendingPacket[_windowSize];
            for (int i = 0; i < _pendingPackets.Length; i++)
            {
                _pendingPackets[i] = new PendingPacket();
            }

            if (_ordered)
                _receivedPackets = new NetPacket[_windowSize];
            else
                _earlyReceived = new bool[_windowSize];

            _localSequence = 0;
            _remoteSequence = 0;
            _packetsToAcknowledge = new List<ushort>();

            _mustSendAcksStartTimer = -1.0f;

            //Init acks packet
            PacketProperty property = _ordered ? PacketProperty.AckReliableOrdered : PacketProperty.AckReliable;
            _outgoingAcks = _peer.GetPacketFromPool(property, channel, NetConstants.MinPacketDataSize);
        }

        //ProcessAck in packet
        public void ProcessAck(NetPacket packet)
        {
            ushort ackWindowStart = packet.Sequence;
            if (ackWindowStart >= NetConstants.MaxSequence)
            {
                NetUtils.DebugWrite("[PA]Bad window start");
                return;
            }

            byte[] acksData = packet.RawData;
            NetUtils.DebugWrite("[PA]AcksStart: {0}", ackWindowStart);
            //Monitor.Enter(_pendingPackets);

            for (int idx = 0; idx < packet.GetDataSize(); ++idx)
            {
                int currentByte = idx / BitsInByte;
                int currentBit = idx % BitsInByte;
                if ((acksData[currentByte] & (1 << currentBit)) == 0)
                {
                    // Packet not ack, will be resent automaticaly as needed
                    continue;
                }

                // packet acknowledged = true
                int seqAck = NetUtils.IncrementSequenceNumber(ackWindowStart, idx);

                PendingPacket pendingPacket = _headPendingPacket;
                PendingPacket prevPacket = null;
                while (pendingPacket != null)
                {
                    // Looking for the packet to acknowledge
                    if (pendingPacket.Packet.Sequence != seqAck)
                    {
                        prevPacket = pendingPacket;
                        pendingPacket = pendingPacket.Next;
                        continue;
                    }

                    // Packet found, remove it from the list
                    if (pendingPacket == _headPendingPacket)
                    {
                        _headPendingPacket = pendingPacket.Next;
                    }

                    var packetToClear = pendingPacket;

                    //move forward
                    pendingPacket = pendingPacket.Next;
                    if (prevPacket != null)
                    {
                        prevPacket.Next = pendingPacket;
                    }

                    //clear acked packet
                    packetToClear.Packet.Recycle();
                    packetToClear.Clear();
                    NetUtils.DebugWrite("[PA]Removing reliableInOrder ack: {0} - true", seqAck);
                    break;
                }
            }
            //Monitor.Exit(_pendingPackets);
        }

        public void AddToQueue(NetPacket packet)
        {
            Monitor.Enter(_outgoingPackets);
            _outgoingPackets.Enqueue(packet);
            Monitor.Exit(_outgoingPackets);
        }

        private void SendAcks(bool aboutToSendData)
        {
            // Try to send acks with data or after end of time
            if (_mustSendAcksStartTimer > 0.0f)
            {
                double elapsedTime = NetTime.Now - _mustSendAcksStartTimer;
                if (aboutToSendData == true || elapsedTime >= (0.5 * _peer.AvgRtt * 1.1))
                {
                    _mustSendAcksStartTimer = -1.0f;
                    NetUtils.DebugWrite("[RR]SendAcks");

                    // Build outgoingAcks packet
                    if (_packetsToAcknowledge.Count > 0)
                    {
                        _packetsToAcknowledge.Sort();
                        int diff = RelativeSequenceDiff(_packetsToAcknowledge[_packetsToAcknowledge.Count - 1], _packetsToAcknowledge[0]);
                        _outgoingAcks.Size = NetConstants.SequencedHeaderSize + diff / 8 + 1;
                        _outgoingAcks.Property = _ordered ? PacketProperty.AckReliableOrdered : PacketProperty.AckReliable;
                        _outgoingAcks.Channel = _channel;
                        _outgoingAcks.Sequence = _packetsToAcknowledge[0];

                        // Set all to 0
                        Array.Clear(_outgoingAcks.RawData, 0, _outgoingAcks.Size - NetConstants.SequencedHeaderSize);
                        // Set bit to 1 foreach packet to ack
                        int ackIdx, ackByte, ackBit;
                        foreach(ushort seq in _packetsToAcknowledge)
                        {
                            ackIdx = RelativeSequenceDiff(seq, _outgoingAcks.Sequence);
                            ackByte = ackIdx / BitsInByte;
                            ackBit = ackIdx % BitsInByte;
                            _outgoingAcks.RawData[ackByte] |= (byte)(1 << ackBit);
                        }
                        //Monitor.Enter(_outgoingAcks);
                        _peer.SendRawData(_outgoingAcks);
                        //Monitor.Exit(_outgoingAcks);
                        _packetsToAcknowledge.Clear();
                    }
                }
            }
        }

        public void SendNextPackets()
        {
            //Monitor.Enter(_pendingPackets);
            //get packets from queue
            Monitor.Enter(_outgoingPackets);
            while (_outgoingPackets.Count > 0)
            {
                int relate = _headPendingPacket == null ? 0 : NetUtils.RelativeSequenceNumber(_localSequence, _headPendingPacket.Packet.Sequence);
                if (relate < _windowSize)
                {
                    PendingPacket pendingPacket = _pendingPackets[_localSequence % _windowSize];
                    pendingPacket.Packet = _outgoingPackets.Dequeue();
                    pendingPacket.Packet.Sequence = (ushort)_localSequence;
                    pendingPacket.Next = _headPendingPacket;
                    _headPendingPacket = pendingPacket;
                    _localSequence = NetUtils.IncrementSequenceNumber(_localSequence, 1);
                }
                else //Queue filled
                {
                    break;
                }
            }
            Monitor.Exit(_outgoingPackets);

            //if no pending packets return
            if (_headPendingPacket == null)
            {
                //Monitor.Exit(_pendingPackets);
                SendAcks(false);
                return;
            }

            //send
            double resendDelay = _peer.ResendDelay;
            PendingPacket currentPacket = _headPendingPacket;
            do
            {
                if (currentPacket.SentCount > 0) //check send time
                {
                    double currentTime = NetTime.Now;
                    double packetHoldTime = NetTime.Now - currentPacket.TimeStamp;  // In Second
                    if (packetHoldTime < resendDelay * (1 + currentPacket.SentCount * currentPacket.SentCount))
                    {
                        continue;
                    }
                    NetUtils.DebugWrite("[RC]Resend: {0} > {1}", (int)packetHoldTime, resendDelay);
#if STATS_ENABLED || DEBUG
                    _peer.Statistics.PacketLoss++;
#endif
                }

                currentPacket.TimeStamp = NetTime.Now;
                currentPacket.SentCount++;
                SendAcks(true);
                _peer.SendRawData(currentPacket.Packet);
            } while ((currentPacket = currentPacket.Next) != null);
            //Monitor.Exit(_pendingPackets);

            SendAcks(false);
        }

        // a - b
        internal int RelativeSequenceDiff(ushort a, ushort b)
        {
            const int MaxOutAckPacket = 3072;   // Max packet data size = 384
            int diff = a - b;
            if (diff < -MaxOutAckPacket)
                diff += NetConstants.MaxSequence;
            else if (diff > MaxOutAckPacket)
                diff -= NetConstants.MaxSequence;
            return diff;
        }

        //Process incoming packet
        public bool ProcessPacket(NetPacket packet)
        {
            if (_mustSendAcksStartTimer <= 0.0f)
                _mustSendAcksStartTimer = NetTime.Now;

            ushort seq = packet.Sequence;
            if (seq >= NetConstants.MaxSequence)
            {
                NetUtils.DebugWrite("[RR]Bad sequence");
                return false;
            }

            _packetsToAcknowledge.Add(seq);

            // Check if its a duplicated packet
            if (RelativeSequenceDiff(seq, _remoteSequence) < 0)
            {
                // Its a duplicated packet
                return false;
            }

            //detailed check
            if (packet.Sequence == _remoteSequence)
            {
                NetUtils.DebugWrite("[RR]ReliableInOrder packet succes");
                _peer.AddIncomingPacket(packet);
                _remoteSequence = NetUtils.IncrementSequenceNumber(_remoteSequence, 1);

                if (_ordered)
                {
                    NetPacket p;
                    while ((p = _receivedPackets[_remoteSequence % _windowSize]) != null)
                    {
                        //process holded packet
                        _receivedPackets[_remoteSequence % _windowSize] = null;
                        _peer.AddIncomingPacket(p);
                        _remoteSequence = NetUtils.IncrementSequenceNumber(_remoteSequence, 1);
                    }
                }
                else
                {
                    while (_earlyReceived[_remoteSequence % _windowSize])
                    {
                        //process early packet
                        _earlyReceived[_remoteSequence % _windowSize] = false;
                        _remoteSequence = NetUtils.IncrementSequenceNumber(_remoteSequence, 1);
                    }
                }

                return true;
            }

            //holded packet
            if (_ordered)
            {
                _receivedPackets[packet.Sequence % _windowSize] = packet;
            }
            else
            {
                _earlyReceived[packet.Sequence % _windowSize] = true;
                _peer.AddIncomingPacket(packet);
            }

            return true;
        }
    }
}