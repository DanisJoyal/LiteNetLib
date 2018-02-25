#if DEBUG
#define STATS_ENABLED
#endif
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

// fix the "Unreachable code detected" did by if(NetConstants.MultiChannelSize == 2)
#pragma warning disable 0162

namespace LiteNetLib
{
    /// <summary>
    /// Peer connection state
    /// </summary>
    [Flags]
    public enum ConnectionState
    {
        InProgress = 1,
        Connected = 2,
        ShutdownRequested = 4,
        Disconnected = 8,
        Any = InProgress | Connected | ShutdownRequested
    }

    /// <summary>
    /// Network peer. Main purpose is sending messages to specific peer.
    /// </summary>
    public sealed class NetPeer
    {
        //Ping and RTT
        private int _ping;
        private long _rtt;
        private long _avgRtt;
        private int _rttCount;
        private ushort _pingSequence;
        private ushort _remotePingSequence;
        private long _resendDelay = 27;
        private bool _pingMustSend;

        private int _pingSendTimer;
        private const int RttResetDelay = 1000;
        private int _rttResetTimer;

        private long _pingTimeStart;
        private int _timeSinceLastPacket;

        //Common            
        private readonly NetEndPoint _remoteEndPoint;
        private readonly NetManager _netManager;
        private readonly NetPacketPool _packetPool;
        private readonly object _sendLock = new object();
        private readonly FastQueue<NetPacket> _mergedPackets;

        //Channels
        private readonly ReliableChannel[] _reliableOrderedChannels;
        private readonly ReliableChannel[] _reliableUnorderedChannels;
        private readonly SequencedChannel[] _sequencedChannels;
        private readonly SimpleChannel[] _simpleChannels;
        //private readonly ReliableSequencedChannel[] _reliableSequencedChannels;

        //MTU
        private int _mtu = NetConstants.PossibleMtu[0];
        private int _mtuIdx;
        private bool _finishMtu;
        private int _mtuCheckTimer;
        private int _mtuCheckAttempts;
        private const int MtuCheckDelay = 1000;
        private const int MaxMtuCheckAttempts = 4;
        private readonly object _mtuMutex = new object();

        //Fragment
        private class IncomingFragments
        {
            public NetPacket[] Fragments;
            public int ReceivedCount;
            public int TotalSize;
        }
        private ushort _fragmentId;
        private readonly Dictionary<ushort, IncomingFragments> _holdedFragments;

        //Merging
        //private readonly NetPacket _mergeData;
        private int _mergePos;
        private int _mergeCount;

        //Connection
        private int _connectAttempts;
        private int _connectTimer;
        private long _connectId;
        private ConnectionState _connectionState;
        private readonly NetDataWriter _connectData;
        //private NetPacket _shutdownPacket;
        private byte[] _shutdownData;

        /// <summary>
        /// Current connection state
        /// </summary>
        public ConnectionState ConnectionState
        {
            get { return _connectionState; }
        }

        /// <summary>
        /// Connection id for internal purposes, but can be used as key in your dictionary of peers
        /// </summary>
        public long ConnectId
        {
            get { return _connectId; }
        }

        /// <summary>
        /// Peer ip address and port
        /// </summary>
        public NetEndPoint EndPoint
        {
            get { return _remoteEndPoint; }
        }

        /// <summary>
        /// Current ping in milliseconds
        /// </summary>
        public int Ping
        {
            get { return _ping; }
        }

        /// <summary>
        /// Current MTU - Maximum Transfer Unit ( maximum udp packet size without fragmentation )
        /// </summary>
        public int Mtu
        {
            get { return _mtu; }
        }

        /// <summary>
        /// Time since last packet received (including internal library packets)
        /// </summary>
        public int TimeSinceLastPacket
        {
            get { return _timeSinceLastPacket; }
        }

        /// <summary>
        /// Peer parent NetManager
        /// </summary>
        public NetManager NetManager
        {
            get { return _netManager; }
        }

        //public int PacketsCountInReliableQueue(int channel = 0)
        //{
        //    return getReliableUnorderedChannel(channel).PacketsInQueue;
        //}

        //public int PacketsCountInReliableOrderedQueue(int channel = 0)
        //{
        //    return getReliableOrderedChannel(channel).PacketsInQueue;
        //}

        internal long AvgRtt
        {
            get { return _avgRtt; }
        }

        internal long ResendDelay
        {
            get { return _resendDelay; }
        }

        /// <summary>
		/// Application defined object containing data about the connection
		/// </summary>
        public object Tag;

        /// <summary>
        /// Statistics of peer connection
        /// </summary>
        public readonly NetStatistics Statistics;

