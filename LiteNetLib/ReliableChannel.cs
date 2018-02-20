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
                //Next = null;
                Packet = null;
                SentCount = 0;
            }
        }

        private readonly FastQueue<NetPacket> _outgoingPackets;
        //private readonly NetPacket _outgoingAcks;            //for send acks
        private readonly PendingPacket[] _pendingPackets;    //for unacked packets and duplicates
        private readonly NetPacket[] _receivedPackets;       //for order
        private readonly bool[] _earlyReceived;              //for unordered
        private PendingPacket _headPendingPacket;
        private PendingPacket _tailPendingPacket;
        private readonly FastQueueTyped<ushort> _packetsToAcknowledge;
        private ushort _packetsToAcknowledgeMin;
        private ushort _packetsToAcknowledgeMax;

        private ushort _localSequence;
        private ushort _remoteSequence;

        private readonly NetPeer _peer;
        private long _mustSendAcksStartTimer;

        private readonly bool _ordered;
        private readonly int _windowSize;
        private const int BitsInByte = 8;
        private readonly int _channel;

        public ReliableChannel(NetPeer peer, bool ordered, int channel)
        {
            _windowSize = NetConstants.DefaultWindowSize;
            _peer = peer;
            _ordered = ordered;
            _channel = channel;

            _outgoingPackets = new FastQueue<NetPacket>(_windowSize);
            _pendingPackets = new PendingPacket[_windowSize];
            for (int i = 0; i < _pendingPackets.Length; i++)
            {
                _pendingPackets[i] = new PendingPacket();
                _pendingPackets[i].idx = i;
            }
            for (int i = 0; i < _pendingPackets.Length - 1; i++)
            {
                _pendingPackets[i].Next = _pendingPackets[i + 1];
            }
            _pendingPackets[_pendingPackets.Length - 1].Next = _pendingPackets[0];

            _headPendingPacket = _tailPendingPacket = _pendingPackets[0];

            if (_ordered)
                _receivedPackets = new NetPacket[_windowSize];
            else
                _earlyReceived = new bool[_windowSize];

            _localSequence = 0;
            _remoteSequence = 0;
            _packetsToAcknowledge = new FastQueueTyped<ushort>(_windowSize);

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
                while (pendingPacket != _tailPendingPacket)
                {
                    // Looking for the packet to acknowledge
                    if (pendingPacket.Packet == null || pendingPacket.Packet.Sequence != seqAck)
                    {
                        prevPacket = pendingPacket;
                        pendingPacket = pendingPacket.Next;
                        continue;
                    }

                    //var packetToClear = pendingPacket;

                    //move forward
                    //pendingPacket = pendingPacket.Next;
                    //if (prevPacket != null)
                    //{
                    //    prevPacket.Next = pendingPacket;
                    //}

                    //clear acked packet
                    pendingPacket.Packet.DontRecycleNow = false;
                    pendingPacket.Packet.Recycle();
                    pendingPacket.Clear();

                    // Packet found, remove it from the list
                    while(_headPendingPacket.Packet == null && _headPendingPacket != _tailPendingPacket)
                        _headPendingPacket = _headPendingPacket.Next;

                    NetUtils.DebugWrite("[PA]Removing reliableInOrder ack: {0} - true", seqAck);
                    break;
                }
            }
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
                    if (_packetsToAcknowledge.Empty == false)
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
                        while (_packetsToAcknowledge.Empty == false)
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

           while(_outgoingPackets.Empty == false)
            {
                if (_tailPendingPacket.Next != _headPendingPacket)
                {
                    NetPacket packet = _outgoingPackets.Dequeue();
                    PendingPacket pendingPacket = _tailPendingPacket;
                    pendingPacket.Packet = packet;
                    pendingPacket.Packet.Sequence = (ushort)_localSequence;
                    _localSequence = NetUtils.IncrementSequenceNumber(_localSequence, 1);
                    _tailPendingPacket = pendingPacket.Next;
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
            if (_headPendingPacket == _tailPendingPacket)
            {
                //Monitor.Exit(_pendingPackets);
                SendAcks(false, currentTime);
                return;
            }

            //send
            long resendDelay = _peer.ResendDelay + _peer.NetManager.AvgUpdateTime;
            PendingPacket currentPacket = _headPendingPacket;
            do
            {
                if (currentPacket.Packet == null)
                    continue;
                if (currentPacket.SentCount > 0) //check send time
                {
                    long packetHoldTime = currentTime - currentPacket.TimeStamp;  // In Second
                    if (packetHoldTime < resendDelay * (1 + currentPacket.SentCount * currentPacket.SentCount))
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
            while ((currentPacket = currentPacket.Next) != _tailPendingPacket);
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

            if(_packetsToAcknowledge.Empty == true)
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
                    while ((p = _receivedPackets[_remoteSequence % _windowSize]) != null)
                    {
                        //process holded packet
                        _receivedPackets[_remoteSequence % _windowSize] = null;
                        p.DontRecycleNow = false;
                        _peer.AddIncomingPacket(p);
                        p.Recycle();
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
                // Doesnt matter if it overwrites multiple time the same packet
                packet.DontRecycleNow = true;
                _receivedPackets[packet.Sequence % _windowSize] = packet;
            }
            else
            {
                if (_earlyReceived[packet.Sequence % _windowSize] == false)
                {
                    _earlyReceived[packet.Sequence % _windowSize] = true;
                    _peer.AddIncomingPacket(packet);
                }
            }

            return true;
        }
    }
}