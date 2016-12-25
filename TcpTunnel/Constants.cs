using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TcpTunnel
{
    internal class Constants
    {
        public const int ReceiveBufferSize = 32 * 1024;

        public const int SendBufferSize = 64 * 1024;

        public static readonly byte[] loginPrerequisiteBytes =
        {
            1, 2, 3, 4,
            // TODO
        };
    }
}