        private ReliableChannel getReliableOrderedChannel(int index)
        {
            if (NetConstants.MultiChannelSize == 0)
                return _reliableOrderedChannels[0];
            return _reliableOrderedChannels[index % NetConstants.MultiChannelCount];
        }

        private ReliableChannel getReliableUnorderedChannel(int index)
        {
            if (NetConstants.MultiChannelSize == 0)
                return _reliableUnorderedChannels[0];
            return _reliableUnorderedChannels[index % NetConstants.MultiChannelCount];
        }

        private SequencedChannel getSequencedChannel(int index)
        {
            if (NetConstants.MultiChannelSize == 0)
                return _sequencedChannels[0];
            return _sequencedChannels[index % NetConstants.MultiChannelCount];
        }

        private SimpleChannel getSimpleChannel(int index)
        {
            if (NetConstants.MultiChannelSize == 0)
                return _simpleChannels[0];
            return _simpleChannels[index % NetConstants.MultiChannelCount];
        }

        private ReliableSequencedChannel getReliableSequencedChannel(int index)
        {
            return null;
            //if (NetConstants.MultiChannelSize == 0)
            //    return _reliableSequencedChannels[0];
            //return _reliableSequencedChannels[index % NetConstants.MultiChannelCount];
        }

        private NetPeer(NetManager netManager, NetEndPoint remoteEndPoint)
        {
            Statistics = new NetStatistics();
            _packetPool = netManager.NetPacketPool;
            _netManager = netManager;
            _remoteEndPoint = remoteEndPoint;
            _mergedPackets = new FastQueue<NetPacket>(NetConstants.DefaultWindowSize);

            if (netManager.MtuStartIdx >= 0 && netManager.MtuStartIdx < NetConstants.PossibleMtu.Length)
            {
                _mtuIdx = netManager.MtuStartIdx;
                _mtu = NetConstants.PossibleMtu[_mtuIdx];
                _finishMtu = true;
            }

            _avgRtt = 0;
            _rtt = 0;
            _pingSendTimer = 0;
            _pingMustSend = false;

            if (NetManager.EnableReliableOrderedChannel)
                _reliableOrderedChannels = new ReliableChannel[NetConstants.MultiChannelCount];
            if (NetManager.EnableReliableUnorderedChannel)
                _reliableUnorderedChannels = new ReliableChannel[NetConstants.MultiChannelCount];
            if (NetManager.EnableSequencedChannel)
                _sequencedChannels = new SequencedChannel[NetConstants.MultiChannelCount];
            if (NetManager.EnableSimpleChannel)
                _simpleChannels = new SimpleChannel[NetConstants.MultiChannelCount];
            //_reliableSequencedChannels = new ReliableSequencedChannel[NetConstants.MultiChannelCount];
            
            // Initialise default channel
            for(int i = 0; i < NetConstants.MultiChannelCount; ++i)
            {
                if (NetManager.EnableReliableOrderedChannel)
                    _reliableOrderedChannels[i] = new ReliableChannel(this, true, i);
                if (NetManager.EnableReliableUnorderedChannel)
                    _reliableUnorderedChannels[i] = new ReliableChannel(this, false, i);
                if (NetManager.EnableSequencedChannel)
                    _sequencedChannels[i] = new SequencedChannel(this, i);
                if (NetManager.EnableSimpleChannel)
                    _simpleChannels[i] = new SimpleChannel(this, i);
                //_reliableSequencedChannels[i] = new ReliableSequencedChannel(this, i);
            }

            _holdedFragments = new Dictionary<ushort, IncomingFragments>();
        }

        //Connect constructor
        internal NetPeer(NetManager netManager, NetEndPoint remoteEndPoint, NetDataWriter connectData) : this(netManager, remoteEndPoint)
        {
            _connectData = connectData;
            _connectAttempts = 0;
            _connectId = DateTime.UtcNow.Ticks;
            _connectionState = ConnectionState.InProgress;
            SendConnectRequest();
            NetUtils.DebugWrite(ConsoleColor.Cyan, "[CC] ConnectId: {0}", _connectId);
        }

        //Accept incoming constructor
        internal NetPeer(NetManager netManager, NetEndPoint remoteEndPoint, long connectId) : this(netManager, remoteEndPoint)
        {
            _connectAttempts = 0;
            _connectId = connectId;
            _connectionState = ConnectionState.Connected;
            SendConnectAccept();
            NetUtils.DebugWrite(ConsoleColor.Cyan, "[CC] ConnectId: {0}", _connectId);
        }

