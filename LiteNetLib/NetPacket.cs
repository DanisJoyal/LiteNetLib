using System;
using System.Diagnostics;
using LiteNetLib.Utils;

// fix the "Unreachable code detected" did by if(NetConstants.MultiChannelSize == 2)
#pragma warning disable 0162

namespace LiteNetLib
{
    internal enum PacketProperty : byte
    {
        Unreliable,             //0
        ReliableUnordered,      //1
        Sequenced,              //2
        ReliableOrdered,        //3
        AckReliable,            //4
        AckReliableOrdered,     //5
        Ping,                   //6
        Pong,                   //7
        ConnectRequest,         //8
        ConnectAccept,          //9
        Disconnect,             //10
        UnconnectedMessage,     //11
        NatIntroductionRequest, //12
        NatIntroduction,        //13
        NatPunchMessage,        //14
        MtuCheck,               //15
        MtuOk,                  //16
        DiscoveryRequest,       //17
        DiscoveryResponse,      //18
        Merged,                 //19
        ShutdownOk,             //20     
        ReliableSequenced       //21
    }

    internal sealed class NetPacket
    {
        private const int LastProperty = 22;

        public const int HeaderSize = 1 + NetConstants.MultiChannelSize;
        public const int SequencedHeaderSize = HeaderSize + _SequenceSize;
        public const int FragmentHeaderSize = _FragmentIdSize + _FragmentPartSize + _FragmentTotalSize;

        private PacketProperty _cachedProperty;
        private int _cachedDataSize;
        private int _cachedChannel;
        private bool _cachedIsFragmented;
        private ushort _cachedSequence;

        //Header
        private const int _PropertySize = 1;
        public PacketProperty Property
        {
            get { return _cachedProperty; }
            set
            {
                _cachedProperty = value;
#if DEBUG_MESSAGES
                // Debug purpose: Avoid mismatch between client and server. Can be removed.
                RawData[Size - _PropertySize] |= (byte)(NetConstants.MultiChannelSize << 5);
#endif
            }
        }

        private const int _SequenceSize = 2;

        public ushort Sequence
        {
            get { return _cachedSequence; }
            set { _cachedSequence = value;  }
        }

        public int Channel
        {
            get
            {
                if (NetConstants.MultiChannelSize == 1)
                    return (int)RawData[Size - _PropertySize - sizeof(byte)];
                if (NetConstants.MultiChannelSize == 2)
                    return (int)BitConverter.ToUInt16(RawData, Size - _PropertySize - sizeof(ushort));
                return 0;
            }
            set
            {
                if (NetConstants.MultiChannelSize == 1)
                    RawData[_PropertySize] = (byte)value;
                if (NetConstants.MultiChannelSize == 2)
                    FastBitConverter.GetBytes(RawData, _PropertySize, (ushort)value);
            }
        }

        public bool IsFragmented
        {
            get { return _cachedIsFragmented; }
            set
            {
                if (value)
                    RawData[Size - _PropertySize] |= 0x80; //set first bit
                else
                    RawData[Size - _PropertySize] &= 0x7F; //unset first bit
                UpdateCache();
            }
        }

        private const int _FragmentIdSize = 2;
        public ushort FragmentId
        {
            get { return BitConverter.ToUInt16(RawData, Size - SequencedHeaderSize - _FragmentIdSize); }
            set { FastBitConverter.GetBytes(RawData, Size - SequencedHeaderSize - _FragmentIdSize, value); }
        }

        private const int _FragmentPartSize = 2;
        public ushort FragmentPart
        {
            get { return BitConverter.ToUInt16(RawData, Size - SequencedHeaderSize - _FragmentIdSize - _FragmentPartSize); }
            set { FastBitConverter.GetBytes(RawData, Size - SequencedHeaderSize - _FragmentIdSize - _FragmentPartSize, value); }
        }

        private const int _FragmentTotalSize = 2;
        public ushort FragmentsTotal
        {
            get { return BitConverter.ToUInt16(RawData, Size - SequencedHeaderSize - _FragmentIdSize - _FragmentPartSize - _FragmentTotalSize); }
            set { FastBitConverter.GetBytes(RawData, Size - SequencedHeaderSize - _FragmentIdSize - _FragmentPartSize - _FragmentTotalSize, value); }
        }

