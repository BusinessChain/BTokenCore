using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTokenCore
{
  enum ErrorCode {
    DUPLICATE,
    ORPHAN,
    INVALID };

  class BitcoinException : Exception
  {
    public ErrorCode ErrorCode;
    

    public BitcoinException()
    { }

    public BitcoinException(string message)
        : base(message)
    { }

    public BitcoinException(
      string message, 
      ErrorCode errorCode)
        : base(message)
    {
      ErrorCode = errorCode;
    }

    public BitcoinException(string message, Exception inner)
        : base(message, inner)
    { }

  }
}