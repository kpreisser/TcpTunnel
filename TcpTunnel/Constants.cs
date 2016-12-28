using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace TcpTunnel
{
    internal class Constants
    {
        public const int ReceiveBufferSize = 32 * 1024;

        public const int SendBufferSize = 64 * 1024;

        public static readonly ArraySegment<byte> loginPrerequisiteBytes = new ArraySegment<byte>(new byte[]
        {
            1, 2, 3, 4,
            // TODO
        });


        [MethodImpl(MethodImplOptions.NoOptimization)]
        public static bool ComparePassword(ArraySegment<byte> enteredPassword, ArraySegment<byte> correctPasswort)
        {
            if (enteredPassword.Count != correctPasswort.Count)
                return false;

            bool ok = true;

            for (int i = 0; i < enteredPassword.Count; i++)
            {
                if (enteredPassword.Array[enteredPassword.Offset + i] != correctPasswort.Array[correctPasswort.Offset + i])
                {
                    ok = false & ok;
                    // kein Return!! Wal des muss komplett durchlaufen, um Timing-Attacken zu verhindern.
                }
                else
                {
                    ok = true & ok;
                }
            }

            return ok;
        }
    }
}
