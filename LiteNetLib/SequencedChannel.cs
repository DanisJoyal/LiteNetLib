using System.Collections.Generic;

namespace LiteNetLib
{
    internal sealed class SequencedChannel
    {
        private int _localSequence;
        private int _remoteSequence;
        private readonly List<NetPacket> _outgoingPackets;
        private readonly NetPeer _peer;
        private readonly int _channel;

        public SequencedChannel(NetPeer peer, int channel)
        {
            _outgoingPackets = new List<NetPacket>();
            _peer = peer;
            _channel = channel;
        }

        public void AddToQueue(NetPacket packet)
        {
            lock (_outgoingPackets)
            { _outgoingPackets.Add(packet); }
        }

        public void SendNextPackets()
        {
            lock (_outgoingPackets)
            {
                foreach (NetPacket packet in _outgoingPackets)
                {
                    _localSequence = (_localSequence + 1) % NetConstants.MaxSequence;
                    packet.Sequence = (ushort)_localSequence;
                    _peer.SendRawData(packet);
                    packet.Recycle();
                }
                _outgoingPackets.Clear();
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
