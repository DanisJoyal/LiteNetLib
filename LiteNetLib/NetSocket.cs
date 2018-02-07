using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;

namespace LiteNetLib
{
    internal class AsyncUserToken
    {
        public Socket Socket { get; set; }
        public int id;
    }

    internal sealed class NetSocket
    {
        private Socket _udpSocketv4;
        private EndPoint _bufferEndPointv4;
        private NetEndPoint _bufferNetEndPointv4;

        private Socket _udpSocketv6;
        private EndPoint _bufferEndPointv6;
        private NetEndPoint _bufferNetEndPointv6;

        Queue<byte[]> _sendBuffers;
        Queue<SocketAsyncEventArgs> _es;
        SocketAsyncEventArgs[] _er;

        private readonly NetManager.OnMessageReceived _onMessageReceived;

        private static readonly IPAddress MulticastAddressV6 = IPAddress.Parse (NetConstants.MulticastGroupIPv6);
        internal static readonly bool IPv6Support;

        public int LocalPort { get; private set; }

        static NetSocket()
        {
#if UNITY_4 || UNITY_5 || UNITY_5_3_OR_NEWER
            IPv6Support = Socket.SupportsIPv6;
#else
            IPv6Support = Socket.OSSupportsIPv6;
#endif
        }

        public NetSocket(NetManager.OnMessageReceived onMessageReceived)
        {
            _onMessageReceived = onMessageReceived;
            _sendBuffers = new Queue<byte[]>();
            _es = new Queue<SocketAsyncEventArgs>();
            _er = new SocketAsyncEventArgs[2];

            _er[0] = new SocketAsyncEventArgs();
            _er[0].SetBuffer(new byte[NetConstants.MaxPacketSize], 0, NetConstants.MaxPacketSize);
            _er[0].SocketFlags = SocketFlags.None;
            _er[0].Completed += new EventHandler<SocketAsyncEventArgs>(ReceiveCompleted);
            _er[0].UserToken = new AsyncUserToken();

            _er[1] = new SocketAsyncEventArgs();
            _er[1].SetBuffer(new byte[NetConstants.MaxPacketSize], 0, NetConstants.MaxPacketSize);
            _er[1].SocketFlags = SocketFlags.None;
            _er[1].Completed += new EventHandler<SocketAsyncEventArgs>(ReceiveCompleted);
            _er[1].UserToken = new AsyncUserToken();

        }

        internal bool hasStarted = false;

        public void Receive(bool ipV6, byte[] receiveBuffer)
        {
            Socket socket;
            EndPoint bufferEndPoint;
            NetEndPoint bufferNetEndPoint;
            int result;

            if (ipV6 == true)
            {
                socket = _udpSocketv6;
                bufferEndPoint = _bufferEndPointv6;
                bufferNetEndPoint = _bufferNetEndPointv6;
            }
            else
            {
                socket = _udpSocketv4;
                bufferEndPoint = _bufferEndPointv4;
                bufferNetEndPoint = _bufferNetEndPointv4;
            }

            lock (dataReceived)
            {
                for (int i = 0; i < dataReceived.Count; ++i)
                {
                    _onMessageReceived(dataReceived[i], dataReceived[i].Length, 0, endPointReceived[i]);
                }
                dataReceived.Clear();
                endPointReceived.Clear();
            }

            while (socket != null)
            {
                if (hasStarted)
                    return;

                _er[1].RemoteEndPoint = _er[0].RemoteEndPoint = bufferEndPoint;
                (_er[0].UserToken as AsyncUserToken).Socket = socket;
                (_er[0].UserToken as AsyncUserToken).id = 0;
                (_er[1].UserToken as AsyncUserToken).Socket = socket;
                (_er[1].UserToken as AsyncUserToken).id = 1;

                //if (socket == null || socket.Available < 1)
                //    return;

                //Reading data
                try
                {
                    if (socket.ReceiveFromAsync(_er[0]) == true)
                    {
                        hasStarted = true;
                        return;
                    }
                    //result = e.BytesTransferred;
                    //if (!bufferNetEndPoint.EndPoint.Equals(e.RemoteEndPoint))
                    //{
                    //    bufferNetEndPoint = new NetEndPoint((IPEndPoint)e.RemoteEndPoint);
                    //}

                    //result = socket.ReceiveFrom(receiveBuffer, 0, receiveBuffer.Length, SocketFlags.None, ref bufferEndPoint);
                    //if (!bufferNetEndPoint.EndPoint.Equals(bufferEndPoint))
                    //{
                    //    bufferNetEndPoint = new NetEndPoint((IPEndPoint)bufferEndPoint);
                    //}
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.WouldBlock)
                    {
                        // Hiii! Increase buffer size
                        return;
                    }
                    if (ex.SocketErrorCode == SocketError.ConnectionReset ||
                        ex.SocketErrorCode == SocketError.MessageSize ||
                        ex.SocketErrorCode == SocketError.Interrupted)
                    {
                        //10040 - message too long
                        //10054 - remote close (not error)
                        //Just UDP
                        NetUtils.DebugWrite(ConsoleColor.DarkRed, "[R] Ingored error: {0} - {1}", (int)ex.SocketErrorCode, ex.ToString());
                        return;
                    }
                    NetUtils.DebugWriteError("[R]Error code: {0} - {1}", (int)ex.SocketErrorCode, ex.ToString());
                    _onMessageReceived(null, 0, (int)ex.SocketErrorCode, bufferNetEndPoint);
                    return;
                }

                //All ok!
                NetUtils.DebugWrite(ConsoleColor.Blue, "[R]Received data from {0}, result: {1}", bufferNetEndPoint.ToString(), result);
                //_onMessageReceived(e.Buffer, e.BytesTransferred, 0, bufferNetEndPoint);
            }
        }

