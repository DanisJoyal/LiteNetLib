using System.Collections.Generic;

namespace LiteNetLib
{
    internal sealed class SequencedChannel
    {
        private int _localSequence;
        private int _remoteSequence;
        private readonly SwitchQueue<NetPacket> _outgoingPackets;
        private readonly NetPeer _peer;
        private readonly int _channel;

        public SequencedChannel(NetPeer peer, int channel)
        {
            _outgoingPackets = new SwitchQueue<NetPacket>();
            _peer = peer;
            _channel = channel;
        }

        public void AddToQueue(NetPacket packet)
        {
            _outgoingPackets.Push(packet);
        }

        public void SendNextPackets()
        {
            _outgoingPackets.Switch();
            while (_outgoingPackets.Empty() != true)
            {
                NetPacket packet = _outgoingPackets.Pop();
                _localSequence = (_localSequence + 1) % NetConstants.MaxSequence;
                packet.Sequence = (ushort)_localSequence;
                _peer.SendRawData(packet);
                packet.Recycle();
            }
        }

        public void ProcessPacket(NetPacket packet)
        {
            if (packet.Sequence < NetConstants.MaxSequence && 
                NetUtils.RelativeSequenceNumber(packet.Sequence, _remoteSequence) > 0)
            {
                _remoteSequence = packet.Sequence;
                _peer.AddIncomingPacket(packet);
            }
        }
    }
}
