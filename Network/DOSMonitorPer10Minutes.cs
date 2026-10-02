using System;


namespace BTokenCore;

internal class DOSMonitorPer10Minutes
{
  double Level;
  int MaxLevel;
  int AmountDrainPer10Minutes;

  DateTime TimestampLastDrain = DateTime.UtcNow;


  internal DOSMonitorPer10Minutes(int maxLevel, int amountDrainPer10Minutes)
  {
    MaxLevel = maxLevel;
    AmountDrainPer10Minutes = amountDrainPer10Minutes;
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
    Level = Math.Max(0, Level - amount);
  }

  void Drain()
  {
    DateTime now = DateTime.UtcNow;

    Level = Math.Max(0, Level - AmountDrainPer10Minutes * (now - TimestampLastDrain) / TimeSpan.FromMinutes(10));
    TimestampLastDrain = now;
  }
}
