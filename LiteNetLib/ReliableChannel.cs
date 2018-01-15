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
            public DateTime? TimeStamp;

            public void Clear()
            {
                Packet = null;
                TimeStamp = null;
            }
        }

        private readonly Queue<NetPacket> _outgoingPackets;
        private readonly bool[] _outgoingAcks;               //for send acks
        private readonly List<PendingPacket> _pendingPackets;    //for unacked packets and duplicates
        private readonly NetPacket[] _receivedPackets;       //for order
        private readonly bool[] _earlyReceived;              //for unordered

        private int _localSequence;
        private int _remoteSequence;
        private int _localWindowStart;
        private int _remoteWindowStart;

        private readonly NetPeer _peer;
        private bool _mustSendAcks;

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
            _pendingPackets = new List<PendingPacket>();

            _outgoingAcks = new bool[_windowSize];

            if (_ordered)
                _receivedPackets = new NetPacket[_windowSize];
            else
                _earlyReceived = new bool[_windowSize];

            _localWindowStart = 0;
            _localSequence = 0;
            _remoteSequence = 0;
            _remoteWindowStart = 0;
        }

        //ProcessAck in packet
        public void ProcessAck(NetPacket packet)
        {
            int validPacketSize = (_windowSize - 1) / BitsInByte + 1 + NetConstants.SequencedHeaderSize;
            if (packet.Size != validPacketSize)
            {
                NetUtils.DebugWrite("[PA]Invalid acks packet size");
                return;
            }

            ushort ackWindowStart = packet.Sequence;
            if (ackWindowStart > NetConstants.MaxSequence)
            {
                NetUtils.DebugWrite("[PA]Bad window start");
                return;
            }

            //check relevance
            //if (NetUtils.RelativeSequenceNumber(ackWindowStart, _localWindowStart) <= -_windowSize)
            //{
            //    NetUtils.DebugWrite("[PA]Old acks");
            //    return;
            //}

            byte[] acksData = packet.RawData;
            NetUtils.DebugWrite("[PA]AcksStart: {0}", ackWindowStart);
            int startByte = NetConstants.SequencedHeaderSize;

            Monitor.Enter(_pendingPackets);
            for (int i = 0; i < _pendingPackets.Count; i++)
            {
                int ackSequence = NetUtils.RelativeSequenceNumber((ackWindowStart + i), _localWindowStart);
                if (ackSequence > NetConstants.HalfMaxSequence || ackSequence >= _pendingPackets.Count)
                {
                    NetUtils.DebugWrite(ConsoleColor.Cyan, "[PA] SKIP OLD: " + ackSequence);
                    //Skip old ack
                    continue;
                }

                int currentByte = startByte + i / BitsInByte;
                int currentBit = i % BitsInByte;

                if ((acksData[currentByte] & (1 << currentBit)) == 0)
                {
#if STATS_ENABLED || DEBUG
                    if(_pendingPackets[ackSequence % _windowSize].TimeStamp.HasValue)
                        _peer.Statistics.PacketLoss++;
#endif
                    //NetUtils.DebugWrite(ConsoleColor.Cyan, "[PA] SKIP FALSE: " + ackSequence);
                    //Skip false ack
                    continue;
                }

                PendingPacket pendingPacket = _pendingPackets[ackSequence % _windowSize];
                if (pendingPacket.Packet != null)
                {
                    _peer.Recycle(pendingPacket.Packet);
                    pendingPacket.Clear();
                    NetUtils.DebugWrite("[PA]Removing reliableInOrder ack: {0} - true", ackSequence);
                }
                else
                {
                    NetUtils.DebugWrite("[PA]Removing reliableInOrder ack: {0} - false", ackSequence);
                }

                if (ackSequence == 0)
                {
                    //Move window
                    _localWindowStart = (_localWindowStart + 1) % NetConstants.MaxSequence;
                    _pendingPackets.RemoveAt(0);
                }
            }
            Monitor.Exit(_pendingPackets);
        }

        public void AddToQueue(NetPacket packet)
        {
            Monitor.Enter(_outgoingPackets);
            _outgoingPackets.Enqueue(packet);
            Monitor.Exit(_outgoingPackets);
        }

        public bool SendNextPackets()
        {
            //check sending acks
            bool packetHasBeenSent = false;
            Monitor.Enter(_pendingPackets);
            //get packets from queue
            Monitor.Enter(_outgoingPackets);
            while (_outgoingPackets.Count > 0)
            {
                if (_pendingPackets.Count < _windowSize)
                {
                    PendingPacket pendingPacket = new PendingPacket();
                    pendingPacket.Packet = _outgoingPackets.Dequeue();
                    pendingPacket.Packet.Sequence = (ushort) _localSequence;
                    _pendingPackets.Add(pendingPacket);
                    _localSequence = (_localSequence + 1) % NetConstants.MaxSequence;
                }
                else //Queue filled
                {
                    break;
                }
            }
            Monitor.Exit(_outgoingPackets);

            //if no pending packets return
            if (_pendingPackets.Count == 0)
            {
                Monitor.Exit(_pendingPackets);
                return false;
            }
            //send
            foreach (PendingPacket currentPacket in _pendingPackets)
            {
                if (currentPacket.Packet != null)
                {
                    if (currentPacket.TimeStamp.HasValue) //check send time
                    {
                        double packetHoldTime = (DateTime.UtcNow - currentPacket.TimeStamp.Value).TotalMilliseconds;
                        if (packetHoldTime < _peer.ResendDelay)
                        {
                            continue;
                        }
                        NetUtils.DebugWrite("[RC]Resend: {0} > {1}", (int)packetHoldTime, _peer.ResendDelay);
                    }

                    currentPacket.TimeStamp = DateTime.UtcNow;
                    packetHasBeenSent = true;
                    _peer.SendRawData(currentPacket.Packet);
                }
            }

            Monitor.Exit(_pendingPackets);
            return packetHasBeenSent;
        }

        public bool SendAcks()
        {
            if (!_mustSendAcks)
                return false;
            _mustSendAcks = false;

            NetUtils.DebugWrite("[RR]SendAcks");

            //Init packet
            int bytesCount = (_windowSize - 1) / BitsInByte + 1;
            PacketProperty property = _ordered ? PacketProperty.AckReliableOrdered : PacketProperty.AckReliable;
            var acksPacket = _peer.GetPacketFromPool(property, _channel, bytesCount);

            //For quick access
            byte[] data = acksPacket.RawData; //window start + acks size

            //Put window start
            Monitor.Enter(_outgoingAcks);
            acksPacket.Sequence = (ushort)_remoteWindowStart;

            //Put acks
            int startAckIndex = _remoteWindowStart % _windowSize;
            int currentAckIndex = startAckIndex;
            int currentBit = 0;
            int currentByte = NetConstants.SequencedHeaderSize;
            do 
            {
                if (_outgoingAcks[currentAckIndex])
                {
                    data[currentByte] |= (byte)(1 << currentBit);
                }

                currentBit++;
                if (currentBit == BitsInByte)
                {
                    currentByte++;
                    currentBit = 0;
                }
                currentAckIndex = (currentAckIndex + 1) % _windowSize;
            } while (currentAckIndex != startAckIndex);
            Monitor.Exit(_outgoingAcks);

            _peer.SendRawData(acksPacket);
            _peer.Recycle(acksPacket);
            return true;
        }

        //Process incoming packet
        public void ProcessPacket(NetPacket packet)
        {
            if (packet.Sequence >= NetConstants.MaxSequence)
            {
                NetUtils.DebugWrite("[RR]Bad sequence");
                return;
            }

            _mustSendAcks = true;

            int relate = NetUtils.RelativeSequenceNumber(packet.Sequence, _remoteWindowStart);
            int relateSeq = NetUtils.RelativeSequenceNumber(packet.Sequence, _remoteSequence);

            if (relateSeq > NetConstants.HalfMaxSequence)
            {
                NetUtils.DebugWrite("[RR]Bad sequence");
                return;
            }

            ////Drop bad packets
            //if(relate < 0)
            //{
            //    //Too old packet doesn't ack
            //    NetUtils.DebugWrite("[RR]ReliableInOrder too old");
            //    return;
            //}
            //if (relate >= _windowSize * 2)
            //{
            //    //Some very new packet
            //    NetUtils.DebugWrite("[RR]ReliableInOrder too new");
            //    return;
            //}

            //If very new - move window
            Monitor.Enter(_outgoingAcks);
            if (relate >= _windowSize)
            {
                //New window position
                int newWindowStart = (_remoteWindowStart + relate - _windowSize + 1) % NetConstants.MaxSequence;

                //Clean old data
                while (_remoteWindowStart != newWindowStart)
                {
                    _outgoingAcks[_remoteWindowStart % _windowSize] = false;
                    _remoteWindowStart = (_remoteWindowStart + 1) % NetConstants.MaxSequence;
                }
            }

            //Final stage - process valid packet
            //trigger acks send
            //_mustSendAcks = true;

            if (_outgoingAcks[packet.Sequence % _windowSize])
            {
                NetUtils.DebugWrite("[RR]ReliableInOrder duplicate");
                Monitor.Exit(_outgoingAcks);
                return;
            }

            //save ack
            _outgoingAcks[packet.Sequence % _windowSize] = true;
            Monitor.Exit(_outgoingAcks);

            //detailed check
            if (packet.Sequence == _remoteSequence)
            {
                NetUtils.DebugWrite("[RR]ReliableInOrder packet succes");
                _peer.AddIncomingPacket(packet);
                _remoteSequence = (_remoteSequence + 1) % NetConstants.MaxSequence;

                if (_ordered)
                {
                    NetPacket p;
                    while ( (p = _receivedPackets[_remoteSequence % _windowSize]) != null)
                    {
                        //process holded packet
                        _receivedPackets[_remoteSequence % _windowSize] = null;
                        _peer.AddIncomingPacket(p);
                        _remoteSequence = (_remoteSequence + 1) % NetConstants.MaxSequence;
                    }
                }
                else
                {
                    while (_earlyReceived[_remoteSequence % _windowSize])
                    {
                        //process early packet
                        _earlyReceived[_remoteSequence % _windowSize] = false;
                        _remoteSequence = (_remoteSequence + 1) % NetConstants.MaxSequence;
                    }
                }

                return;
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
        }
    }
}