        private void SendConnectRequest()
        {
            //Make initial packet
            var connectPacket = _packetPool.Get(PacketProperty.ConnectRequest, 0, sizeof(int) + sizeof(long) + _connectData.Length);

            //Add data
            FastBitConverter.GetBytes(connectPacket.RawData, 0, NetConstants.ProtocolId);
            FastBitConverter.GetBytes(connectPacket.RawData, sizeof(int), _connectId);
            Buffer.BlockCopy(_connectData.Data, 0, connectPacket.RawData, sizeof(int) + sizeof(long), _connectData.Length);

            //Send raw
            _netManager.SendRawAndRecycle(connectPacket, _remoteEndPoint);
        }

        private void SendConnectAccept()
        {
            //Reset connection timer
            _timeSinceLastPacket = 0;

            //Make initial packet
            var connectPacket = _packetPool.Get(PacketProperty.ConnectAccept, 0, sizeof(long)); // sizeof(_connectId)

            //Add data
            FastBitConverter.GetBytes(connectPacket.RawData, 0, _connectId);

            //Send raw
            _netManager.SendRawAndRecycle(connectPacket, _remoteEndPoint);
        }

        internal bool ProcessConnectAccept(NetPacket packet)
        {
            if (_connectionState != ConnectionState.InProgress)
                return false;

            //check connection id
            if (BitConverter.ToInt64(packet.RawData, 0) != _connectId)
            {
                NetUtils.DebugWrite(ConsoleColor.Cyan, "[NC] Invalid connectId: {0}", _connectId);
                return false;
            }

            NetUtils.DebugWrite(ConsoleColor.Cyan, "[NC] Received connection accept");
            _timeSinceLastPacket = 0;
            _connectionState = ConnectionState.Connected;
            return true;
        }

        private static PacketProperty SendOptionsToProperty(DeliveryMethod options)
        {
            switch (options)
            {
                case DeliveryMethod.ReliableUnordered:
                    return PacketProperty.ReliableUnordered;
                case DeliveryMethod.Sequenced:
                    return PacketProperty.Sequenced;
                case DeliveryMethod.ReliableOrdered:
                    return PacketProperty.ReliableOrdered;
                //TODO: case DeliveryMethod.ReliableSequenced:
                //    return PacketProperty.ReliableSequenced;
                default:
                    return PacketProperty.Unreliable;
            }
        }

        /// <summary>
        /// Gets maximum size of packet that will be not fragmented.
        /// </summary>
        /// <param name="options">Type of packet that you want send</param>
        /// <returns>size in bytes</returns>
        public int GetMaxSinglePacketSize(DeliveryMethod options)
        {
            return _mtu - NetPacket.GetHeaderSize(SendOptionsToProperty(options));
        }

        /// <summary>
        /// Send data to peer
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        /// <param name="channel">Set the channel wanted. See NetConstants.MultiChannelSize</param>
        /// <exception cref="TooBigPacketException">
        ///     If size exceeds maximum limit:<para/>
        ///     MTU - headerSize bytes for Unreliable<para/>
        ///     Fragment count exceeded ushort.MaxValue<para/>
        /// </exception>
        public void Send(byte[] data, DeliveryMethod options)
        {
            Send(data, 0, data.Length, options);
        }

        /// <summary>
        /// Send data to peer
        /// </summary>
        /// <param name="dataWriter">DataWriter with data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        /// <param name="channel">Set the channel wanted. See NetConstants.MultiChannelSize</param>
        /// <exception cref="TooBigPacketException">
        ///     If size exceeds maximum limit:<para/>
        ///     MTU - headerSize bytes for Unreliable<para/>
        ///     Fragment count exceeded ushort.MaxValue<para/>
        /// </exception>
        public void Send(NetDataWriter dataWriter, DeliveryMethod options)
        {
            Send(dataWriter.Data, 0, dataWriter.Length, options);
        }

