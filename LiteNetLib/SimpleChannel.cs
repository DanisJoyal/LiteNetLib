using System.Collections.Generic;

namespace LiteNetLib
{
    internal sealed class SimpleChannel
    {
        private readonly FastQueue<NetPacket> _outgoingPackets;
        private readonly NetPeer _peer;
        private readonly int _channel;

        public SimpleChannel(NetPeer peer, int channel)
        {
            _outgoingPackets = new FastQueue<NetPacket>(NetConstants.DefaultWindowSize);
            _peer = peer;
            _channel = channel;
        }

        public void AddToQueue(NetPacket packet)
        {
            packet.DontRecycleNow = false;
            _outgoingPackets.Enqueue(packet);
        }

        public void SendNextPackets()
        {
            NetPacket packet;
            while (_outgoingPackets.Empty == false)
            {
                packet = _outgoingPackets.Dequeue();
                packet.DontRecycleNow = false;
                _peer.SendRawData(packet);
            }
        }
    }
}
