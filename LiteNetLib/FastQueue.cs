using System;
using System.Collections;

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
        Node[]  _nodes;
        Node    _head;
        Node    _tail;

        // Need to set the capacity
        FastQueue()
        { }

        ~FastQueue()
        { Clear(); }

        public FastQueue(int capacity)
        {
            _nodes = new Node[capacity];
            for (int i = 0; i < capacity; i++)
            {
                _nodes[i] = new Node();
#if DEBUG
                _nodes[i].id = i;
#endif
            }
            for (int i = 0; i < capacity - 1; i++)
            {
                _nodes[i].next = _nodes[i + 1];
            }
            _nodes[capacity - 1].next = _nodes[0];    // Loop to beginning
            _head = _tail = _nodes[0];
        }

        public void Enqueue(T value)
        {
            if (value != null && _head.next != _tail)
            {
                _head.value = value;
                _head = _head.next;
            }
        }
        public T Dequeue()
        {
            if (_tail != _head)
            {
                T value = _tail.value;
                _tail.value = null;         // Release the reference, otherwise it leaks
                _tail = _tail.next;
                return value;
            }
            return null;
        }

        public bool Empty { 
            get { return _tail == _head; }
        }

        public void Clear()
        {
            while (Empty == false)
                Dequeue();
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
        Node[] _nodes;
        Node _head;
        Node _tail;

        // Need to set the capacity
        FastQueueTyped()
        { }

        public FastQueueTyped(int capacity)
        {
            _nodes = new Node[capacity];
            for (int i = 0; i < capacity; i++)
            {
                _nodes[i] = new Node();
#if DEBUG
                _nodes[i].id = i;
#endif
            }
            for (int i = 0; i < capacity - 1; i++)
            {
                _nodes[i].next = _nodes[i + 1];
            }
            _nodes[capacity - 1].next = _nodes[0];    // Loop to beginning
            _head = _tail = _nodes[0];
        }

        public void Enqueue(T value)
        {
            if (_head.next != _tail)
            {
                _head.value = value;
                _head = _head.next;
            }
        }
        public T Dequeue()
        {
            if (_tail != _head)
            {
                T value = _tail.value;
                _tail = _tail.next;
                return value;
            }
            return default(T);
        }

        public bool Empty
        {
            get { return _tail == _head; }
        }

        public void Clear()
        {
            _tail = _head;
        }
    }
}