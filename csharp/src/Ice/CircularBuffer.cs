// Copyright (c) ZeroC, Inc. All rights reserved.

using System;
using System.Diagnostics;
using System.Threading;

namespace ZeroC.Ice
{
    internal class CircularBuffer
    {
        internal int Available => Capacity - Count;
        internal int Capacity => _buffer.Length;
        internal int Count
        {
            get
            {
                int count = _tail - _head;
                return count < 0 ? _buffer.Length + count : count;
            }
        }

        private readonly byte[] _buffer;
        private volatile int _head;
        private volatile int _tail;

        internal CircularBuffer(int capacity) => _buffer = new byte[capacity];

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
                if (tail < _buffer.Length)
                {
                    int count = Math.Min(_buffer.Length - tail, size);
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
                int count = Math.Min(head - tail, size);
                segment = new(_buffer, tail, count);
                _tail = tail + count;
            }
            return segment;
        }

        internal void Consume(Memory<byte> buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int head = _head;
                int tail = _tail;

                Memory<byte> segment;
                // Remaining size that needs to be filled up in the buffer
                int size = buffer.Length - offset;
                if (head < tail)
                {
                    int count = Math.Min(tail - head, size);
                    segment = new(_buffer, head, count);
                    _head = head + count;
                }
                else
                {
                    if (head < _buffer.Length)
                    {
                        int count = Math.Min(_buffer.Length - head, size);
                        segment = new(_buffer, head, count);
                        _head = head + count;
                    }
                    else
                    {
                        int count = Math.Min(tail, size);
                        segment = new(_buffer, 0, count);
                        _head = count;
                    }
                }
                Debug.Assert(segment.Length <= buffer.Length);
                segment.CopyTo(buffer[offset..]);
                offset += segment.Length;
            }
        }
    }
}
