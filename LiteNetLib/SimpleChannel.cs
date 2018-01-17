using System.Collections.Generic;

namespace LiteNetLib
{
    internal sealed class SimpleChannel
    {
        private readonly SwitchQueue<NetPacket> _outgoingPackets;
        private readonly NetPeer _peer;
        private readonly int _channel;

        public SimpleChannel(NetPeer peer, int channel)
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
            NetPacket packet;
            _outgoingPackets.Switch();
            while (_outgoingPackets.Empty() != true)
            {
                packet = _outgoingPackets.Pop();
                _peer.SendRawData(packet);
                _peer.Recycle(packet);
            }
        }
    }
}
