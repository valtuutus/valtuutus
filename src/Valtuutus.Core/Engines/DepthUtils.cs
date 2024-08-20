using System;
using System.Collections.Generic;
using System.Text;

namespace Valtuutus.Core.Engines
{
    public static class DepthUtils
    {
        public static int Depth;
        public static bool CheckDepthLimit(this IWithDepth req) =>
            req.Depth == 0;

        public static void DecreaseDepth(this IWithDepth req) =>
            req.Depth--;
    }
}
