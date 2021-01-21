// Copyright (c) ZeroC, Inc. All rights reserved.

using System;
using System.Diagnostics;

namespace ZeroC.Ice
{
    internal class CircularBuffer
    {
        internal int Available => _buffer.Length - Count;
        internal int Capacity => _buffer.Length;
        internal int Count => (_tail >= _head ? 0 : _buffer.Length) + _tail - _head;
        private readonly byte[] _buffer;
        private volatile int _head;
        private volatile int _tail;

        internal CircularBuffer(int capacity)
        {
            _buffer = new byte[capacity + 1];
            _tail = 0;
            _head = 0;
        }

        internal Memory<byte> Enqueue(int size)
        {
            if (size > Available)
            {
                throw new InvalidArgumentException("not enough space");
            }

            int head = _head;
            int tail = _tail;
            Memory<byte> segment;
            if (head <= tail)
            {
                if (tail < _buffer.Length - 1)
                {
                    int count = Math.Min(_buffer.Length - tail - 1, size);
                    segment = new(_buffer, tail, count);
                    _tail = tail + count;
                }
                else
                {
                    int count = Math.Min(head, size);
                    segment = new(_buffer, 0, count);
                    _tail = count;
                }
            }
            else
            {
                int count = Math.Min(head - tail - 1, size);
                segment = new(_buffer, tail, count);
                _tail = tail + count;
            }
            return segment;
        }

        internal int Consume(Memory<byte> buffer)
        {
            int head = _head;
            int tail = _tail;
            Memory<byte> segment;
            if (head <= tail)
            {
                int count = Math.Min(tail - head, buffer.Length);
                segment = new(_buffer, head, count);
                _head = head + count;
            }
            else
            {
                if (head < _buffer.Length)
                {
                    int count = Math.Min(_buffer.Length - head, buffer.Length);
                    segment = new(_buffer, head, count);
                    _head = head + count;
                }
                else
                {
                    int count = Math.Min(tail, buffer.Length);
                    segment = new(_buffer, 0, count);
                    _head = count;
                }
            }
            Debug.Assert(segment.Length <= buffer.Length);
            segment.CopyTo(buffer);
            return segment.Length;
        }
    }
}
