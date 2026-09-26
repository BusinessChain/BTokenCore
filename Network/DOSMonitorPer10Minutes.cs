using System;


namespace BTokenCore;

internal class DOSMonitorPer10Minutes
{
  double Level;
  int MaxLevel;

  DateTime TimestampLastDrain = DateTime.UtcNow;


  internal DOSMonitorPer10Minutes(int maxLevel)
  {
    MaxLevel = maxLevel;
  }

  internal void Increment(int amount)
  {
    Drain();

    Level += amount;

    if (Level > MaxLevel)
      throw new ProtocolException($"Exceed MaxLevel in DoS counter {GetType()}");
  }

  internal void Decrement(int amount)
  {
    Drain();

    Level = Math.Max(0, Level - amount);
  }

  void Drain()
  {
    DateTime now = DateTime.UtcNow;

    Level = Math.Max(0, Level - MaxLevel * (now - TimestampLastDrain) / TimeSpan.FromMinutes(10));
    TimestampLastDrain = now;
  }
}
