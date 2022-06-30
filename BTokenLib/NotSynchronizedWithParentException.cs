using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenLib
{
  public class NotSynchronizedWithParentException : Exception
  {
    public NotSynchronizedWithParentException()
    { }

    public NotSynchronizedWithParentException(string message)
        : base(message)
    { }

    public NotSynchronizedWithParentException(string message, Exception inner)
        : base(message, inner)
    { }
  }
}
