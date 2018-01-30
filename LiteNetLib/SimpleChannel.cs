using System.Collections.Generic;

namespace LiteNetLib
{
    internal sealed class SimpleChannel
    {
        private readonly Queue<NetPacket> _outgoingPackets;
        private readonly NetPeer _peer;
        private readonly int _channel;

        public SimpleChannel(NetPeer peer, int channel)
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
            NetPacket packet;
            while (_outgoingPackets.Count > 0)
            {
                packet = _outgoingPackets.Dequeue();
                _peer.SendRawData(packet);
                packet.Recycle();
            }
        }
    }
}
