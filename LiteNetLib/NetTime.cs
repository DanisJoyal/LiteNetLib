using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;

namespace LiteNetLib
{
    static public class NetTime
    {
        private static long s_timeInitialized = Stopwatch.GetTimestamp();
        private static double s_dInvFreq = 1.0 / (double)Stopwatch.Frequency;

        /// <summary>
        /// Get number of seconds since the application started
        /// </summary>
        public static double Now { get { return (double)(Stopwatch.GetTimestamp() - s_timeInitialized) * s_dInvFreq; } }

        public static long NowMs { get { return (Stopwatch.GetTimestamp() - s_timeInitialized) * 1000 / Stopwatch.Frequency; } }

        public static int ToMs(double time)
        {
            return (int)(time * 1000.0);
        }

        public static string ToMsString(double time)
        {
            return ToMs(time).ToString();
        }
    }
}
