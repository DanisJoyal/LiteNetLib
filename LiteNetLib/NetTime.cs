using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;

namespace LiteNetLib
{
    static public class NetTime
    {
        private static int s_startTick = Environment.TickCount;
        /// <summary>
        /// Get number of seconds since the application started
        /// </summary>
        public static double Now { get { return (Environment.TickCount - s_startTick) / 1000.0f; } }

        public static int NowMs { get { return Environment.TickCount - s_startTick; } }

        //public static void StartNewFrame() { s_startTick = Environment.TickCount; }

        public static int ToMs(double time)
        {
            return (int)(time * 1000.0);
        }

        public static string ToMsString(double time)
        {
            return ToMs(time).ToString();
        }
    }

    //static public class NetTime
    //{
    //    private static Stopwatch s_stopWath = Stopwatch.StartNew();
    //    /// <summary>
    //    /// Get number of seconds since the application started
    //    /// </summary>
    //    public static double Now { get { return s_stopWath.Elapsed.TotalSeconds; } }

    //    public static long NowMs { get { return s_stopWath.ElapsedMilliseconds; } }

    //    public static int ToMs(double time)
    //    {
    //        return (int)(time * 1000.0);
    //    }

    //    public static string ToMsString(double time)
    //    {
    //        return ToMs(time).ToString();
    //    }
    //}
}
