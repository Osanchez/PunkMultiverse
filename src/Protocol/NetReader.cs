using System;
using System.Text;
using UnityEngine;

namespace PunkMultiverse.Protocol
{
    /// <summary>Mirror of <see cref="NetWriter"/>. Wraps a received buffer without copying.</summary>
    public sealed class NetReader
    {
        private byte[] _buf;
        private int _end;
        public int Position { get; private set; }

        public void Assign(ArraySegment<byte> segment)
        {
            _buf = segment.Array;
            Position = segment.Offset;
            _end = segment.Offset + segment.Count;
        }

        public int Remaining => _end - Position;

        private void Require(int bytes)
        {
            if (Position + bytes > _end)
                throw new InvalidOperationException($"NetReader overrun: need {bytes}, have {Remaining}");
        }

        public byte ReadByte() { Require(1); return _buf[Position++]; }

        public MsgType ReadMsgType() => (MsgType)ReadByte();

        public bool ReadBool() => ReadByte() != 0;

        public ushort ReadUShort()
        {
            Require(2);
            ushort v = (ushort)(_buf[Position] | (_buf[Position + 1] << 8));
            Position += 2;
            return v;
        }

        public int ReadInt() => (int)ReadUInt();

        public uint ReadUInt()
        {
            Require(4);
            uint v = (uint)(_buf[Position] | (_buf[Position + 1] << 8) | (_buf[Position + 2] << 16) | (_buf[Position + 3] << 24));
            Position += 4;
            return v;
        }

        public ulong ReadULong()
        {
            Require(8);
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)_buf[Position + i] << (i * 8);
            Position += 8;
            return v;
        }

        public uint ReadVarUInt()
        {
            uint v = 0;
            int shift = 0;
            while (true)
            {
                byte b = ReadByte();
                v |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return v;
                shift += 7;
                if (shift > 35) throw new InvalidOperationException("VarUInt too long");
            }
        }

        public float ReadFloat() => BitConverter.Int32BitsToSingle((int)ReadUInt());

        public float ReadHalf() => Mathf.HalfToFloat(ReadUShort());

        public Vector2 ReadPosition() => new Vector2(ReadInt() / 32f, ReadInt() / 32f);

        public Vector2 ReadVector2Half() => new Vector2(ReadHalf(), ReadHalf());

        public string ReadString()
        {
            int len = (int)ReadVarUInt();
            if (len == 0) return string.Empty;
            Require(len);
            var s = Encoding.UTF8.GetString(_buf, Position, len);
            Position += len;
            return s;
        }

        public byte[] ReadBytes()
        {
            int len = (int)ReadVarUInt();
            Require(len);
            var result = new byte[len];
            Buffer.BlockCopy(_buf, Position, result, 0, len);
            Position += len;
            return result;
        }
    }
}
