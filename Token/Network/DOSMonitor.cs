namespace BTokenCore;


internal partial class Network
{
  internal class DOSMonitor
  {
    double Level;
    int MaxLevel;
    int AmountDrainPer10Minutes;

    DateTime TimestampLastDrain = DateTime.UtcNow;


    internal DOSMonitor(int maxLevel, int amountDrainPer10Minutes)
    {
      MaxLevel = maxLevel;
      AmountDrainPer10Minutes = amountDrainPer10Minutes;
    }

    internal void Increment(int amount)
    {
      if (!TryIncrement(amount))
        throw new ProtocolException($"Exceed MaxLevel in DoS counter {GetType()}");
    }

    internal bool TryIncrement(int amount)
    {
      Drain();

      if (Level + amount > MaxLevel)
        return false;

      Level += amount;
      return true;
    }

    internal void Decrement(int amount)
    {
      Level = Math.Max(0, Level - amount);
    }

    internal Dictionary<string, object> GetSnapshotState()
    {
      return new() { ["level"] = GetLevelDrained(DateTime.UtcNow), ["levelMax"] = MaxLevel };
    }

    double GetLevelDrained(DateTime now)
    {
      return Math.Max(0, Level - AmountDrainPer10Minutes * (now - TimestampLastDrain) / TimeSpan.FromMinutes(10));
    }

    void Drain()
    {
      DateTime now = DateTime.UtcNow;

      Level = GetLevelDrained(now);
      TimestampLastDrain = now;
    }
  }
}
