using System.Collections.Generic;

namespace LiteNetLib
{
    internal sealed class SequencedChannel
    {
        private int _localSequence;
        private int _remoteSequence;
        private readonly Queue<NetPacket> _outgoingPackets;
        private readonly NetPeer _peer;
        private readonly int _channel;

        public SequencedChannel(NetPeer peer, int channel)
        {
            _outgoingPackets = new Queue<NetPacket>();
            _peer = peer;
            _channel = channel;
        }

        public void AddToQueue(NetPacket packet)
        {
            _outgoingPackets.Enqueue(packet);
        }

        public void SendNextPackets()
        {
            while (_outgoingPackets.Count > 0)
            {
                NetPacket packet = _outgoingPackets.Dequeue();
                _localSequence = (_localSequence + 1) % NetConstants.MaxSequence;
                packet.Sequence = (ushort)_localSequence;
                _peer.SendRawData(packet);
                packet.Recycle();
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