        /// <summary>
        /// Send data to peer
        /// </summary>
        /// <param name="data">Data</param>
        /// <param name="start">Start of data</param>
        /// <param name="length">Length of data</param>
        /// <param name="options">Send options (reliable, unreliable, etc.)</param>
        /// <param name="channel">Set the channel wanted. See NetConstants.MultiChannelSize</param>
        /// <exception cref="TooBigPacketException">
        ///     If size exceeds maximum limit:<para/>
        ///     MTU - headerSize bytes for Unreliable<para/>
        ///     Fragment count exceeded ushort.MaxValue<para/>
        /// </exception>
        public void Send(byte[] data, int start, int length, DeliveryMethod options)
        {
            if (_connectionState == ConnectionState.ShutdownRequested || 
                _connectionState == ConnectionState.Disconnected)
            {
                return;
            }
            //Prepare
            PacketProperty property = SendOptionsToProperty(options);
            int headerSize = NetPacket.GetHeaderSize(property);
            int mtu = _mtu;
            //Check fragmentation
            if (length + headerSize > mtu)
            {
                if (options == DeliveryMethod.Sequenced || options == DeliveryMethod.Unreliable)
                {
                    throw new TooBigPacketException("Unreliable packet size exceeded maximum of " + (_mtu - headerSize) + " bytes");
                }
                
                int packetFullSize = mtu - headerSize;
                int packetDataSize = packetFullSize - NetPacket.FragmentHeaderSize;

                int fullPacketsCount = length / packetDataSize;
                int lastPacketSize = length % packetDataSize;
                int totalPackets = fullPacketsCount + (lastPacketSize == 0 ? 0 : 1);

                NetUtils.DebugWrite("FragmentSend:\n" +
                           " MTU: {0}\n" +
                           " headerSize: {1}\n" +
                           " packetFullSize: {2}\n" +
                           " packetDataSize: {3}\n" +
                           " fullPacketsCount: {4}\n" +
                           " lastPacketSize: {5}\n" +
                           " totalPackets: {6}",
                    mtu, headerSize, packetFullSize, packetDataSize, fullPacketsCount, lastPacketSize, totalPackets);

                if (totalPackets > ushort.MaxValue)
                {
                    throw new TooBigPacketException("Data was split in " + totalPackets + " fragments, which exceeds " + ushort.MaxValue);
                }

                int dataOffset = 0;

                lock (_sendLock)
                {
                    for (ushort i = 0; i < fullPacketsCount; i++)
                    {
                        NetPacket p = _packetPool.Get(property, 0, packetFullSize);
                        p.FragmentId = _fragmentId;
                        p.FragmentPart = i;
                        p.FragmentsTotal = (ushort)totalPackets;
                        p.IsFragmented = true;
                        Buffer.BlockCopy(data, i * packetDataSize, p.RawData, dataOffset, packetDataSize);
                        SendPacket(p);
                    }
                    if (lastPacketSize > 0)
                    {
                        NetPacket p = _packetPool.Get(property, 0, lastPacketSize + NetPacket.FragmentHeaderSize);
                        p.FragmentId = _fragmentId;
                        p.FragmentPart = (ushort)fullPacketsCount; //last
                        p.FragmentsTotal = (ushort)totalPackets;
                        p.IsFragmented = true;
                        Buffer.BlockCopy(data, fullPacketsCount * packetDataSize, p.RawData, dataOffset, lastPacketSize);
                        SendPacket(p);
                    }
                    _fragmentId++;
                }
                return;
            }

            //Else just send
            NetPacket packet = _packetPool.GetWithData(property, 0, data, start, length);
            SendPacket(packet);
        }

        private void CreateAndSend(PacketProperty property, ushort sequence)
        {
            NetPacket packet = _packetPool.Get(property, 0, 0);
            packet.Sequence = sequence;
            SendPacket(packet);
        }

        public void Disconnect(byte[] data)
        {
            _netManager.DisconnectPeer(this, data);
        }

        public void Disconnect(NetDataWriter writer)
        {
            _netManager.DisconnectPeer(this, writer);
        }

        public void Disconnect(byte[] data, int start, int count)
        {
            _netManager.DisconnectPeer(this, data, start, count);
        }

        public void Disconnect()
        {
            _netManager.DisconnectPeer(this);
        }

        internal void SendShutdownPacket()
        {
            NetPacket _shutdownPacket = _packetPool.Get(PacketProperty.Disconnect, 0, sizeof(long) + _shutdownData.Length);
            FastBitConverter.GetBytes(_shutdownPacket.RawData, 0, _connectId);
            if (_shutdownData.Length + sizeof(long) >= _mtu)
            {
                //Drop additional data
                NetUtils.DebugWriteError("[Peer] Disconnect additional data size more than MTU - 8!");
            }
            else if (_shutdownData != null && _shutdownData.Length > 0)
            {
                Buffer.BlockCopy(_shutdownData, 0, _shutdownPacket.RawData, sizeof(long), _shutdownData.Length);
            }
            _connectionState = ConnectionState.ShutdownRequested;
            SendRawData(_shutdownPacket);
        }

        internal bool Shutdown(byte[] data, int start, int length, bool force)
        {
            //don't send anything
            if (force)
            {
                _connectionState = ConnectionState.Disconnected;
                return true;
            }

            //trying to shutdown already disconnected
            if (_connectionState == ConnectionState.Disconnected ||
                _connectionState == ConnectionState.ShutdownRequested)
            {
                NetUtils.DebugWriteError("Trying to shutdown already shutdowned peer!");
                return false;
            }

            if (length > 0)
            {
                _shutdownData = new byte[length];
                Buffer.BlockCopy(data, start, _shutdownData, 0, length);
            }
            return true;
        }

