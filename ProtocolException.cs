using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace BTokenCore;

public class ProtocolException : Exception
{
  public readonly List<string> Parameters = [];


  public ProtocolException()
  { }

  public ProtocolException(string message, params string[] parameters)
      : base(message)
  {
    Parameters = [.. parameters];
  }

  public ProtocolException(string message, Exception inner)
      : base(message, inner)
  { }
}