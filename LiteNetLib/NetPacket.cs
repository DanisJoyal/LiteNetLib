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

        //Header
        private const int _PropertySize = 1;
        public PacketProperty Property
        {
            get { return (PacketProperty)(RawData[0] & 0x1F); }
            set
            {
                RawData[0] = (byte)((RawData[0] & 0x80) | ((byte)value & 0x1F));
#if DEBUG_MESSAGES
                // Debug purpose: Avoid mismatch between client and server. Can be removed.
                RawData[0] |= (byte)(NetConstants.MultiChannelSize << 5);
#endif
            }
        }

        private const int _SequenceSize = 2;

        public ushort Sequence
        {
            get { return BitConverter.ToUInt16(RawData, HeaderSize); }
            set { FastBitConverter.GetBytes(RawData, HeaderSize, value); }
        }

        public int Channel
        {
            get
            {
                if (NetConstants.MultiChannelSize == 1)
                    return (int)RawData[_PropertySize];
                if (NetConstants.MultiChannelSize == 2)
                    return (int)BitConverter.ToUInt16(RawData, _PropertySize);
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
            get { return (RawData[0] & 0x80) != 0; }
            set
            {
                if (value)
                    RawData[0] |= 0x80; //set first bit
                else
                    RawData[0] &= 0x7F; //unset first bit
            }
        }

        private const int _FragmentIdSize = 2;
        public ushort FragmentId
        {
            get { return BitConverter.ToUInt16(RawData, SequencedHeaderSize); }
            set { FastBitConverter.GetBytes(RawData, SequencedHeaderSize, value); }
        }

        private const int _FragmentPartSize = 2;
        public ushort FragmentPart
        {
            get { return BitConverter.ToUInt16(RawData, SequencedHeaderSize + _FragmentIdSize); }
            set { FastBitConverter.GetBytes(RawData, SequencedHeaderSize + _FragmentIdSize, value); }
        }

        private const int _FragmentTotalSize = 2;
        public ushort FragmentsTotal
        {
            get { return BitConverter.ToUInt16(RawData, SequencedHeaderSize + _FragmentIdSize + _FragmentPartSize); }
            set { FastBitConverter.GetBytes(RawData, SequencedHeaderSize + _FragmentIdSize + _FragmentPartSize, value); }
        }

        //Data
        public byte[] RawData;
        public int Size;

        public NetPacket(int size)
        {
            RawData = new byte[size];
            Size = 0;
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
            return GetHeaderSize(Property);
        }

        public byte[] CopyPacketData()
        {
            int headerSize = GetHeaderSize(Property);
            int dataSize = Size - headerSize;
            byte[] data = new byte[dataSize];
            Buffer.BlockCopy(RawData, headerSize, data, 0, dataSize);
            return data;
        }

        //Packet contstructor from byte array
        public bool FromBytes(byte[] data, int start, int packetSize)
        {
            //Reading property
            byte property = (byte)(data[start] & 0x1F);
            bool fragmented = (data[start] & 0x80) != 0;
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
