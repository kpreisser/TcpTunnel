using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TcpTunnel.Utils
{
    internal static class BitConverterUtils
    {
        public static unsafe void ToBytes(short s, byte[] bytes, int offset)
        {
            if (offset < 0 || bytes.Length < offset + sizeof(short))
                throw new ArgumentOutOfRangeException();

            fixed (byte* pBytes = bytes)
            {
                *(short*)(pBytes + offset) = s;
            }
        }

        public static unsafe void ToBytes(int i, byte[] bytes, int offset)
        {
            if (offset < 0 || bytes.Length < offset + sizeof(int))
                throw new ArgumentOutOfRangeException();

            fixed (byte* pBytes = bytes)
            {
                *(int*)(pBytes + offset) = i;
            }
        }

        public static unsafe void ToBytes(long l, byte[] bytes, int offset)
        {
            if (offset < 0 || bytes.Length < offset + sizeof(long))
                throw new ArgumentOutOfRangeException();

            fixed (byte* pBytes = bytes)
            {
                *(long*)(pBytes + offset) = l;
            }
        }
    }
}
