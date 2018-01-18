﻿using System;
using System.Collections.Generic;

namespace LiteNetLib
{
    internal sealed class NetPeerCollection
    {
        private readonly Dictionary<NetEndPoint, NetPeer> _peersDict;
        private readonly NetPeer[] _peersArray;
        private bool _peersArrayHasChanged;
        public NetPeer[] _peersArrayClone;
        public int CloneCount;
        public int Count;

        public NetPeer this[int index]
        {
            get { return _peersArray[index]; }
        }

        public NetPeerCollection(int maxPeers)
        {
            _peersArray = new NetPeer[maxPeers];
            _peersArrayClone = new NetPeer[maxPeers];
            _peersDict = new Dictionary<NetEndPoint, NetPeer>();
            _peersArrayHasChanged = false;
        }

        public bool TryGetValue(NetEndPoint endPoint, out NetPeer peer)
        {
            return _peersDict.TryGetValue(endPoint, out peer);
        }

        public void Clear()
        {
            Array.Clear(_peersArrayClone, 0, Count);
            Array.Clear(_peersArray, 0, Count);
            _peersDict.Clear();
            CloneCount = Count = 0;
        }

        public void Add(NetEndPoint endPoint, NetPeer peer)
        {
            _peersArray[Count] = peer;
            _peersDict.Add(endPoint, peer);
            Count++;
            _peersArrayHasChanged = true;
        }

        public bool ContainsAddress(NetEndPoint endPoint)
        {
            return _peersDict.ContainsKey(endPoint);
        }

        public int UpdateClone()
        {
            if(_peersArrayHasChanged)
            {
                lock (_peersArrayClone)
                {
                    lock (_peersArray)
                    {
                        Array.Copy(_peersArray, _peersArrayClone, Count);
                        CloneCount = Count;
                        _peersArrayHasChanged = false;
                        return CloneCount;
                    }
                }
            }
            return CloneCount;
        }

        public NetPeer[] GetClone()
        {
            return _peersArrayClone;
        }

        public NetPeer[] ToArray()
        {
            NetPeer[] result = new NetPeer[Count];
            Array.Copy(_peersArray, 0, result, 0, Count);
            return result;
        }

        public void Remove(NetPeer peer)
        {
            for (int idx = 0; idx < Count; idx++)
            {
                if (_peersArray[idx] == peer)
                {
                    _peersDict.Remove(_peersArray[idx].EndPoint);
                    _peersArray[idx] = _peersArray[Count - 1];
                    _peersArray[Count - 1] = null;
                    Count--;
                    _peersArrayHasChanged = true;
                    break;
                }
            }
        }

        public void RemoveAt(int idx)
        {
            _peersDict.Remove(_peersArray[idx].EndPoint);
            _peersArray[idx] = _peersArray[Count - 1];
            _peersArray[Count - 1] = null;
            Count--;
            _peersArrayHasChanged = true;
        }
    }
}
