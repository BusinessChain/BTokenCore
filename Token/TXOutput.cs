namespace BTokenCore;

public class TXOutput
{
  internal byte[] Script;


  internal virtual Dictionary<string, object> GetSnapshotState()
  {
    return new Dictionary<string, object>();
  }
}
