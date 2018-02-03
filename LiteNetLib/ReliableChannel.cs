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

        private int _localSequence;
        private int _remoteSequence;
        private int _localWindowStart;
        private int _remoteWindowStart;

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

            _localWindowStart = 0;
            _localSequence = 0;
            _remoteSequence = 0;
            _remoteWindowStart = 0;

            _mustSendAcksStartTimer = -1.0f;

            //Init acks packet
            int bytesCount = (_windowSize - 1) / BitsInByte + 1;
            PacketProperty property = _ordered ? PacketProperty.AckReliableOrdered : PacketProperty.AckReliable;
            _outgoingAcks = _peer.GetPacketFromPool(property, channel, bytesCount);
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
            if (NetUtils.RelativeSequenceNumber(ackWindowStart, _localWindowStart) <= -_windowSize)
            {
                NetUtils.DebugWrite("[PA]Old acks");
                return;
            }

            byte[] acksData = packet.RawData;
            NetUtils.DebugWrite("[PA]AcksStart: {0}", ackWindowStart);
            //Monitor.Enter(_pendingPackets);
            PendingPacket pendingPacket = _headPendingPacket;
            PendingPacket prevPacket = null;
            while (pendingPacket != null)
            {
                int seq = pendingPacket.Packet.Sequence;
                int rel = NetUtils.RelativeSequenceNumber(seq, ackWindowStart);
                if (rel < 0)
                {
                    prevPacket = pendingPacket;
                    pendingPacket = pendingPacket.Next;
                    continue;
                }
                if (rel >= _windowSize)
                {
                    break;
                }

                int idx = (ackWindowStart + seq) % _windowSize;
                int currentByte = idx / BitsInByte;
                int currentBit = idx % BitsInByte;
                if ((acksData[currentByte] & (1 << currentBit)) == 0)
                {
                    //Skip false ack
                    prevPacket = pendingPacket;
                    pendingPacket = pendingPacket.Next;
                    continue;
                }

                if (seq == _localWindowStart)
                {
                    //Move window
                    _headPendingPacket = _headPendingPacket.Next;
                    if (_headPendingPacket == null)
                    {
                        _localWindowStart = (_localWindowStart + 1) % NetConstants.MaxSequence;
                    }
                    else
                    {
                        _localWindowStart = _headPendingPacket.Packet.Sequence;
                    }
                }

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
                NetUtils.DebugWrite("[PA]Removing reliableInOrder ack: {0} - true", seq);
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
                    //Monitor.Enter(_outgoingAcks);
                    _peer.SendRawData(_outgoingAcks);
                    //Monitor.Exit(_outgoingAcks);
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
                int relate = NetUtils.RelativeSequenceNumber(_localSequence, _localWindowStart);
                if (relate < _windowSize)
                {
                    PendingPacket pendingPacket = _pendingPackets[_localSequence % _windowSize];
                    pendingPacket.Packet = _outgoingPackets.Dequeue();
                    pendingPacket.Packet.Sequence = (ushort)_localSequence;
                    pendingPacket.Next = _headPendingPacket;
                    _headPendingPacket = pendingPacket;
                    _localSequence = (_localSequence + 1) % NetConstants.MaxSequence;
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

        //Process incoming packet
        public bool ProcessPacket(NetPacket packet)
        {
            if (_mustSendAcksStartTimer <= 0.0f)
                _mustSendAcksStartTimer = NetTime.Now;

            int seq = packet.Sequence;
            if (seq >= NetConstants.MaxSequence)
            {
                NetUtils.DebugWrite("[RR]Bad sequence");
                return false;
            }

            int relate = NetUtils.RelativeSequenceNumber(seq, _remoteWindowStart);
            int relateSeq = NetUtils.RelativeSequenceNumber(seq, _remoteSequence);

            if (relateSeq > NetConstants.HalfMaxSequence)
            {
                NetUtils.DebugWrite("[RR]Bad sequence");
                return false;
            }

            //Drop bad packets
            if (relate < 0)
            {
                //Too old packet doesn't ack
                NetUtils.DebugWrite("[RR]ReliableInOrder too old");
                return false;
            }
            if (relate >= _windowSize * 2)
            {
                //Some very new packet
                NetUtils.DebugWrite("[RR]ReliableInOrder too new");
                return false;
            }

            //If very new - move window
            //Monitor.Enter(_outgoingAcks);
            int ackIdx;
            int ackByte;
            int ackBit;
            if (relate >= _windowSize)
            {
                //New window position
                int newWindowStart = (_remoteWindowStart + relate - _windowSize + 1) % NetConstants.MaxSequence;
                _outgoingAcks.Sequence = (ushort)newWindowStart;

                //Clean old data
                while (_remoteWindowStart != newWindowStart)
                {
                    ackIdx = _remoteWindowStart % _windowSize;
                    ackByte = ackIdx / BitsInByte;
                    ackBit = ackIdx % BitsInByte;
                    _outgoingAcks.RawData[ackByte] &= (byte)~(1 << ackBit);
                    _remoteWindowStart = (_remoteWindowStart + 1) % NetConstants.MaxSequence;
                }
            }

            //Final stage - process valid packet
            //trigger acks send
            ackIdx = packet.Sequence % _windowSize;
            ackByte = ackIdx / BitsInByte;
            ackBit = ackIdx % BitsInByte;
            if ((_outgoingAcks.RawData[ackByte] & (1 << ackBit)) != 0)
            {
                NetUtils.DebugWrite("[RR]ReliableInOrder duplicate");
                //Monitor.Exit(_outgoingAcks);
                return false;
            }

            //save ack
            _outgoingAcks.RawData[ackByte] |= (byte)(1 << ackBit);
            //Monitor.Exit(_outgoingAcks);

            //detailed check
            if (packet.Sequence == _remoteSequence)
            {
                NetUtils.DebugWrite("[RR]ReliableInOrder packet succes");
                _peer.AddIncomingPacket(packet);
                _remoteSequence = (_remoteSequence + 1) % NetConstants.MaxSequence;

                if (_ordered)
                {
                    NetPacket p;
                    while ((p = _receivedPackets[_remoteSequence % _windowSize]) != null)
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