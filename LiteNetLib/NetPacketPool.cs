using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace LiteNetLib
{
    internal class NetPacketPool : INetPacketRecyle
    {
        private readonly FastQueue<NetPacket>[] _pool;

        public int MinimumSize = NetConstants.PossibleMtu[0];
        public int MaximumSize = NetConstants.PossibleMtu[0];
        public int Subdivision = 2;
        public int PoolLimit = NetConstants.PoolLimit;

        public int newPacketCreated;
        public int packetPooled;
        public int packetGet;

        public bool FreePackets = false;

        public NetPacketPool()
        {
            _pool = new FastQueue<NetPacket>[Subdivision];
            for(int i = 0; i < Subdivision; ++i)
            {
                _pool[i] = new FastQueue<NetPacket>();
            }
        }

        ~NetPacketPool()
        {
            FreePackets = true;
        }

        public bool Dispose(int size)
        {
            int section = size * Subdivision / MaximumSize;
            if (section >= _pool.Length)
                section = _pool.Length - 1;
            return FreePackets || _pool[section].Count >= PoolLimit;
        }

        public NetPacket GetWithData(PacketProperty property, int channel, NetDataWriter writer)
        {
            var packet = Get(property, writer.Length, channel);
            Buffer.BlockCopy(writer.Data, 0, packet.RawData, 0, writer.Length);
            return packet;
        }

        public NetPacket GetWithData(PacketProperty property, int channel, byte[] data, int start, int length)
        {
            var packet = Get(property, channel, length);
            Buffer.BlockCopy(data, start, packet.RawData, 0, length);
            return packet;
        }

        private NetPacket GetPacket(int size, bool clear)
        {
            NetPacket packet = null;
            if (size <= NetConstants.MaxPacketSize)
            {
                int section = size * Subdivision / MaximumSize;
                if (section >= _pool.Length)
                    section = _pool.Length - 1;
                while (packet == null && _pool[section].Empty == false)
                {
                    packet = _pool[section].Dequeue();
                }
            }
            if (packet == null)
            {
                //allocate new packet
                //packet = new NetPacket(size < MinimumSize ? MinimumSize : size, this);
                packet = new NetPacket(size, this);
                packet.Size = size;
                newPacketCreated++;
            }
            else
            {
                //reallocate packet data if packet not fits
                if (!packet.Realloc(size) && clear)
                {
                    //clear in not reallocated
                    Array.Clear(packet.RawData, 0, size);
                }
                packet.Size = size;
            }
            packet.DontRecycleNow = false;
            packetGet++;
            return packet;
        }

        //Get packet just for read
        public NetPacket GetAndRead(byte[] data, int start, int count)
        {
            NetPacket packet = GetPacket(count, false);
            if (!packet.FromBytes(data, start, count))
            {
                Recycle(packet);
                return null;
            }
            return packet;
        }

        //Get packet with size
        public NetPacket Get(PacketProperty property, int channel, int size)
        {
            size += NetPacket.GetHeaderSize(property);
            NetPacket packet = GetPacket(size, true);
            packet.Channel = channel;
            packet.Size = size;
            packet.Property = property;
            return packet;
        }

        public void Prepool(int nbPackets, int size)
        {
            if (size > NetConstants.MaxPacketSize)
            {
                //Dont pool big packets. Save memory
                return;
            }

            for (int i = 0; i < nbPackets; ++i)
            {
                //allocate new packet
                NetPacket packet = new NetPacket(size, this);
                packet.Size = size;

                int section = packet.RawData.Length * Subdivision / MaximumSize;
                if (section >= _pool.Length)
                    section = _pool.Length - 1;
                if (_pool[section].Count < PoolLimit)
                {
                    packet.DontRecycleNow = true;
                    _pool[section].Enqueue(packet);
                    packetPooled++;
                }
            }
        }

        public void Recycle(NetPacket packet)
        {
            if (packet.Size > NetConstants.MaxPacketSize) 
            {
                //Dont pool big packets. Save memory
                return;
            }

            //Clean fragmented flag
            packet.IsFragmented = false;
            if(packet.DontRecycleNow == false)
            {
                int section = packet.RawData.Length * Subdivision / MaximumSize;
                if (section >= _pool.Length)
                    section = _pool.Length - 1;
                if (_pool[section].Count < PoolLimit)
                {
                    packet.DontRecycleNow = true;
                    _pool[section].Enqueue(packet);
                    packetPooled++;
                }
            }
        }
    }
}