        public bool Bind(IPAddress addressIPv4, IPAddress addressIPv6, int port, bool reuseAddress, bool enableIPv4, bool enableIPv6)
        {
            if (enableIPv4)
            {
                _udpSocketv4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _udpSocketv4.Blocking = false;
                _udpSocketv4.ReceiveBufferSize = NetConstants.SocketBufferSize;
                _udpSocketv4.SendBufferSize = NetConstants.SocketBufferSize;
                _udpSocketv4.Ttl = NetConstants.SocketTTL;
                if (reuseAddress)
                    _udpSocketv4.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
#if !NETCORE
                _udpSocketv4.DontFragment = true;
#endif
                try
                {
                    _udpSocketv4.EnableBroadcast = true;
                }
                catch (SocketException e)
                {
                    NetUtils.DebugWriteError("Broadcast error: {0}", e.ToString());
                }

                if (!BindSocket(_udpSocketv4, new IPEndPoint(addressIPv4, port)))
                {
                    return false;
                }
                LocalPort = ((IPEndPoint)_udpSocketv4.LocalEndPoint).Port;

                _bufferEndPointv4 = new IPEndPoint(_udpSocketv4.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
                _bufferNetEndPointv4 = new NetEndPoint((IPEndPoint)_bufferEndPointv4);
            }

            //Check IPv6 support
            if (!IPv6Support || enableIPv6 == false)
                return true;

            _udpSocketv6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            _udpSocketv6.Blocking = false;
            _udpSocketv6.ReceiveBufferSize = NetConstants.SocketBufferSize;
            _udpSocketv6.SendBufferSize = NetConstants.SocketBufferSize;
            //_udpSocketv6.Ttl = NetConstants.SocketTTL;
            if (reuseAddress)
                _udpSocketv6.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            //Use one port for two sockets
            if (BindSocket(_udpSocketv6, new IPEndPoint(addressIPv6, LocalPort)))
            {
                try
                {
#if !ENABLE_IL2CPP
                    _udpSocketv6.SetSocketOption(
                        SocketOptionLevel.IPv6, 
                        SocketOptionName.AddMembership,
                        new IPv6MulticastOption(MulticastAddressV6));
#endif
                }
                catch(Exception)
                {
                    // Unity3d throws exception - ignored
                }
            }

            _bufferEndPointv6 = new IPEndPoint(_udpSocketv4.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
            _bufferNetEndPointv6 = new NetEndPoint((IPEndPoint)_bufferEndPointv6);

            return true;
        }

        private bool BindSocket(Socket socket, IPEndPoint ep)
        {
            try
            {
                socket.Bind(ep);
                NetUtils.DebugWrite(ConsoleColor.Blue, "[B]Succesfully binded to port: {0}", ((IPEndPoint)socket.LocalEndPoint).Port);
            }
            catch (SocketException ex)
            {
                NetUtils.DebugWriteError("[B]Bind exception: {0}", ex.ToString());
                //TODO: very temporary hack for iOS (Unity3D)
                if (ex.SocketErrorCode == SocketError.AddressFamilyNotSupported)
                {
                    return true;
                }
                return false;
            }
            return true;
        }

        public bool SendBroadcast(byte[] data, int offset, int size, int port)
        {
            try
            {
                if (_udpSocketv4.SendTo(
                        data,
                        offset,
                        size,
                        SocketFlags.None,
                        new IPEndPoint(IPAddress.Broadcast, port)) <= 0)
                        return false;
           
                if (IPv6Support)
                {
                    if (_udpSocketv6.SendTo(
                            data, 
                            offset, 
                            size, 
                            SocketFlags.None, 
                            new IPEndPoint(MulticastAddressV6, port)) <= 0)
                        return false;
                }
            }
            catch (Exception ex)
            {
                NetUtils.DebugWriteError("[S][MCAST]" + ex);
                return false;
            }
            return true;
        }

        public void SendCompleted(Object sender, SocketAsyncEventArgs e)
        {
            lock (_es) { _es.Enqueue(e); }
        }

        internal List<Byte[]> dataReceived = new List<Byte[]>();
        internal List<NetEndPoint> endPointReceived = new List<NetEndPoint>();

        public void ReceiveCompleted(Object sender, SocketAsyncEventArgs e)
        {
            if((e.UserToken as AsyncUserToken).id == 0)
            {
                _er[1].SetBuffer(0, NetConstants.MaxPacketSize);
                _er[1].RemoteEndPoint = e.RemoteEndPoint;
                (e.UserToken as AsyncUserToken).Socket.ReceiveFromAsync(_er[1]);
            }
            else if ((e.UserToken as AsyncUserToken).id == 1)
            {
                _er[0].SetBuffer(0, NetConstants.MaxPacketSize);
                _er[0].RemoteEndPoint = e.RemoteEndPoint;
                (e.UserToken as AsyncUserToken).Socket.ReceiveFromAsync(_er[0]);
            }
            if (e.BytesTransferred > 0 && e.SocketError == SocketError.Success)
            {
                Byte[] newData = new Byte[e.BytesTransferred];
                Buffer.BlockCopy(e.Buffer, 0, newData, 0, e.BytesTransferred);

                lock (dataReceived)
                {
                    dataReceived.Add(newData);
                    endPointReceived.Add(new NetEndPoint((IPEndPoint)e.RemoteEndPoint));
                }

                //_onMessageReceived(e.Buffer, e.BytesTransferred, 0, new NetEndPoint((IPEndPoint)e.RemoteEndPoint));
            }
            else
                NetUtils.DebugWriteError("[S][MCAST]" + e.SocketError.ToString());
            //lock (_er) { _er.Enqueue(e); }
        }

        internal SocketAsyncEventArgs GetSocketAsyncEventArgs(bool send)
        {
            SocketAsyncEventArgs e = null;
            if (_es.Count == 0)
            {
                e = new SocketAsyncEventArgs();
                e.SetBuffer(new byte[NetConstants.MaxPacketSize], 0, NetConstants.MaxPacketSize);
                e.SocketFlags = SocketFlags.None;
                e.Completed += new EventHandler<SocketAsyncEventArgs>(SendCompleted);
            }
            else
            {
                lock (_es)
                {
                    e = _es.Dequeue();
                }
            }
            return e;
        }

        public int SendTo(byte[] data, int offset, int size, NetEndPoint remoteEndPoint, ref int errorCode)
        {
            SocketAsyncEventArgs e = GetSocketAsyncEventArgs(true);
            Buffer.BlockCopy(data, offset, e.Buffer, 0, size);
            e.SetBuffer(0, size);
            e.RemoteEndPoint = remoteEndPoint.EndPoint;

            try
            {
                _udpSocketv4.SendToAsync(e);
                int result = e.BytesTransferred;

                //int result = 0;
                //if (_udpSocketv4 != null && remoteEndPoint.EndPoint.AddressFamily == AddressFamily.InterNetwork)
                //{
                //    result = _udpSocketv4.SendTo(data, offset, size, SocketFlags.None, remoteEndPoint.EndPoint);
                //}
                //else if (_udpSocketv6 != null)
                //{
                //    result = _udpSocketv6.SendTo(data, offset, size, SocketFlags.None, remoteEndPoint.EndPoint);
                //}

                NetUtils.DebugWrite(ConsoleColor.Blue, "[S]Send packet to {0}, result: {1}", remoteEndPoint.EndPoint, result);
                return result;
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.Interrupted || 
                    ex.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
                {
                    return 0;
                }
                if (ex.SocketErrorCode != SocketError.MessageSize)
                {
                    NetUtils.DebugWriteError("[S]" + ex);
                }
                
                errorCode = (int)ex.SocketErrorCode;
                return -1;
            }
            catch (Exception ex)
            {
                NetUtils.DebugWriteError("[S]" + ex);
                return -1;
            }
        }

        private void CloseSocket(Socket s)
        {
#if NETCORE
            s.Dispose();
#else
            s.Close();
#endif
        }

        public void Close()
        {
            //Close IPv4
            if (_udpSocketv4 != null)
            {
                CloseSocket(_udpSocketv4);
                _udpSocketv4 = null;
            }

            //No ipv6
            if (_udpSocketv6 == null)
                return;

            //Close IPv6
            if (_udpSocketv6 != null)
            {
                CloseSocket(_udpSocketv6);
                _udpSocketv6 = null;
            }
        }
    }
}