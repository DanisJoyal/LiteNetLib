using System.Collections.Generic;

namespace LiteNetLib
{
    internal sealed class SequencedChannel
    {
        private int _localSequence;
        private int _remoteSequence;
        private readonly FastQueue<NetPacket> _outgoingPackets;
        private readonly NetPeer _peer;
        private readonly int _channel;

        public SequencedChannel(NetPeer peer, int channel)
        {
            _outgoingPackets = new FastQueue<NetPacket>(NetConstants.DefaultWindowSize);
            _peer = peer;
            _channel = channel;
        }

        public void AddToQueue(NetPacket packet)
        {
            packet.DontRecycleNow = true;
            _outgoingPackets.Enqueue(packet);
        }

        public void SendNextPackets()
        {
            while (_outgoingPackets.Empty == false)
            {
                NetPacket packet = _outgoingPackets.Dequeue();
                _localSequence = (_localSequence + 1); // % NetConstants.MaxSequence;
                if (_localSequence == NetConstants.MaxSequence)
                    _localSequence = 0;
                packet.Sequence = (ushort)_localSequence;
                packet.DontRecycleNow = false;
                _peer.SendRawData(packet);
            }
        }

        public bool ProcessPacket(NetPacket packet)
        {
            if (packet.Sequence < NetConstants.MaxSequence && 
                NetUtils.RelativeSequenceNumber(packet.Sequence, _remoteSequence) > 0)
            {
                _remoteSequence = packet.Sequence;
                _peer.AddIncomingPacket(packet);
                return true;
            }
            return false;
        }
    }
}
