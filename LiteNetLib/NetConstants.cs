namespace LiteNetLib
{
    /// <summary>
    /// Sending method type
    /// </summary>
    public enum DeliveryMethod
    {
        /// <summary>
        /// Unreliable. Packets can be dropped, duplicated or arrive without order
        /// </summary>
        Unreliable,

        /// <summary>
        /// Reliable. All packets will be sent and received, but without order
        /// </summary>
        ReliableUnordered,

        /// <summary>
        /// Unreliable. Packets can be dropped, but never duplicated and arrive in order
        /// </summary>
        Sequenced,

        /// <summary>
        /// Reliable and ordered. All packets will be sent and received in order
        /// </summary>
        ReliableOrdered,

        /// <summary>
        /// Reliable only last packet
        /// </summary>
        //ReliableSequenced
    }

    /// <summary>
    /// Network constants. Can be tuned from sources for your purposes.
    /// </summary>
    public static class NetConstants
    {
        //can be tuned
		public const int DefaultUpdateTime = 15;		
        public const int DefaultWindowSize = 64;
        public const int SocketBufferSize = 4 * 1024 * 1024; //1mb
        public const int SocketTTL = 255;
        public const int PoolLimit = 1000;

        public const ushort MaxSequence = 32768;
        public const ushort HalfMaxSequence = MaxSequence / 2;
        public const int MinPacketSize = 576 - MaxUdpHeaderSize;

        public const int MultiChannelSize = 0;  // Number of bytes
        public const int HeaderSize = NetPacket.HeaderSize;
        public const int SequencedHeaderSize = NetPacket.SequencedHeaderSize;
        public const int FragmentHeaderSize = NetPacket.FragmentHeaderSize;
        public const int MinPacketDataSize = NetConstants.MinPacketSize - NetPacket.HeaderSize;
        public const int MinSequencedPacketDataSize = NetConstants.MinPacketSize - SequencedHeaderSize;

        //internal
        internal const string MulticastGroupIPv4 = "224.0.0.1";
        internal const string MulticastGroupIPv6 = "FF02:0:0:0:0:0:0:1";

        //protocol
        internal const int ProtocolId = 2;
        internal const int MaxUdpHeaderSize = 68;
        internal const int PacketSizeLimit = ushort.MaxValue - MaxUdpHeaderSize;
        internal const int AcceptConnectIdIndex = HeaderSize;
        internal const int RequestConnectIdIndex = HeaderSize + sizeof(int);

        internal static readonly int[] PossibleMtu =
        {
            576 - MaxUdpHeaderSize,  //Internet Path MTU for X.25 (RFC 879)
            1492 - MaxUdpHeaderSize, //Ethernet with LLC and SNAP, PPPoE (RFC 1042)
            1500 - MaxUdpHeaderSize, //Ethernet II (RFC 1191)
            4352 - MaxUdpHeaderSize, //FDDI
            4464 - MaxUdpHeaderSize, //Token ring
            7981 - MaxUdpHeaderSize  //WLAN
        };

        internal static int MaxPacketSize = PossibleMtu[PossibleMtu.Length - 1];

        //peer specific
        public const int FlowUpdateTime = 1000;
        public const int FlowIncreaseThreshold = 4;
    }
}