        //from user thread, our thread, or recv?
        private void SendPacket(NetPacket packet)
        {
            NetUtils.DebugWrite("[RS]Packet: " + packet.Property);
            switch (packet.Property)
            {
                case PacketProperty.ReliableUnordered:
                    if(NetManager.EnableReliableUnorderedChannel)
                        getReliableUnorderedChannel(packet.Channel).AddToQueue(packet);
                    break;
                case PacketProperty.Sequenced:
                    if (NetManager.EnableSequencedChannel)
                        getSequencedChannel(packet.Channel).AddToQueue(packet);
                    break;
                case PacketProperty.ReliableOrdered:
                    if (NetManager.EnableReliableOrderedChannel)
                        getReliableOrderedChannel(packet.Channel).AddToQueue(packet);
                    break;
                case PacketProperty.Unreliable:
                    if (NetManager.EnableSimpleChannel)
                        getSimpleChannel(packet.Channel).AddToQueue(packet);
                    break;
                case PacketProperty.ReliableSequenced:
                    //getReliableSequencedChannel(packet.Channel).AddToQueue(packet);
                    break;

                case PacketProperty.MtuCheck:
                    //Must check result for MTU fix
                    if (!_netManager.SendRawAndRecycle(packet, _remoteEndPoint))
                    {
                        _finishMtu = true;
                    }
                    break;
                case PacketProperty.AckReliable:
                case PacketProperty.AckReliableOrdered:
                case PacketProperty.Ping:
                case PacketProperty.Pong:
                case PacketProperty.MtuOk:
                    {
                        SendRawData(packet);
                    }
                    break;
                default:
                    throw new InvalidPacketException("Unknown packet property: " + packet.Property);
            }
        }

        private void UpdateRoundTripTime(long roundTripTime)
        {
            //Calc average round trip time
            _rtt += roundTripTime;
            _rttCount++;
            _avgRtt = _rtt/_rttCount;

            if (_avgRtt <= 0)
            {
                _avgRtt = 25; // 25 ms
                _rtt = 0;
                _rttCount = 0;
            }
            
            //recalc resend delay
            _resendDelay = (long)(_avgRtt * 2.7f); // double rtt
        }

        internal void AddIncomingPacket(NetPacket p)
        {
            if (p.IsFragmented)
            {
                NetUtils.DebugWrite("Fragment. Id: {0}, Part: {1}, Total: {2}", p.FragmentId, p.FragmentPart, p.FragmentsTotal);
                //Get needed array from dictionary
                ushort packetFragId = p.FragmentId;
                IncomingFragments incomingFragments;
                if (!_holdedFragments.TryGetValue(packetFragId, out incomingFragments))
                {
                    incomingFragments = new IncomingFragments
                    {
                        Fragments = new NetPacket[p.FragmentsTotal]
                    };
                    _holdedFragments.Add(packetFragId, incomingFragments);
                }

                //Cache
                var fragments = incomingFragments.Fragments;

                //Error check
                if (p.FragmentPart >= fragments.Length || fragments[p.FragmentPart] != null)
                {
                    p.Recycle();
                    NetUtils.DebugWriteError("Invalid fragment packet");
                    return;
                }
                //Fill array
                p.DontRecycleNow = true;
                fragments[p.FragmentPart] = p;

                //Increase received fragments count
                incomingFragments.ReceivedCount++;

                //Increase total size
                incomingFragments.TotalSize += p.GetDataSize();

                //Check for finish
                if (incomingFragments.ReceivedCount != fragments.Length)
                {
                    return;
                }

                NetUtils.DebugWrite("Received all fragments!");
                NetPacket resultingPacket = _packetPool.Get( p.Property, 0, incomingFragments.TotalSize );

                int resultingPacketOffset = 0;
                int firstFragmentSize = fragments[0].GetDataSize();
                for (int i = 0; i < incomingFragments.ReceivedCount; i++)
                {
                    //Create resulting big packet
                    int fragmentSize = fragments[i].GetDataSize();
                    Buffer.BlockCopy(
                        fragments[i].RawData,
                        0,
                        resultingPacket.RawData,
                        resultingPacketOffset + firstFragmentSize * i,
                        fragmentSize);

                    //Free memory
                    fragments[i].DontRecycleNow = false;
                    fragments[i].Recycle();
                    fragments[i] = null;
                }

                //Send to process
                _netManager.ReceiveFromPeer(resultingPacket, this);
                resultingPacket.Recycle();

                //Clear memory
                _holdedFragments.Remove(packetFragId);
            }
            else //Just simple packet
            {
                _netManager.ReceiveFromPeer(p, this);
                p.Recycle();
            }
        }

