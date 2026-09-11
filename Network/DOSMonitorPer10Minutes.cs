using System;


namespace BTokenCore;

internal class DOSMonitorPer10Minutes
{
  int Counter;
  int MaxLevel;

  DateTime TimestampLastIncrement = DateTime.Now;


  internal DOSMonitorPer10Minutes(int maxLevel)
  {
    MaxLevel = maxLevel;
  }

  internal void Increment(int amount)
  {
    if (DateTime.Now - TimestampLastIncrement > TimeSpan.FromMinutes(10))
      Counter = 0;

    Counter += amount;
    TimestampLastIncrement = DateTime.Now;

    if (Counter > MaxLevel)
      throw new ProtocolException($"Exceed MaxLevel in DoS counter {GetType()}");
  }

  internal void Decrement(int amount)
  {
    Counter -= amount;
  }
}