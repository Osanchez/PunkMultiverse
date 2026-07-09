using System;
using System.Text;
using UnityEngine;

namespace PunkMultiverse.Protocol
{
    /// <summary>
    /// Growable little-endian binary writer over a pooled buffer. Not thread-safe; use from the
    /// main thread only (all our capture hooks run there). Reuse via Reset() to avoid allocs.
    /// </summary>
    public sealed class NetWriter
    {
        private byte[] _buf;
        public int Position { get; private set; }

        public NetWriter(int capacity = 1024) => _buf = new byte[capacity];

        public byte[] Buffer => _buf;

        public void Reset() => Position = 0;

        public ArraySegment<byte> ToSegment() => new ArraySegment<byte>(_buf, 0, Position);

        private void Ensure(int bytes)
        {
            if (Position + bytes <= _buf.Length) return;
            int newSize = _buf.Length * 2;
            while (newSize < Position + bytes) newSize *= 2;
            Array.Resize(ref _buf, newSize);
        }

        public void WriteByte(byte v) { Ensure(1); _buf[Position++] = v; }

        public void WriteMsgType(MsgType t) => WriteByte((byte)t);

        public void WriteBool(bool v) => WriteByte(v ? (byte)1 : (byte)0);

        public void WriteUShort(ushort v)
        {
            Ensure(2);
            _buf[Position++] = (byte)v;
            _buf[Position++] = (byte)(v >> 8);
        }

        public void WriteInt(int v) => WriteUInt((uint)v);

        public void WriteUInt(uint v)
        {
            Ensure(4);
            _buf[Position++] = (byte)v;
            _buf[Position++] = (byte)(v >> 8);
            _buf[Position++] = (byte)(v >> 16);
            _buf[Position++] = (byte)(v >> 24);
        }

        public void WriteULong(ulong v)
        {
            Ensure(8);
            for (int i = 0; i < 8; i++) _buf[Position++] = (byte)(v >> (i * 8));
        }

        /// <summary>7-bit variable-length unsigned int (1-5 bytes).</summary>
        public void WriteVarUInt(uint v)
        {
            Ensure(5);
            while (v >= 0x80)
            {
                _buf[Position++] = (byte)(v | 0x80);
                v >>= 7;
            }
            _buf[Position++] = (byte)v;
        }

        public void WriteFloat(float v)
        {
            Ensure(4);
            uint bits = (uint)BitConverter.SingleToInt32Bits(v);
            _buf[Position++] = (byte)bits;
            _buf[Position++] = (byte)(bits >> 8);
            _buf[Position++] = (byte)(bits >> 16);
            _buf[Position++] = (byte)(bits >> 24);
        }

        /// <summary>16-bit half float — velocities, HP fractions, angles.</summary>
        public void WriteHalf(float v) => WriteUShort(Mathf.FloatToHalf(v));

        /// <summary>Position quantized to 1/32 world unit, stored as two int32s (level ≤ ~2000 units).</summary>
        public void WritePosition(Vector2 p)
        {
            WriteInt(Mathf.RoundToInt(p.x * 32f));
            WriteInt(Mathf.RoundToInt(p.y * 32f));
        }

        public void WriteVector2Half(Vector2 v) { WriteHalf(v.x); WriteHalf(v.y); }

        public void WriteString(string s)
        {
            if (string.IsNullOrEmpty(s)) { WriteVarUInt(0); return; }
            var bytes = Encoding.UTF8.GetBytes(s);
            WriteVarUInt((uint)bytes.Length);
            Ensure(bytes.Length);
            System.Buffer.BlockCopy(bytes, 0, _buf, Position, bytes.Length);
            Position += bytes.Length;
        }

        public void WriteBytes(byte[] data, int offset, int count)
        {
            WriteVarUInt((uint)count);
            Ensure(count);
            System.Buffer.BlockCopy(data, offset, _buf, Position, count);
            Position += count;
        }
    }
}