        private void ProcessMtuPacket(NetPacket packet)
        {
            if (packet.Size == 1 || 
                packet.RawData[0] >= NetConstants.PossibleMtu.Length)
                return;

            //MTU auto increase
            if (packet.Property == PacketProperty.MtuCheck)
            {
                if (packet.Size != NetConstants.PossibleMtu[packet.RawData[0]])
                {
                    return;
                }
                _mtuCheckAttempts = 0;
                NetUtils.DebugWrite("MTU check. Resend: " + packet.RawData[0]);
                var mtuOkPacket = _packetPool.Get(PacketProperty.MtuOk, 0, 1);
                mtuOkPacket.RawData[0] = packet.RawData[0];
                SendPacket(mtuOkPacket);
            }
            else if(packet.RawData[0] > _mtuIdx) //MtuOk
            {
                lock (_mtuMutex)
                {
                    _mtuIdx = packet.RawData[0];
                    _mtu = NetConstants.PossibleMtu[_mtuIdx];
                }
                //if maxed - finish.
                if (_mtuIdx == NetConstants.PossibleMtu.Length - 1)
                {
                    _finishMtu = true;
                }
                NetUtils.DebugWrite("MTU ok. Increase to: " + _mtu);
            }
        }

        //Process incoming packet
        internal void ProcessPacket(NetPacket packet)
        {
            _timeSinceLastPacket = 0;

            NetUtils.DebugWrite("[RR]PacketProperty: {0}", packet.Property);
            switch (packet.Property)
            {
                case PacketProperty.ConnectRequest:
                    //response with connect
                    long newId = BitConverter.ToInt64(packet.RawData, NetConstants.RequestConnectIdIndex);

                    NetUtils.DebugWrite("ConnectRequest LastId: {0}, NewId: {1}, EP: {2}", _connectId, newId, _remoteEndPoint);
                    if (newId > _connectId)
                    {
                        _connectId = newId;
                    }
                    
                    SendConnectAccept();
                    packet.Recycle();
                    break;

                case PacketProperty.Merged:
                    int pos = 0;
                    while (pos < packet.GetDataSize())
                    {
                        ushort size = BitConverter.ToUInt16(packet.RawData, pos);
                        pos += sizeof(ushort);
                        NetPacket mergedPacket = _packetPool.GetAndRead(packet.RawData, pos, size);
                        if (mergedPacket == null)
                        {
                            break;
                        }
                        pos += size;
                        ProcessPacket(mergedPacket);
                        mergedPacket.Recycle();
                    }
                    break;
                //If we get ping, send pong
                case PacketProperty.Ping:
                    if (NetUtils.RelativeSequenceNumber(packet.Sequence, _remotePingSequence) < 0)
                    {
                        packet.Recycle();
                        break;
                    }
                    NetUtils.DebugWrite("[PP]Ping receive, send pong");
                    _remotePingSequence = packet.Sequence;
                    packet.Recycle();

                    //send
                    _pingMustSend = true;
                    break;

                //If we get pong, calculate ping time and rtt
                case PacketProperty.Pong:
                    if (NetUtils.RelativeSequenceNumber(packet.Sequence, _pingSequence) < 0)
                    {
                        packet.Recycle();
                        break;
                    }
                    _pingSequence = packet.Sequence;
                    long rtt = NetTime.NowMs - _pingTimeStart;
                    UpdateRoundTripTime(rtt);
                    NetUtils.DebugWrite("[PP]Ping: {0}", rtt);
                    packet.Recycle();
                    break;

                //Process ack
                case PacketProperty.AckReliable:
                    getReliableUnorderedChannel(packet.Channel).ProcessAck(packet);
                    packet.Recycle();
                    break;

                case PacketProperty.AckReliableOrdered:
                    getReliableOrderedChannel(packet.Channel).ProcessAck(packet);
                    packet.Recycle();
                    break;

                //Process in order packets
                case PacketProperty.Sequenced:
                    if (getSequencedChannel(packet.Channel).ProcessPacket(packet) == false)
                    {
                        packet.Recycle();
                    }
                    break;

                case PacketProperty.ReliableUnordered:
                    if(getReliableUnorderedChannel(packet.Channel).ProcessPacket(packet) == false)
                    {
                        packet.Recycle();
                    }
                    break;

                case PacketProperty.ReliableOrdered:
                    if(getReliableOrderedChannel(packet.Channel).ProcessPacket(packet) == false)
                    {
                        packet.Recycle();
                    }
                    break;

                case PacketProperty.ReliableSequenced:
                    getReliableSequencedChannel(packet.Channel).ProcessPacket(packet);
                    break;

                //Simple packet without acks
                case PacketProperty.Unreliable:
                    AddIncomingPacket(packet);
                    return;

                case PacketProperty.MtuCheck:
                case PacketProperty.MtuOk:
                    ProcessMtuPacket(packet);
                    break;

                case PacketProperty.ShutdownOk:
                    _connectionState = ConnectionState.Disconnected;
                    break;
                
                default:
                    NetUtils.DebugWriteError("Error! Unexpected packet type: " + packet.Property);
                    break;
            }
        }

