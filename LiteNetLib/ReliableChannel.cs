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
            public long TimeStamp;
            public int SentCount;
            public PendingPacket Next;
            public int idx;

            public override string ToString()
            {
                return (Packet != null).ToString();
            }

            public void Clear()
            {
                Packet = null;
                SentCount = 0;
            }
        }

        private readonly FastQueue<NetPacket> _outgoingPackets;
        //private readonly NetPacket _outgoingAcks;            //for send acks
        private readonly PendingPacket[] _pendingPackets;    //for unacked packets and duplicates
        private readonly NetPacket[] _receivedPackets;       //for order
        private readonly bool[] _earlyReceived;              //for unordered
        private PendingPacket _tailPendingPacket;
        private PendingPacket _headPendingPacket;
        private readonly FastQueueTyped<ushort> _packetsToAcknowledge;
        private ushort _packetsToAcknowledgeMin;
        private ushort _packetsToAcknowledgeMax;

        private int _localSequence;
        private ushort _remoteSequence;

        private readonly NetPeer _peer;
        private long _mustSendAcksStartTimer;

        private readonly bool _ordered;
        private const int BitsInByte = 8;
        private readonly int _channel;

        public ReliableChannel(NetPeer peer, bool ordered, int channel)
        {
            _peer = peer;
            _ordered = ordered;
            _channel = channel;

            _outgoingPackets = new FastQueue<NetPacket>(NetConstants.DefaultWindowSize);
            _pendingPackets = new PendingPacket[NetConstants.DefaultWindowSize];
            for (int i = 0; i < _pendingPackets.Length; i++)
            {
                _pendingPackets[i] = new PendingPacket();
                _pendingPackets[i].idx = i;
                if(i != 0)
                    _pendingPackets[i - 1].Next = _pendingPackets[i];
            }
            _pendingPackets[_pendingPackets.Length - 1].Next = _pendingPackets[0];

            _tailPendingPacket = _headPendingPacket = _pendingPackets[0];

            if (_ordered)
                _receivedPackets = new NetPacket[NetConstants.DefaultWindowSize];
            else
                _earlyReceived = new bool[NetConstants.DefaultWindowSize];

            _localSequence = 0;
            _remoteSequence = NetConstants.MaxSequence;
            _packetsToAcknowledge = new FastQueueTyped<ushort>(NetConstants.DefaultWindowSize);

            _mustSendAcksStartTimer = -1;
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

            for (int idx = 0; idx < packet.GetDataSize() * 8; ++idx)
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

                PendingPacket pendingPacket = _tailPendingPacket;
                PendingPacket prevPacket = null;
                while (pendingPacket != _headPendingPacket)
                {
                    // Looking for the packet to acknowledge
                    if (pendingPacket.Packet == null || pendingPacket.Packet.Sequence != seqAck)
                    {
                        prevPacket = pendingPacket;
                        pendingPacket = pendingPacket.Next;
                        continue;
                    }

                    //clear acked packet
                    pendingPacket.Packet.DontRecycleNow = false;
                    pendingPacket.Packet.Recycle();
                    pendingPacket.Clear();

                    // Packet found, remove it from the list
                    while(_tailPendingPacket.Packet == null && _tailPendingPacket != _headPendingPacket)
                        _tailPendingPacket = _tailPendingPacket.Next;

                    NetUtils.DebugWrite("[PA]Removing reliableInOrder ack: {0} - true", seqAck);
                    break;
                }
            }
            packet.Recycle();
            //Monitor.Exit(_pendingPackets);
        }

        public void AddToQueue(NetPacket packet)
        {
            //Monitor.Enter(_outgoingPackets);
            packet.DontRecycleNow = true;
            _outgoingPackets.Enqueue(packet);
            //Monitor.Exit(_outgoingPackets);
        }

        private void SendAcks(bool aboutToSendData, long currentTime)
        {
            // Try to send acks with data or after end of time
            if (_mustSendAcksStartTimer > 0)
            {
                long elapsedTime = currentTime - _mustSendAcksStartTimer;
                if (aboutToSendData == true || elapsedTime >= (long)(0.5f * (float)_peer.AvgRtt * 1.1f))
                {
                    _mustSendAcksStartTimer = -1;
                    NetUtils.DebugWrite("[RR]SendAcks");

                    // Build outgoingAcks packet
                    if (_packetsToAcknowledge.Count > 0)
                    {
                        //Init acks packet
                        PacketProperty property = _ordered ? PacketProperty.AckReliableOrdered : PacketProperty.AckReliable;
                        NetPacket _outgoingAcks = _peer.GetPacketFromPool(property, _channel, NetConstants.MinPacketDataSize);

                        int diff = RelativeSequenceDiff(_packetsToAcknowledgeMax, _packetsToAcknowledgeMin);
                        _outgoingAcks.Size = NetConstants.SequencedHeaderSize + diff / 8 + 1;
                        _outgoingAcks.Property = _ordered ? PacketProperty.AckReliableOrdered : PacketProperty.AckReliable;
                        _outgoingAcks.Channel = _channel;
                        _outgoingAcks.Sequence = _packetsToAcknowledgeMin;

                        // Set all to 0
                        Array.Clear(_outgoingAcks.RawData, 0, _outgoingAcks.Size - NetConstants.SequencedHeaderSize);
                        // Set bit to 1 foreach packet to ack
                        int ackIdx, ackByte, ackBit;
                        while (_packetsToAcknowledge.Count > 0)
                        {
                            ushort seq = _packetsToAcknowledge.Dequeue();

                            ackIdx = RelativeSequenceDiff(seq, _outgoingAcks.Sequence);
                            ackByte = ackIdx / BitsInByte;
                            ackBit = ackIdx % BitsInByte;
                            _outgoingAcks.RawData[ackByte] |= (byte)(1 << ackBit);
                        }

                        //Monitor.Enter(_outgoingAcks);
                        _peer.SendRawData(_outgoingAcks);
                        //Monitor.Exit(_outgoingAcks);
                    }
                }
            }
        }

        public void SendNextPackets()
        {
            //Monitor.Enter(_pendingPackets);
            //get packets from queue
            //Monitor.Enter(_outgoingPackets);

            while (_outgoingPackets.Empty == false)
            {
                if (_headPendingPacket.Next != _tailPendingPacket)
                {
                    NetPacket packet = _outgoingPackets.Dequeue();
                    PendingPacket pendingPacket = _headPendingPacket;
                    pendingPacket.Packet = packet;
                    _localSequence = (_localSequence + 1); // % NetConstants.MaxSequence;
                    if (_localSequence == NetConstants.MaxSequence)
                        _localSequence = 0;
                    pendingPacket.Packet.Sequence = (ushort)_localSequence;
                    _headPendingPacket = _headPendingPacket.Next;
                }
                else //Queue filled
                {
                    break;
                }
            }
            //_outgoingPackets.RemoveRange(0, packetUsed);

            //Monitor.Exit(_outgoingPackets);

            ResendPackets();
        }

        public void ResendPackets()
        {
            long currentTime = NetTime.NowMs;

            //if no pending packets return
            if (_tailPendingPacket == _headPendingPacket)
            {
                //Monitor.Exit(_pendingPackets);
                SendAcks(false, currentTime);
                return;
            }

            //send
            long resendDelay = _peer.ResendDelay + _peer.NetManager.AvgUpdateTime;
            PendingPacket currentPacket = _tailPendingPacket;
            do
            {
                if (currentPacket.Packet == null)
                    continue;
                if (currentPacket.SentCount > 0) //check send time
                {
                    long packetHoldTime = currentTime - currentPacket.TimeStamp;  // In Second
                    if (packetHoldTime < resendDelay * Math.Min(1 + currentPacket.SentCount, 5))
                    {
                        continue;
                    }
                    NetUtils.DebugWrite("[RC]Resend: {0} > {1}", (int)packetHoldTime, resendDelay);
#if STATS_ENABLED || DEBUG
                    _peer.Statistics.PacketLoss++;
#endif
                }

                currentPacket.TimeStamp = currentTime;
                currentPacket.SentCount++;
                SendAcks(true, currentTime);
                _peer.SendRawData(currentPacket.Packet);
            }
            while ((currentPacket = currentPacket.Next) != _headPendingPacket);
            //Monitor.Exit(_pendingPackets);

            SendAcks(false, currentTime);
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
            if (_mustSendAcksStartTimer <= 0)
                _mustSendAcksStartTimer = NetTime.NowMs;

            if (packet.Sequence >= NetConstants.MaxSequence)
            {
                NetUtils.DebugWrite("[RR]Bad sequence");
                return false;
            }

            if(_packetsToAcknowledge.Count == 0)
            {
                _packetsToAcknowledgeMin = _packetsToAcknowledgeMax = packet.Sequence;
            }
            else
            {
                if (_packetsToAcknowledgeMin > packet.Sequence)
                    _packetsToAcknowledgeMin = packet.Sequence;
                if (_packetsToAcknowledgeMax < packet.Sequence)
                    _packetsToAcknowledgeMax = packet.Sequence;
            }
            _packetsToAcknowledge.Enqueue(packet.Sequence);

            if(_remoteSequence == NetConstants.MaxSequence)
            {
                _remoteSequence = packet.Sequence;
            }

            // Check if its a duplicated packet
            if (_ordered && RelativeSequenceDiff(packet.Sequence, _remoteSequence) < 0)
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
                    while ((p = _receivedPackets[_remoteSequence % NetConstants.DefaultWindowSize]) != null)
                    {
                        //process holded packet
                        _receivedPackets[_remoteSequence % NetConstants.DefaultWindowSize] = null;
                        p.DontRecycleNow = false;
                        _peer.AddIncomingPacket(p);
                        p.Recycle();
                        _remoteSequence = NetUtils.IncrementSequenceNumber(_remoteSequence, 1);
                    }
                }
                else
                {
                    while (_earlyReceived[_remoteSequence % NetConstants.DefaultWindowSize])
                    {
                        //process early packet
                        _earlyReceived[_remoteSequence % NetConstants.DefaultWindowSize] = false;
                        _remoteSequence = NetUtils.IncrementSequenceNumber(_remoteSequence, 1);
                    }
                }

                return true;
            }

            //holded packet
            if (_ordered)
            {
                // Doesnt matter if it overwrites multiple time the same packet
                packet.DontRecycleNow = true;
                _receivedPackets[packet.Sequence % NetConstants.DefaultWindowSize] = packet;
            }
            else
            {
                if (_earlyReceived[packet.Sequence % NetConstants.DefaultWindowSize] == false)
                {
                    _earlyReceived[packet.Sequence % NetConstants.DefaultWindowSize] = true;
                    _peer.AddIncomingPacket(packet);
                }
            }

            return true;
        }
    }
}