        //Data
        public byte[] RawData;
        private int _size;
        public int Size {
            get { return _size; }
            set
            {
                // Flush previous data
                RawData[Size - _PropertySize] = 0;
                if (GetHeaderSize(_cachedProperty) == NetPacket.SequencedHeaderSize)
                {
                    FastBitConverter.GetBytes(RawData, Size - HeaderSize - sizeof(ushort), 0);
                }
                    
                _size = value;
                RawData[Size - _PropertySize] = (byte)((RawData[Size - _PropertySize] & 0x80) | ((byte)_cachedProperty & 0x1F));
                if (_cachedIsFragmented)
                    RawData[Size - _PropertySize] |= 0x80; //set first bit
                else
                    RawData[Size - _PropertySize] &= 0x7F; //unset first bit
                _cachedDataSize = Size - GetHeaderSize();
                if(GetHeaderSize(_cachedProperty) == NetPacket.SequencedHeaderSize)
                    FastBitConverter.GetBytes(RawData, Size - HeaderSize - sizeof(ushort), _cachedSequence);
            }
        }

        public void UpdateCache()
        {
            _cachedProperty = (PacketProperty)(RawData[Size - _PropertySize] & 0x1F);
            _cachedDataSize = Size - GetHeaderSize();
            _cachedIsFragmented = (RawData[Size - _PropertySize] & 0x80) != 0;
            if (Size > HeaderSize + sizeof(ushort))
                _cachedSequence = BitConverter.ToUInt16(RawData, Size - HeaderSize - sizeof(ushort));
        }

        private NetPacketPool _packetPool;

        public void Recycle()
        {
            if (_packetPool != null)
                _packetPool.Recycle(this);
        }

        public NetPacket(int size, NetPacketPool packetPool)
        {
            RawData = new byte[size];  // Try to save realloc
            Size = size;
            _packetPool = packetPool;
        }

        public bool Realloc(int toSize)
        {
            if (RawData.Length < toSize)
            {
                RawData = new byte[toSize];
                return true;
            }
            return false;
        }

        public static int GetHeaderSize(PacketProperty property)
        {
            switch (property)
            {
                case PacketProperty.ReliableOrdered:
                case PacketProperty.ReliableUnordered:
                case PacketProperty.Sequenced:
                case PacketProperty.Ping:
                case PacketProperty.Pong:
                case PacketProperty.AckReliable:
                case PacketProperty.AckReliableOrdered:
                case PacketProperty.ReliableSequenced:
                    return NetPacket.SequencedHeaderSize;
                default:
                    return NetPacket.HeaderSize;
            }
        }

        public int GetHeaderSize()
        {
            if(IsFragmented)
            {
                return GetHeaderSize(Property) + NetPacket.FragmentHeaderSize;
            }
            return GetHeaderSize(Property);
        }

        public int GetDataSize()
        {
            return _cachedDataSize;
        }

        public byte[] CopyPacketData()
        {
            byte[] data = new byte[GetDataSize()];
            Buffer.BlockCopy(RawData, 0, data, 0, GetDataSize());
            return data;
        }

        //Packet contstructor from byte array
        public bool FromBytes(byte[] data, int start, int packetSize)
        {
            //Reading property
            byte property = (byte)(data[start + packetSize - _PropertySize] & 0x1F);
            bool fragmented = (data[start + packetSize - _PropertySize] & 0x80) != 0;
            int headerSize = GetHeaderSize((PacketProperty) property);

#if DEBUG_MESSAGES
            // Debug purpose: Avoid mismatch between client and server. Can be removed.
            int multichannel = ((data[start] >> 5) & 0x03);
            if(multichannel != NetConstants.MultiChannelSize)
            {
                NetUtils.DebugWrite("[PA]Invalid multichannel size");
            }
#endif

            if (property > LastProperty ||
                packetSize > NetConstants.PacketSizeLimit ||
                packetSize < headerSize ||
                fragmented && packetSize < headerSize + NetPacket.FragmentHeaderSize)
            {
                return false;
            }

            Buffer.BlockCopy(data, start, RawData, 0, packetSize);
            Size = packetSize;
            return true;
        }
    }
}
