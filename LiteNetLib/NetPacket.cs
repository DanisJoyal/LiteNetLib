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

    internal interface INetPacketRecyle
    {
        void Recycle(NetPacket packet);
        bool Dispose();
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
        private ushort _cachedFragmentId;
        private ushort _cachedFragmentPart;
        private ushort _cachedFragmentsTotal;
        private int _size;

        //Header
        private const int _PropertySize = 1;
        public PacketProperty Property
        {
            get { return _cachedProperty; }
            set
            {
                if (_cachedProperty != value)
                {
                    _cachedProperty = value;
                    _cachedDataSize = _size - GetHeaderSize();
                }
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
                return _cachedChannel;
            }
            set
            {
                _cachedChannel = value;
            }
        }

        public bool IsFragmented
        {
            get { return _cachedIsFragmented; }
            set
            {
                if (_cachedIsFragmented != value)
                {
                    _cachedIsFragmented = value;
                    _cachedDataSize = _size - GetHeaderSize();
                }
            }
        }

        private const int _FragmentIdSize = 2;
        public ushort FragmentId
        {
            get { return _cachedFragmentId; }
            set { _cachedFragmentId = value; }
        }

        private const int _FragmentPartSize = 2;
        public ushort FragmentPart
        {
            get { return _cachedFragmentPart; }
            set { _cachedFragmentPart = value; }
        }

        private const int _FragmentTotalSize = 2;
        public ushort FragmentsTotal
        {
            get { return _cachedFragmentsTotal; }
            set { _cachedFragmentsTotal = value; }
        }

        //Data
        public byte[] RawData;

        public int Size {
            get { return _size; }
            set
            {
                _size = value;
                _cachedDataSize = _size - GetHeaderSize();
            }
        }

        // Read from rawdata
        internal void UpdateCached()
        {
            _cachedProperty = (PacketProperty)(RawData[Size - _PropertySize] & 0x1F);
            _cachedIsFragmented = (RawData[Size - _PropertySize] & 0x80) != 0;
            if (NetConstants.MultiChannelSize == 1)
                _cachedChannel = (int)RawData[Size - _PropertySize - sizeof(byte)];
            else if (NetConstants.MultiChannelSize == 2)
                _cachedChannel = (int)BitConverter.ToUInt16(RawData, Size - _PropertySize - sizeof(ushort));
            else
                _cachedChannel = 0;
            if(GetHeaderSize(_cachedProperty) == NetConstants.SequencedHeaderSize)
            {
                _cachedSequence = BitConverter.ToUInt16(RawData, Size - HeaderSize - sizeof(ushort));
            }
            if (_cachedIsFragmented)
            {
                _cachedFragmentId = BitConverter.ToUInt16(RawData, Size - SequencedHeaderSize - _FragmentIdSize);
                _cachedFragmentPart = BitConverter.ToUInt16(RawData, Size - SequencedHeaderSize - _FragmentIdSize - _FragmentPartSize);
                _cachedFragmentsTotal = BitConverter.ToUInt16(RawData, Size - SequencedHeaderSize - _FragmentIdSize - _FragmentPartSize - _FragmentTotalSize);
            }
            _cachedDataSize = _size - GetHeaderSize();
        }

        // Write to rawdata
        internal void Prepare()
        {
            RawData[Size - _PropertySize] = (byte)((int)_cachedProperty & 0x1F);

            if (NetConstants.MultiChannelSize == 1)
                RawData[Size - _PropertySize - sizeof(byte)] = (byte)_cachedChannel;
            if (NetConstants.MultiChannelSize == 2)
                FastBitConverter.GetBytes(RawData, Size - _PropertySize - sizeof(ushort), (ushort)_cachedChannel);

            if (GetHeaderSize(_cachedProperty) == NetConstants.SequencedHeaderSize)
            {
                FastBitConverter.GetBytes(RawData, Size - SequencedHeaderSize, _cachedSequence);
            }
            if (_cachedIsFragmented)
            {
                RawData[Size - _PropertySize] |= ((byte)0x80);
                FastBitConverter.GetBytes(RawData, Size - SequencedHeaderSize - _FragmentIdSize, _cachedFragmentId);
                FastBitConverter.GetBytes(RawData, Size - SequencedHeaderSize - _FragmentIdSize - _FragmentPartSize, _cachedFragmentPart);
                FastBitConverter.GetBytes(RawData, Size - SequencedHeaderSize - _FragmentIdSize - _FragmentPartSize - _FragmentTotalSize, _cachedFragmentsTotal);
            }
        }

        public bool DontRecycleNow = false;
        private INetPacketRecyle _packetRecycle;

        public void Recycle()
        {
            if (_packetRecycle != null)
                _packetRecycle.Recycle(this);
        }

        public NetPacket(int size, INetPacketRecyle packetPool)
        {
            RawData = new byte[size];  // Try to save realloc
            Size = size;
            _packetRecycle = packetPool;
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

        ~NetPacket()
        {
            if(_packetRecycle != null && _packetRecycle.Dispose() == false)
                _packetRecycle.Recycle(this);
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
            UpdateCached();
            return true;
        }
    }
}