        private static bool CanMerge(PacketProperty property)
        {
            switch (property)
            {
                case PacketProperty.ConnectAccept:
                case PacketProperty.ConnectRequest:
                case PacketProperty.MtuOk:
                case PacketProperty.Pong:
                case PacketProperty.Disconnect:
                    return false;
                default:
                    return true;
            }
        }

        internal void FlushMergePacket()
        {
            //If merging enabled
            if (_mergePos > 0)
            {
                if (_mergeCount > 1)
                {
                    // build the mergeData
                    NetPacket _mergeData = _packetPool.Get(PacketProperty.Merged, 0, _mtu - NetPacket.GetHeaderSize(PacketProperty.Merged));
                    _mergePos = 0;
                    _mergeCount = 0;
                    while (_mergedPackets.Empty == false)
                    {
                        _mergeCount++;
                        NetPacket packet = _mergedPackets.Dequeue();
                        FastBitConverter.GetBytes(_mergeData.RawData, _mergePos, (ushort)packet.Size);
                        Buffer.BlockCopy(packet.RawData, 0, _mergeData.RawData, _mergePos + sizeof(ushort), packet.Size);
                        _mergePos += packet.Size + sizeof(ushort);
                        packet.Recycle();
                    }
                    if (_mergeCount > 0)
                    {
                        NetUtils.DebugWrite("Send merged: " + _mergePos + ", count: " + _mergeCount);
                        _mergeData.Size = NetPacket.GetHeaderSize(PacketProperty.Merged) + _mergePos;
                        _mergeData.Property = PacketProperty.Merged;
                        _netManager.SendRawAndRecycle(_mergeData, _remoteEndPoint);
                    }
#if STATS_ENABLED
                    Statistics.PacketsSent++;
                    Statistics.BytesSent += (ulong)(NetConstants.HeaderSize + _mergePos);
#endif
                }
                else
                {
                    NetPacket packet = _mergedPackets.Dequeue();
                    //Send without length information and merging
                    _netManager.SendRawAndRecycle(packet, _remoteEndPoint);
#if STATS_ENABLED
                    Statistics.PacketsSent++;
                    Statistics.BytesSent += (ulong)(_mergePos - 2);
#endif
                }
                _mergePos = 0;
                _mergeCount = 0;
            }
        }

        internal void SendRawData(NetPacket packet)
        {
            packet.Prepare();

            //2 - merge byte + minimal packet size + datalen(ushort)
            if (_netManager.MergeEnabled &&
                CanMerge(packet.Property))
            {
                if(_mergePos + packet.Size + sizeof(ushort) >= (_mtu - NetPacket.GetHeaderSize(PacketProperty.Merged)))
                    FlushMergePacket();

                _mergedPackets.Enqueue(packet);
                //FastBitConverter.GetBytes(_mergeData.RawData, _mergePos, (ushort)packet.Size);
                //Buffer.BlockCopy(packet.RawData, 0, _mergeData.RawData, _mergePos + sizeof(ushort), packet.Size);
                _mergePos += packet.Size + sizeof(ushort);
                _mergeCount++;

                //DebugWriteForce("Merged: " + _mergePos + "/" + (_mtu - 2) + ", count: " + _mergeCount);
                return;
            }

            NetUtils.DebugWrite(ConsoleColor.DarkYellow, "[P]SendingPacket: " + packet.Property);
            _netManager.SendRawAndRecycle(packet, _remoteEndPoint);
#if STATS_ENABLED
            Statistics.PacketsSent++;
            Statistics.BytesSent += (ulong)packet.Size;
#endif
            return;
        }

