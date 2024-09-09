using System;
using System.Collections.Generic;
using System.Text;

namespace CryBar.BCnEncoder.Shared
{
    internal static class InternalUtils
    {
        public static void Swap<T>(ref T lhs, ref T rhs)
        {
            var temp = lhs;
            lhs = rhs;
            rhs = temp;
        }
    }
}
