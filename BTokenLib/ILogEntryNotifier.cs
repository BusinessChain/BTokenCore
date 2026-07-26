using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenLib
{
  public abstract partial class Token
  {
    public interface ILogEntryNotifier
    {
      public void NotifyLogEntry(string logEntry, string source);
    }
  }
}
