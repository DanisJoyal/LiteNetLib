using System;
using System.Collections.Generic;

namespace LiteNetLib
{
    class FastQueue<T> where T : class
    {
        private class Node
        {
            public T value;
            public Node next;
#if DEBUG
            public int id;
#endif
        }
        List<Node[]>    _nodes;
        Node            _head;
        Node            _tail;
        int             _growFactor; // 100 == 1.0, 130 == 1.3, 200 == 2.0
        int             _length;
        int             _size;
        const int       _MinimumGrow = 8;

        // Need to set the capacity
        public FastQueue() : this(32, (float)2.0)
        {
        }

        public FastQueue(int capacity) : this(capacity, (float)2.0)
        {
        }

        public FastQueue(int capacity, float growFactor)
        {
            if (capacity < 0)
                throw new ArgumentOutOfRangeException("capacity", "FastQueue: Capacity must be greater than 0.");
            if (!(growFactor >= 1.0 && growFactor <= 10.0))
                throw new ArgumentOutOfRangeException("growFactor", "FastQueue: growFactor must be set between 1.0f and 10.0f.");

            _nodes = new List<Node[]>(4);
            _growFactor = (int)((1.0f - growFactor) * 100);
            AddNodeArray(capacity);
            _head = _tail = _nodes[0][0];
            _size = 0;
        }

        ~FastQueue()
        { Clear(); }

        private Node[] AddNodeArray(int size)
        {
            Node[] newNodes = new Node[size];
            for (int i = 0; i < size; i++)
            {
                newNodes[i] = new Node();
#if DEBUG
                newNodes[i].id = i;
#endif
                if(i != 0)
                    newNodes[i - 1].next = newNodes[i];
            }
            newNodes[size - 1].next = newNodes[0];    // Loop to beginning
            _length += size;
            _nodes.Add(newNodes);
            return newNodes;
        }

        public int Count { get { return _size; } }

        public void Enqueue(T value)
        {
            if (value != null)
            {
                if (_head.next == _tail)
                {
                    // Queue is full
                    int newCapacity = (_length * _growFactor) / 100;
                    if (newCapacity < _MinimumGrow)
                        newCapacity = _MinimumGrow;
                    Node[] createNodes = AddNodeArray(newCapacity);
                    createNodes[createNodes.Length - 1].next = _tail;
                    _head.next = createNodes[0];
                }

                _head.value = value;
                _head = _head.next;
                _size++;
            }
        }
        public T Dequeue()
        {
            if (_tail != _head)
            {
                T value = _tail.value;
                _tail.value = null;         // Release the reference, otherwise it leaks
                _tail = _tail.next;
                --_size;
                return value;
            }
            return null;
        }

        public void Resize()
        {
            if(Empty == true && _nodes.Count > 1)
            {
                _nodes.RemoveRange(1, _nodes.Count - 1);
                _length = _nodes[0].Length;
                _head = _tail = _nodes[0][0];
                _size = 0;
            }
        }

        public bool Empty { 
            get { return _tail == _head; }
        }

        public void Clear()
        {
            while (Empty == false)
                Dequeue();
            _size = 0;
        }
    }

    class FastQueueTyped<T> where T : struct
    {
        private class Node
        {
            public T value;
            public Node next;
#if DEBUG
            public int id;
#endif
        }
        List<Node[]> _nodes;
        Node _head;
        Node _tail;
        int _growFactor; // 100 == 1.0, 130 == 1.3, 200 == 2.0
        int _length;
        int _size;
        const int _MinimumGrow = 8;

        // Need to set the capacity
        public FastQueueTyped() : this(32, (float)2.0)
        {
        }

        public FastQueueTyped(int capacity) : this(capacity, (float)2.0)
        {
        }

        public FastQueueTyped(int capacity, float growFactor)
        {
            if (capacity < 0)
                throw new ArgumentOutOfRangeException("capacity", "FastQueue: Capacity must be greater than 0.");
            if (!(growFactor >= 1.0 && growFactor <= 10.0))
                throw new ArgumentOutOfRangeException("growFactor", "FastQueue: growFactor must be set between 1.0f and 10.0f.");

            _nodes = new List<Node[]>(4);
            _growFactor = (int)((1.0f - growFactor) * 100);
            AddNodeArray(capacity);
            _head = _tail = _nodes[0][0];
            _size = 0;
        }

        ~FastQueueTyped()
        { Clear(); }

        private Node[] AddNodeArray(int size)
        {
            Node[] newNodes = new Node[size];
            for (int i = 0; i < size; i++)
            {
                newNodes[i] = new Node();
#if DEBUG
                newNodes[i].id = i;
#endif
                if (i != 0)
                    newNodes[i - 1].next = newNodes[i];
            }
            newNodes[size - 1].next = newNodes[0];    // Loop to beginning
            _length += size;
            _nodes.Add(newNodes);
            return newNodes;
        }

        public int Count { get { return _size; } }

        public void Enqueue(T value)
        {
            if (_head.next == _tail)
            {
                // Queue is full
                int newCapacity = (_length * _growFactor) / 100;
                if (newCapacity < _MinimumGrow)
                    newCapacity = _MinimumGrow;
                Node[] createNodes = AddNodeArray(newCapacity);
                createNodes[createNodes.Length - 1].next = _tail;
                _head.next = createNodes[0];
            }

            _head.value = value;
            _head = _head.next;
            _size++;
        }
        public T Dequeue()
        {
            if (_tail != _head)
            {
                T value = _tail.value;
                _tail = _tail.next;
                --_size;
                return value;
            }
            return default(T);
        }

        public void Resize()
        {
            if (Empty == true && _nodes.Count > 1)
            {
                _nodes.RemoveRange(1, _nodes.Count - 1);
                _length = _nodes[0].Length;
                _head = _tail = _nodes[0][0];
                _size = 0;
            }
        }

        public bool Empty
        {
            get { return _tail == _head; }
        }

        public void Clear()
        {
            _tail = _head;
            _size = 0;
        }
    }
}