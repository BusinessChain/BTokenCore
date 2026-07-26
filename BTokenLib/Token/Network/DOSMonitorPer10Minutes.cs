using System;

namespace BTokenLib
{
  public abstract partial class Token
  {
    partial class NetworkToken
    {
      class DOSMonitorPer10Minutes
      {
        int Counter;
        int MaxLevel;
        public bool IsOverflow;

        DateTime TimestampLastIncrement = DateTime.Now;


        public DOSMonitorPer10Minutes(int maxLevel)
        {
          MaxLevel = maxLevel;
        }

        public void Increment(int amount)
        {
          if (DateTime.Now - TimestampLastIncrement > TimeSpan.FromMinutes(10))
            Counter = 0;

          Counter += amount;
          TimestampLastIncrement = DateTime.Now;

          if (Counter > MaxLevel)
          {
            IsOverflow = true;
            throw new ProtocolException($"Exceed MaxLevel in DoS counter {GetType()}");
          }
        }

        public void Decrement(int amount)
        {
          Counter -= amount;
        }
      }
    }
  }
}