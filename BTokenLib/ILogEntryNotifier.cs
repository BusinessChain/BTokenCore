using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace BTokenCore;

public interface ILogEntryNotifier
{
  public void NotifyLogEntry(string logEntry, string source);
}