        /// <summary>
        /// Flush all queued packets
        /// </summary>
        public void Flush()
        {
            if (NetConstants.MultiChannelSize == 0)
            {
                if(NetManager.EnableReliableOrderedChannel)
                    _reliableOrderedChannels[0].SendNextPackets();
                if (NetManager.EnableReliableUnorderedChannel)
                    _reliableUnorderedChannels[0].SendNextPackets();
                //_reliableSequencedChannels[0].SendNextPackets();
                if (NetManager.EnableSequencedChannel)
                    _sequencedChannels[0].SendNextPackets();
                if (NetManager.EnableSimpleChannel)
                    _simpleChannels[0].SendNextPackets();
            }
            else
            {
                if (NetManager.EnableReliableOrderedChannel)
                    foreach (var channel in _reliableOrderedChannels) channel?.SendNextPackets();
                if (NetManager.EnableReliableUnorderedChannel)
                    foreach (var channel in _reliableUnorderedChannels) channel?.SendNextPackets();
                if (NetManager.EnableSequencedChannel)
                    foreach (var channel in _sequencedChannels) channel?.SendNextPackets();
                if (NetManager.EnableSimpleChannel)
                    foreach (var channel in _simpleChannels) channel?.SendNextPackets();
                //foreach (var channel in _reliableSequencedChannels) channel?.SendNextPackets();
            }

            FlushMergePacket();
        }

        internal void ProcessPong(int deltaTime)
        {
            if(_pingMustSend == true)
            {
                _pingMustSend = false;
                CreateAndSend(PacketProperty.Pong, _remotePingSequence);
            }
        }

        internal void Update(int deltaTime)
        {
            if ((_connectionState == ConnectionState.Connected || _connectionState == ConnectionState.ShutdownRequested) 
            && _timeSinceLastPacket > _netManager.DisconnectTimeout)
            {
                NetUtils.DebugWrite("[UPDATE] Disconnect by timeout: {0} > {1}", _timeSinceLastPacket, _netManager.DisconnectTimeout);
                _netManager.DisconnectPeer(this, DisconnectReason.Timeout, 0, true, null, 0, 0);
                return;
            }
            if (_connectionState == ConnectionState.ShutdownRequested)
            {
                SendShutdownPacket();
                return;
            }
            if (_connectionState == ConnectionState.Disconnected)
            {
                return;
            }

            _timeSinceLastPacket += deltaTime;
            if (_connectionState == ConnectionState.InProgress)
            {
                _connectTimer += deltaTime;
                if (_connectTimer > _netManager.ReconnectDelay)
                {
                    _connectTimer = 0;
                    _connectAttempts++;
                    if (_connectAttempts > _netManager.MaxConnectAttempts)
                    {
                        _netManager.DisconnectPeer(this, DisconnectReason.ConnectionFailed, 0, true, null, 0, 0);
                        return;
                    }

                    //else send connect again
                    SendConnectRequest();
                }
                return;
            }

            //Send ping
            _pingSendTimer += deltaTime;
            if (_pingSendTimer >= _netManager.PingInterval)
            {
                NetUtils.DebugWrite("[PP] Send ping...");

                //reset timer
                _pingSendTimer = 0;

                //send ping
                CreateAndSend(PacketProperty.Ping, _pingSequence);

                //reset timer
                _pingTimeStart = NetTime.NowMs;
            }

            //RTT - round trip time
            _rttResetTimer += deltaTime;
            if (_rttResetTimer >= RttResetDelay)
            {
                _rttResetTimer = 0;
                //Rtt update
                _rtt = _avgRtt;
                _ping = (int)_avgRtt;
                _netManager.ConnectionLatencyUpdated(this, _ping);
                _rttCount = 1;
            }

            //MTU - Maximum transmission unit
            if (!_finishMtu)
            {
                _mtuCheckTimer += deltaTime;
                if (_mtuCheckTimer >= MtuCheckDelay)
                {
                    _mtuCheckTimer = 0;
                    _mtuCheckAttempts++;
                    if (_mtuCheckAttempts >= MaxMtuCheckAttempts)
                    {
                        _finishMtu = true;
                    }
                    else
                    {
                        lock (_mtuMutex)
                        {
                            //Send increased packet
                            if (_mtuIdx < NetConstants.PossibleMtu.Length - 1)
                            {
                                int newMtu = NetConstants.PossibleMtu[_mtuIdx + 1] - NetConstants.HeaderSize;
                                var p = _packetPool.Get(PacketProperty.MtuCheck, 0, newMtu);
                                p.RawData[0] = (byte)(_mtuIdx + 1);
                                SendPacket(p);
                            }
                        }
                    }
                }
            }
            //MTU - end
            //Pending send
            Flush();
        }

        internal NetPacket GetPacketFromPool(PacketProperty property, int channel, int bytesCount)
        {
            return _packetPool.Get(property, channel, bytesCount);
        }
    }
}
